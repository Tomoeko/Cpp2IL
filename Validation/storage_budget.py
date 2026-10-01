"""Keep local validation artifacts within the repository's storage budget."""

import os
from pathlib import Path
import stat


LIMIT_BYTES = 6_000_000_000
BUILD_RESERVE_BYTES = 1_000_000_000
STOP_RESERVE_BYTES = 300_000_000
POLL_SECONDS = 2


def usage(root):
    """Count logical and allocated bytes without following symbolic links."""
    logical, allocated = 0, 0
    pending = [Path(root)]
    while pending:
        path = pending.pop()
        try:
            info = path.lstat()
        except FileNotFoundError:
            # A reviewed prune may remove an entry while another run samples it.
            continue
        allocated += getattr(info, "st_blocks", 0) * 512
        if stat.S_ISDIR(info.st_mode):
            try:
                with os.scandir(path) as entries:
                    pending.extend(Path(entry.path) for entry in entries)
            except FileNotFoundError:
                continue
        else:
            logical += info.st_size
    return {"logicalBytes": logical, "allocatedBytes": allocated,
            "countedBytes": max(logical, allocated), "limitBytes": LIMIT_BYTES}


def check_headroom(root, reserve=STOP_RESERVE_BYTES):
    if type(reserve) is not int or reserve < 0:
        raise ValueError("Storage reserve must be a nonnegative integer")
    measured = usage(root)
    if measured["countedBytes"] + reserve > LIMIT_BYTES:
        raise ValueError("Validation storage budget reached: {} bytes used; {} bytes "
                         "required free within the {} byte limit. Prune reviewed generated "
                         "projects and players before continuing.".format(
                             measured["countedBytes"], reserve, LIMIT_BYTES))
    return {**measured, "requiredHeadroomBytes": reserve}


def root_for_log(repository, log):
    root = Path(repository) / "Files"
    if root.is_symlink():
        raise ValueError("The validation storage root must not be a symbolic link")
    return root if Path(log).absolute().is_relative_to(root.absolute()) else None
