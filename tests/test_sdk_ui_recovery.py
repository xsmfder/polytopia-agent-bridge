"""Public UI writes keep matching in-memory/durable outcomes; no game or window."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
import sys

WORK = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORK))
from sdk import PolytopiaSDK, SDKError
from test_sdk import Fake


class UIRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="ui-recovery-", dir=WORK)
        self.addCleanup(self.tmp.cleanup)

    def sdk(self, fake, name, durable=False):
        sdk = PolytopiaSDK(fake, player_id=2,
                          session_path=Path(self.tmp.name) / (name + ".json") if durable else None)
        self.addCleanup(sdk.close)
        return sdk

    @staticmethod
    def popup(fake, kind="BasicPopup"):
        fake.handlers["popup_info"] = {"ok": True, "showing": True, "popupType": kind}
        return copy.deepcopy(fake.handlers["popup_info"])

    def test_popup_without_callback_proof_is_unknown_in_both_modes(self):
        replies = ({"ok": True}, {"ok": False, "error": "callback failed"},
                   {"ok": True, "invokedCallback": True, "callbackError": "failed after effect"},
                   {"ok": False, "callbackAttempted": False, "invokedCallback": True},
                   {"ok": False, "dispatched": False, "callbackAttempted": True})
        for durable in (False, True):
            for index, reply in enumerate(replies):
                with self.subTest(durable=durable, reply=reply):
                    fake = Fake(); sdk = self.sdk(fake, f"popup-{durable}-{index}", durable)
                    popup = self.popup(fake); fake.handlers["popup"] = reply
                    result = sdk.choose_popup(-1, expected_popup=popup)
                    self.assertEqual(result["status"], "unknown")
                    self.assertIs(result["retryAllowed"], False)
                    self.assertEqual(sdk._pending_writes[0]["state"], "uncertain")
                    with self.assertRaises(SDKError): sdk.choose_popup(-1, expected_popup=popup)
                    self.assertEqual(len([c for c, _ in fake.calls if c == "popup"]), 1)

    def test_positive_popup_callback_and_consistent_refusal_release_intent(self):
        for durable in (False, True):
            for index, (reply, expected) in enumerate((
                    ({"ok": True, "invokedCallback": True}, "submitted"),
                    ({"ok": False, "callbackAttempted": False, "outcome": "not_dispatched"}, "rejected"))):
                with self.subTest(durable=durable, reply=reply):
                    fake = Fake(); sdk = self.sdk(fake, f"positive-{durable}-{index}", durable)
                    popup = self.popup(fake); fake.handlers["popup"] = reply
                    self.assertEqual(sdk.choose_popup(-1, expected_popup=popup)["status"], expected)
                    self.assertEqual(sdk._pending_writes, [])

    def test_resolved_reward_uses_same_callback_evidence(self):
        for durable in (False, True):
            for index, reply in enumerate(({"ok": True}, {"ok": False, "error": "hide failed"},
                    {"ok": False, "outcome": "not_dispatched", "dismissRequested": True, "rewardResubmitted": False})):
                with self.subTest(durable=durable, reply=reply):
                    fake = Fake(); sdk = self.sdk(fake, f"reward-{durable}-{index}", durable)
                    popup = self.popup(fake, "RewardPopup"); fake.handlers["popup"] = reply
                    self.assertEqual(sdk.dismiss_resolved_reward("Workshop", expected_popup=popup)["status"], "unknown")
                    self.assertEqual(sdk._pending_writes[0]["state"], "uncertain")

    def test_memory_view_recovers_only_matching_passive_read(self):
        for command, schema in (("tribe_info", "tribe-info-1"), ("leaderboard", "leaderboard-1")):
            with self.subTest(command=command):
                fake = Fake(); sdk = self.sdk(fake, command)
                native = {"ok": False, "status": "unknown", "outcome": "unknown", "schema": schema,
                          "episodeId": "ep-one", "controlledPlayerId": 2, "playerId": 1,
                          "streamId": "stream-one"}
                fake.handlers[command] = native
                call = (lambda mode: sdk.tribe_info(1, mode=mode)) if command == "tribe_info" else sdk.leaderboard
                self.assertEqual(call("open")["status"], "unknown")
                intent = copy.deepcopy(sdk._pending_writes[0])
                wire = next(args for cmd, args in fake.calls if cmd == command)
                self.assertEqual(wire["viewRequestId"], intent["id"])
                sdk.observe(); self.assertEqual(sdk._pending_writes, [intent])
                with self.assertRaises(SDKError): call("open")
                fake.handlers[command] = {**native, "ok": True, "status": "ok", "outcome": "confirmed",
                    "viewRequestId": "wrong-id", "viewOperation": "open", "viewEffectConfirmed": True}
                call("read"); self.assertEqual(sdk._pending_writes, [intent])
                fake.handlers[command]["viewRequestId"] = intent["id"]
                call("read"); self.assertEqual(sdk._pending_writes, [])
                self.assertEqual([args["mode"] for cmd, args in fake.calls if cmd == command], ["open", "read", "read"])

    def test_view_contradictory_refusal_does_not_clear_durable_intent(self):
        fake = Fake(); sdk = self.sdk(fake, "contradictory-view", True)
        fake.handlers["tribe_info"] = lambda args: {"ok": False, "schema": "tribe-info-1",
            "status": "unknown", "episodeId": "ep-one", "controlledPlayerId": 2, "playerId": 1,
            "viewRequestId": args["viewRequestId"], "viewOperation": "open", "callbackCompleted": True,
            "callbackAttempted": False, "outcome": "invoked"}
        sdk.tribe_info(1, mode="open")
        self.assertEqual(sdk._pending_writes[0]["state"], "uncertain")
        state = json.loads((Path(self.tmp.name) / "contradictory-view.json").read_text(encoding="utf-8"))
        self.assertEqual(state["pendingWrites"], sdk._pending_writes)

    def test_malformed_popup_response_keeps_memory_intent(self):
        fake = Fake(); sdk = self.sdk(fake, "invalid-popup")
        popup = self.popup(fake); fake.handlers["popup"] = []
        result = sdk.choose_popup(-1, expected_popup=popup)
        self.assertEqual(result["status"], "unknown")
        self.assertEqual(sdk._pending_writes[0]["state"], "uncertain")

    def test_popup_wrong_response_scope_cannot_report_submission_or_unlock(self):
        for index, extra in enumerate(({"episodeId": "another-game"}, {"controlledPlayerId": 1},
                                       {"streamId": "another-stream"})):
            with self.subTest(extra=extra):
                fake = Fake(); sdk = self.sdk(fake, f"wrong-scope-{index}")
                popup = self.popup(fake)
                fake.handlers["popup"] = {"ok": True, "invokedCallback": True, **extra}
                self.assertEqual(sdk.choose_popup(-1, expected_popup=popup)["status"], "unknown")
                self.assertEqual(sdk._pending_writes[0]["state"], "uncertain")

    def test_handoff_lost_reply_cannot_be_submitted_twice_in_memory(self):
        fake = Fake(); sdk = self.sdk(fake, "handoff")
        status = {"ok": True, "configuredPlayerId": 2, "targetPlayerId": 2, "canStart": True, "handoffId": "h1"}
        fake.handlers["handoff_start"] = TimeoutError("reply lost")
        with self.assertRaises(TimeoutError): sdk.handoff_start(status)
        with self.assertRaises(SDKError): sdk.handoff_start(status)
        self.assertEqual(len([c for c, _ in fake.calls if c == "handoff_start"]), 1)

    def test_native_popup_refusal_codes_require_actual_noninvocation_evidence(self):
        for durable in (False, True):
            for index, code in enumerate(("POPUP_BUTTON_UNAVAILABLE", "POPUP_CALLBACK_UNAVAILABLE",
                    "POPUP_BUTTON_AMBIGUOUS", "REWARD_HIDE_UNAVAILABLE", "REWARD_DISPLAY_NOT_CURRENT")):
                for proof in (False, True):
                    with self.subTest(durable=durable, code=code, proof=proof):
                        fake = Fake(); sdk = self.sdk(fake, f"native-{durable}-{index}-{proof}", durable)
                        popup = self.popup(fake)
                        reply = {"ok": False, "errorCode": code}
                        if proof:
                            reply.update(callbackAttempted=False, dispatched=False, accepted=False, outcome="not_dispatched")
                        fake.handlers["popup"] = reply
                        self.assertEqual(sdk.choose_popup(-1, expected_popup=popup)["status"],
                                         "rejected" if proof else "unknown")
                        self.assertEqual(bool(sdk._pending_writes), not proof)

    def test_native_handoff_errors_preserve_attempt_phase_across_reconnect(self):
        for submitted in (False, True):
            with self.subTest(submitted=submitted):
                name = f"native-handoff-{submitted}"
                fake = Fake(); sdk = self.sdk(fake, name, True)
                status = {"ok": True, "configuredPlayerId": 2, "targetPlayerId": 2,
                          "canStart": True, "handoffId": "h1"}
                reply = {"ok": False, "accepted": False, "callbackSubmitted": submitted,
                         "callbackAttempted": submitted, "outcome": "unknown" if submitted else "not_dispatched",
                         "errorCode": "HANDOFF_INVOCATION_UNCERTAIN" if submitted else "STALE_HANDOFF"}
                if not submitted:
                    reply["dispatched"] = False
                fake.handlers["handoff_start"] = reply
                sdk.handoff_start(status)
                self.assertEqual(bool(sdk._pending_writes), submitted)
                sdk.close()
                recovered = self.sdk(fake, name, True)
                recovered.observe()
                self.assertEqual(bool(recovered._pending_writes), submitted)
                if submitted:
                    with self.assertRaises(SDKError): recovered.handoff_start(status)
                self.assertEqual(len([c for c, _ in fake.calls if c == "handoff_start"]), 1)


if __name__ == "__main__":
    unittest.main()
