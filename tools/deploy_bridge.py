"""Verified bridge install/rollback. Default plan is read-only; no game/save APIs."""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

sys.dont_write_bytecode = True  # A default read-only plan must not create local __pycache__.
from verify_bridge import sha256, test_count

WORK = Path(__file__).resolve().parents[1]
DLL_NAME = "PolytopiaAgentBridge.dll"
RECORD_SCHEMA = "bridge-deployment-1"


def inside(path, base):
    path = Path(path).resolve()
    if not path.is_relative_to(Path(base).resolve()):
        raise ValueError("Path escapes expected directory: " + str(path))
    return path


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def checked_file(path, expected):
    require(isinstance(expected, str) and re.fullmatch(r"[0-9a-f]{64}", expected), "Invalid SHA256: " + str(path))
    require(Path(path).is_file() and sha256(path) == expected, "File/hash mismatch: " + str(path))


def snapshot_file(manifest_path, name):
    require(isinstance(name, str) and "\\" not in name, "Invalid input name")
    return inside(Path(manifest_path).parent / "inputs" / name, Path(manifest_path).parent / "inputs")


def verification(path, work, allow_failed=False):
    """Read original candidate, snapshots, compile/test steps and logs, not just ok."""
    path = inside(path, work)
    data = read_json(path)
    require(data.get("schema") == "bridge-offline-verification-1", "Unsupported verification schema")
    require(data.get("ok") is True or allow_failed, "Verification is not successful")
    require(not data.get("error"), "Verification recorded an error")
    for field in ("changedSnapshotFiles", "changedWorkingFiles", "addedWorkingFiles"):
        require(data.get(field) == [], "Verification inputs drifted: " + field)
    hashes = data.get("inputSha256")
    require(isinstance(hashes, dict) and hashes and "sdk.py" in hashes, "Missing input hashes")
    for name, value in hashes.items():
        checked_file(snapshot_file(path, name), value)
    require(data.get("sdkSha256") == hashes["sdk.py"], "SDK hash disagrees with snapshot")
    dll = inside(data.get("candidateDll", ""), path.parent)
    require(dll.name == DLL_NAME, "Unexpected candidate name")
    checked_file(dll, data.get("dllSha256"))
    runtime_inputs = dict(data.get("referenceSha256") or {})
    for name, value in runtime_inputs.items():
        reference = inside(name, work.parents[1] / "MelonLoader")
        checked_file(reference, value)
        checked_file(dll.parent / reference.name, value)
    if data.get("gameAssemblySha256"):
        checked_file(work.parents[1] / "GameAssembly.dll", data["gameAssemblySha256"])
        runtime_inputs[str(work.parents[1] / "GameAssembly.dll")] = data["gameAssemblySha256"]
    scope = data.get("scope") or {}
    suites = scope.get("csharp")
    modules = scope.get("python")
    require(isinstance(suites, list) and suites and isinstance(modules, list), "Missing candidate test scope")
    require(len(suites) == len(set(suites)), "Duplicate suite names")
    if scope.get("all") is True:
        require(scope.get("campaignRecovery") is True and scope.get("toolTests") is True,
                "Full verification omitted campaign/tool coverage")
        discovered = {Path(name).stem for name in hashes if name.startswith("tests/") and name.endswith("Tests.cs")}
        require(set(suites) == discovered, "Full verification omitted C# suites")
        discovered = {Path(name).stem for name in hashes if name.startswith("tests/test") and name.endswith(".py")}
        require(set(modules) == discovered, "Full verification omitted Python modules")
    expected = set(suites)
    if modules:
        expected.add("python")
    if scope.get("campaignRecovery") is True:
        expected.add("campaignRecovery")
    if scope.get("toolTests") is True:
        expected.add("verificationTool")
    steps = data.get("steps")
    require(isinstance(steps, list) and steps, "Missing compile/test evidence")
    seen, failed = set(), []
    built = False
    for step in steps:
        require(step.get("kind") in ("compile", "test"), "Unknown verification step")
        require(type(step.get("exitCode")) is int, "Invalid step exit code")
        log = inside(step.get("log", ""), path.parent)
        require(log.is_file(), "Missing original log: " + str(log))
        command = step.get("command")
        require(isinstance(command, list) and command, "Missing original command")
        output = log.read_text(encoding="utf-8")
        if step["kind"] == "compile" and step.get("name") == DLL_NAME:
            require(not built and step["exitCode"] == 0, "Candidate compile did not succeed exactly once")
            built = True
        if step["kind"] == "test":
            name = step.get("name")
            require(name in expected and name not in seen, "Unexpected/duplicate test step: " + str(name))
            seen.add(name)
            require(output.strip(), "Empty test log: " + str(log))
            count_key = "assertionCount" if name.endswith("Tests") else "testCount"
            if step.get(count_key) is not None:
                require(test_count(output) == step[count_key], "Log count disagrees with recorded result: " + name)
            if step["exitCode"] == 0:
                summaries = re.findall(r"passed=\d+\s+failed=(\d+)", output)
                require(not summaries or summaries[-1] == "0", "Failed assertions in successful log: " + name)
                if name in ("python", "campaignRecovery", "verificationTool"):
                    require(re.search(r"\nOK(?:\s*\([^\n]*\))?\s*$", output), "Missing unittest success: " + name)
        if step["exitCode"] != 0:
            failed.append(step)
    require(built and seen == expected, "Missing expected compile/test coverage")
    require(not failed or allow_failed, "Original test/compile steps failed")
    if failed:
        require(data.get("ok") is False, "Failed steps contradict successful manifest")
    if not failed:
        require(data.get("ok") is True, "Failure manifest has no explained failed test")
    return {"path": path, "manifest": data, "hashes": hashes, "dll": dll,
            "failed": failed, "scope": scope, "runtimeInputs": runtime_inputs}


def evidence(path, work):
    path = inside(path, work)
    raw = read_json(path)
    proof_paths = [path]
    if raw.get("schema") == "bridge-batch-supplemental-verification-1":
        require(raw.get("ok") is True, "Batch is not marked successful")
        full = verification(inside(path.parent / raw["fullManifest"], work), work, allow_failed=True)
        extra = verification(inside(path.parent / raw["supplementalManifest"], work), work)
        require(full["scope"].get("all") is True, "Batch base is not a complete regression")
        failures = full["failed"]
        require(failures and all(step["kind"] == "test" and step["name"].endswith("Tests") for step in failures),
                "Only failed C# fixture tests can be supplemented")
        names = {step["name"] for step in failures}
        require(set(extra["scope"]["csharp"]) == names and not extra["scope"]["python"],
                "Supplement does not match failed suites")
        differences = sorted(name for name in set(full["hashes"]) | set(extra["hashes"])
                             if full["hashes"].get(name) != extra["hashes"].get(name))
        allowed = {"tests/" + name + ".cs" for name in names}
        require(set(differences).issubset(allowed), "Supplement changes production or unrelated inputs")
        require(raw.get("inputsChangedSinceFullRun") == differences, "Batch input difference claim is incorrect")
        require(raw.get("productionSourcesChangedSinceFullRun") == [], "Batch claims changed production sources")
        require(inside(raw["candidateDll"], work) == full["dll"], "Batch candidate is not the full-run candidate")
        require(raw.get("candidateDllSha256") == full["manifest"]["dllSha256"], "Batch candidate hash disagrees")
        require(raw.get("sdkSha256") == full["manifest"]["sdkSha256"], "Batch SDK hash disagrees")
        merged = dict(full["hashes"])
        merged.update(extra["hashes"])
        proof_paths += [full["path"], extra["path"]]
        result = full
        result["hashes"] = merged
        result["supplementedSuites"] = sorted(names)
    else:
        result = verification(path, work)
        result["supplementedSuites"] = []
    result["evidenceFiles"] = {str(p): sha256(p) for p in proof_paths}
    return result


def working_inputs(work, proof):
    """Tests/tools may advance; deployment must still match production and SDK inputs."""
    expected = {name: value for name, value in proof["hashes"].items()
                if name.startswith("src/") or ("/" not in name and not name.startswith("test_"))}
    require(any(name.startswith("src/") for name in expected), "Missing production source hashes")
    for name, value in expected.items():
        checked_file(inside(work / name, work), value)
    sources = {p.relative_to(work).as_posix() for p in (work / "src").glob("*.cs")}
    require(sources == {name for name in expected if name.startswith("src/")}, "Production source file set drifted")
    require(all(p.name in expected for p in work.glob("sdk_*.py")), "New SDK submodule is unverified")
    return expected


def game_processes():
    """Use both CIM and Get-Process; a failed query is not proof the game stopped."""
    require(os.name == "nt", "Game process query requires Windows desktop context")
    script = (
        "$ErrorActionPreference='Stop'; $ids=@(); "
        "$ids+=@(Get-CimInstance Win32_Process -Filter \"Name='Polytopia.exe'\" | ForEach-Object { [int]$_.ProcessId }); "
        "$ids+=@(Get-Process -Name Polytopia -ErrorAction SilentlyContinue | ForEach-Object { [int]$_.Id }); "
        "ConvertTo-Json -Compress -InputObject @($ids | Sort-Object -Unique)"
    )
    result = subprocess.run([r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                             "-NoProfile", "-NonInteractive", "-Command", script],
                            capture_output=True, encoding="utf-8", errors="replace", timeout=20)
    require(result.returncode == 0, "Cannot establish game process status: " + result.stderr.strip())
    pids = json.loads(result.stdout.strip())
    require(isinstance(pids, list) and all(type(pid) is int and pid > 0 for pid in pids), "Invalid process query result")
    return pids


def stopped(probe=None):
    pids = (probe or game_processes)()
    require(not pids, "Game is running; no write allowed: " + str(pids))
    return pids


def install_plan(work, path, probe=None):
    work = Path(work).resolve()
    proof = evidence(path, work)
    inputs = working_inputs(work, proof)
    target = deployment_target(work)
    exists = target.exists()
    require(not exists or target.is_file(), "Installed target is not a regular file")
    if not exists:
        require((work.parents[1] / 'Polytopia.exe').is_file(), "Initial install requires the game executable in the workspace's game root")
    pids = (probe or game_processes)()
    return {"operation": "install", "candidate": str(proof["dll"]), "target": str(target),
            "beforeExists": exists, "initialInstall": not exists,
            "beforeSha256": sha256(target) if exists else None, "afterSha256": proof["manifest"]["dllSha256"],
            "evidence": str(Path(path).resolve()), "evidenceFiles": proof["evidenceFiles"],
            "productionInputSha256": inputs, "runtimeInputSha256": proof["runtimeInputs"], "scope": proof["scope"],
            "supplementedSuites": proof["supplementedSuites"], "gamePids": pids,
            "canWrite": not pids, "deployed": False}


def rollback_plan(work, path, probe=None):
    work = Path(work).resolve()
    path = inside(path, work / "deployments")
    record = read_json(path)
    require(record.get("schema") == RECORD_SCHEMA and record.get("status") == "completed"
            and record.get("operation") == "install", "Rollback requires a completed install record")
    target = deployment_target(work)
    require(Path(record["target"]).resolve() == target.resolve(), "Install record target mismatch")
    checked_file(target, record["afterSha256"])
    require(type(record.get('initialInstall', False)) is bool, 'Invalid initial-install flag')
    first = record.get('initialInstall') is True
    if first:
        require(record.get('beforeExists') is False and record.get('beforeSha256') is None
                and record.get('backup') is None and record.get('backupSha256') is None,
                'Invalid initial-install rollback record')
        backup = None
    else:
        require(isinstance(record.get('backup'), str) and isinstance(record.get('beforeSha256'), str),
                'Missing original install backup or hash')
        backup = inside(record["backup"], path.parent)
        checked_file(backup, record["beforeSha256"])
        require(record.get("backupSha256") == record["beforeSha256"], "Backup hash disagrees with original install")
    pids = (probe or game_processes)()
    return {"operation": "rollback", "candidate": str(backup) if backup else None, "target": str(target),
            "beforeSha256": record["afterSha256"], "afterSha256": record["beforeSha256"],
            "rollbackOf": str(path), "rollbackOfSha256": sha256(path),
            "gamePids": pids, "canWrite": not pids, "deployed": False}


def deployment_target(work):
    work = Path(work).resolve()
    target = inside(work.parents[1] / 'Mods' / DLL_NAME, work.parents[1])
    require(not (work.parents[1] / 'Mods' / DLL_NAME).is_symlink(), 'Installed target cannot be a symbolic link')
    return target


def checked_target(path, expected):
    if expected is None:
        require(not Path(path).exists() and not Path(path).is_symlink(), 'Initial target appeared before commit')
    else:
        checked_file(path, expected)


def write_new(path, data):
    with Path(path).open("x", encoding="utf-8") as handle:
        json.dump(data, handle, ensure_ascii=False, indent=2)
        handle.flush()
        os.fsync(handle.fileno())


def copy_new(source, target):
    with Path(source).open("rb") as reader, Path(target).open("xb") as writer:
        while True:
            chunk = reader.read(1024 * 1024)
            if not chunk:
                break
            writer.write(chunk)
        writer.flush()
        os.fsync(writer.fileno())


def publish_new(source, target):
    if os.name == 'nt':
        # Windows rename refuses an existing destination; no hard-link privilege
        # or placeholder is needed to atomically publish a verified first install.
        os.rename(source, target)
    else:
        # POSIX rename replaces the destination, so use an exclusive link there.
        os.link(source, target)
        Path(source).unlink()


def execute(work, path, rollback=False, probe=None):
    work = Path(work).resolve()
    make_plan = rollback_plan if rollback else install_plan
    plan = make_plan(work, path, probe)
    require(plan["canWrite"], "Game is running; plan only")
    require(plan["beforeSha256"] != plan["afterSha256"], "Target already has requested hash; no deployment needed")
    records = inside(work / "deployments", work)
    records.mkdir(exist_ok=True)
    lock = records / ".deployment.lock"
    lock_fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    record_dir = None
    temp = None
    try:
        # Revalidate after acquiring the single-writer lock.
        require(make_plan(work, path, probe) == plan, "Deployment plan changed before lock acquisition")
        name = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ-") + uuid.uuid4().hex[:8]
        record_dir = records / name
        record_dir.mkdir()
        record = dict(plan, schema=RECORD_SCHEMA, status="started", startedUtc=datetime.now(timezone.utc).isoformat())
        write_new(record_dir / "started.json", record)
        target = Path(plan["target"])
        backup = None
        if plan['beforeSha256'] is not None:
            backup = record_dir / "before.dll"
            copy_new(target, backup)
            checked_file(backup, plan["beforeSha256"])
        if plan['candidate'] is not None:
            target.parent.mkdir(exist_ok=True)
            temp = target.parent / ("." + DLL_NAME + "." + uuid.uuid4().hex + ".tmp")
            copy_new(plan["candidate"], temp)
            checked_file(temp, plan["afterSha256"])
        if rollback:
            checked_file(plan["rollbackOf"], plan["rollbackOfSha256"])
        else:
            for name, value in plan["productionInputSha256"].items():
                checked_file(work / name, value)
            for name, value in plan["evidenceFiles"].items():
                checked_file(name, value)
            for name, value in plan["runtimeInputSha256"].items():
                checked_file(name, value)
        stopped(probe)
        require(deployment_target(work) == target, 'Deployment target changed before commit')
        checked_target(target, plan["beforeSha256"])
        if plan['candidate'] is None:
            # Roll back a first install to verified absence, retaining its DLL backup.
            target.unlink()
        elif plan['beforeSha256'] is None:
            # Exclusive atomic publication: a racing file must never be overwritten.
            publish_new(temp, target)
        else:
            os.replace(temp, target)
        temp = None
        checked_target(target, plan["afterSha256"])
        record.update(status="completed", deployed=True, backup=str(backup) if backup else None, backupSha256=sha256(backup) if backup else None,
                      finishedUtc=datetime.now(timezone.utc).isoformat())
        completed = record_dir / "completed.json"
        write_new(completed, record)
        return dict(record, record=str(completed))
    except Exception as exc:
        if record_dir:
            write_new(record_dir / "failed.json", {"schema": RECORD_SCHEMA, "status": "failed",
                      "operation": plan["operation"], "error": type(exc).__name__ + ": " + str(exc),
                      "target": plan["target"], "actualTargetSha256": sha256(plan["target"]) if Path(plan["target"]).is_file() else None})
        raise
    finally:
        if temp and temp.is_file():
            temp.unlink()
        os.close(lock_fd)
        lock.unlink()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", nargs="?", choices=("plan", "install", "rollback"), default="plan")
    parser.add_argument("--evidence", help="verification.json or batch-verification.json inside agent-bridge")
    parser.add_argument("--record", help="completed install record, for rollback or read-only rollback plan")
    args = parser.parse_args(argv)
    if bool(args.evidence) == bool(args.record):
        parser.error("Provide exactly one --evidence or --record")
    if args.operation == "install" and not args.evidence or args.operation == "rollback" and not args.record:
        parser.error("install requires --evidence; rollback requires --record")
    try:
        path = args.record or args.evidence
        if args.operation == "plan":
            result = rollback_plan(WORK, path) if args.record else install_plan(WORK, path)
        else:
            result = execute(WORK, path, rollback=args.operation == "rollback")
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except (ValueError, OSError, KeyError, subprocess.SubprocessError) as exc:
        print(json.dumps({"ok": False, "operation": args.operation, "error": str(exc)}, ensure_ascii=False), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    raise SystemExit(main())
