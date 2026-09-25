"""Shared helpers for the benchmark, repro and stress drivers."""

import json
import os
import subprocess
import sys
import time


def parse_build(spec):
    """label=path[@max_threads]: max_threads caps the thread counts a build is run with (2.6.1 crashes above 1)."""
    label, _, rest = spec.partition("=")
    path, _, cap = rest.partition("@")

    if not label or not path:
        raise SystemExit(f"--build expects label=path[@max_threads], got {spec!r}")

    return {"label": label, "path": os.path.abspath(path), "max_threads": int(cap) if cap else None}


def exit_code_name(code):
    if code is None:
        return "timeout"

    if code < 0:
        return f"signal {-code}"

    unsigned = code & 0xFFFFFFFF
    known = {
        0: "ok",
        0xC0000005: "0xC0000005 access violation",
        0xC0000374: "0xC0000374 heap corruption",
        0xC0000409: "0xC0000409 fail-fast / stack buffer overrun",
        0xE0434352: "0xE0434352 unhandled .NET exception",
        134: "134 SIGABRT",
        139: "139 SIGSEGV",
    }

    return known.get(unsigned, f"0x{unsigned:08X}" if unsigned > 255 else str(unsigned))


def run_json(command, timeout):
    """Runs a command that prints one JSON object on stdout; returns (json or None, returncode, stderr)."""
    try:
        completed = subprocess.run(command, capture_output=True, text=True, timeout=timeout)
    except subprocess.TimeoutExpired as expired:
        return None, None, (expired.stderr or b"").decode(errors="replace") if isinstance(expired.stderr, bytes) else (expired.stderr or "")

    result = None

    for line in reversed(completed.stdout.splitlines()):
        line = line.strip()

        if line.startswith("{"):
            try:
                result = json.loads(line)
            except json.JSONDecodeError:
                pass

            break

    return result, completed.returncode, completed.stderr


def append_jsonl(path, record):
    with open(path, "a", encoding="utf-8") as handle:
        handle.write(json.dumps(record) + "\n")


def system_cpu_percent(interval=1.0):
    """Machine-wide CPU busy percentage over the interval (Windows GetSystemTimes, Linux /proc/stat)."""
    if sys.platform == "win32":
        import ctypes

        class FILETIME(ctypes.Structure):
            _fields_ = [("low", ctypes.c_uint32), ("high", ctypes.c_uint32)]

        def sample():
            idle, kernel, user = FILETIME(), FILETIME(), FILETIME()
            ctypes.windll.kernel32.GetSystemTimes(ctypes.byref(idle), ctypes.byref(kernel), ctypes.byref(user))
            value = lambda t: (t.high << 32) | t.low
            return value(idle), value(kernel) + value(user)
    else:
        def sample():
            with open("/proc/stat") as handle:
                fields = [int(v) for v in handle.readline().split()[1:]]
            return fields[3] + fields[4], sum(fields)

    idle0, total0 = sample()
    time.sleep(interval)
    idle1, total1 = sample()
    total = total1 - total0

    return 0.0 if total <= 0 else (1.0 - (idle1 - idle0) / total) * 100.0


def wait_for_quiet(threshold, max_wait, log=print):
    """Waits until machine CPU stays under threshold percent (other processes' load); returns the last reading."""
    started = time.time()
    reading = system_cpu_percent()

    while reading > threshold and time.time() - started < max_wait:
        log(f"  waiting for quiet: machine CPU {reading:.0f}% > {threshold:.0f}%")
        time.sleep(4)
        reading = system_cpu_percent()

    return reading
