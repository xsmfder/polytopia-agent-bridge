"""A completed status label cannot override missing or contradictory receipt proof."""
import copy
import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from sdk import PolytopiaSDK, SDKError
from test_sdk import Fake


class ReceiptConsistencyTests(unittest.TestCase):
    def test_inconsistent_completion_keeps_original_write(self):
        cases = (
            {"settlementComplete": False}, {"settlementComplete": None}, {"settlementComplete": 1},
            {"actionCompleted": False}, {"actionCompleted": 1},
            {"effectConfirmation": "not_confirmed", "actionCompleted": True},
            {"effectConfirmation": "unexpected_native_state"}, {"ok": None}, {"ok": 1},
            {"settlementStatus": "unexpected_native_state"},
        )
        for patch in (*cases, "missing-complete", "missing-ok"):
            with self.subTest(patch=patch):
                fake = Fake()
                fake.handlers["settlement"]["settlementComplete"] = True
                if patch == "missing-complete":
                    fake.handlers["settlement"].pop("settlementComplete")
                elif patch == "missing-ok":
                    fake.handlers["settlement"].pop("ok")
                else:
                    fake.handlers["settlement"].update(patch)
                sdk = PolytopiaSDK(fake, player_id=2)
                self.addCleanup(sdk.close)
                action = next(a for a in sdk.list_actions()["actions"] if a["args"].get("name") == "research")
                sdk.execute(action)
                original = copy.deepcopy(sdk.unresolved)
                result = sdk.wait_result(8, timeout=0)
                self.assertEqual(result["status"], "unknown")
                self.assertEqual(sdk.unresolved, original)
                self.assertTrue(sdk._pending_writes)
                self.assertFalse(result["retryAllowed"])
                with self.assertRaises(SDKError):
                    sdk.execute(action)
                self.assertEqual(sum(c == "action" for c, _ in fake.calls), 1)
                self.assertEqual(sum(c == "settlement" for c, _ in fake.calls), 1)

    def test_consistent_terminal_receipt_releases_original_write(self):
        for effect, completed in (("confirmed", True), ("not_confirmed", False)):
            with self.subTest(effect=effect):
                fake = Fake()
                fake.handlers["settlement"].update(settlementComplete=True,
                    effectConfirmation=effect, actionCompleted=completed)
                sdk = PolytopiaSDK(fake, player_id=2)
                self.addCleanup(sdk.close)
                action = next(a for a in sdk.list_actions()["actions"] if a["args"].get("name") == "research")
                result = sdk.execute(action, wait=True)
                self.assertEqual(result["status"], effect)
                self.assertIsNone(sdk.unresolved)
                self.assertEqual(sdk._pending_writes, [])
                self.assertEqual(sum(c == "action" for c, _ in fake.calls), 1)


if __name__ == "__main__":
    unittest.main()
