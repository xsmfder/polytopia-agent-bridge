"""Visible game startup with a temporary, recoverable controlled-seat selection.

No arguments only prints a read-only plan. This tool never deploys, submits game
commands, selects saves, or terminates a process, including after a timeout.
"""
from __future__ import annotations

import argparse
from contextlib import closing
import hashlib
import json
import math
import os
import subprocess
import sys
import tempfile
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

sys.dont_write_bytecode = True
from deploy_bridge import WORK, game_processes, inside, require

sys.path.insert(0, str(WORK))
from sdk_state import SessionStore

SCHEMA = "bridge-launch-1"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def atomic_bytes(path, data):
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=path.name + ".", suffix=".tmp", delete=False) as out:
            temporary = Path(out.name)
            out.write(data)
            out.flush()
            os.fsync(out.fileno())
        os.replace(temporary, path)
        temporary = None
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def write_record(path, record):
    atomic_bytes(path, (json.dumps(record, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8"))


def seat_bytes(player):
    require(type(player) is int and 1 <= player <= 254, "player must be an integer from 1 to 254")
    return (json.dumps({"playerId": player}, separators=(",", ":")) + "\n").encode("utf-8")


def paths(work):
    work = Path(work).resolve()
    return work, work.parents[1] / "Polytopia.exe", work / "controlled-seat.json"


def launch_command(executable, via):
    require(via in ("direct", "steam"), "launch via must be direct or steam")
    if via == "direct":
        return [str(executable)]
    # Only use the client belonging to this Steam library's installation.
    # Do not inject an Overlay DLL or emulate Steam environment variables.
    steam = next((parent / "steam.exe" for parent in executable.parents
                  if (parent / "steam.exe").is_file()), None)
    if steam is None and os.name == "nt":
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
                steam = Path(winreg.QueryValueEx(key, "SteamExe")[0])
        except OSError:
            pass
    require(steam is not None and steam.is_file(), "Steam client not found")
    return [str(steam), "-applaunch", "874390"]


def plan(work=WORK, player=1, probe=None, *, via="direct"):
    work, executable, config = paths(work)
    seat_bytes(player)
    pids = (probe or game_processes)()
    require(isinstance(pids, list) and all(type(pid) is int and pid > 0 for pid in pids), "Invalid game process query")
    arguments = launch_command(executable, via)
    return {"operation": "plan", "executable": str(executable), "cwd": str(executable.parent),
            "arguments": arguments, "launchVia": via, "windowMode": "normal", "playerId": player,
            "readinessContract": "native_start_menu",
            "configuration": str(config), "configurationExists": config.exists(),
            "gamePids": pids, "canStart": executable.is_file() and not pids,
            "started": False, "deployed": False}


def ready_status(work, pid, player, baseline=None):
    """Require this launch's loaded seat AND a native interactive start menu."""
    try:
        raw = (work / "bridge-ready.json").read_bytes()
        if raw == baseline:
            return False, "ready_unchanged_since_launch"
        data = json.loads(raw.decode("utf-8"))
        if not isinstance(data, dict):
            return False, "ready_not_object"
        expected = {"startupSchema": "bridge-startup-1", "controlledSeatLoaded": True,
                    "configuredPlayerId": player, "pid": pid, "transport": "file"}
        for name, value in expected.items():
            if type(data.get(name)) is not type(value) or data.get(name) != value:
                return False, "ready_mismatch_" + name
        for name, filename in (("cmd", "cmd.jsonl"), ("out", "out.jsonl")):
            value = data.get(name)
            if not isinstance(value, str):
                return False, "ready_mismatch_" + name
            queue = Path(value)
            if not queue.is_absolute():
                queue = work.parents[1] / queue
            if queue.resolve() != work / filename:
                return False, "ready_mismatch_" + name
        if not (work / "cmd.jsonl").is_file():
            return False, "command_queue_missing"
        if type(data.get("startupUiReady")) is not bool:
            return False, "ready_ui_flag_unreadable"
        phase = data.get("startupUiPhase")
        if data["startupUiReady"] is not True or phase != "start_menu":
            return False, "ready_ui_" + (phase if isinstance(phase, str) else "unknown")
        # The output queue is created on the first response, so it may not exist yet.
        return True, None
    except (OSError, ValueError, TypeError):
        return False, "ready_unreadable"


def restore_config(work, record_path, record):
    """Compare exact temporary bytes; never overwrite someone else's edit."""
    work, _, config = paths(work)
    require(record.get("schema") == SCHEMA, "Unsupported launch record")
    require(record.get("configuration") == str(config), "Launch configuration path mismatch")
    temporary = seat_bytes(record.get("playerId"))
    require(record.get("temporaryConfigSha256") == digest(temporary), "Temporary configuration hash mismatch")
    existed = record.get("originalConfigExisted")
    require(type(existed) is bool, "Invalid original configuration presence")
    original = None
    if existed:
        backup = inside(record_path.parent / "controlled-seat.before", record_path.parent)
        original = backup.read_bytes()
        require(record.get("originalConfigSha256") == digest(original), "Configuration backup hash mismatch")
    current = config.read_bytes() if config.exists() else None
    if current == original:
        return "already_restored"
    if current != temporary:
        return "external_change_preserved"
    if existed:
        atomic_bytes(config, original)
    else:
        config.unlink()
    return "restored"


def restore(work, record_path):
    work = Path(work).resolve()
    record_path = inside(record_path, work / "launches")
    require(record_path.name == "result.json", "Expected launch result.json")
    with closing(SessionStore(work / "launches" / "startup-lock.json")):
        record = json.loads(record_path.read_text(encoding="utf-8"))
        record["configurationRestoration"] = restore_config(work, record_path, record)
        record["ok"] = record.get("status") == "ready" and record["configurationRestoration"] in ("restored", "already_restored")
        write_record(record_path, record)
    return record


def start(work, player, *, timeout=60.0, probe=None, popen=None, clock=None, sleep=None, via="direct"):
    require(isinstance(timeout, (int, float)) and not isinstance(timeout, bool)
            and math.isfinite(timeout) and timeout > 0, "timeout must be finite and positive")
    temporary = seat_bytes(player)
    work, executable, config = paths(work)
    require(work.is_dir() and executable.is_file(), "Game executable/work directory missing")
    probe, popen = probe or game_processes, popen or subprocess.Popen
    clock, sleep = clock or time.monotonic, sleep or time.sleep
    with closing(SessionStore(work / "launches" / "startup-lock.json")):
        checked = plan(work, player, probe, via=via)
        require(checked["canStart"], "Game already running; startup refused")
        directory = work / "launches" / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ-") + uuid.uuid4().hex)
        directory.mkdir()
        record_path = directory / "result.json"
        original = config.read_bytes() if config.exists() else None
        ready_path = work / "bridge-ready.json"
        baseline = ready_path.read_bytes() if ready_path.exists() else None
        if original is not None:
            atomic_bytes(directory / "controlled-seat.before", original)
        record = {"schema": SCHEMA, "status": "prepared", "playerId": player,
                  "readinessContract": "native_start_menu",
                  "configuration": str(config), "temporaryConfigSha256": digest(temporary),
                  "originalConfigExisted": original is not None,
                  "originalConfigSha256": digest(original) if original is not None else None,
                  "executable": str(executable), "pid": None, "ready": False, "launchVia": via,
                  "log": str(directory / "startup.log"), "record": str(record_path),
                  "deployed": False, "gameTerminated": False}
        write_record(record_path, record)
        try:
            require((config.read_bytes() if config.exists() else None) == original,
                    "Configuration changed during preparation; startup refused")
            atomic_bytes(config, temporary)
            # Catch a second launcher/manual start between the first query and write.
            require(plan(work, player, probe, via=via)["canStart"], "Game appeared during preparation; startup refused")
            record["status"] = "launching"
            write_record(record_path, record)
            with (directory / "startup.log").open("ab") as log:
                process = popen(checked["arguments"], cwd=str(executable.parent), stdout=log, stderr=subprocess.STDOUT)
            require(type(process.pid) is int and process.pid > 0, "Launcher returned invalid process id")
            # Steam's dispatcher often exits immediately: its PID is not the game PID.
            record.update(status="waiting_ready", pid=process.pid if via == "direct" else None)
            if via == "steam":
                record["dispatcherPid"] = process.pid
            write_record(record_path, record)
            deadline = clock() + timeout
            while True:
                if via == "direct":
                    exit_code = process.poll()
                    if exit_code is not None:
                        record.update(status="exited_before_ready", exitCode=exit_code)
                        break
                    live = [process.pid]
                else:
                    live = probe()
                    require(isinstance(live, list) and all(type(pid) is int and pid > 0 for pid in live),
                            "Invalid game process query")
                    if record["pid"] is None and len(live) == 1:
                        record["pid"] = live[0]
                        write_record(record_path, record)
                    if record["pid"] is not None and record["pid"] not in live:
                        record.update(status="exited_before_ready")
                        break
                ready, reason = (ready_status(work, record["pid"], player, baseline)
                                 if record["pid"] is not None else (False, "waiting_for_steam_game_process"))
                record["lastReadyRejection"] = reason
                if ready and ((via == "direct" and process.poll() is None)
                              or (via == "steam" and record["pid"] in probe())):
                    record.update(status="ready", ready=True)
                    break
                remaining = deadline - clock()
                if remaining <= 0:
                    record["status"] = "startup_unknown"
                    break
                sleep(min(0.1, remaining))
        except Exception as exc:
            record.update(status="startup_error" if record["pid"] is None else "startup_unknown",
                          error=type(exc).__name__ + ": " + str(exc))
        finally:
            try:
                record["configurationRestoration"] = restore_config(work, record_path, record)
            except Exception as exc:
                record["configurationRestoration"] = "restore_error"
                record["restoreError"] = type(exc).__name__ + ": " + str(exc)
            restored = record["configurationRestoration"] in ("restored", "already_restored")
            record["configurationRestoredBeforeReady"] = not record["ready"] and restored
            if via == "steam" and record["status"] == "startup_unknown":
                record["steamRequestMayStillBePending"] = True
                record["warning"] = "Steam may still launch the game after timeout. Inspect Steam and the game process; do not blindly retry."
            record["ok"] = record["status"] == "ready" and restored
            write_record(record_path, record)
        return record


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="operation")
    p = sub.add_parser("plan", help="read-only startup plan")
    p.add_argument("--player", type=int, default=1)
    p.add_argument("--via", choices=("direct", "steam"), default="direct")
    p = sub.add_parser("start", help="explicitly start a visible game window")
    p.add_argument("--player", type=int, required=True)
    p.add_argument("--timeout", type=float, default=None,
                   help="seconds to wait (default: Steam 300, direct 60); timeout never cancels Steam's pending launch")
    p.add_argument("--via", choices=("direct", "steam"), default="direct",
                   help="use steam for Steam Online/Overlay; direct preserves the original startup path")
    p = sub.add_parser("restore", help="restore configuration from a recorded interrupted launch")
    p.add_argument("--record", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        if args.operation == "restore":
            result = restore(WORK, args.record)
        elif args.operation == "start":
            timeout = args.timeout if args.timeout is not None else (300.0 if args.via == "steam" else 60.0)
            result = start(WORK, args.player, timeout=timeout, via=args.via)
        else:
            result = plan(WORK, getattr(args, "player", 1), via=getattr(args, "via", "direct"))
        print(json.dumps(result, ensure_ascii=False, indent=2))
        if args.operation == "restore":
            return 0 if result.get("configurationRestoration") in ("restored", "already_restored") else 1
        return 0 if result.get("ok", result.get("status") is None) else 1
    except (OSError, ValueError, RuntimeError) as exc:
        print(json.dumps({"status": "refused", "error": str(exc)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
