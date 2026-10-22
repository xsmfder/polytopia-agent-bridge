"""A real discovered treaty response must resolve, rather than replace, End Turn."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from sdk import PolytopiaSDK, SDKError
from test_sdk import Fake, world


class PeaceTransport(Fake):
    def __init__(self):
        super().__init__()
        self.pending = False
        self.resolved = False
        self.available = True
        self.lose_response = False
        self.hold_response = False
        self.on_response = None
        self.handlers.update(world=self.current_world, action=self.end_turn,
                             tribe_relations=self.relations, tribe_action=self.respond,
                             settlement=self.settlement)

    def current_world(self, args):
        row = world(3 if self.resolved else 2 if self.pending else 1)
        row.update(canAct=not self.pending or self.resolved, pendingTriggerCountMine=int(self.pending and not self.resolved))
        return row

    def end_turn(self, args):
        assert args["name"] == "end_turn"
        self.pending = True
        return {"ok": True, "accepted": True, "settlementTicket": 8}

    def relations(self, args):
        return {"ok": True, "tribes": [{"playerId": 3, "operations": [
            {"actionId": "peace_response", "accept": accept, "adapterSupported": True,
             "canExecute": self.available and not self.resolved} for accept in (False, True)] + [
            {"actionId": "peace_request", "adapterSupported": True, "canExecute": True}]}]}

    def respond(self, args):
        if self.on_response:
            self.on_response(args)
        if self.lose_response:
            raise TimeoutError("response receipt lost")
        if not self.hold_response:
            self.resolved = True
        return {"ok": True, "nativeExecution": {"ok": True, "accepted": True, "settlementTicket": 9}}

    def settlement(self, args):
        ticket = int(args["ticket"])
        return {"ok": True, "ticketId": ticket, "episodeId": "ep-one", "controlledPlayerId": "2",
                "streamId": "stream-one", "beforeStateVersion": 1 if ticket == 8 else 2,
                "stateVersion": 3 if self.resolved else 2,
                "action": "end_turn" if ticket == 8 else "peace_response",
                "settlementStatus": "complete" if self.resolved else "needs_resolution" if ticket == 8 else "pending",
                "settlementComplete": self.resolved, "actionCompleted": self.resolved,
                "effectConfirmation": "confirmed" if self.resolved else "unknown"}


class PeaceResolutionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1])
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / "session.json"
        self.transport = PeaceTransport()
        self.sdk = self.open_sdk()

    def open_sdk(self):
        sdk = PolytopiaSDK(self.transport, player_id=2, session_path=self.path)
        self.addCleanup(sdk.close)
        return sdk

    def start_parent(self, wait=True):
        end = next(a for a in self.sdk.list_actions()["actions"] if a["args"] == {"name": "end_turn"})
        return self.sdk.execute(end, wait=wait, timeout=0)

    def choice(self, accept="true"):
        return next(a for a in self.sdk.list_actions("tribe")["actions"]
                    if a["args"] == {"operation": "peace_response", "opponent": "3", "accept": accept})

    def verify_response_chain(self, accept):
        self.assertEqual(self.start_parent()["status"], "needs_resolution")
        parent = copy.deepcopy(self.sdk.unresolved)
        choice = self.choice(accept)

        def before_transport(args):
            state = json.loads(self.path.read_text(encoding="utf-8"))
            self.assertEqual(state["unresolved"], parent)
            self.assertEqual(state["pendingWrites"][-1]["parentTicket"], 8)
            self.assertEqual(state["pendingWrites"][-1]["state"], "uncertain")
            self.assertEqual(args["accept"], accept)
            self.assertEqual(args["expectedStateVersion"], "2")
        self.transport.on_response = before_transport
        self.assertEqual(self.sdk.execute(choice)["ticket"], 9)
        self.assertEqual(self.sdk.unresolved, parent)
        self.assertEqual([r["ticket"] for r in self.sdk._pending_writes], [8, 9])
        self.sdk.close()
        restored = self.open_sdk()
        self.transport.calls.clear()
        result = restored.resume_pending(timeout=0)
        self.assertEqual(result["status"], "confirmed")
        self.assertEqual(result["ticket"], 8)
        self.assertEqual([args["ticket"] for command, args in self.transport.calls], ["9", "8"])
        self.assertIsNone(restored.unresolved)
        self.assertEqual(restored._pending_writes, [])

    def test_accept_response_child_is_durable_and_recovered_before_end_turn(self):
        self.verify_response_chain("true")

    def test_reject_response_child_is_durable_and_recovered_before_end_turn(self):
        self.verify_response_chain("false")

    def test_parent_must_explicitly_need_resolution(self):
        self.start_parent(wait=False)
        choice = self.choice()
        with self.assertRaises(SDKError) as failure:
            self.sdk.execute(choice)
        self.assertEqual(failure.exception.code, "UNRESOLVED_ACTION")
        self.assertFalse(any(command == "tribe_action" for command, _ in self.transport.calls))

    def test_unrelated_diplomacy_operation_cannot_bypass_parent(self):
        self.start_parent()
        other = next(a for a in self.sdk.list_actions("tribe")["actions"] if a["args"]["operation"] == "peace_request")
        with self.assertRaises(SDKError) as failure:
            self.sdk.execute(other)
        self.assertEqual(failure.exception.code, "UNRESOLVED_ACTION")
        self.assertFalse(any(command == "tribe_action" for command, _ in self.transport.calls))

    def test_response_is_revalidated_against_current_native_offer(self):
        self.start_parent()
        choice = self.choice()
        self.transport.available = False
        with self.assertRaises(SDKError) as failure:
            self.sdk.execute(choice)
        self.assertEqual(failure.exception.code, "ACTION_NOT_AVAILABLE")
        self.assertEqual(len(self.sdk._pending_writes), 1)
        self.assertFalse(any(command == "tribe_action" for command, _ in self.transport.calls))

    def test_lost_child_response_preserves_parent_and_blocks_replay_after_reconnect(self):
        self.start_parent()
        choice = self.choice()
        self.transport.lose_response = True
        self.assertEqual(self.sdk.execute(choice)["status"], "unknown")
        self.assertEqual(self.sdk.unresolved["ticket"], 8)
        self.sdk.close()
        restored = self.open_sdk()
        self.transport.calls.clear()
        self.assertEqual(restored.resume_pending(timeout=0)["errorCode"], "RECOVERY_REQUIRED")
        self.assertEqual(self.transport.calls, [])
        with self.assertRaises(SDKError) as failure:
            restored.execute(choice)
        self.assertEqual(failure.exception.code, "RECOVERY_REQUIRED")
        self.assertFalse(any(command == "tribe_action" for command, _ in self.transport.calls))

    def test_a_pending_response_child_blocks_another_choice(self):
        self.start_parent()
        self.transport.hold_response = True
        self.sdk.execute(self.choice())
        with self.assertRaises(SDKError) as failure:
            self.sdk.execute(self.choice("false"))
        self.assertEqual(failure.exception.code, "UNRESOLVED_ACTION")
        self.assertEqual(sum(command == "tribe_action" for command, _ in self.transport.calls), 1)
        self.assertEqual([row["ticket"] for row in self.sdk._pending_writes], [8, 9])


if __name__ == "__main__":
    unittest.main()
