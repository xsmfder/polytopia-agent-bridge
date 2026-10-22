"""A native modal blocker returns immediately while retaining the original write."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

from test_sdk import Fake
from sdk import PolytopiaSDK, SDKError

WORK = Path(__file__).resolve().parents[1]


class ModalSettlementTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='modal-', dir=WORK)
        self.addCleanup(self.tmp.cleanup)
        self.path = Path(self.tmp.name) / 'session.json'
        self.fake = Fake()
        self.sleeps = []

    def sdk(self):
        sdk = PolytopiaSDK(self.fake, player_id=2, session_path=self.path,
                          clock=lambda: 0.0, sleep=self.sleeps.append)
        self.addCleanup(sdk.close)
        return sdk

    def test_modal_returns_without_waiting_and_reconnect_resumes_same_ticket(self):
        sdk = self.sdk()
        actions = sdk.list_actions()['actions']
        end = next(a for a in actions if a['args'].get('name') == 'end_turn')
        other = next(a for a in actions if a['args'].get('name') == 'research')
        self.fake.handlers['settlement'].update(action='end_turn', settlementStatus='needs_resolution',
            effectConfirmation='unknown', actionCompleted=False, settlementComplete=False, pipelineIdle=False)
        result = sdk.execute(end, wait=True, timeout=30)
        self.assertEqual(result['status'], 'needs_resolution')
        self.assertFalse(result['retryAllowed'])
        self.assertFalse(result['native']['pipelineIdle'])
        self.assertEqual(self.sleeps, [])
        self.assertEqual(len([c for c, _ in self.fake.calls if c == 'settlement']), 1)
        parent = copy.deepcopy(sdk.unresolved)
        writes = copy.deepcopy(sdk._pending_writes)
        self.assertEqual(parent['ticket'], 8)
        self.assertTrue(sdk._resolution_required)
        with self.assertRaises(SDKError):
            sdk.execute(other)
        self.assertEqual(sdk.unresolved, parent)
        self.assertEqual(sdk._pending_writes, writes)
        sdk.close()
        persisted = json.loads(self.path.read_text(encoding='utf-8'))
        self.assertTrue(persisted['resolutionRequired'])
        self.assertEqual(persisted['unresolved'], parent)
        restored = self.sdk()
        self.assertTrue(restored._resolution_required)
        self.assertEqual(restored._pending_writes, writes)
        # The caller resolves the modal through its separate normal UI route;
        # this test supplies only the subsequent native settlement observation.
        self.fake.handlers['settlement'].update(settlementStatus='complete', effectConfirmation='confirmed',
            actionCompleted=True, settlementComplete=True, pipelineIdle=True)
        resumed = restored.resume_pending(timeout=0)
        self.assertEqual(resumed['status'], 'confirmed')
        self.assertIsNone(restored.unresolved)
        self.assertFalse(restored._resolution_required)
        self.assertEqual(restored._pending_writes, [])
        self.assertEqual([a['name'] for c, a in self.fake.calls if c == 'action'], ['end_turn'])
        self.assertEqual([a['ticket'] for c, a in self.fake.calls if c == 'settlement'], ['8', '8'])

    def test_unproven_pending_is_not_promoted_to_resolution_or_completion(self):
        sdk = self.sdk()
        end = next(a for a in sdk.list_actions()['actions'] if a['args'].get('name') == 'end_turn')
        self.fake.handlers['settlement'].update(action='end_turn', settlementStatus='pending',
            effectConfirmation='unknown', actionCompleted=False, settlementComplete=False, pipelineIdle=False)
        result = sdk.execute(end, wait=True, timeout=0)
        self.assertEqual(result['status'], 'timeout')
        self.assertEqual(sdk.unresolved['ticket'], 8)
        self.assertFalse(sdk._resolution_required)
        self.assertEqual(len(sdk._pending_writes), 1)
        self.assertEqual([c for c, _ in self.fake.calls if c == 'action'], ['action'])


if __name__ == '__main__':
    unittest.main()
