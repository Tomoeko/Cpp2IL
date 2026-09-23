#!/usr/bin/env python3
"""Prune bulky, reproducible Unity build trees from completed local runs.

Receipts, logs, recovered source and recovery reports remain in the ignored
Files/ directory. The default is a dry run; --apply is deliberately explicit.
"""

import argparse
from dataclasses import dataclass
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import shutil
import stat
import subprocess
import time


ROOT = Path(__file__).resolve().parent.parent
VALIDATION = ROOT / "Files" / "validation"
HEAVY_TREES = ("project", "player", "player-input")
FINAL_STATUSES = frozenset(("passed", "failed", "observed"))
MAX_REPORT_BYTES = 16 * 1024 * 1024


@dataclass(frozen=True)
class Candidate:
    path: Path
    receipt: Path
    status: str
    receipt_mtime_ns: int
    allocated_bytes: int


def allocated_bytes(path):
    """Count allocated blocks without following links outside a generated tree."""
    total = 0
    pending = [path]
    while pending:
        current = pending.pop()
        info = current.lstat()
        total += info.st_blocks * 512
        if stat.S_ISDIR(info.st_mode):
            with os.scandir(current) as entries:
                pending.extend(Path(entry.path) for entry in entries)
    return total


def plan(validation_root, minimum_age_seconds=24 * 3600, now=None, excluded=()):
    if not math.isfinite(minimum_age_seconds) or minimum_age_seconds < 0:
        raise ValueError("minimum age must be finite and nonnegative")
    validation_root = Path(validation_root)
    if validation_root.is_symlink() or not validation_root.is_dir():
        return []
    root = validation_root.resolve()
    cutoff = (time.time() if now is None else now) - minimum_age_seconds
    excluded = set(excluded)
    candidates = []
    for run in sorted(validation_root.iterdir()):
        if not run.is_dir() or run.is_symlink() or run.name in excluded:
            continue
        receipt = next((run / name for name in ("roundtrip.json", "receipt.json")
                        if (run / name).is_file() and not (run / name).is_symlink()), None)
        if receipt is None or receipt.stat().st_mtime > cutoff:
            continue
        try:
            status = json.loads(receipt.read_text(encoding="utf-8")).get("status")
        except (OSError, ValueError):
            continue
        if not isinstance(status, str) or status not in FINAL_STATUSES:
            continue
        parents = [run] + [child for child in run.iterdir()
                           if child.is_dir() and not child.is_symlink() and child.name not in HEAVY_TREES]
        for parent in parents:
            for name in HEAVY_TREES:
                path = parent / name
                if (path.is_dir() and not path.is_symlink() and
                        path.resolve().is_relative_to(root)):
                    candidates.append(Candidate(path, receipt, status,
                                                receipt.stat().st_mtime_ns, allocated_bytes(path)))
    return candidates


def preserve_project_reports(project):
    reports = project / "Reports"
    if not reports.is_dir() or reports.is_symlink():
        return []
    items = list(reports.rglob("*"))
    if any(item.is_symlink() for item in items):
        raise ValueError("a generated project report contains a symbolic link")
    files = [item for item in items if item.is_file()]
    if sum(item.stat().st_size for item in files) > MAX_REPORT_BYTES:
        raise ValueError("generated project reports exceed the retention limit")
    destination = project.parent / "preserved-project-reports"
    if destination.is_symlink():
        raise ValueError("the preserved report directory is a symbolic link")
    kept = []
    for source in files:
        target = destination / source.relative_to(reports)
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            if target.is_symlink() or target.read_bytes() != source.read_bytes():
                raise ValueError("an existing preserved project report differs")
        else:
            shutil.copy2(source, target)
        kept.append(str(target.relative_to(project.parent)))
    return kept


def prune(candidates, validation_root, manifest_root):
    validation_root = Path(validation_root).resolve()
    manifest_root = Path(manifest_root)
    manifest_root.mkdir(parents=True, exist_ok=True)
    manifest = manifest_root / ("prune-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") +
                                "-" + str(time.time_ns()) + ".json")
    rows = []
    for candidate in candidates:
        path = candidate.path
        if (path.is_symlink() or not path.is_dir() or path.name not in HEAVY_TREES or
                not path.resolve().is_relative_to(validation_root) or
                not candidate.receipt.is_file() or candidate.receipt.is_symlink() or
                candidate.receipt.stat().st_mtime_ns != candidate.receipt_mtime_ns or
                json.loads(candidate.receipt.read_text(encoding="utf-8")).get("status") != candidate.status):
            raise ValueError("a planned generated tree changed before removal")
        rows.append({"path": str(path.resolve().relative_to(validation_root)),
                     "allocatedBytes": candidate.allocated_bytes,
                     "receipt": str(candidate.receipt.resolve().relative_to(validation_root)),
                     "status": candidate.status})
    # Record the exact scope before removing anything. No original or external
    # project directory is in the plan, and links are never followed.
    manifest.write_text(json.dumps({"status": "planned", "trees": rows}, indent=2) + "\n",
                        encoding="utf-8")
    removed = 0
    try:
        for candidate, row in zip(candidates, rows):
            if candidate.path.name == "project":
                row["preservedReports"] = preserve_project_reports(candidate.path)
            shutil.rmtree(candidate.path)
            removed += 1
    finally:
        manifest.write_text(json.dumps({"status": "complete" if removed == len(candidates) else "partial",
                                        "removedTrees": removed, "trees": rows}, indent=2) + "\n",
                            encoding="utf-8")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true", help="Remove planned generated trees")
    parser.add_argument("--min-age-hours", type=float, default=24,
                        help="Require a terminal receipt at least this old (default: 24)")
    parser.add_argument("--exclude", action="append", default=[], metavar="RUN_NAME",
                        help="Keep one immediate Files/validation run directory")
    parser.add_argument("--show-paths", action="store_true", help="List each planned tree")
    args = parser.parse_args()
    if not math.isfinite(args.min_age_hours) or args.min_age_hours < 0:
        parser.error("--min-age-hours must be finite and nonnegative")
    candidates = plan(VALIDATION, args.min_age_hours * 3600, excluded=args.exclude)
    gib = sum(candidate.allocated_bytes for candidate in candidates) / 1024**3
    print(f"{len(candidates)} generated trees; about {gib:.2f} GiB allocated")
    if args.show_paths:
        for candidate in candidates:
            print(candidate.path.relative_to(VALIDATION))
    if not args.apply or not candidates:
        return
    for path in (VALIDATION, ROOT / "Files" / "cleanup"):
        if subprocess.run(["git", "check-ignore", "--quiet", str(path)], cwd=ROOT).returncode:
            parser.error("cleanup paths must remain gitignored")
    manifest = prune(candidates, VALIDATION, ROOT / "Files" / "cleanup")
    print("Removed generated trees; private cleanup manifest:", manifest)


if __name__ == "__main__":
    main()
