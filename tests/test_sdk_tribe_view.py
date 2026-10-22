"""Information-view callback receipts and passive effect recovery; no game/UI."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

WORK = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORK))
from sdk import PolytopiaSDK, SDKError
from test_sdk import Fake


class TribeViewTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="view-test-", dir=WORK)
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "session.json"
        self.fake = Fake()

    def sdk(self, durable=True):
        value = PolytopiaSDK(self.fake, player_id=2,
                            session_path=self.path if durable else None)
        self.addCleanup(value.close)
        return value

    def state(self):
        return json.loads(self.path.read_text(encoding="utf-8"))

    @staticmethod
    def native(**extra):
        return {"ok": True, "schema": "tribe-info-1", "status": "ok",
                "episodeId": "ep-one", "controlledPlayerId": 2, "playerId": 1, **extra}

    def unknown(self, sdk, mode="open"):
        self.fake.handlers["tribe_info"] = self.native(ok=False, status="unknown", outcome="unknown")
        result = sdk.tribe_info(1, mode=mode)
        self.assertEqual(result["status"], "unknown")
        self.assertFalse(result["ok"])
        return copy.deepcopy(sdk._pending_writes[0])

    def evidence(self, intent, **extra):
        mode = intent["args"]["mode"]
        return self.native(status="ok" if mode == "open" else "closed",
            viewRequestId=intent["id"], viewOperation=mode, viewEffectConfirmed=True,
            callbackCompleted=False, outcome="confirmed", **extra)

    def test_wire_id_is_durable_intent_and_other_parameters_are_preserved(self):
        sdk = self.sdk()
        seen = []
        def reply(args):
            intent = self.state()["pendingWrites"][0]
            self.assertEqual(args["viewRequestId"], intent["id"])
            self.assertNotIn("viewRequestId", intent["args"])
            self.assertEqual({k: v for k, v in args.items() if k != "viewRequestId"}, intent["args"])
            seen.append(args)
            return self.native(status="opening", viewRequestId=args["viewRequestId"],
                viewOperation="open", callbackCompleted=True, outcome="invoked")
        self.fake.handlers["tribe_info"] = reply
        result = sdk.tribe_info(1, mode="open")
        self.assertEqual(result["status"], "opening")
        self.assertNotEqual(result["status"], "confirmed")
        self.assertEqual(self.state()["pendingWrites"], [])
        self.assertEqual(len(seen), 1)
        with self.assertRaises(SDKError):
            sdk._request("tribe_info", mode="open", viewRequestId="caller-id")
        self.assertEqual(len(seen), 1)

    def test_nondurable_view_also_has_unique_request_id(self):
        sdk = self.sdk(False)
        self.fake.handlers["tribe_info"] = lambda args: self.native(status="opening",
            viewRequestId=args["viewRequestId"], viewOperation="open", callbackCompleted=True, outcome="invoked")
        self.assertEqual(sdk.tribe_info(1, mode="open")["status"], "opening")
        wire = next(args for command, args in self.fake.calls if command == "tribe_info")
        self.assertIsInstance(wire["viewRequestId"], str)
        self.assertTrue(wire["viewRequestId"])

    def test_closing_callback_is_pending_and_passive_read_does_not_resubmit(self):
        sdk = self.sdk()
        self.fake.handlers["tribe_info"] = lambda args: self.native(status="closing",
            viewRequestId=args["viewRequestId"], viewOperation="close", callbackCompleted=True, outcome="invoked")
        result = sdk.tribe_info(1, mode="close")
        self.assertEqual(result["status"], "closing")
        self.assertEqual(self.state()["pendingWrites"], [])
        self.fake.handlers["tribe_info"] = self.native(status="partial")
        sdk.tribe_info(1)
        self.assertEqual([args["mode"] for command, args in self.fake.calls if command == "tribe_info"],
                         ["close", "read"])

    def test_unknown_open_recovers_after_reconnect_only_from_matching_passive_read(self):
        first = self.sdk()
        intent = self.unknown(first)
        first.observe()
        self.assertEqual(first._pending_writes, [intent])
        first.close()
        second = self.sdk()
        self.fake.handlers["tribe_info"] = self.evidence(intent)
        self.assertEqual(second.tribe_info(1)["status"], "observed")
        self.assertEqual(self.state()["pendingWrites"], [])
        self.assertEqual([args["mode"] for command, args in self.fake.calls if command == "tribe_info"],
                         ["open", "read"])

    def test_unknown_close_recovers_when_native_target_is_actually_inactive(self):
        sdk = self.sdk()
        intent = self.unknown(sdk, "close")
        self.fake.handlers["tribe_info"] = self.evidence(intent)
        self.assertEqual(sdk.tribe_info(1)["status"], "closed")
        self.assertEqual(self.state()["pendingWrites"], [])

    def test_direct_native_closed_effect_does_not_need_an_extra_read(self):
        sdk = self.sdk()
        self.fake.handlers["tribe_info"] = lambda args: self.native(status="closed",
            viewRequestId=args["viewRequestId"], viewOperation="close", callbackCompleted=False,
            outcome="confirmed", viewEffectConfirmed=True)
        self.assertEqual(sdk.tribe_info(1, mode="close")["status"], "closed")
        self.assertEqual(self.state()["pendingWrites"], [])

    def test_wrong_or_unconfirmed_read_evidence_cannot_release_intent(self):
        variants = [
            {"viewRequestId": "another-request"}, {"viewOperation": "close"},
            {"playerId": 3}, {"episodeId": "another-episode"}, {"controlledPlayerId": 1},
            {"viewEffectConfirmed": False}, {"viewEffectConfirmed": None},
            {"status": "partial"}, {"status": "opening"}, {"status": "closed"}]
        for index, change in enumerate(variants):
            with self.subTest(change=change):
                self.path = Path(self.tmp.name) / (str(index) + ".json")
                sdk = self.sdk()
                intent = self.unknown(sdk)
                self.fake.handlers["tribe_info"] = {**self.evidence(intent), **change}
                try:
                    sdk.tribe_info(1)
                except SDKError as error:
                    self.assertIn(error.code, {"READ_TARGET_CHANGED", "READ_SCOPE_CHANGED"})
                self.assertEqual(sdk._pending_writes, [intent])
                with self.assertRaises(SDKError) as error:
                    sdk.tribe_info(1, mode="close")
                self.assertEqual(error.exception.code, "RECOVERY_REQUIRED")
                sdk.close()

    def test_matching_read_clears_only_its_own_uncertain_ui_intent(self):
        sdk = self.sdk()
        intent = self.unknown(sdk)
        unrelated = copy.deepcopy(intent)
        unrelated["id"] = "another-request"
        sdk._pending_writes.append(unrelated)
        self.fake.handlers["tribe_info"] = self.evidence(intent)
        sdk.tribe_info(1)
        self.assertEqual(sdk._pending_writes, [unrelated])

    def test_new_callback_requires_exact_id_operation_scope_and_target(self):
        for index, change in enumerate([
                {"viewRequestId": "other"}, {"viewOperation": "close"},
                {"playerId": 3}, {"episodeId": "other"}, {"controlledPlayerId": 1},
                {"callbackCompleted": False}, {"outcome": "unknown"}]):
            with self.subTest(change=change):
                self.path = Path(self.tmp.name) / ("callback" + str(index) + ".json")
                sdk = self.sdk()
                self.fake.handlers["tribe_info"] = lambda args: {
                    **self.native(status="opening", viewRequestId=args["viewRequestId"],
                        viewOperation="open", callbackCompleted=True, outcome="invoked"), **change}
                try:
                    sdk.tribe_info(1, mode="open")
                except SDKError:
                    pass
                self.assertEqual(len(sdk._pending_writes), 1)
                sdk.close()

    def test_timeout_remains_blocked_and_legacy_replies_keep_compatibility(self):
        sdk = self.sdk()
        self.fake.handlers["tribe_info"] = TimeoutError("reply lost")
        self.assertEqual(sdk.tribe_info(1, mode="open")["status"], "unknown")
        with self.assertRaises(SDKError):
            sdk.tribe_info(1, mode="close")
        sdk.close()
        self.path = Path(self.tmp.name) / "legacy.json"
        legacy = self.sdk()
        self.fake.handlers["tribe_info"] = self.native(status="opening")
        self.assertEqual(legacy.tribe_info(1, mode="open")["status"], "opening")
        self.assertEqual(legacy._pending_writes, [])
        self.fake.handlers["tribe_info"] = {"ok": True, "schema": "tribe-info-1", "status": "closed"}
        self.assertEqual(legacy.tribe_info(1, mode="close")["status"], "closed")
        self.assertEqual(legacy._pending_writes, [])

    def test_explicit_not_dispatched_rejection_releases_only_current_intent(self):
        sdk = self.sdk()
        self.fake.handlers["tribe_info"] = self.native(ok=False, status="unavailable", outcome="not_dispatched")
        self.assertFalse(sdk.tribe_info(1, mode="open")["ok"])
        self.assertEqual(sdk._pending_writes, [])


if __name__ == "__main__":
    unittest.main()
