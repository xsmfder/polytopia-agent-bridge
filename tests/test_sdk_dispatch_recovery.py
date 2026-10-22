"""Native pre-dispatch refusal must not leave a durable uncertain write."""
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


class DispatchRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="test-dispatch-", dir=WORK)
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "session.json"
        self.fake = Fake()

    def sdk(self):
        value = PolytopiaSDK(self.fake, player_id=2, session_path=self.path)
        self.addCleanup(value.close)
        return value

    def test_immediate_reply_flags_never_override_original_ticket(self):
        for index, (immediate, effect, completed, expected) in enumerate((
                ("confirmed", "unknown", False, "unknown"),
                ("unknown", "confirmed", True, "confirmed"))):
            with self.subTest(immediate=immediate, effect=effect):
                self.fake = Fake()
                self.path = Path(self.tmp.name) / ("immediate-" + str(index) + ".json")
                sdk = self.sdk()
                self.fake.handlers["action"] = {"ok": True, "accepted": True, "settlementTicket": 8,
                    "verdict": immediate, "actionCompleted": immediate == "confirmed"}
                self.fake.handlers["settlement"].update(effectConfirmation=effect, actionCompleted=completed)
                result = sdk.execute(self.action(sdk), wait=True)
                self.assertEqual(result["status"], expected)
                self.assertEqual(bool(sdk._pending_writes), expected == "unknown")
                if expected == "unknown":
                    with self.assertRaises(SDKError): sdk.execute(self.action(sdk))
                self.assertEqual(len([c for c, _ in self.fake.calls if c == "action"]), 1)
                self.assertEqual(len([c for c, _ in self.fake.calls if c == "settlement"]), 1)
                sdk.close()

    def test_unavailable_command_binding_releases_only_proven_nonexecution(self):
        for index, code in enumerate(("COMMAND_CONSTRUCTOR_UNAVAILABLE", "COMMAND_EXECUTOR_UNAVAILABLE", "ACTION_MANAGER_UNAVAILABLE", "CITY_REWARD_COORDINATES_UNAVAILABLE", "INVALID_ENUM", "INVALID_BOOLEAN", "INVALID_COORDINATES", "NO_ACTIVE_GAME", "UNKNOWN_ACTION", "NATIVE_COMMAND_PARAMETERS_UNAVAILABLE", "ACTION_NOT_IN_CURRENT_MENU", "TRIBE_NOT_ENCOUNTERED")):
            with self.subTest(code=code):
                self.path = Path(self.tmp.name) / ("binding-" + str(index) + ".json")
                self.fake = Fake()
                sdk = self.sdk()
                self.fake.handlers["action"] = {"ok": False, "accepted": False, "dispatched": False,
                    "outcome": "not_dispatched", "errorCode": code}
                self.assertEqual(sdk.execute(self.action(sdk))["status"], "rejected")
                self.assertIsNone(sdk.unresolved)
                self.assertEqual(self.state()["pendingWrites"], [])
                sdk.close()
                restored = self.sdk()
                self.fake.handlers["action"] = {"ok": True, "accepted": True, "settlementTicket": 8}
                self.assertEqual(restored.execute(self.action(restored))["status"], "accepted")
                self.assertEqual(len([c for c in self.fake.calls if c[0] == "action"]), 2)
                restored.close()

    def test_entered_call_failure_stays_unknown_without_resending_after_reconnect(self):
        for index, code in enumerate(("COMMAND_CONSTRUCTION_FAILED", "CAPTURE_CONSTRUCTION_FAILED", "REQUEST_FAILED")):
            with self.subTest(code=code):
                self.path = Path(self.tmp.name) / ("construction-" + str(index) + ".json")
                self.fake = Fake()
                sdk = self.sdk()
                self.fake.handlers["action"] = {"ok": False, "outcome": "unknown", "verdict": "unknown",
                    "diagnosticsWithheld": True, "errorCode": code}
                response = sdk.execute(self.action(sdk))
                self.assertEqual(response["status"], "unknown")
                self.assertIs(response["retryAllowed"], False)
                self.assertEqual(self.state()["pendingWrites"][0]["state"], "uncertain")
                sdk.close()
                restored = self.sdk()
                with self.assertRaises(SDKError):
                    restored.execute(self.action(restored))
                self.assertEqual(len([c for c in self.fake.calls if c[0] == "action"]), 1)
                restored.close()

    def test_execution_exception_keeps_original_ticket_for_read_only_reconciliation(self):
        sdk = self.sdk()
        self.fake.handlers["action"] = {"ok": False, "outcome": "unknown", "verdict": "unknown",
            "errorCode": "INVOKE_FAILED", "settlementTicket": 8,
            "settlement": {"ticketId": 8, "settlementStatus": "unknown"}}
        result = sdk.execute(self.action(sdk))
        self.assertEqual((result["status"], result["ticket"]), ("accepted", 8))
        sdk.close()
        restored = self.sdk()
        self.assertEqual(restored.unresolved["ticket"], 8)
        self.fake.handlers["settlement"].update(settlementStatus="rejected", actionCompleted=False,
                                                effectConfirmation="unknown")
        self.assertEqual(restored.wait_result(8)["status"], "rejected")
        self.assertEqual(restored._pending_writes, [])
        self.assertIsNone(restored.unresolved)
        self.assertEqual(len([c for c in self.fake.calls if c[0] == "action"]), 1)

    def test_router_failure_with_recovered_ticket_reconnects_without_resubmitting(self):
        sdk = self.sdk()
        self.fake.handlers["action"] = {"ok": False, "errorCode": "REQUEST_FAILED", "outcome": "unknown",
                                         "verdict": "unknown", "diagnosticsWithheld": True, "settlementTicket": 8}
        receipt = sdk.execute(self.action(sdk))
        self.assertEqual((receipt["status"], receipt["ticket"]), ("accepted", 8))
        sdk.close()
        restored = self.sdk()
        self.assertEqual(restored.wait_result(8)["status"], "confirmed")
        self.assertEqual(restored._pending_writes, [])
        self.assertIsNone(restored.unresolved)
        self.assertEqual([args["ticket"] for cmd, args in self.fake.calls if cmd == "settlement"], ["8"])
        self.assertEqual(len([cmd for cmd, _ in self.fake.calls if cmd == "action"]), 1)

    def test_ambiguous_errors_are_unknown_and_block_in_memory_resend(self):
        for response in ({"ok": False, "error": "callback failed"},
                         {"ok": False, "outcome": "unknown"},
                         {"ok": False, "errorCode": "INVALID_ENUM"},
                         {"ok": False, "errorCode": "INVALID_COORDINATES"},
                         {"ok": False, "accepted": False, "dispatched": True},
                         {"ok": False, "accepted": False, "outcome": "unknown"}):
            with self.subTest(response=response):
                fake = Fake()
                sdk = PolytopiaSDK(fake, player_id=2)
                self.addCleanup(sdk.close)
                fake.handlers["action"] = response
                action = self.action(sdk)
                result = sdk.execute(action)
                self.assertEqual(result["status"], "unknown")
                self.assertIs(result["retryAllowed"], False)
                self.assertIsNotNone(sdk.unresolved)
                with self.assertRaises(SDKError):
                    sdk.execute(action)
                self.assertEqual(len([c for c in fake.calls if c[0] == "action"]), 1)

    def test_invalid_response_or_ticket_never_becomes_an_accepted_receipt(self):
        for bad in (None, True, False, 0, -1, 8.5, "bad", {}, []):
            with self.subTest(ticket=bad):
                fake = Fake(); sdk = PolytopiaSDK(fake, player_id=2)
                self.addCleanup(sdk.close)
                fake.handlers["action"] = {"ok": True, "accepted": True, "settlementTicket": bad}
                result = sdk.execute(self.action(sdk))
                self.assertEqual(result["status"], "unknown")
                self.assertIsNotNone(sdk.unresolved)
                with self.assertRaises(SDKError): sdk.execute(self.action(sdk))
        for response in ([], {"nativeExecution": []}, {"nativeExecution": None}):
            with self.subTest(response=response):
                fake = Fake(); sdk = PolytopiaSDK(fake, player_id=2)
                self.addCleanup(sdk.close)
                fake.handlers["action"] = response
                result = sdk.execute(self.action(sdk))
                self.assertEqual(result["status"], "unknown")
                self.assertIsNotNone(sdk.unresolved)

    def test_valid_ticket_with_error_is_queried_instead_of_discarded(self):
        for durable in (False, True):
            with self.subTest(durable=durable):
                fake = Fake(); path = Path(self.tmp.name) / "ticket-error.json" if durable else None
                sdk = PolytopiaSDK(fake, player_id=2, session_path=path)
                self.addCleanup(sdk.close)
                fake.handlers["action"] = {"ok": False, "accepted": False, "settlementTicket": "8"}
                result = sdk.execute(self.action(sdk))
                self.assertEqual((result["status"], result["ticket"]), ("accepted", 8))
                self.assertEqual(sdk.unresolved["ticket"], 8)
                fake.handlers["settlement"].update(settlementStatus="rejected", actionCompleted=False,
                                                    effectConfirmation="unknown")
                self.assertEqual(sdk.wait_result(8)["status"], "rejected")
                self.assertIsNone(sdk.unresolved)
                self.assertEqual(sdk._pending_writes, [])
                self.assertEqual(len([c for c in fake.calls if c[0] == "action"]), 1)
                sdk.close()

    def test_conflicting_refusal_stays_uncertain_after_reconnect(self):
        for index, response in enumerate((
                {"ok": False, "accepted": False, "dispatched": True},
                {"ok": False, "accepted": False, "outcome": "unknown"},
                {"ok": False, "dispatched": False, "accepted": True})):
            with self.subTest(response=response):
                self.path = Path(self.tmp.name) / ("conflict-" + str(index) + ".json")
                sdk = self.sdk(); self.fake.handlers["action"] = response
                self.assertEqual(sdk.execute(self.action(sdk))["status"], "unknown")
                self.assertEqual(self.state()["pendingWrites"][0]["state"], "uncertain")
                sdk.close(); restored = self.sdk()
                result = restored.resume_pending(timeout=0)
                self.assertEqual(result["errorCode"], "RECOVERY_REQUIRED")
                restored.close()

    def test_in_memory_child_reward_cannot_bypass_an_unresolved_child(self):
        for child_reply in (TimeoutError("reply lost"),
                            {"ok": True, "accepted": True, "settlementTicket": 9}):
            with self.subTest(reply=child_reply):
                fake = Fake(); sdk = PolytopiaSDK(fake, player_id=2)
                self.addCleanup(sdk.close)
                sdk.execute(self.action(sdk))
                fake.handlers["settlement"]["settlementStatus"] = "needs_resolution"
                sdk.wait_result(8)
                parent = copy.deepcopy(sdk.unresolved)
                fake.handlers["world"]["canAct"] = False
                fake.handlers["city_options"]["pendingCityReward"] = {
                    "city": "(2,3)", "choices": [{"reward": "FutureReward"}]}
                reward = next(a for a in sdk.list_actions("city")["actions"]
                              if a["args"].get("name") == "city_reward")
                fake.handlers["action"] = child_reply
                child = sdk.execute(reward)
                self.assertEqual(child["status"], "unknown" if isinstance(child_reply, Exception) else "accepted")
                with self.assertRaises(SDKError): sdk.execute(reward)
                self.assertEqual(sdk.unresolved, parent)
                self.assertEqual(len([c for c in fake.calls if c[0] == "action"]), 2)

    def test_in_memory_completed_child_then_parent_release_original_intents(self):
        fake = Fake(); sdk = PolytopiaSDK(fake, player_id=2)
        self.addCleanup(sdk.close)
        sdk.execute(self.action(sdk))
        fake.handlers["settlement"]["settlementStatus"] = "needs_resolution"; sdk.wait_result(8)
        parent = copy.deepcopy(sdk.unresolved)
        fake.handlers["world"]["canAct"] = False
        fake.handlers["city_options"]["pendingCityReward"] = {"city": "(2,3)", "choices": [{"reward": "Workshop"}]}
        reward = next(a for a in sdk.list_actions("city")["actions"] if a["args"].get("name") == "city_reward")
        fake.handlers["action"]["settlementTicket"] = 9
        sdk.execute(reward)
        self.assertEqual(len(sdk._pending_writes), 2)
        fake.handlers["settlement"]["settlementStatus"] = "complete"
        self.assertEqual(sdk.wait_result(9, binding=reward["binding"])["status"], "confirmed")
        self.assertEqual(sdk.unresolved, parent)
        self.assertEqual([p["ticket"] for p in sdk._pending_writes], [8])
        self.assertEqual(sdk.wait_result(8)["status"], "confirmed")
        self.assertEqual(sdk._pending_writes, [])

    def test_explicit_refusal_without_session_does_not_block_new_action(self):
        fake = Fake(); sdk = PolytopiaSDK(fake, player_id=2)
        self.addCleanup(sdk.close)
        fake.handlers["action"] = self.busy()
        self.assertEqual(sdk.execute(self.action(sdk))["status"], "rejected")
        self.assertIsNone(sdk.unresolved)
        self.assertEqual(sdk._pending_writes, [])
        fake.handlers["action"] = {"ok": True, "accepted": True, "settlementTicket": 8}
        self.assertEqual(sdk.execute(self.action(sdk))["status"], "accepted")

    @staticmethod
    def action(sdk):
        return next(a for a in sdk.list_actions()["actions"] if a["args"].get("name") == "research")

    def state(self):
        return json.loads(self.path.read_text(encoding="utf-8"))

    @staticmethod
    def busy():
        # Exact fields produced by ExecuteGameAction's pipeline gate, before
        # BeginCombatSettlement / ActionManager.ExecuteCommand are called.
        return {"ok": False, "errorCode": "ACTION_PIPELINE_BUSY",
                "outcome": "not_dispatched", "state": {"settlementStatus": "pending"}}

    def test_pipeline_refusal_survives_reconnect_without_blocking_new_action(self):
        first = self.sdk()
        action = self.action(first)
        self.fake.handlers["action"] = self.busy()
        self.assertEqual(first.execute(action)["status"], "rejected")
        self.assertEqual(self.state()["pendingWrites"], [])
        self.assertIsNone(self.state()["unresolved"])
        first.close()
        second = self.sdk()
        self.fake.handlers["action"] = {"ok": True, "accepted": True, "settlementTicket": 8}
        self.assertEqual(second.execute(self.action(second))["status"], "accepted")
        self.assertEqual(self.state()["pendingWrites"][0]["ticket"], 8)

    def test_nested_diplomacy_refusal_uses_native_execution_evidence(self):
        sdk = self.sdk()
        self.fake.handlers["tribe_relations"] = {"ok": True, "tribes": [{"playerId": 1,
            "operations": [{"actionId": "break_peace", "canExecute": True, "adapterSupported": True}]}]}
        action = sdk.list_actions("tribe")["actions"][0]
        self.fake.handlers["tribe_action"] = {"ok": False, "schema": "tribe-action-1",
            "nativeExecution": self.busy()}
        self.assertEqual(sdk.execute(action)["status"], "rejected")
        self.assertEqual(self.state()["pendingWrites"], [])

    def test_rejected_child_does_not_release_parent_receipt(self):
        sdk = self.sdk()
        sdk.execute(self.action(sdk))
        self.fake.handlers["settlement"]["settlementStatus"] = "needs_resolution"
        sdk.wait_result(8)
        parent = copy.deepcopy(sdk.unresolved)
        parent_intents = copy.deepcopy(sdk._pending_writes)
        self.fake.handlers["world"]["canAct"] = False
        self.fake.handlers["city_options"]["pendingCityReward"] = {
            "city": "(2,3)", "choices": [{"reward": "FutureReward"}]}
        reward = next(a for a in sdk.list_actions("city")["actions"]
                      if a["args"].get("name") == "city_reward")
        self.fake.handlers["action"] = self.busy()
        self.assertEqual(sdk.execute(reward)["status"], "rejected")
        self.assertEqual(sdk.unresolved, parent)
        self.assertTrue(sdk._resolution_required)
        self.assertEqual(sdk._pending_writes, parent_intents)
        sdk.close()
        restored = self.sdk()
        self.assertEqual(restored.unresolved, parent)
        self.assertEqual(restored._pending_writes, parent_intents)

    def test_unproven_refusal_remains_blocked_after_reconnect(self):
        for index, response in enumerate([
                {"ok": False, "errorCode": "ACTION_PIPELINE_BUSY"},
                {"ok": False, "status": "rejected", "error": "callback failed"},
                {"ok": False, "outcome": "unknown"},
                         {"ok": False, "errorCode": "INVALID_ENUM"},
                         {"ok": False, "errorCode": "INVALID_COORDINATES"},
                {"ok": True, "accepted": None},
                {"ok": False, "accepted": True, "outcome": "not_dispatched"},
                {"ok": False, "dispatched": True, "outcome": "not_dispatched"},
                TimeoutError("reply lost after transport write")]):
            with self.subTest(response=response):
                self.path = Path(self.tmp.name) / (str(index) + ".json")
                first = self.sdk()
                action = self.action(first)
                self.fake.handlers["action"] = response
                first.execute(action)
                self.assertEqual(self.state()["pendingWrites"][0]["state"], "uncertain")
                first.close()
                restored = self.sdk()
                self.fake.handlers["action"] = {"ok": True, "accepted": True, "settlementTicket": 8}
                writes_before = len([c for c in self.fake.calls if c[0] == "action"])
                with self.assertRaises(SDKError) as error:
                    restored.execute(self.action(restored))
                self.assertIn(error.exception.code, {"RECOVERY_REQUIRED", "UNRESOLVED_ACTION"})
                self.assertEqual(len([c for c in self.fake.calls if c[0] == "action"]), writes_before)
                restored.close()

    def test_ticket_takes_precedence_over_conflicting_not_dispatched(self):
        sdk = self.sdk()
        self.fake.handlers["action"] = {"ok": True, "accepted": True,
            "settlementTicket": 8, "outcome": "not_dispatched"}
        self.assertEqual(sdk.execute(self.action(sdk))["status"], "accepted")
        self.assertEqual(self.state()["pendingWrites"][0]["state"], "receipt")
        self.assertEqual(self.state()["pendingWrites"][0]["ticket"], 8)
        self.assertEqual(sdk.wait_result(8)["status"], "confirmed")
        self.assertEqual(self.state()["pendingWrites"], [])

    def test_handle_command_pre_dispatch_shapes_allow_new_action_after_reconnect(self):
        # Actual HandleCommand response shapes. The Fake injects the reply rather
        # than claiming these guards were reached by a real native invocation.
        details = [
            {"errorCode": "CONTROLLED_SEAT_PAUSED", "canAct": False},
            {"error": "audit-only command 'native_debug'; add \"audit\":\"1\" to opt in (debug convenience, not a permission boundary)",
             "decisionCommands": ["world", "action"]},
            {"errorCode": "STALE_ACTION_CONTEXT",
             "recovery": "observe and select a fresh action; no action dispatched"}]
        for index, detail in enumerate(details):
            with self.subTest(detail=detail):
                self.path = Path(self.tmp.name) / ("guard-" + str(index) + ".json")
                first = self.sdk()
                response = {"ok": False, "accepted": False, "dispatched": False,
                            "outcome": "not_dispatched", **detail}
                self.fake.handlers["action"] = response
                receipt = first.execute(self.action(first))
                self.assertEqual(receipt["status"], "rejected")
                self.assertEqual(receipt["native"], response)
                self.assertEqual(self.state()["pendingWrites"], [])
                self.assertIsNone(self.state()["unresolved"])
                first.close()
                restored = self.sdk()
                self.fake.handlers["action"] = {"ok": True, "accepted": True, "settlementTicket": 8}
                count = len([c for c in self.fake.calls if c[0] == "action"])
                self.assertEqual(restored.execute(self.action(restored))["status"], "accepted")
                self.assertEqual(len([c for c in self.fake.calls if c[0] == "action"]), count + 1)
                self.assertEqual(self.state()["pendingWrites"][0]["ticket"], 8)
                restored.close()

    def test_same_guard_errors_without_dispatch_evidence_stay_blocked(self):
        for index, detail in enumerate([
                {"errorCode": "CONTROLLED_SEAT_PAUSED", "canAct": False},
                {"error": "audit-only command 'native_debug'", "decisionCommands": ["world", "action"]},
                {"errorCode": "STALE_ACTION_CONTEXT",
                 "recovery": "observe and select a fresh action; no action dispatched"}]):
            with self.subTest(detail=detail):
                self.path = Path(self.tmp.name) / ("unproven-guard-" + str(index) + ".json")
                first = self.sdk()
                self.fake.handlers["action"] = {"ok": False, **detail}
                self.assertEqual(first.execute(self.action(first))["status"], "unknown")
                self.assertEqual(self.state()["pendingWrites"][0]["state"], "uncertain")
                first.close()
                restored = self.sdk()
                self.fake.handlers["action"] = {"ok": True, "accepted": True, "settlementTicket": 8}
                count = len([c for c in self.fake.calls if c[0] == "action"])
                with self.assertRaises(SDKError) as error:
                    restored.execute(self.action(restored))
                self.assertEqual(error.exception.code, "UNRESOLVED_ACTION")
                self.assertEqual(len([c for c in self.fake.calls if c[0] == "action"]), count)
                restored.close()


if __name__ == "__main__":
    unittest.main()
