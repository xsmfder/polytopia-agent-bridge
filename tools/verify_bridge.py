"""One offline entry point for bridge candidates and regression evidence.

No deployment, game launch, process lookup, or live bridge requests are performed.
Every run compiles/tests a private input snapshot and retains its exact inputs.
"""
from __future__ import annotations

import argparse
import ast
import hashlib
import json
import re
import shutil
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

WORK = Path(__file__).resolve().parents[1]
ROOT = WORK.parents[1]
CSC = Path(r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe")
SEPARATE = {
    "UnitRuntimeTests", "IntegratedFeaturesTests", "CompositePreviewTests",
    "CombatRuntimeTests", "TransportRuntimeTests", "TransportPerformanceTests",
    "ReflectionPerformanceTests", "VisibilityMetadataTests", "ResolverPerformanceTests",
    "AttackReaderMemberTests",
}
SDK_FILES = ("sdk.py", "agentcli.py", "client.py", "bridge_transport.py")
SDK_DATA_FILES = ("VERSION", "LICENSE", "polymod/manifest.json", "tools/build_polymod.ps1")
LEGACY_ENTRY_FILES = ("deploy.ps1", "start_ai_match.py")
CAMPAIGN = Path("tests/fixtures/recovery")  # Small frozen recovery cases, independent of private campaigns.


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def inventory(work):
    return {
        "csharp": sorted(p.stem for p in (work / "tests").glob("*Tests.cs")),
        "python": sorted(p.stem for p in (work / "tests").glob("test*.py")),
    }


def select(requested, available, label):
    names = list(dict.fromkeys(requested))
    unknown = sorted(set(names) - set(available))
    if unknown:
        raise ValueError("Unknown " + label + ": " + ", ".join(unknown))
    return names


def output_path(work, requested):
    work = Path(work).resolve()
    target = Path(requested).resolve() if requested else (
        work / "verification-runs" / datetime.now().strftime("%Y%m%d-%H%M%S-%f")
    )
    if not target.is_relative_to(work.resolve()) or target == work.resolve():
        raise ValueError("Output must be a new directory inside agent-bridge")
    if target.exists():
        raise ValueError("Output already exists; historical evidence is never overwritten")
    if any(target.is_relative_to(work / name) for name in ("src", "tests", "tools")):
        raise ValueError("Output cannot be placed inside source/test/tool directories")
    return target


def input_files(work):
    files = set((work / "src").glob("*.cs"))
    files.update((work / "tests").glob("*Tests.cs"))
    files.update((work / "tests").glob("test*.py"))
    files.update(p for p in (work / "tests/fixtures").rglob("*") if p.is_file())
    files.update(work / name for name in SDK_FILES)
    # SDK submodules can grow during checklist work; retain them without another
    # hand-maintained build script for each extraction.
    files.update(work.glob("sdk_*.py"))
    files.update(work / name for name in SDK_DATA_FILES if (work / name).is_file())
    files.update((work / "tools").glob("*.py"))
    # Retired wrappers are validated as files, never imported or executed while
    # discovering inputs. Keep them in the snapshot used by entry-point tests.
    files.update(work / name for name in LEGACY_ENTRY_FILES if (work / name).is_file())
    files.update(work / CAMPAIGN / name for name in (
        "test_settled_unknown.py", "attacker-disappeared-ticket34.json"))
    # Resolve local Python imports without importing/executing modules (some older
    # helper scripts have live side effects). This includes imports inside methods.
    pending = [path for path in files if path.suffix == ".py"]
    seen = set()
    while pending:
        path = pending.pop()
        if path in seen or not path.is_file():
            continue
        seen.add(path)
        tree = ast.parse(path.read_text(encoding="utf-8-sig"), filename=str(path))
        for node in ast.walk(tree):
            names = ([alias.name for alias in node.names] if isinstance(node, ast.Import) else
                     [node.module] if isinstance(node, ast.ImportFrom) and node.module else [])
            for name in names:
                local = work / (name.split(".")[0] + ".py")
                if local.is_file() and local not in files:
                    files.add(local)
                    pending.append(local)
    return sorted(files)


def snapshot(work, dest):
    hashes = {}
    for original in input_files(work):
        relative = original.relative_to(work)
        target = dest / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(original, target)
        hashes[relative.as_posix()] = sha256(target)
    return hashes


def changed_inputs(base, expected):
    return [name for name, value in expected.items()
            if not (base / name).is_file() or sha256(base / name) != value]


def test_count(output):
    counts = re.findall(
        r"(?:PASS total|ALL PASS|TOTAL)\s+(\d+)|passed=(\d+)|\b(\d+) passed\b|Ran (\d+) tests?",
        output,
    )
    return int(next(value for value in counts[-1] if value)) if counts else None


def run_step(command, cwd, log, timeout, kind, name):
    started = time.monotonic()
    try:
        result = subprocess.run(command, cwd=cwd, capture_output=True,
                                encoding="utf-8", errors="replace", timeout=timeout)
        output, code = result.stdout + result.stderr, result.returncode
    except (OSError, subprocess.TimeoutExpired) as exc:
        output, code = str(exc), 1
        if isinstance(exc, subprocess.TimeoutExpired):
            for stream in (exc.stdout, exc.stderr):
                if stream:
                    output += "\n" + (stream.decode("utf-8", "replace") if isinstance(stream, bytes) else stream)
    log.write_text(output, encoding="utf-8")
    record = {"kind": kind, "name": name, "command": [str(p) for p in command],
              "cwd": str(cwd), "log": str(log), "exitCode": code,
              "elapsedMs": round((time.monotonic() - started) * 1000, 3)}
    if kind == "test":
        record["assertionCount" if name.endswith("Tests") else "testCount"] = test_count(output)
    print(kind, name, "PASS" if code == 0 else "FAIL", flush=True)
    if code:
        print(output[-3000:], flush=True)
    return record


def powershell_fixture(fixture, dll, assembly):
    def literal(value):
        return "'" + str(value).replace("'", "''") + "'"
    script = (
        "$ErrorActionPreference='Stop'; try { $a=[Reflection.Assembly]::LoadFrom(" + literal(fixture)
        + "); $t=$a.GetType('UnitRuntimeTests',$true); $m=$t.GetMethod('Main',"
        + "[Reflection.BindingFlags]'Public,NonPublic,Static'); $argsForTest=[string[]]@(" + literal(dll)
        + "," + literal(assembly) + "); $code=$m.Invoke($null,[object[]]@(,$argsForTest)); exit $code } "
        + "catch { Write-Output $_.Exception.ToString(); exit 1 }"
    )
    return [r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-NoProfile", "-Command", script]


def verify(args, work=WORK, root=ROOT):
    available = inventory(work)
    csharp = select(available["csharp"] if args.all else args.csharp, available["csharp"], "C# suite")
    python = select(available["python"] if args.all else args.python, available["python"], "Python module")
    if not csharp and not python:
        raise ValueError("Select --all, --csharp Suite, or --python test_module")
    out = output_path(work, args.out)
    out.mkdir(parents=True)
    inputs, candidate = out / "inputs", out / "candidate"
    candidate.mkdir()
    manifest = {"schema": "bridge-offline-verification-1", "startedUtc": datetime.now(timezone.utc).isoformat(),
                "scope": {"all": args.all, "csharp": csharp, "python": python,
                          "campaignRecovery": args.all, "toolTests": args.all},
                "python": {"executable": sys.executable, "version": sys.version},
                "deployed": False, "gameLaunched": False, "liveConnection": False,
                "steps": [], "ok": False}
    steps = manifest["steps"]
    try:
        manifest["inputSha256"] = snapshot(work, inputs)
        manifest["sdkSha256"] = manifest["inputSha256"]["sdk.py"]
        sources = sorted((inputs / "src").glob("*.cs"))
        refs = [root / "MelonLoader/net472" / name for name in ("MelonLoader.dll", "0Harmony.dll")]
        if csharp:
            manifest["compiler"] = {"path": str(CSC), "sha256": sha256(CSC)}
            manifest["referenceSha256"] = {str(path): sha256(path) for path in refs}
            for path in refs:
                shutil.copy2(path, candidate / path.name)
            if "UnitRuntimeTests" in csharp:
                manifest["gameAssemblySha256"] = sha256(root / "GameAssembly.dll")

            def compile_target(name, files, main=None):
                target = candidate / name
                command = [str(CSC), "/nologo", "/utf8output", "/platform:x64",
                           "/target:" + ("exe" if main else "library"), "/out:" + str(target),
                           "/reference:System.dll", "/reference:System.Core.dll", "/reference:System.Web.Extensions.dll"]
                command += ["/reference:" + str(candidate / ref.name) for ref in refs]
                if main:
                    command.append("/main:" + main)
                command += [str(path) for path in files]
                record = run_step(command, inputs, out / (name + ".compile.log"), args.timeout, "compile", name)
                steps.append(record)
                return target if record["exitCode"] == 0 else None

            dll = compile_target("PolytopiaAgentBridge.dll", sources)
            if dll:
                manifest["candidateDll"] = str(dll)
                manifest["dllSha256"] = sha256(dll)
                for name in csharp:
                    files = [inputs / "tests" / (name + ".cs")]
                    if name == "PublicMarkerIntegrationTests":
                        files.append(inputs / "tests/PublicMarkerReaderTests.cs")
                    main = "MarkerIntegration041.PublicMarkerIntegrationTests" if name == "PublicMarkerIntegrationTests" else name
                    if name == "UnitRuntimeTests":
                        fixture = compile_target(name + ".dll", files)
                        command = powershell_fixture(fixture, dll, root / "GameAssembly.dll") if fixture else None
                    else:
                        exe = compile_target(name + ".exe", files if name in SEPARATE else sources + files, main)
                        command = [str(exe)] + ([str(dll)] if name in SEPARATE else []) if exe else None
                        if command and name == "AttackSettlementTests":
                            command.append(str(inputs / "tests/fixtures/attack-settlement-20261011.json"))
                        if command and name in ("RequestContextTests", "FormatterTests"):
                            command.append(str(inputs / "src/BridgeMod.cs"))
                    if command:
                        steps.append(run_step(command, candidate, out / (name + ".run.log"), args.timeout, "test", name))
        if python:
            command = [sys.executable, "-m", "unittest", "discover", "-s", "tests", "-p", "test*.py", "-v"] if args.all else [
                sys.executable, "-m", "unittest", "-v", *python]
            # Explicit modules need test_sdk and sibling modules on the import path.
            steps.append(run_step(command, inputs if args.all else inputs / "tests", out / "python-tests.log",
                                  args.timeout, "test", "python"))
        if args.all:
            steps.append(run_step([sys.executable, str(inputs / CAMPAIGN / "test_settled_unknown.py")],
                                  inputs, out / "campaign-recovery.log", args.timeout, "test", "campaignRecovery"))
            steps.append(run_step([sys.executable, "-m", "unittest", "discover", "-s", "tools", "-p", "test*.py", "-v"],
                                  inputs, out / "tool-tests.log", args.timeout, "test", "verificationTool"))
        manifest["changedSnapshotFiles"] = changed_inputs(inputs, manifest["inputSha256"])
        manifest["changedWorkingFiles"] = changed_inputs(work, manifest["inputSha256"])
        manifest["addedWorkingFiles"] = sorted(set(p.relative_to(work).as_posix() for p in input_files(work))
                                               - set(manifest["inputSha256"]))
        manifest["ok"] = bool(steps) and all(s["exitCode"] == 0 for s in steps) and not (
            manifest["changedSnapshotFiles"] or manifest["changedWorkingFiles"] or manifest["addedWorkingFiles"])
    except Exception as exc:
        manifest["error"] = type(exc).__name__ + ": " + str(exc)
        print(manifest["error"], file=sys.stderr)
    finally:
        manifest["finishedUtc"] = datetime.now(timezone.utc).isoformat()
        (out / "verification.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
        print("Evidence:", out / "verification.json", flush=True)
    return 0 if manifest["ok"] else 1


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--all", action="store_true", help="all discovered C# suites and Python modules, campaign fixture, tool tests")
    parser.add_argument("--csharp", nargs="+", action="extend", default=[], metavar="SUITE")
    parser.add_argument("--python", nargs="+", action="extend", default=[], metavar="MODULE")
    parser.add_argument("--out", help="new output directory inside agent-bridge (default: timestamped verification-runs directory)")
    parser.add_argument("--timeout", type=int, default=180, help="seconds per compiler/test process")
    parser.add_argument("--game-root", type=Path, help="local owned game installation providing compile/test references; default follows workspace layout")
    parser.add_argument("--list", action="store_true", help="list available suites/modules without running")
    args = parser.parse_args(argv)
    if args.list:
        print(json.dumps(inventory(WORK), indent=2))
        return 0
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    if args.all and (args.csharp or args.python):
        parser.error("--all cannot be combined with explicit suite/module selections")
    try:
        return verify(args, root=args.game_root.resolve() if args.game_root else ROOT)
    except ValueError as exc:
        parser.error(str(exc))


if __name__ == "__main__":
    raise SystemExit(main())
