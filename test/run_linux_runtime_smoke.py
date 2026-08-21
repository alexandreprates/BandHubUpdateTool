#!/usr/bin/env python3

import argparse
import os
from pathlib import Path
import signal
import subprocess
import time


def stop_process_group(process: subprocess.Popen[str]) -> str:
    if process.poll() is None:
        os.killpg(process.pid, signal.SIGTERM)
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
    output, _ = process.communicate()
    return output


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Verify that the installed Linux updater reaches its GTK event loop."
    )
    parser.add_argument("launcher", type=Path)
    parser.add_argument("--startup-seconds", type=float, default=3.0)
    args = parser.parse_args()

    launcher = args.launcher.resolve()
    if not launcher.is_file():
        raise FileNotFoundError(f"updater launcher was not found: {launcher}")

    environment = os.environ.copy()
    environment.pop("MONO_PATH", None)
    process = subprocess.Popen(
        ["xvfb-run", "-a", str(launcher)],
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        env=environment,
        start_new_session=True,
    )
    time.sleep(args.startup_seconds)
    exited_early = process.poll() is not None
    output = stop_process_group(process)
    if exited_early:
        raise RuntimeError(
            f"updater exited during startup with code {process.returncode}:\n{output}"
        )
    fatal_markers = ("Unhandled exception", "FATAL UNHANDLED EXCEPTION")
    if any(marker.lower() in output.lower() for marker in fatal_markers):
        raise RuntimeError(f"updater reported a fatal startup error:\n{output}")

    print("PASS: self-contained Linux updater reached the GTK event loop")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
