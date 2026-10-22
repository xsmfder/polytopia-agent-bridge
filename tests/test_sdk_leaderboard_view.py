"""Leaderboard navigation receipts share only proven UI recovery semantics."""
import copy
import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest

WORK = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORK))
from sdk import PolytopiaSDK, SDKError
from test_sdk import Fake
from agentcli import main


class LeaderboardViewTests(unittest.TestCase):
    def test_cli_pending_close_is_not_a_completed_exit(self):
        for command, schema, argv in [
                ("leaderboard", "leaderboard-1", ["leaderboard", "--mode", "close"]),
                ("tribe_info", "tribe-info-1", ["tribe_info", "1", "--mode", "close"])]:
            with self.subTest(command=command):
                self.path = Path(self.tmp.name) / (command + "-cli.json")
                sdk = self.sdk()
                self.fake.handlers[command] = lambda args: {
                    **self.native(schema=schema, status="closing", playerId=1),
                    "viewRequestId": args["viewRequestId"], "viewOperation": "close",
                    "callbackCompleted": True, "outcome": "invoked"}
                with contextlib.redirect_stdout(io.StringIO()) as output:
                    code = main(["--player", "2", *argv], sdk_factory=lambda **kw: sdk)
                self.assertEqual(code, 6)
                self.assertEqual(json.loads(output.getvalue())["status"], "closing")

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="leaderboard-test-", dir=WORK)
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "session.json"
        self.fake = Fake()

    def sdk(self, durable=True):
        value = PolytopiaSDK(self.fake, player_id=2,
                            session_path=self.path if durable else None)
        self.addCleanup(value.close)
        return value

    @staticmethod
    def native(**extra):
        return {"ok": True, "schema": "leaderboard-1", "status": "ok",
                "episodeId": "ep-one", "controlledPlayerId": 2,
                "streamId": "stream-one", "rows": [], **extra}

    def unknown(self, sdk, mode="open"):
        self.fake.handlers["leaderboard"] = self.native(ok=False, status="unknown", outcome="unknown")
        result = sdk.leaderboard(mode)
        self.assertEqual(result["status"], "unknown")
        self.assertFalse(result["ok"])
        return copy.deepcopy(sdk._pending_writes[0])

    def evidence(self, intent):
        mode = intent["args"]["mode"]
        return self.native(status="ok" if mode == "open" else "closed",
            viewRequestId=intent["id"], viewOperation=mode, viewEffectConfirmed=True,
            callbackCompleted=False, outcome="confirmed")

    def test_wire_receipt_identity_precedes_dispatch_and_preserves_arguments(self):
        sdk = self.sdk()
        def reply(args):
            intent = json.loads(self.path.read_text(encoding="utf-8"))["pendingWrites"][0]
            self.assertEqual(args["viewRequestId"], intent["id"])
            self.assertNotIn("viewRequestId", intent["args"])
            self.assertEqual({k: v for k, v in args.items() if k != "viewRequestId"}, intent["args"])
            self.assertEqual(intent["args"]["expectedEpisodeId"], "ep-one")
            self.assertEqual(intent["args"]["expectedStreamId"], "stream-one")
            return self.native(status="opening", viewRequestId=args["viewRequestId"],
                viewOperation="open", callbackCompleted=True, outcome="invoked")
        self.fake.handlers["leaderboard"] = reply
        self.assertEqual(sdk.leaderboard("open")["status"], "opening")
        self.assertEqual(sdk._pending_writes, [])
        count = len(self.fake.calls)
        with self.assertRaises(SDKError):
            sdk._request("leaderboard", mode="open", viewRequestId="caller-id")
        self.assertEqual(len(self.fake.calls), count)

    def test_nondurable_navigation_sends_request_identity(self):
        sdk = self.sdk(False)
        self.fake.handlers["leaderboard"] = lambda args: self.native(status="opening",
            viewRequestId=args["viewRequestId"], viewOperation="open", callbackCompleted=True, outcome="invoked")
        self.assertEqual(sdk.leaderboard("open")["status"], "opening")
        args = next(args for command, args in self.fake.calls if command == "leaderboard")
        self.assertTrue(args["viewRequestId"])

    def test_closing_is_pending_and_read_never_repeats_navigation(self):
        sdk = self.sdk()
        self.fake.handlers["leaderboard"] = lambda args: self.native(status="closing",
            viewRequestId=args["viewRequestId"], viewOperation="close", callbackCompleted=True, outcome="invoked")
        result = sdk.leaderboard("close")
        self.assertEqual(result["status"], "closing")
        self.assertNotEqual(result["status"], "closed")
        self.assertEqual(sdk._pending_writes, [])
        self.fake.handlers["leaderboard"] = self.native(status="historical", fresh=False)
        sdk.leaderboard()
        self.assertEqual([args["mode"] for command, args in self.fake.calls if command == "leaderboard"],
                         ["close", "read"])

    def test_unknown_open_survives_restart_and_only_matching_capture_recovers(self):
        first = self.sdk()
        intent = self.unknown(first)
        first.observe()
        self.assertEqual(first._pending_writes, [intent])
        first.close()
        restored = self.sdk()
        self.fake.handlers["leaderboard"] = self.evidence(intent)
        self.assertEqual(restored.leaderboard()["status"], "ok")
        self.assertEqual(restored._pending_writes, [])
        self.assertEqual([args["mode"] for command, args in self.fake.calls if command == "leaderboard"],
                         ["open", "read"])

    def test_observe_details_uses_same_passive_recovery(self):
        sdk = self.sdk()
        intent = self.unknown(sdk)
        self.fake.handlers["leaderboard"] = self.evidence(intent)
        result = sdk.observe(details=True)
        self.assertEqual(result["status"], "observed")
        self.assertEqual(sdk._pending_writes, [])
        self.assertEqual([args["mode"] for command, args in self.fake.calls if command == "leaderboard"],
                         ["open", "read"])

    def test_native_unavailable_before_first_open_is_not_a_stream_change(self):
        sdk = self.sdk()
        # Exact native no-capture shape observed in the restored solo game.
        response = {"ok": False, "schema": "leaderboard-1", "status": "unavailable",
            "reason": "no native leaderboard captured in this game; open first",
            "outcome": "not_dispatched", "callbackAttempted": False,
            "viewRequestId": None, "errorCode": "REQUEST_FAILED"}
        self.fake.handlers["leaderboard"] = response
        observed = sdk.observe(details=True)
        self.assertEqual(observed["status"], "observed")
        self.assertEqual(observed["leaderboard"], response)
        self.assertEqual(sdk.leaderboard()["status"], "unavailable")
        intent = self.unknown(sdk)
        self.fake.handlers["leaderboard"] = response
        sdk.observe(details=True)
        self.assertEqual(sdk._pending_writes, [intent])

    def test_hotseat_recap_unavailable_blank_navigation_id_preserves_decision(self):
        # Actual P2/T30 rejection recap returned this unavailable shape: an empty
        # request id, no stream, and no navigation dispatch. Keep the real popup.
        sdk = self.sdk()
        self.fake.handlers["world"].update(myTurn=False, currentPlayerId=1,
            canAct=False, popupNeedsChoice=False)
        self.fake.handlers["handoff_status"] = {"ok": True, "phase": "needs_decision",
            "needsDecision": True, "overlayActive": False, "overlayPresented": False,
            "configuredPlayerId": 2, "targetPlayerId": 2, "recapping": True}
        popup = {"ok": True, "showing": True, "popupType": "BasicPopupLegacy",
            "title": "Peace treaty rejected", "body": "Bardur has rejected the peace treaty you offered.",
            "buttons": {"buttons": [{"id": "-1", "closesPopup": "True"}]}}
        self.fake.handlers["popup_info"] = popup
        response = {"ok": False, "schema": "leaderboard-1", "status": "unavailable",
            "reason": "not controlled player's turn", "outcome": "not_dispatched",
            "callbackAttempted": False, "viewRequestId": "", "errorCode": "REQUEST_FAILED"}
        self.fake.handlers["leaderboard"] = response
        observed = sdk.observe(details=True)
        self.assertEqual(observed["status"], "needs_resolution")
        self.assertEqual(observed["popup"], popup)
        self.assertEqual(observed["leaderboard"], response)
        self.assertEqual([c for c, _ in self.fake.calls].count("popup"), 0)
        self.assertEqual(sdk.choose_popup(-1, expected_popup=popup)["status"], "submitted")
        self.assertEqual([a["op"] for c, a in self.fake.calls if c == "popup"], ["choose"])

    def test_blank_unavailable_id_never_clears_unknown_navigation(self):
        sdk = self.sdk()
        intent = self.unknown(sdk)
        self.fake.handlers["leaderboard"] = {"ok": False, "schema": "leaderboard-1",
            "status": "unavailable", "viewRequestId": "", "outcome": "not_dispatched",
            "callbackAttempted": False}
        sdk.observe(details=True)
        self.assertEqual(sdk._pending_writes, [intent])
        for extra in ({"streamId": "foreign"}, {"viewOperation": "open"},
                      {"viewRequestId": "different-attempt"}, {"viewEffectConfirmed": True}):
            with self.subTest(extra=extra):
                self.fake.handlers["leaderboard"].update(extra)
                with self.assertRaises(SDKError) as error:
                    sdk.observe(details=True)
                self.assertEqual(error.exception.code, "PUBLIC_DISPLAY_SCOPE_CHANGED")
                self.assertEqual(sdk._pending_writes, [intent])
                self.fake.handlers["leaderboard"] = {"ok": False, "schema": "leaderboard-1",
                    "status": "unavailable", "viewRequestId": "", "outcome": "not_dispatched",
                    "callbackAttempted": False}

    def test_unavailable_navigation_identity_still_requires_matching_stream(self):
        sdk = self.sdk()
        for extra in ({"viewRequestId": "original-attempt"}, {"viewOperation": "close"},
                      {"streamId": "wrong-stream"}, {"viewEffectConfirmed": True}):
            with self.subTest(extra=extra):
                self.fake.handlers["leaderboard"] = {"ok": False, "schema": "leaderboard-1",
                    "status": "unavailable", "viewRequestId": None, **extra}
                with self.assertRaises(SDKError) as error:
                    sdk.observe(details=True)
                self.assertEqual(error.exception.code, "PUBLIC_DISPLAY_SCOPE_CHANGED")

    def test_close_effect_can_recover_after_callback_exception(self):
        sdk = self.sdk()
        intent = self.unknown(sdk, "close")
        self.fake.handlers["leaderboard"] = self.evidence(intent)
        self.assertEqual(sdk.leaderboard()["status"], "closed")
        self.assertEqual(sdk._pending_writes, [])

    def test_direct_closed_proof_ends_only_current_intent(self):
        sdk = self.sdk()
        self.fake.handlers["leaderboard"] = lambda args: self.native(status="closed",
            viewRequestId=args["viewRequestId"], viewOperation="close", callbackCompleted=False,
            outcome="confirmed", viewEffectConfirmed=True)
        self.assertEqual(sdk.leaderboard("close")["status"], "closed")
        self.assertEqual(sdk._pending_writes, [])

    def test_wrong_or_historical_evidence_does_not_unlock(self):
        for index, change in enumerate([
                {"viewRequestId": "other"}, {"viewOperation": "close"},
                {"schema": "tribe-info-1"}, {"episodeId": "other"}, {"controlledPlayerId": 1},
                {"streamId": "other"}, {"streamId": None}, {"viewEffectConfirmed": False},
                {"status": "historical"}, {"status": None}, {"status": "opening"}, {"status": "closing"}]):
            with self.subTest(change=change):
                self.path = Path(self.tmp.name) / (str(index) + ".json")
                sdk = self.sdk()
                intent = self.unknown(sdk)
                self.fake.handlers["leaderboard"] = {**self.evidence(intent), **change}
                try:
                    sdk.leaderboard()
                except SDKError as error:
                    self.assertIn(error.code, {"PUBLIC_DISPLAY_SCOPE_CHANGED"})
                self.assertEqual(sdk._pending_writes, [intent])
                with self.assertRaises(SDKError) as error:
                    sdk.leaderboard("close")
                self.assertEqual(error.exception.code, "RECOVERY_REQUIRED")
                sdk.close()

    def test_tribe_and_leaderboard_never_recover_each_other(self):
        sdk = self.sdk()
        intent = self.unknown(sdk)
        self.fake.handlers["tribe_info"] = {**self.evidence(intent), "schema": "tribe-info-1", "playerId": 1}
        sdk.tribe_info(1)
        self.assertEqual(sdk._pending_writes, [intent])
        other = copy.deepcopy(intent)
        other["id"] = "tribe-attempt"
        other["command"] = "tribe_info"
        other["args"]["opponent"] = "1"
        sdk._pending_writes.append(other)
        self.fake.handlers["leaderboard"] = {**self.evidence(intent), "viewRequestId": other["id"]}
        sdk.leaderboard()
        self.assertEqual(sdk._pending_writes, [intent, other])
        self.fake.handlers["leaderboard"] = self.evidence(intent)
        sdk.leaderboard()
        self.assertEqual(sdk._pending_writes, [other])

    def test_new_callback_must_match_schema_id_operation_and_stream(self):
        for index, change in enumerate([
                {"viewRequestId": "other"}, {"viewOperation": "close"},
                {"schema": "tribe-info-1"}, {"streamId": "other"}, {"streamId": None},
                {"callbackCompleted": False}, {"outcome": "unknown"}]):
            with self.subTest(change=change):
                self.path = Path(self.tmp.name) / ("callback" + str(index) + ".json")
                sdk = self.sdk()
                self.fake.handlers["leaderboard"] = lambda args: {
                    **self.native(status="opening", viewRequestId=args["viewRequestId"],
                        viewOperation="open", callbackCompleted=True, outcome="invoked"), **change}
                sdk.leaderboard("open")
                self.assertEqual(len(sdk._pending_writes), 1)
                sdk.close()

    def test_legacy_and_explicit_not_dispatched_are_preserved(self):
        sdk = self.sdk()
        self.fake.handlers["leaderboard"] = self.native(status="opening")
        self.assertEqual(sdk.leaderboard("open")["status"], "opening")
        self.assertEqual(sdk._pending_writes, [])
        self.fake.handlers["leaderboard"] = {"ok": True, "status": "closed"}
        self.assertEqual(sdk.leaderboard("close")["status"], "closed")
        self.assertEqual(sdk._pending_writes, [])
        self.fake.handlers["leaderboard"] = self.native(ok=False, status="unavailable", outcome="not_dispatched")
        self.assertFalse(sdk.leaderboard("open")["ok"])
        self.assertEqual(sdk._pending_writes, [])

    def test_anonymous_legacy_history_never_recovers_unknown_navigation(self):
        sdk = self.sdk()
        intent = self.unknown(sdk)
        self.fake.handlers["leaderboard"] = {"ok": True, "status": "historical", "rows": []}
        self.assertEqual(sdk.leaderboard()["status"], "historical")
        self.assertEqual(sdk._pending_writes, [intent])
        with self.assertRaises(SDKError) as error:
            sdk.observe(details=True)
        self.assertEqual(error.exception.code, "PUBLIC_DISPLAY_SCOPE_UNKNOWN")
        self.assertEqual(sdk._pending_writes, [intent])

    def test_lost_reply_is_never_replayed_by_read(self):
        sdk = self.sdk()
        self.fake.handlers["leaderboard"] = TimeoutError("navigation reply lost")
        with self.assertRaises(TimeoutError):
            sdk.leaderboard("open")
        intent = copy.deepcopy(sdk._pending_writes[0])
        self.fake.handlers["leaderboard"] = self.native(status="historical")
        sdk.leaderboard()
        self.assertEqual(sdk._pending_writes, [intent])
        self.assertEqual([args["mode"] for command, args in self.fake.calls if command == "leaderboard"],
                         ["open", "read"])


if __name__ == "__main__":
    unittest.main()
