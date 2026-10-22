import contextlib
import copy
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from sdk import PolytopiaSDK, SDKError
import agentcli
from bridge_transport import Bridge


class LifecycleTransport:
    def __init__(self):
        self.calls = []
        self.status = "submitted"
        self.action_error = None
        self.exited = False
        self.last = None
        self.other_seat = False
        self.quit_scheduled = True
        self.durable = None

    def request(self, command, timeout=None, **args):
        self.calls.append((command, copy.deepcopy(args)))
        if command == "world":
            return {"ok": True, "controlledIdentityVerified": True, "localPlayerId": 1,
                    "episodeId": "original", "state": {"streamId": "original-stream"}}
        if command == "game_lifecycle":
            self.last = dict(args)
            if self.action_error:
                raise self.action_error
        args = self.last
        return {"schema": "game-lifecycle-1", "ok": True, "status": self.status,
                "operationId": args["operationId"], "operation": args["operation"],
                "controlledPlayerId": 2 if self.other_seat else 1,
                "pid": 123, "callbackSubmitted": True,
                "quitScheduled": args["operation"] == "quit" and self.quit_scheduled, "saved": args["operation"] == "save"}

    def lifecycle_receipt(self, operation_id):
        return copy.deepcopy(self.durable)

    def bound_process_exited(self, pid):
        return self.exited and pid == 123


class LifecycleSDKTests(unittest.TestCase):
    def test_delayed_quit_query_uses_durable_progress_after_process_exit(self):
        with tempfile.TemporaryDirectory() as folder:
            t = LifecycleTransport()
            t.quit_scheduled = False
            session = Path(folder) / "session.json"
            with PolytopiaSDK(t, player_id=1, session_path=session) as sdk:
                first = sdk.quit_game()
                self.assertFalse(first["native"]["quitScheduled"])
            t.durable = dict(first["native"], quitScheduled=True, phase="quit_requested", saved=True)
            t.exited = True
            with PolytopiaSDK(t, player_id=1, session_path=session) as sdk:
                final = sdk.resume_pending(timeout=0)
                self.assertEqual(final["status"], "confirmed")
                self.assertTrue(final["processExited"])
                self.assertTrue(final["native"]["saved"])
                self.assertEqual(sdk._pending_writes, [])
            self.assertEqual([c for c, _ in t.calls], ["game_lifecycle"])

    def test_durable_receipt_rejects_another_process_start_or_operation(self):
        with tempfile.TemporaryDirectory() as folder:
            b = object.__new__(Bridge)
            b.work = Path(folder)
            b._bound_pid, b._bound_started = 123, "original-start"
            op = "a" * 32
            directory = b.work / "lifecycle-receipts"
            directory.mkdir()
            path = directory / (op + ".json")
            valid = {"schema":"game-lifecycle-1", "pid":123, "bridgeStarted":"original-start",
                     "operationId":op, "operation":"quit", "phase":"quit_requested", "quitScheduled":True}
            path.write_text(json.dumps(valid), encoding="utf-8")
            self.assertEqual(b.lifecycle_receipt(op), valid)
            for overrides in [{"pid":124}, {"bridgeStarted":"new-start"}, {"operationId":"b"*32},
                              {"phase":"saving"}, {"quitScheduled":False}, {"pid":True}]:
                path.write_text(json.dumps(dict(valid, **overrides)), encoding="utf-8")
                self.assertIsNone(b.lifecycle_receipt(op))
            self.assertIsNone(b.lifecycle_receipt("../" + op))
            path.write_text("{", encoding="utf-8")
            self.assertIsNone(b.lifecycle_receipt(op))

    def test_load_sends_formal_command_and_wait_queries_only(self):
        t = LifecycleTransport()
        sdk = PolytopiaSDK(t, player_id=1)
        receipt = sdk.load_game()
        self.assertEqual(receipt["status"], "submitted")
        self.assertFalse(receipt["retryAllowed"])
        t.status = "confirmed"
        self.assertEqual(sdk.wait_lifecycle(receipt)["status"], "confirmed")
        self.assertEqual([c for c, _ in t.calls], ["game_lifecycle", "game_lifecycle_status"])
        self.assertEqual(sdk._pending_writes, [])

    def test_refuse_load_with_old_bound_episode(self):
        t = LifecycleTransport()
        sdk = PolytopiaSDK(t, player_id=1, episode_id="old")
        with self.assertRaisesRegex(SDKError, "new SDK session"):
            sdk.load_game()
        self.assertEqual(t.calls, [])

    def test_save_binds_episode_and_does_not_bypass_pending_action(self):
        t = LifecycleTransport()
        sdk = PolytopiaSDK(t, player_id=1)
        sdk.save_game()
        self.assertEqual(t.last["expectedEpisodeId"], "original")
        with self.assertRaises(SDKError):
            sdk.return_to_menu()
        self.assertEqual(sum(c == "game_lifecycle" for c, _ in t.calls), 1)

    def test_uncertain_submission_can_resume_by_durable_operation_id_without_replay(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "session.json"
            t = LifecycleTransport()
            t.action_error = TimeoutError("lost reply")
            with PolytopiaSDK(t, player_id=1, session_path=path) as sdk:
                receipt = sdk.load_game()
                self.assertEqual(receipt["status"], "unknown")
            t.status = "confirmed"
            with PolytopiaSDK(t, player_id=1, session_path=path) as sdk:
                self.assertEqual(sdk.resume_pending()["status"], "confirmed")
                self.assertEqual(sdk._pending_writes, [])
            self.assertEqual(sum(c == "game_lifecycle" for c, _ in t.calls), 1)

    def test_quit_only_confirms_when_original_process_exited(self):
        t = LifecycleTransport()
        sdk = PolytopiaSDK(t, player_id=1)
        receipt = sdk.quit_game()
        result = sdk.wait_lifecycle(receipt, timeout=0)
        self.assertEqual(result["status"], "timeout")
        t.exited = True
        result = sdk.wait_lifecycle(receipt, timeout=0)
        self.assertEqual(result["status"], "confirmed")
        self.assertTrue(result["processExited"])
        self.assertEqual(result["exitCause"], "not_verified")

    def test_wrong_seat_cannot_clear_intent(self):
        t = LifecycleTransport()
        sdk = PolytopiaSDK(t, player_id=1)
        receipt = sdk.load_game()
        t.other_seat = True
        t.status = "confirmed"
        self.assertEqual(sdk.wait_lifecycle(receipt, timeout=0)["status"], "unknown")
        self.assertEqual(len(sdk._pending_writes), 1)

    def test_invalid_hotseat_ids_never_write(self):
        t = LifecycleTransport()
        sdk = PolytopiaSDK(t, player_id=1)
        for game_id in [None, "abc", "00000000-0000-0000-0000-000000000000"]:
            with self.subTest(game_id=game_id), self.assertRaises(SDKError):
                sdk.load_game(mode="hotseat", game_id=game_id)
        self.assertEqual(t.calls, [])

    def test_invalid_timeouts_never_write(self):
        t = LifecycleTransport()
        sdk = PolytopiaSDK(t, player_id=1)
        for timeout in [True, -1, float("nan"), float("inf")]:
            with self.subTest(timeout=timeout), self.assertRaises(SDKError):
                sdk.save_game(timeout=timeout)
        self.assertEqual(t.calls, [])

    def test_exit_probe_unknown_and_wrong_pid_are_not_success(self):
        bridge = object.__new__(Bridge)
        bridge._bound_pid = 123
        bridge.liveness = lambda: True
        self.assertFalse(bridge.bound_process_exited(123))
        self.assertIsNone(bridge.bound_process_exited(124))
        bridge.liveness = lambda: False
        self.assertTrue(bridge.bound_process_exited(123))

    def test_cli_load_save_wait_and_quit_are_distinct_from_ticket_resume(self):
        for command in ["load_game", "save_game", "return_to_menu", "quit_game", "saved_games"]:
            with self.subTest(command=command):
                t = LifecycleTransport()
                t.status = "confirmed" if command != "quit_game" else "submitted"
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    code = agentcli.main(["--player", "1", command], sdk_factory=lambda **kw: PolytopiaSDK(t, **kw))
                result = json.loads(output.getvalue())
                self.assertEqual(code, 6 if command == "quit_game" else 0)
                self.assertEqual(result["native"]["operation"], {"load_game": "load_singleplayer", "save_game": "save",
                    "return_to_menu": "return_to_menu", "quit_game": "quit", "saved_games": "open_hotseat"}[command])


if __name__ == "__main__":
    unittest.main()
