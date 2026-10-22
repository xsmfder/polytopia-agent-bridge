"""Install/rollback tests only touch a temporary fake game installation."""
import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import deploy_bridge as deploy


def dump(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        # AppContainer's system temp permits creation but can reject replacing a
        # .dll. Keep the fake installation under the explicitly writable input
        # workspace; validate its boundary before recursive TemporaryDirectory cleanup.
        writable = Path(__file__).resolve().parents[1]
        self.temp = tempfile.TemporaryDirectory(dir=writable)
        assert Path(self.temp.name).resolve().is_relative_to(writable)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "game"
        self.work = self.root / ".offline-work/agent-bridge"
        self.work.mkdir(parents=True)
        self.target = self.root / "Mods" / deploy.DLL_NAME
        self.target.parent.mkdir()
        self.target.write_bytes(b"installed before")
        self.inputs = {"src/BridgeMod.cs": "// source\n", "sdk.py": "# SDK\n",
                       "sdk_memory.py": "# module\n", "unit_type_names.json": "{}",
                       "tests/FixtureTests.cs": "// fixture\n", "tests/OtherTests.cs": "// other\n",
                       "tests/test_sdk.py": "# test\n"}
        for name, content in self.inputs.items():
            path = self.work / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")
        self.manifest = self.fixture("direct")

    def fixture(self, name, failed=False, inputs=None, suites=None, all_scope=False):
        directory = self.work / name
        original = inputs or self.inputs
        hashes = {}
        for relative, content in original.items():
            path = directory / "inputs" / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8")
            hashes[relative] = deploy.sha256(path)
        dll = directory / "candidate" / deploy.DLL_NAME
        dll.parent.mkdir()
        dll.write_bytes(b"verified candidate")
        log = directory / "compile.log"
        log.write_text("", encoding="utf-8")
        steps = [{"kind": "compile", "name": deploy.DLL_NAME, "exitCode": 0,
                  "command": ["csc", str(dll)], "log": str(log)}]
        names = suites or ["FixtureTests"]
        for suite in names:
            log = directory / (suite + ".log")
            bad = failed and suite == "FixtureTests"
            log.write_text("passed=2 failed=1\n" if bad else "ALL PASS 3\n", encoding="utf-8")
            steps.append({"kind": "test", "name": suite, "exitCode": int(bad), "assertionCount": 2 if bad else 3,
                          "command": [str(directory / "candidate" / (suite + ".exe"))], "log": str(log)})
        if all_scope:
            for stage in ("python", "campaignRecovery", "verificationTool"):
                log = directory / (stage + ".log")
                log.write_text("Ran 1 test in 0.01s\n\nOK\n", encoding="utf-8")
                steps.append({"kind": "test", "name": stage, "exitCode": 0, "testCount": 1,
                              "command": ["python", "-m", "unittest"], "log": str(log)})
        manifest = directory / "verification.json"
        dump(manifest, {"schema": "bridge-offline-verification-1", "ok": not failed,
                        "candidateDll": str(dll), "dllSha256": deploy.sha256(dll), "inputSha256": hashes,
                        "sdkSha256": hashes["sdk.py"], "changedSnapshotFiles": [], "changedWorkingFiles": [],
                        "addedWorkingFiles": [], "steps": steps, "scope": {"all": all_scope, "csharp": names,
                        "python": ["test_sdk"] if all_scope else [], "campaignRecovery": all_scope, "toolTests": all_scope}})
        return manifest

    def batch(self):
        full = self.fixture("batch/full", failed=True, suites=["FixtureTests", "OtherTests"], all_scope=True)
        revised = dict(self.inputs, **{"tests/FixtureTests.cs": "// corrected fixture\n"})
        extra = self.fixture("batch/extra", inputs=revised)
        batch = self.work / "batch/batch-verification.json"
        full_data = deploy.read_json(full)
        dump(batch, {"schema": "bridge-batch-supplemental-verification-1", "ok": True,
                     "fullManifest": "full/verification.json", "supplementalManifest": "extra/verification.json",
                     "inputsChangedSinceFullRun": ["tests/FixtureTests.cs"], "productionSourcesChangedSinceFullRun": [],
                     "candidateDll": full_data["candidateDll"], "candidateDllSha256": full_data["dllSha256"],
                     "sdkSha256": full_data["sdkSha256"]})
        return batch, full, extra

    def test_default_plan_is_read_only(self):
        before = {p.relative_to(self.root): p.read_bytes() for p in self.root.rglob("*") if p.is_file()}
        plan = deploy.install_plan(self.work, self.manifest, lambda: [])
        after = {p.relative_to(self.root): p.read_bytes() for p in self.root.rglob("*") if p.is_file()}
        self.assertEqual(before, after)
        self.assertFalse(plan["deployed"])
        self.assertTrue(plan["canWrite"])
        self.assertFalse((self.work / "deployments").exists())

    def test_cli_defaults_to_plan(self):
        with patch.object(deploy, "WORK", self.work), patch.object(deploy, "game_processes", return_value=[]), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(deploy.main(["--evidence", str(self.manifest)]), 0)
        self.assertEqual(self.target.read_bytes(), b"installed before")
        self.assertFalse((self.work / "deployments").exists())

    def test_install_and_rollback_keep_distinct_records_and_hashes(self):
        installed = deploy.execute(self.work, self.manifest, probe=lambda: [])
        original_record = Path(installed["record"]).read_bytes()
        self.assertEqual(self.target.read_bytes(), b"verified candidate")
        self.assertEqual(Path(installed["backup"]).read_bytes(), b"installed before")
        result = deploy.execute(self.work, installed["record"], rollback=True, probe=lambda: [])
        self.assertEqual(self.target.read_bytes(), b"installed before")
        self.assertNotEqual(result["record"], installed["record"])
        self.assertEqual(Path(result["backup"]).read_bytes(), b"verified candidate")
        self.assertEqual(Path(installed["record"]).read_bytes(), original_record)
        with self.assertRaisesRegex(ValueError, "File/hash mismatch"):
            deploy.rollback_plan(self.work, installed["record"], lambda: [])

    def test_running_game_blocks_install_before_any_write(self):
        plan = deploy.install_plan(self.work, self.manifest, lambda: [123])
        self.assertFalse(plan["canWrite"])
        with self.assertRaisesRegex(ValueError, "Game is running"):
            deploy.execute(self.work, self.manifest, probe=lambda: [123])
        self.assertFalse((self.work / "deployments").exists())

    def fresh_target(self, remove_directory=False):
        self.target.unlink()
        if remove_directory:
            self.target.parent.rmdir()
        (self.root / 'Polytopia.exe').write_bytes(b'fake game, never executed')

    def test_first_install_plan_requires_no_placeholder_and_is_read_only(self):
        self.fresh_target(remove_directory=True)
        plan = deploy.install_plan(self.work, self.manifest, lambda: [])
        self.assertTrue(plan['initialInstall'])
        self.assertIsNone(plan['beforeSha256'])
        self.assertFalse(self.target.parent.exists())
        self.assertFalse((self.work / 'deployments').exists())

    def test_first_install_and_rollback_remove_only_the_new_bridge(self):
        self.fresh_target()
        other_mod = self.target.parent / 'Unrelated.dll'
        other_mod.write_bytes(b'leave this mod alone')
        installed = deploy.execute(self.work, self.manifest, probe=lambda: [])
        self.assertTrue(installed['initialInstall'])
        self.assertFalse(installed['beforeExists'])
        self.assertIsNone(installed['backup'])
        self.assertEqual(self.target.read_bytes(), b'verified candidate')
        plan = deploy.rollback_plan(self.work, installed['record'], lambda: [])
        self.assertIsNone(plan['candidate'])
        self.assertIsNone(plan['afterSha256'])
        restored = deploy.execute(self.work, installed['record'], rollback=True, probe=lambda: [])
        self.assertFalse(self.target.exists())
        self.assertEqual(other_mod.read_bytes(), b'leave this mod alone')
        self.assertEqual(Path(restored['backup']).read_bytes(), b'verified candidate')

    def test_first_install_creates_missing_mods_directory_only_when_committing(self):
        self.fresh_target(remove_directory=True)
        deploy.execute(self.work, self.manifest, probe=lambda: [])
        self.assertEqual(self.target.read_bytes(), b'verified candidate')

    def test_first_install_running_or_unknown_process_creates_no_mods_directory(self):
        self.fresh_target(remove_directory=True)
        with self.assertRaisesRegex(ValueError, 'Game is running'):
            deploy.execute(self.work, self.manifest, probe=lambda: [789])
        self.assertFalse(self.target.parent.exists())
        self.assertFalse((self.work / 'deployments').exists())

    def test_first_install_requires_game_executable(self):
        self.fresh_target()
        (self.root / 'Polytopia.exe').unlink()
        with self.assertRaisesRegex(ValueError, 'game executable'):
            deploy.install_plan(self.work, self.manifest, lambda: [])

    def test_initial_rollback_rejects_modified_installed_file(self):
        self.fresh_target()
        installed = deploy.execute(self.work, self.manifest, probe=lambda: [])
        self.target.write_bytes(b'another file appeared')
        with self.assertRaisesRegex(ValueError, 'File/hash mismatch'):
            deploy.execute(self.work, installed['record'], rollback=True, probe=lambda: [])
        self.assertEqual(self.target.read_bytes(), b'another file appeared')

    def test_first_install_atomic_commit_does_not_overwrite_a_racing_file(self):
        self.fresh_target()
        real_publish = deploy.publish_new
        def racing_publish(source, target):
            Path(target).write_bytes(b'installed by somebody else')
            return real_publish(source, target)
        with patch.object(deploy, 'publish_new', side_effect=racing_publish), self.assertRaises(FileExistsError):
            deploy.execute(self.work, self.manifest, probe=lambda: [])
        self.assertEqual(self.target.read_bytes(), b'installed by somebody else')

    def test_first_install_refuses_a_directory_target(self):
        self.fresh_target()
        self.target.mkdir()
        with self.assertRaisesRegex(ValueError, 'not a regular file'):
            deploy.install_plan(self.work, self.manifest, lambda: [])

    def test_initial_rollback_metadata_corruption_is_refused_without_removal(self):
        self.fresh_target()
        installed = deploy.execute(self.work, self.manifest, probe=lambda: [])
        for change in ({'initialInstall': 'true'}, {'initialInstall': 1}, {'initialInstall': False},
                       {'beforeExists': True}, {'backup': 'invented.dll'}):
            record = deploy.read_json(installed['record'])
            bad = dict(record, **change)
            path = Path(installed['record']).parent / ('tampered-' + str(len(list(Path(installed['record']).parent.glob('tampered-*')))) + '.json')
            dump(path, bad)
            with self.subTest(change=change), self.assertRaises(ValueError):
                deploy.rollback_plan(self.work, path, lambda: [])
            self.assertEqual(self.target.read_bytes(), b'verified candidate')

    def test_process_query_failure_is_not_treated_as_stopped(self):
        def unknown():
            raise ValueError("Cannot establish game process status")
        with self.assertRaisesRegex(ValueError, "Cannot establish"):
            deploy.execute(self.work, self.manifest, probe=unknown)
        self.assertEqual(self.target.read_bytes(), b"installed before")

    def test_process_start_before_commit_preserves_original_and_logs_failure(self):
        calls = iter([[], [], [234]])
        with self.assertRaisesRegex(ValueError, "Game is running"):
            deploy.execute(self.work, self.manifest, probe=lambda: next(calls))
        self.assertEqual(self.target.read_bytes(), b"installed before")
        self.assertEqual(len(list((self.work / "deployments").glob("*/failed.json"))), 1)
        self.assertEqual(list(self.target.parent.glob("*.tmp")), [])

    def test_candidate_hash_mismatch_is_refused(self):
        Path(deploy.read_json(self.manifest)["candidateDll"]).write_bytes(b"corrupt")
        with self.assertRaisesRegex(ValueError, "File/hash mismatch"):
            deploy.install_plan(self.work, self.manifest, lambda: [])

    def test_success_flag_does_not_hide_failed_steps(self):
        data = deploy.read_json(self.manifest)
        data["steps"][-1]["exitCode"] = 1
        dump(self.manifest, data)
        with self.assertRaisesRegex(ValueError, "steps failed"):
            deploy.install_plan(self.work, self.manifest, lambda: [])

    def test_missing_test_log_or_coverage_is_refused(self):
        data = deploy.read_json(self.manifest)
        Path(data["steps"][-1]["log"]).unlink()
        with self.assertRaisesRegex(ValueError, "Missing original log"):
            deploy.install_plan(self.work, self.manifest, lambda: [])

    def test_missing_test_step_is_refused_even_when_ok_is_true(self):
        data = deploy.read_json(self.manifest)
        data["steps"] = data["steps"][:-1]
        dump(self.manifest, data)
        with self.assertRaisesRegex(ValueError, "coverage"):
            deploy.install_plan(self.work, self.manifest, lambda: [])

    def test_recorded_count_must_match_real_log(self):
        data = deploy.read_json(self.manifest)
        data["steps"][-1]["assertionCount"] = 900
        dump(self.manifest, data)
        with self.assertRaisesRegex(ValueError, "Log count disagrees"):
            deploy.install_plan(self.work, self.manifest, lambda: [])

    def test_runtime_reference_or_gameassembly_drift_is_refused(self):
        data = deploy.read_json(self.manifest)
        reference = self.root / "MelonLoader/net472/MelonLoader.dll"
        reference.parent.mkdir(parents=True)
        reference.write_bytes(b"reference")
        (Path(data["candidateDll"]).parent / reference.name).write_bytes(b"reference")
        data["referenceSha256"] = {str(reference): deploy.sha256(reference)}
        assembly = self.root / "GameAssembly.dll"
        assembly.write_bytes(b"game assembly")
        data["gameAssemblySha256"] = deploy.sha256(assembly)
        dump(self.manifest, data)
        self.assertTrue(deploy.install_plan(self.work, self.manifest, lambda: [])["canWrite"])
        for path in (reference, assembly):
            before = path.read_bytes()
            path.write_bytes(b"changed")
            with self.subTest(path=path), self.assertRaisesRegex(ValueError, "File/hash mismatch"):
                deploy.install_plan(self.work, self.manifest, lambda: [])
            path.write_bytes(before)

    def test_snapshot_tampering_is_refused(self):
        (self.manifest.parent / "inputs/sdk.py").write_text("tamper", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "File/hash mismatch"):
            deploy.install_plan(self.work, self.manifest, lambda: [])

    def test_source_sdk_metadata_changes_are_refused(self):
        for name in ("src/BridgeMod.cs", "sdk.py", "sdk_memory.py", "unit_type_names.json"):
            path = self.work / name
            before = path.read_bytes()
            path.write_bytes(b"changed")
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "File/hash mismatch"):
                deploy.install_plan(self.work, self.manifest, lambda: [])
            path.write_bytes(before)

    def test_new_production_or_sdk_file_is_refused(self):
        for name in ("src/New.cs", "sdk_new.py"):
            path = self.work / name
            path.write_text("# new", encoding="utf-8")
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "drifted|unverified"):
                deploy.install_plan(self.work, self.manifest, lambda: [])
            path.unlink()

    def test_batch_revalidates_original_full_and_supplemental_records(self):
        batch, full, extra = self.batch()
        plan = deploy.install_plan(self.work, batch, lambda: [])
        self.assertEqual(plan["supplementedSuites"], ["FixtureTests"])
        self.assertEqual(plan["candidate"], deploy.read_json(full)["candidateDll"])
        self.assertFalse(deploy.read_json(full)["ok"])
        self.assertEqual(len(plan["evidenceFiles"]), 3)

    def test_batch_true_flag_cannot_substitute_for_original_supplement(self):
        batch, full, extra = self.batch()
        data = deploy.read_json(extra)
        data["steps"][-1]["exitCode"] = 1
        dump(extra, data)
        with self.assertRaisesRegex(ValueError, "steps failed"):
            deploy.install_plan(self.work, batch, lambda: [])

    def test_batch_cannot_supplement_a_production_change(self):
        batch, full, extra = self.batch()
        source = extra.parent / "inputs/src/BridgeMod.cs"
        source.write_text("// changed production", encoding="utf-8")
        data = deploy.read_json(extra)
        data["inputSha256"]["src/BridgeMod.cs"] = deploy.sha256(source)
        dump(extra, data)
        with self.assertRaisesRegex(ValueError, "production or unrelated"):
            deploy.install_plan(self.work, batch, lambda: [])

    def test_batch_claims_must_match_actual_candidate_and_input_diff(self):
        batch, full, extra = self.batch()
        original = deploy.read_json(batch)
        for field, value in (("inputsChangedSinceFullRun", []), ("candidateDllSha256", "0" * 64)):
            changed = dict(original)
            changed[field] = value
            dump(batch, changed)
            with self.subTest(field=field), self.assertRaises(ValueError):
                deploy.install_plan(self.work, batch, lambda: [])
        dump(batch, original)

    def test_batch_cannot_hide_another_failed_suite(self):
        batch, full, extra = self.batch()
        data = deploy.read_json(full)
        step = next(s for s in data["steps"] if s["name"] == "OtherTests")
        step["exitCode"] = 1
        dump(full, data)
        with self.assertRaisesRegex(ValueError, "does not match failed suites"):
            deploy.install_plan(self.work, batch, lambda: [])

    def test_rollback_requires_completed_install_and_matching_current_backup(self):
        installed = deploy.execute(self.work, self.manifest, probe=lambda: [])
        with self.assertRaisesRegex(ValueError, "completed install"):
            deploy.rollback_plan(self.work, Path(installed["record"]).parent / "started.json", lambda: [])
        backup = Path(installed["backup"])
        backup.write_bytes(b"corrupt")
        with self.assertRaisesRegex(ValueError, "File/hash mismatch"):
            deploy.rollback_plan(self.work, installed["record"], lambda: [])

    def test_failed_replace_keeps_original_backup_and_failed_record(self):
        with patch.object(deploy.os, "replace", side_effect=OSError("locked")), self.assertRaisesRegex(OSError, "locked"):
            deploy.execute(self.work, self.manifest, probe=lambda: [])
        self.assertEqual(self.target.read_bytes(), b"installed before")
        records = self.work / "deployments"
        self.assertEqual(len(list(records.glob("*/failed.json"))), 1)
        self.assertEqual(len(list(records.glob("*/before.dll"))), 1)
        self.assertEqual(list(records.glob("*/completed.json")), [])
        self.assertFalse((records / ".deployment.lock").exists())

    def test_running_game_blocks_rollback_without_another_record(self):
        installed = deploy.execute(self.work, self.manifest, probe=lambda: [])
        before = sorted((self.work / "deployments").iterdir())
        with self.assertRaisesRegex(ValueError, "Game is running"):
            deploy.execute(self.work, installed["record"], rollback=True, probe=lambda: [99])
        self.assertEqual(sorted((self.work / "deployments").iterdir()), before)
        self.assertEqual(self.target.read_bytes(), b"verified candidate")

    def test_rollback_refuses_changed_current_dll(self):
        installed = deploy.execute(self.work, self.manifest, probe=lambda: [])
        self.target.write_bytes(b"third party replacement")
        with self.assertRaisesRegex(ValueError, "File/hash mismatch"):
            deploy.rollback_plan(self.work, installed["record"], lambda: [])

    def test_candidate_outside_evidence_directory_is_refused(self):
        data = deploy.read_json(self.manifest)
        data["candidateDll"] = str(self.target)
        data["dllSha256"] = deploy.sha256(self.target)
        dump(self.manifest, data)
        with self.assertRaisesRegex(ValueError, "escapes"):
            deploy.install_plan(self.work, self.manifest, lambda: [])

    def test_concurrent_writer_lock_is_not_removed_by_another_caller(self):
        directory = self.work / "deployments"
        directory.mkdir()
        lock = directory / ".deployment.lock"
        lock.write_text("existing owner", encoding="utf-8")
        with self.assertRaises(FileExistsError):
            deploy.execute(self.work, self.manifest, probe=lambda: [])
        self.assertEqual(lock.read_text(), "existing owner")


if __name__ == "__main__":
    unittest.main()
