"""Identify descendants of one validation invocation across POSIX sessions."""

import ctypes
import os
from pathlib import Path
import re
import secrets
import signal
import struct
import subprocess
import sys
import time


SCOPE_ENVIRONMENT = "CPP2IL_VALIDATION_PROCESS_SCOPES"
MAX_ENVIRONMENT_BYTES = 1024 * 1024
CTL_KERN = 1
KERN_PROCARGS2 = 49


def child_environment(environment):
    """Give this invocation a unique scope while retaining its outer monitors."""
    inherited = os.environ.get(SCOPE_ENVIRONMENT, "")
    ancestors = inherited.split(":") if inherited else []
    if len(ancestors) > 16 or any(not re.fullmatch(r"[0-9a-f]{32}", item) for item in ancestors):
        raise ValueError("Invalid validation process ownership chain")
    scope = secrets.token_hex(16)
    child = dict(environment)
    child[SCOPE_ENVIRONMENT] = ":".join(ancestors + [scope])
    return child, scope


def _darwin_environment(pid):
    # KERN_PROCARGS2 starts with argc, the executable path, then argv and envp.
    # Keep the native buffer private; only the ownership entry leaves this reader.
    libc = ctypes.CDLL(None, use_errno=True)
    libc.sysctl.argtypes = [ctypes.POINTER(ctypes.c_int), ctypes.c_uint, ctypes.c_void_p,
                           ctypes.POINTER(ctypes.c_size_t), ctypes.c_void_p, ctypes.c_size_t]
    libc.sysctl.restype = ctypes.c_int
    query = (ctypes.c_int * 3)(CTL_KERN, KERN_PROCARGS2, pid)
    length = ctypes.c_size_t()
    if libc.sysctl(query, 3, None, ctypes.byref(length), None, 0):
        return b""
    if length.value < 4 or length.value > MAX_ENVIRONMENT_BYTES:
        return b""
    buffer = ctypes.create_string_buffer(length.value)
    if libc.sysctl(query, 3, buffer, ctypes.byref(length), None, 0):
        return b""
    data = buffer.raw[:length.value]
    argc = struct.unpack_from("=i", data)[0]
    if argc < 0 or argc > len(data):
        return b""
    offset = data.find(b"\0", 4)
    if offset < 0:
        return b""
    while offset < len(data) and data[offset] == 0:
        offset += 1
    for _ in range(argc):
        offset = data.find(b"\0", offset)
        if offset < 0:
            return b""
        offset += 1
    return data[offset:]


def _scope_chain(pid):
    try:
        if sys.platform == "darwin":
            environment = _darwin_environment(pid)
        elif sys.platform.startswith("linux"):
            with (Path("/proc") / str(pid) / "environ").open("rb") as source:
                environment = source.read(MAX_ENVIRONMENT_BYTES + 1)
            if len(environment) > MAX_ENVIRONMENT_BYTES:
                return []
        else:
            raise ValueError("POSIX validation ownership requires macOS or Linux")
    except (FileNotFoundError, ProcessLookupError, PermissionError):
        return []
    prefix = SCOPE_ENVIRONMENT.encode("ascii") + b"="
    for entry in environment.split(b"\0"):
        if entry.startswith(prefix):
            try:
                chain = entry[len(prefix):].decode("ascii").split(":")
            except UnicodeDecodeError:
                return []
            return chain if all(re.fullmatch(r"[0-9a-f]{32}", item) for item in chain) else []
    return []


def members(scope):
    # Query numeric PIDs only. Never log commands or another process's environment.
    output = subprocess.check_output(["ps", "-U", str(os.getuid()), "-o", "pid="], timeout=5)
    return [pid for item in output.split() if (pid := int(item)) != os.getpid()
            and scope in _scope_chain(pid)]


def signal_members(scope, number):
    """Signal only processes that still carry this invocation's unique scope."""
    for pid in members(scope):
        if scope not in _scope_chain(pid):
            continue
        try:
            os.kill(pid, number)
        except ProcessLookupError:
            pass


def kill_members(scope):
    """Rescan after killing a scope so a concurrently forked child is included."""
    deadline = time.monotonic() + 2
    while members(scope):
        signal_members(scope, signal.SIGKILL)
        if time.monotonic() >= deadline:
            raise RuntimeError("Validation descendants remain after ownership cleanup")
        time.sleep(0.05)
