"""Crash/reconnect tests for optional durable SDK sessions, with no game process."""
import copy
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

WORK = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORK))
from sdk import PolytopiaSDK, SDKError
from sdk_state import SessionStore, SessionStateError
from test_sdk import Fake, world
from test_sdk_setup import MenuTransport, menu


class SessionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="test-state-", dir=WORK)
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "session.json"
        self.fake = Fake()

    def sdk(self, **kwargs):
        value = PolytopiaSDK(kwargs.pop("transport", self.fake), player_id=kwargs.pop("player_id", 2),
                            session_path=self.path, **kwargs)
        self.addCleanup(value.close)
        return value

    def action(self, sdk):
        return next(row for row in sdk.list_actions()["actions"] if row["args"].get("name") == "research")

    def state(self):
        return json.loads(self.path.read_text(encoding="utf-8"))

    def writes(self):
        return [row for row in self.fake.calls if row[0] in {"action", "popup", "handoff_start"}
                or row[0] in {"leaderboard", "tribe_info"} and row[1].get("mode") != "read"]

    def test_context_releases_real_lock_and_preserves_lock_file(self):
        with self.sdk() as sdk:
            with self.assertRaises(SDKError) as error:
                self.sdk()
            self.assertEqual(error.exception.code, "SESSION_LOCKED")
        self.assertTrue(self.path.with_name("session.json.lock").exists())
        with self.sdk():
            pass

    def test_child_process_lock_is_exclusive(self):
        sdk = self.sdk()
        code = "from sdk_state import SessionStore,SessionStateError; import sys\ntry:\n s=SessionStore(sys.argv[1]); s.close(); sys.exit(4)\nexcept SessionStateError as e:\n print(e.code); sys.exit(0 if e.code=='SESSION_LOCKED' else 3)"
        result = subprocess.run([sys.executable, "-c", code, str(self.path)], cwd=WORK,
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("SESSION_LOCKED", result.stdout)
        sdk.close()

    def test_pending_ticket_roundtrip_and_query_only_recovery(self):
        first = self.sdk()
        reply = first.execute(self.action(first))
        binding = reply["binding"]
        self.assertEqual(self.state()["pendingWrites"][0]["ticket"], 8)
        first.close()
        self.fake.calls.clear()
        second = self.sdk()
        self.assertEqual(second.unresolved, {"ticket": 8, "binding": binding})
        result = second.resume_pending(timeout=0)
        self.assertEqual(result["status"], "confirmed")
        self.assertEqual(result["ticket"], 8)
        self.assertEqual(result["binding"], binding)
        self.assertEqual([row[0] for row in self.fake.calls], ["settlement"])
        self.assertIsNone(self.state()["unresolved"])
        self.assertEqual(self.state()["pendingWrites"], [])

    def test_intent_exists_on_disk_before_transport_and_timeout_blocks_all_writes(self):
        sdk = self.sdk(); action = self.action(sdk)
        def lost(args):
            state = self.state()
            self.assertEqual(state["pendingWrites"][0]["state"], "uncertain")
            self.assertIsNone(state["pendingWrites"][0]["ticket"])
            raise TimeoutError("lost original response")
        self.fake.handlers["action"] = lost
        result = sdk.execute(action)
        self.assertEqual(result["status"], "unknown")
        sdk.close()
        restored = self.sdk()
        self.fake.calls.clear()
        result = restored.resume_pending(timeout=0)
        self.assertEqual(result["errorCode"], "RECOVERY_REQUIRED")
        self.assertFalse(result["retryAllowed"])
        self.assertEqual(self.fake.calls, [])
        for command, args in [("popup", {"op": "choose"}), ("popup", {"op": "dismiss_resolved_reward"}),
                ("handoff_start", {}), ("leaderboard", {"mode": "open"}), ("leaderboard", {"mode": "close"}),
                ("tribe_info", {"mode": "open"}), ("tribe_info", {"mode": "close"})]:
            with self.subTest(command=command, args=args), self.assertRaises(SDKError) as error:
                restored._request(command, **args)
            self.assertEqual(error.exception.code, "RECOVERY_REQUIRED")
        self.assertEqual(self.writes(), [])

    def test_crash_before_reply_preserves_intent_and_os_releases_lock(self):
        code = "import os,sys; from sdk import PolytopiaSDK; sys.path.insert(0,'tests'); from test_sdk import Fake\nf=Fake(); f.handlers['action']=lambda args: os._exit(23)\ns=PolytopiaSDK(f,player_id=2,session_path=sys.argv[1]); a=next(a for a in s.list_actions()['actions'] if a['args'].get('name')=='research'); s.execute(a)"
        result = subprocess.run([sys.executable, "-c", code, str(self.path)], cwd=WORK,
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 23, result.stdout + result.stderr)
        self.assertEqual(self.state()["pendingWrites"][0]["state"], "uncertain")
        with self.sdk() as sdk:
            self.assertEqual(sdk.resume_pending(timeout=0)["errorCode"], "RECOVERY_REQUIRED")
        self.assertEqual(self.fake.calls, [])

    def test_read_observation_does_not_clear_unknown_intent(self):
        sdk = self.sdk(); action = self.action(sdk)
        self.fake.handlers["action"] = TimeoutError("lost")
        sdk.execute(action)
        sdk.observe()
        self.assertTrue(self.state()["pendingWrites"])
        with self.assertRaises(SDKError):
            sdk.execute(action)
        self.assertEqual(len(self.writes()), 1)

    def test_missing_ticket_is_not_false_success(self):
        sdk = self.sdk(); action = self.action(sdk)
        self.fake.handlers["action"] = {"ok": True, "accepted": True}
        sdk.execute(action)
        sdk.close()
        self.assertEqual(self.sdk().resume_pending(timeout=0)["errorCode"], "RECOVERY_REQUIRED")

    def test_rejected_dispatch_leaves_no_pending_write(self):
        sdk = self.sdk(); action = self.action(sdk)
        self.fake.handlers["action"] = {"ok": False, "accepted": False, "dispatched": False}
        self.assertEqual(sdk.execute(action)["status"], "rejected")
        self.assertEqual(self.state()["pendingWrites"], [])
        self.assertIsNone(sdk.unresolved)

    def test_pending_or_unknown_settlement_keeps_lock(self):
        sdk = self.sdk(); sdk.execute(self.action(sdk)); sdk.close()
        for status in ("pending", "unknown"):
            with self.subTest(status=status):
                sdk = self.sdk()
                self.fake.handlers["settlement"]["settlementStatus"] = status
                self.fake.handlers["settlement"]["actionCompleted"] = False
                sdk.resume_pending(timeout=0)
                self.assertTrue(self.state()["pendingWrites"])
                sdk.close()
        self.fake.handlers["settlement"]["settlementStatus"] = "complete"
        self.fake.handlers["settlement"]["actionCompleted"] = True
        sdk = self.sdk(); sdk.resume_pending(timeout=0); sdk.close()

    def test_reward_child_is_saved_and_recovered_before_parent(self):
        sdk = self.sdk(); action = self.action(sdk); sdk.execute(action)
        self.fake.handlers["settlement"]["settlementStatus"] = "needs_resolution"
        sdk.wait_result(8, timeout=0)
        self.fake.handlers["action"] = {"ok": True, "accepted": True, "settlementTicket": 9}
        reward = {**action, "args": {"name": "city_reward", "kind": "FutureReward"}, "actionId": "child"}
        with patch.object(sdk, "_revalidate_action", return_value={"binding": action["binding"], "actions": [reward]}):
            sdk.execute(reward)
        self.assertEqual([r["ticket"] for r in self.state()["pendingWrites"]], [8, 9])
        self.assertEqual(sdk.unresolved["ticket"], 8)
        sdk.close()
        self.fake.handlers["settlement"]["settlementStatus"] = "complete"
        self.fake.calls.clear()
        restored = self.sdk(); result = restored.resume_pending(timeout=0)
        self.assertEqual([r[1]["ticket"] for r in self.fake.calls], ["9", "8"])
        self.assertEqual(result["ticket"], 8)
        self.assertIsNone(restored.unresolved)
        self.assertEqual(restored._pending_writes, [])

    def test_unknown_reward_child_never_overwrites_parent_or_can_be_bypassed(self):
        sdk = self.sdk(); action = self.action(sdk); sdk.execute(action)
        self.fake.handlers["settlement"]["settlementStatus"] = "needs_resolution"
        sdk.wait_result(8, timeout=0)
        self.fake.handlers["action"] = TimeoutError("child response lost")
        reward = {**action, "args": {"name": "city_reward", "kind": "FutureReward"}, "actionId": "child"}
        with patch.object(sdk, "_revalidate_action", return_value={"binding": action["binding"], "actions": [reward]}):
            sdk.execute(reward)
        self.assertEqual(sdk.unresolved["ticket"], 8)
        self.fake.handlers["settlement"]["settlementStatus"] = "complete"
        sdk.wait_result(8, timeout=0)
        self.assertIsNone(sdk.unresolved)
        self.assertTrue(sdk._pending_writes)
        self.assertEqual(sdk.resume_pending(timeout=0)["errorCode"], "RECOVERY_REQUIRED")
        with self.assertRaises(SDKError):
            sdk._request("popup", op="choose")

    def test_auxiliary_write_loss_is_persisted_and_never_replayed(self):
        sdk = self.sdk(); sdk.observe()
        self.fake.handlers["leaderboard"] = TimeoutError("view callback response lost")
        with self.assertRaises(TimeoutError):
            sdk.leaderboard("open")
        sdk.close()
        restored = self.sdk(); self.fake.calls.clear()
        self.assertEqual(restored.resume_pending(timeout=0)["errorCode"], "RECOVERY_REQUIRED")
        self.assertEqual(self.fake.calls, [])

    def test_successful_auxiliary_response_is_not_left_uncertain(self):
        sdk = self.sdk(); sdk.observe()
        self.fake.handlers["leaderboard"] = {"ok": True, "status": "opening"}
        sdk.leaderboard("open")
        self.assertEqual(sdk._pending_writes, [])

    def test_setup_recovery_reads_original_and_revision_alone_does_not_clear(self):
        transport = MenuTransport(); sdk = self.sdk(transport=transport, player_id=1)
        transport.reply = TimeoutError("setup response lost")
        sdk.setup_action("select:map_preset:77", expected_setup=sdk.setup_status())
        sdk.close(); transport.state = menu(revision=2)
        restored = self.sdk(transport=transport, player_id=1); transport.calls.clear()
        result = restored.resume_pending(timeout=0)
        self.assertIsNotNone(restored._setup_unresolved)
        self.assertTrue(restored._pending_writes)
        self.assertFalse(any(c == "setup_action" for c, _ in transport.calls))
        self.assertIn(result["status"], ("observed", "timeout"))

    def test_setup_verified_configuration_clears_write_intent(self):
        transport = MenuTransport(); sdk = self.sdk(transport=transport, player_id=1)
        reply = sdk.setup_action("select:map_preset:77", expected_setup=sdk.setup_status())
        self.assertEqual(reply["status"], "configured")
        self.assertEqual(sdk._pending_writes, [])
        self.assertIsNone(sdk._setup_unresolved)

    def test_scope_mismatch_and_corrupt_state_refuse_without_transport(self):
        sdk = self.sdk(); sdk.observe(); sdk.close(); self.fake.calls.clear()
        for kwargs in ({"player_id": 1}, {"episode_id": "other"}):
            with self.subTest(kwargs=kwargs), self.assertRaises(SDKError) as error:
                self.sdk(**kwargs)
            self.assertEqual(error.exception.code, "SESSION_SCOPE_MISMATCH")
        self.path.write_text("{broken", encoding="utf-8")
        with self.assertRaises(SDKError) as error:
            self.sdk()
        self.assertEqual(error.exception.code, "SESSION_INVALID")
        self.assertEqual(self.fake.calls, [])

    def test_stream_change_does_not_drop_original_receipt(self):
        sdk = self.sdk(); sdk.execute(self.action(sdk)); sdk.close()
        self.fake.handlers["world"]["state"]["streamId"] = "new-stream"
        restored = self.sdk()
        with self.assertRaises(SDKError) as error:
            restored.observe()
        self.assertEqual(error.exception.code, "STREAM_CHANGED")
        self.assertEqual(restored.unresolved["ticket"], 8)
        self.assertTrue(self.state()["pendingWrites"])

    def test_close_prevents_all_further_requests(self):
        sdk = self.sdk(); sdk.close()
        for call in (sdk.observe, sdk.setup_status, lambda: sdk._request("world")):
            with self.assertRaises(SDKError) as error:
                call()
            self.assertEqual(error.exception.code, "SESSION_CLOSED")
        self.assertEqual(self.fake.calls, [])

    def test_session_and_log_cannot_share_path(self):
        with self.assertRaises(SDKError) as error:
            self.sdk(log_path=self.path)
        self.assertEqual(error.exception.code, "INVALID_ARGUMENT")
        self.assertFalse(self.path.exists())

    def test_empty_resume_is_observed_without_native_calls(self):
        sdk = self.sdk()
        self.assertEqual(sdk.resume_pending(timeout=0)["status"], "observed")
        self.assertEqual(self.fake.calls, [])

    def test_unchanged_reads_skip_redundant_state_fsync(self):
        sdk = self.sdk()
        with patch.object(sdk._session_store, "save", wraps=sdk._session_store.save) as save:
            sdk.setup_status = lambda: None
            sdk._save_state(); sdk._save_state()
            self.assertEqual(save.call_count, 0)

    def test_write_ahead_io_failure_prevents_transport_write(self):
        sdk = self.sdk(); action = self.action(sdk); self.fake.calls.clear()
        with patch.object(sdk._session_store, "save", side_effect=SessionStateError("SESSION_IO_ERROR", "disk full")):
            with self.assertRaises(SDKError) as error:
                sdk.execute(action)
        self.assertEqual(error.exception.code, "SESSION_IO_ERROR")
        self.assertFalse(error.exception.details["retryAllowed"])
        self.assertEqual(self.writes(), [])

    def test_receipt_io_failure_reports_saved_intent_and_original_ticket(self):
        sdk = self.sdk(); action = self.action(sdk)
        original_save = sdk._session_store.save
        def fail_receipt(state):
            if any(row.get("ticket") == 8 for row in state["pendingWrites"]):
                raise SessionStateError("SESSION_IO_ERROR", "receipt checkpoint failed")
            return original_save(state)
        with patch.object(sdk._session_store, "save", side_effect=fail_receipt):
            with self.assertRaises(SDKError) as error:
                sdk.execute(action)
        self.assertFalse(error.exception.details["retryAllowed"])
        self.assertEqual(error.exception.details["pendingWrites"][0]["ticket"], 8)
        self.assertEqual(error.exception.details["native"]["settlementTicket"], 8)
        self.assertEqual(self.state()["pendingWrites"][0]["state"], "uncertain")
        self.assertEqual(len(self.writes()), 1)

    def test_failed_atomic_replace_preserves_old_json_and_removes_temporary_file(self):
        sdk = self.sdk(); before = self.path.read_bytes()
        with patch("sdk_state.os.replace", side_effect=OSError("replacement blocked")):
            with self.assertRaises(SessionStateError):
                sdk._session_store.save({"schema": "changed"})
        self.assertEqual(self.path.read_bytes(), before)
        self.assertEqual(list(self.path.parent.glob("*.tmp")), [])

    def test_ui_ok_alone_or_unknown_status_never_clears_intent(self):
        for index, reply in enumerate(({"ok": True}, {"ok": True, "status": "unknown", "invokedCallback": True},
                {"ok": True, "status": "pending", "invokedCallback": True},
                {"ok": True, "callbackAttempted": True}, {"ok": True, "invokedHide": True, "callbackError": "failed"})):
            with self.subTest(reply=reply):
                self.fake.handlers["popup"] = reply
                with PolytopiaSDK(self.fake, player_id=2, session_path=self.path.with_name(str(index) + ".json")) as sdk:
                    sdk._request("popup", op="choose")
                    self.assertEqual(sdk._pending_writes[0]["state"], "uncertain")
                    self.assertEqual(sdk.resume_pending(timeout=0)["errorCode"], "RECOVERY_REQUIRED")

    def test_ui_explicit_native_callback_success_is_a_callback_receipt(self):
        sdk = self.sdk(); self.fake.handlers["popup"] = {"ok": True, "invokedCallback": True}
        sdk._request("popup", op="choose")
        self.assertEqual(sdk._pending_writes, [])

    def test_diplomacy_action_is_a_durable_gameplay_write(self):
        sdk = self.sdk(); action = self.action(sdk)
        diplomacy = {**action, "command": "tribe_action", "args": {"operation": "FutureOperation", "opponent": "1"}}
        self.fake.handlers["tribe_action"] = {"ok": True, "nativeExecution": {"accepted": True, "settlementTicket": 8}}
        with patch.object(sdk, "_revalidate_action", return_value={"binding": action["binding"], "actions": [diplomacy]}):
            sdk.execute(diplomacy)
        self.assertEqual(self.state()["pendingWrites"][0]["kind"], "action")
        self.assertEqual(self.state()["pendingWrites"][0]["ticket"], 8)

    def test_original_zero_state_version_is_legal_across_recovery(self):
        self.fake.handlers["world"]["state"]["stateVersion"] = 0
        self.fake.handlers["settlement"]["beforeStateVersion"] = 0
        sdk = self.sdk(); sdk.execute(self.action(sdk)); sdk.close()
        self.assertEqual(self.sdk().resume_pending(timeout=0)["status"], "confirmed")

    def test_read_result_survives_checkpoint_failure_and_new_write_is_blocked(self):
        sdk = self.sdk()
        with patch.object(sdk._session_store, "save", side_effect=SessionStateError("SESSION_IO_ERROR", "disk full")):
            result = sdk.observe()
            self.assertEqual(result["status"], "observed")
            self.assertEqual(result["world"]["episodeId"], "ep-one")
            self.assertEqual(result["stateSaveError"]["errorCode"], "SESSION_IO_ERROR")
            with self.assertRaises(SDKError):
                sdk.leaderboard("open")
        self.assertEqual(self.writes(), [])

    def test_method_exception_is_not_replaced_by_checkpoint_failure(self):
        sdk = self.sdk(); sdk.episode_id = "ep-one"
        original = TimeoutError("original world read failure")
        self.fake.handlers["world"] = original
        with patch.object(sdk._session_store, "save", side_effect=SessionStateError("SESSION_IO_ERROR", "disk full")):
            with self.assertRaises(TimeoutError) as error:
                sdk.observe()
        self.assertIs(error.exception, original)
        self.assertEqual(original.stateSaveError["errorCode"], "SESSION_IO_ERROR")

    def test_context_body_exception_is_not_replaced_by_close_failure(self):
        sdk = self.sdk(); original = RuntimeError("original body failure")
        with patch.object(sdk._session_store, "save", side_effect=SessionStateError("SESSION_IO_ERROR", "disk full")):
            with self.assertRaises(RuntimeError) as error:
                with sdk:
                    sdk.episode_id = "ep-one"
                    raise original
        self.assertIs(error.exception, original)
        self.assertEqual(original.stateSaveError["errorCode"], "SESSION_IO_ERROR")
        with self.sdk():
            pass

    def test_nested_corrupt_json_reports_invalid_and_releases_lock_without_overwrite(self):
        sdk = self.sdk(); sdk.close(); state = self.state()
        state["pendingWrites"] = [{"kind": [], "state": {}, "id": "broken", "command": "popup", "args": {}}]
        self.path.write_text(json.dumps(state), encoding="utf-8"); before = self.path.read_bytes()
        with self.assertRaises(SDKError) as error:
            self.sdk()
        self.assertEqual(error.exception.code, "SESSION_INVALID")
        self.assertEqual(self.path.read_bytes(), before)
        store = SessionStore(self.path); store.close()


if __name__ == "__main__":
    unittest.main()
