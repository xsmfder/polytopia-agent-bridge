"""Regression checks for evidence isolation and failure reporting; no game needed."""
import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import verify_bridge as runner


class VerificationToolTests(unittest.TestCase):
    def test_unknown_selection_is_rejected_instead_of_skipped(self):
        with self.assertRaisesRegex(ValueError, "Unknown C# suite"):
            runner.select(["TypoTests"], ["ActualTests"], "C# suite")
        self.assertEqual(runner.select(["ActualTests", "ActualTests"], ["ActualTests"], "C# suite"), ["ActualTests"])

    def test_existing_and_external_evidence_destinations_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory) / "work"
            work.mkdir()
            (work / "existing").mkdir()
            for destination in (work, work / "existing", work.parent / "outside", work / "src/new"):
                with self.subTest(destination=destination), self.assertRaises(ValueError):
                    runner.output_path(work, destination)
            self.assertEqual(runner.output_path(work, work / "candidate"), work / "candidate")

    def test_snapshot_is_independent_and_drift_is_reported(self):
        with tempfile.TemporaryDirectory() as directory:
            work, snapshot = Path(directory) / "work", Path(directory) / "snapshot"
            files = ["src/Test.cs", "tests/ActualTests.cs", "tests/test_actual.py", "tests/fixtures/data.json",
                     *runner.SDK_FILES, "tools/verify_bridge.py", "tools/test_verify_bridge.py",
                     str(runner.CAMPAIGN / "test_settled_unknown.py"),
                     str(runner.CAMPAIGN / "attacker-disappeared-ticket34.json")]
            for name in files:
                path = work / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("'old'", encoding="utf-8")
            hashes = runner.snapshot(work, snapshot)
            (work / "sdk.py").write_text("new", encoding="utf-8")
            self.assertEqual((snapshot / "sdk.py").read_text(), "'old'")
            self.assertEqual(runner.changed_inputs(work, hashes), ["sdk.py"])
            self.assertEqual(runner.changed_inputs(snapshot, hashes), [])

    def test_nonzero_test_exit_is_retained_with_full_log(self):
        import sys
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            log = Path(directory) / "failure.log"
            result = runner.run_step([sys.executable, "-c", "print('failure evidence'); raise SystemExit(7)"],
                                     directory, log, 10, "test", "fixture")
            self.assertEqual(result["exitCode"], 7)
            self.assertIn("failure evidence", log.read_text())
            self.assertIsNone(result["testCount"])

    def test_local_imports_are_snapshotted_recursively_without_execution(self):
        with tempfile.TemporaryDirectory() as directory:
            work, snapshot = Path(directory) / "work", Path(directory) / "snapshot"
            for name in (*runner.SDK_FILES, "tools/verify_bridge.py", "tools/test_verify_bridge.py",
                         str(runner.CAMPAIGN / "test_settled_unknown.py"),
                         str(runner.CAMPAIGN / "attacker-disappeared-ticket34.json")):
                path = work / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("", encoding="utf-8")
            (work / "sdk.py").write_text("def load():\n from sdk_memory import Memory\n", encoding="utf-8")
            (work / "sdk_memory.py").write_text("from observation import World\nraise RuntimeError('never execute')", encoding="utf-8")
            (work / "observation.py").write_text("from sightings import Memory", encoding="utf-8")
            (work / "sightings.py").write_text("", encoding="utf-8")
            (work / "runtime_rules.json").write_text("{}", encoding="utf-8")
            (work / "unit_type_names.json").write_text("{}", encoding="utf-8")
            with patch.object(runner, 'SDK_DATA_FILES', ('runtime_rules.json',)):
                hashes = runner.snapshot(work, snapshot)
            for name in ("sdk_memory.py", "observation.py", "sightings.py", "runtime_rules.json"):
                self.assertIn(name, hashes)
                self.assertTrue((snapshot / name).is_file())
            self.assertNotIn('unit_type_names.json', hashes)

    def test_timeout_is_a_failed_step_not_a_missing_result(self):
        import subprocess
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()), patch.object(
            runner.subprocess, "run", side_effect=subprocess.TimeoutExpired(["fixture"], 1, output=b"partial")
        ):
            log = Path(directory) / "timeout.log"
            result = runner.run_step(["fixture"], directory, log, 1, "test", "fixture")
            self.assertNotEqual(result["exitCode"], 0)
            self.assertIn("partial", log.read_text())

    def test_snapshot_failure_still_writes_failed_manifest(self):
        import argparse
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            work = Path(directory) / "work"
            (work / "tests").mkdir(parents=True)
            (work / "tests/test_actual.py").write_text("", encoding="utf-8")
            args = argparse.Namespace(all=False, csharp=[], python=["test_actual"], out=str(work / "out"), timeout=1)
            self.assertEqual(runner.verify(args, work, Path(directory)), 1)
            manifest = json.loads((work / "out/verification.json").read_text(encoding="utf-8"))
            self.assertFalse(manifest["ok"])
            self.assertIn("FileNotFoundError", manifest["error"])
            self.assertFalse(manifest["deployed"])

    def test_count_does_not_invent_missing_assertion_totals(self):
        self.assertEqual(runner.test_count("ALL PASS 51"), 51)
        self.assertEqual(runner.test_count("Ran 220 tests in 0.5s\nOK"), 220)
        self.assertIsNone(runner.test_count("fixture completed"))

    def test_explicit_local_game_root_and_canonical_recovery_path(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(runner, 'verify', return_value=0) as verify:
            self.assertEqual(runner.main(['--python', 'test_sdk', '--game-root', directory]), 0)
            self.assertEqual(verify.call_args.kwargs['root'], Path(directory).resolve())
        self.assertTrue(str(runner.CAMPAIGN).replace('\\', '/').startswith('tests/fixtures/'))


if __name__ == "__main__":
    unittest.main()
