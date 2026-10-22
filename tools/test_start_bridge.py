"""Startup tests use fake processes and a temporary fake installation only."""
import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import start_bridge as launcher


class Clock:
    def __init__(self):
        self.now = 0.0

    def __call__(self):
        return self.now

    def sleep(self, duration):
        self.now += duration


class Process:
    pid = 4321

    def __init__(self, exit_code=None):
        self.exit_code = exit_code

    def poll(self):
        return self.exit_code


class StartupTests(unittest.TestCase):
    def setUp(self):
        writable = Path(__file__).resolve().parents[1]
        self.temp = tempfile.TemporaryDirectory(dir=writable)
        assert Path(self.temp.name).resolve().is_relative_to(writable)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "game"
        self.work = self.root / ".offline-work" / "agent-bridge"
        self.work.mkdir(parents=True)
        (self.root / "Polytopia.exe").write_bytes(b"fake, never executed")
        self.config = self.work / "controlled-seat.json"
        self.clock = Clock()
        self.calls = []

    def ready(self, **changes):
        data = {"startupSchema": "bridge-startup-1", "controlledSeatLoaded": True,
                "configuredPlayerId": 2, "pid": Process.pid, "transport": "file",
                "startupUiReady": True, "startupUiPhase": "start_menu",
                "cmd": str(self.work / "cmd.jsonl"), "out": str(self.work / "out.jsonl")}
        data.update(changes)
        (self.work / "cmd.jsonl").write_bytes(b"")
        (self.work / "bridge-ready.json").write_text(json.dumps(data), encoding="utf-8")

    def factory(self, hook=None, exit_code=None):
        def create(*args, **kwargs):
            self.calls.append((args, kwargs))
            self.assertEqual(self.config.read_bytes(), b'{"playerId":2}\n')
            if hook:
                hook()
            return Process(exit_code)
        return create

    def start(self, **kwargs):
        opts = dict(timeout=0.3, probe=lambda: [], popen=self.factory(self.ready),
                    clock=self.clock, sleep=self.clock.sleep)
        opts.update(kwargs)
        return launcher.start(self.work, 2, **opts)

    def test_default_plan_has_no_writes_or_process_start(self):
        before = {str(p.relative_to(self.root)): p.read_bytes() for p in self.root.rglob("*") if p.is_file()}
        with patch.object(launcher, "WORK", self.work), patch.object(launcher, "game_processes", return_value=[]), \
                patch.object(launcher.subprocess, "Popen", side_effect=AssertionError("must not start")), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(launcher.main([]), 0)
        self.assertEqual(json.loads(output.getvalue())["operation"], "plan")
        after = {str(p.relative_to(self.root)): p.read_bytes() for p in self.root.rglob("*") if p.is_file()}
        self.assertEqual(before, after)

    def test_ready_exact_original_bytes_restored_and_visible_flags(self):
        original = b'\xef\xbb\xbf{ "playerId": 1 }\r\n'
        self.config.write_bytes(original)
        result = self.start()
        self.assertEqual(result["status"], "ready")
        self.assertTrue(result["ready"])
        self.assertEqual(result["configurationRestoration"], "restored")
        self.assertFalse(result["configurationRestoredBeforeReady"])
        self.assertEqual(self.config.read_bytes(), original)
        self.assertEqual((Path(result["record"]).parent / "controlled-seat.before").read_bytes(), original)
        args, kwargs = self.calls[0]
        self.assertEqual(args, ([str(self.root / "Polytopia.exe")],))
        self.assertEqual(set(kwargs), {"cwd", "stdout", "stderr"})
        self.assertEqual(kwargs["cwd"], str(self.root))
        self.assertFalse(result["deployed"])
        self.assertFalse(result["gameTerminated"])

    def test_originally_absent_is_absent_after_ready(self):
        result = self.start()
        self.assertEqual(result["status"], "ready")
        self.assertFalse(self.config.exists())
        self.assertFalse(result["originalConfigExisted"])

    def test_native_relative_paths_resolve_from_game_root(self):
        result = self.start(popen=self.factory(lambda: self.ready(
            cmd=str(Path(".offline-work") / "agent-bridge" / "cmd.jsonl"),
            out=str(Path(".offline-work") / "agent-bridge" / "out.jsonl"))))
        self.assertEqual(result["status"], "ready")
        self.assertTrue(result["ok"])

    def test_existing_process_refused_before_configuration_write(self):
        self.config.write_bytes(b"original")
        with self.assertRaisesRegex(ValueError, "already running"):
            self.start(probe=lambda: [8])
        self.assertEqual(self.config.read_bytes(), b"original")
        self.assertEqual(self.calls, [])

    def test_query_failure_refused(self):
        def broken():
            raise OSError("query denied")
        with self.assertRaisesRegex(OSError, "query denied"):
            self.start(probe=broken)
        self.assertFalse(self.config.exists())
        self.assertEqual(self.calls, [])

    def test_second_query_running_restores_and_does_not_launch(self):
        probes = iter([[], [9]])
        self.config.write_bytes(b"old")
        result = self.start(probe=lambda: next(probes))
        self.assertEqual(result["status"], "startup_error")
        self.assertEqual(self.config.read_bytes(), b"old")
        self.assertEqual(self.calls, [])

    def test_old_ready_schema_is_not_success(self):
        result = self.start(popen=self.factory(lambda: self.ready(startupSchema="old")))
        self.assertEqual(result["status"], "startup_unknown")
        self.assertEqual(result["lastReadyRejection"], "ready_mismatch_startupSchema")
        self.assertEqual(result["pid"], Process.pid)
        self.assertFalse(result["ready"])
        self.assertFalse(self.config.exists())

    def test_unchanged_ready_even_same_pid_is_stale(self):
        self.ready()
        result = self.start(popen=self.factory())
        self.assertEqual(result["status"], "startup_unknown")
        self.assertEqual(result["lastReadyRejection"], "ready_unchanged_since_launch")

    def test_wrong_ready_fields_are_not_success(self):
        for field, wrong in (("pid", True), ("pid", 11), ("configuredPlayerId", True),
                             ("configuredPlayerId", 1), ("controlledSeatLoaded", False),
                             ("transport", "socket"), ("cmd", "cmd.jsonl"),
                             ("out", str(self.root / "foreign.jsonl"))):
            with self.subTest(field=field, wrong=wrong):
                ready_path = self.work / "bridge-ready.json"
                ready_path.unlink(missing_ok=True)
                result = self.start(popen=self.factory(lambda: self.ready(**{field: wrong})))
                self.assertEqual(result["status"], "startup_unknown")
                self.assertEqual(result["lastReadyRejection"], "ready_mismatch_" + field)
                self.assertFalse(self.config.exists())

    def test_unreadable_ready_times_out_without_killing(self):
        result = self.start(popen=self.factory(lambda: (self.work / "bridge-ready.json").write_bytes(b"{")))
        self.assertEqual(result["status"], "startup_unknown")
        self.assertEqual(result["lastReadyRejection"], "ready_unreadable")
        self.assertTrue(result["configurationRestoredBeforeReady"])
        self.assertFalse(result["gameTerminated"])

    def test_early_exit_not_ready_even_with_correct_ready(self):
        result = self.start(popen=self.factory(self.ready, exit_code=7))
        self.assertEqual(result["status"], "exited_before_ready")
        self.assertEqual(result["exitCode"], 7)
        self.assertFalse(result["ready"])
        self.assertFalse(self.config.exists())

    def test_transport_initialization_is_not_interactive_menu_readiness(self):
        result = self.start(popen=self.factory(lambda: self.ready(
            startupUiReady=False, startupUiPhase="initializing")))
        self.assertEqual(result["status"], "startup_unknown")
        self.assertFalse(result["ready"])
        self.assertFalse(result["gameTerminated"])

    def test_exit_after_transport_ready_before_menu_is_not_success(self):
        clock = self.clock
        class BootCrash(Process):
            def poll(self):
                return 7 if clock.now >= 0.1 else None
        def create(*args, **kwargs):
            self.ready(startupUiReady=False, startupUiPhase="loading")
            return BootCrash()
        result = self.start(popen=create)
        self.assertEqual(result["status"], "exited_before_ready")
        self.assertEqual(result["exitCode"], 7)
        self.assertFalse(result["ready"])

    def test_waits_for_real_menu_transition_without_starting_again(self):
        def advance(duration):
            self.clock.sleep(duration)
            if self.clock.now >= 0.2:
                self.ready()
        result = self.start(popen=self.factory(lambda: self.ready(
            startupUiReady=False, startupUiPhase="loading")), sleep=advance)
        self.assertEqual(result["status"], "ready")
        self.assertGreaterEqual(self.clock.now, 0.2)
        self.assertEqual(len(self.calls), 1)

    def test_ui_readiness_requires_exact_boolean_and_matching_phase(self):
        for ready, phase in (("true", "start_menu"), (1, "start_menu"), (True, "loading"),
                             (True, "needs_resolution"), (None, None)):
            with self.subTest(ready=ready, phase=phase):
                (self.work / "bridge-ready.json").unlink(missing_ok=True)
                result = self.start(popen=self.factory(lambda: self.ready(
                    startupUiReady=ready, startupUiPhase=phase)))
                self.assertEqual(result["status"], "startup_unknown")
                self.assertFalse(result["ready"])

    def test_launch_exception_restores_original(self):
        self.config.write_bytes(b"old")
        def fail(*args, **kwargs):
            raise OSError("fake spawn error")
        result = self.start(popen=fail)
        self.assertEqual(result["status"], "startup_error")
        self.assertIn("fake spawn error", result["error"])
        self.assertEqual(self.config.read_bytes(), b"old")

    def test_external_edit_preserved(self):
        def edit():
            self.ready()
            self.config.write_bytes(b"external")
        result = self.start(popen=self.factory(edit))
        self.assertEqual(result["status"], "ready")
        self.assertEqual(result["configurationRestoration"], "external_change_preserved")
        self.assertFalse(result["ok"])
        self.assertEqual(self.config.read_bytes(), b"external")

    def test_external_edit_during_preparation_not_overwritten(self):
        write = launcher.write_record
        def edit_after_record(path, record):
            write(path, record)
            if record["status"] == "prepared":
                self.config.write_bytes(b"external")
        with patch.object(launcher, "write_record", side_effect=edit_after_record):
            result = self.start()
        self.assertEqual(result["status"], "startup_error")
        self.assertEqual(result["configurationRestoration"], "external_change_preserved")
        self.assertEqual(self.config.read_bytes(), b"external")
        self.assertEqual(self.calls, [])

    def test_restore_error_is_not_overall_success(self):
        with patch.object(launcher, "restore_config", side_effect=OSError("fake restore denied")):
            result = self.start()
        self.assertTrue(result["ready"])
        self.assertFalse(result["ok"])
        self.assertEqual(result["configurationRestoration"], "restore_error")
        with patch.object(launcher, "WORK", self.work), patch.object(launcher, "start", return_value=result), \
                contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(launcher.main(["start", "--player", "2"]), 1)

    def test_restore_cli_reports_conflict(self):
        result = self.start()
        self.config.write_bytes(b"external")
        with patch.object(launcher, "WORK", self.work), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(launcher.main(["restore", "--record", result["record"]]), 1)

    def test_restore_interrupted_launch_from_record(self):
        self.config.write_bytes(b"before")
        result = self.start()
        self.config.write_bytes(launcher.seat_bytes(2))
        restored = launcher.restore(self.work, result["record"])
        self.assertEqual(restored["configurationRestoration"], "restored")
        self.assertEqual(self.config.read_bytes(), b"before")
        again = launcher.restore(self.work, result["record"])
        self.assertEqual(again["configurationRestoration"], "already_restored")

    def test_restore_absent_and_external_edit(self):
        result = self.start()
        self.config.write_bytes(launcher.seat_bytes(2))
        restored = launcher.restore(self.work, result["record"])
        self.assertEqual(restored["configurationRestoration"], "restored")
        self.assertFalse(self.config.exists())
        self.config.write_bytes(b"external")
        restored = launcher.restore(self.work, result["record"])
        self.assertEqual(restored["configurationRestoration"], "external_change_preserved")
        self.assertEqual(self.config.read_bytes(), b"external")

    def test_corrupt_backup_is_not_applied(self):
        self.config.write_bytes(b"before")
        result = self.start()
        self.config.write_bytes(launcher.seat_bytes(2))
        (Path(result["record"]).parent / "controlled-seat.before").write_bytes(b"bad")
        with self.assertRaisesRegex(ValueError, "backup hash"):
            launcher.restore(self.work, result["record"])
        self.assertEqual(self.config.read_bytes(), launcher.seat_bytes(2))

    def test_invalid_player_timeout_rejected_before_write(self):
        for player in (True, 0, 255, "2", 2.0):
            with self.subTest(player=player), self.assertRaises(ValueError):
                launcher.start(self.work, player, probe=lambda: [])
        for timeout in (0, -1, float("inf"), float("nan"), True):
            with self.subTest(timeout=timeout), self.assertRaises(ValueError):
                self.start(timeout=timeout)
        self.assertFalse((self.work / "launches").exists())


if __name__ == "__main__":
    unittest.main()
