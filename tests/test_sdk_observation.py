import contextlib
import copy
import io
import json
import tempfile
import unittest
from pathlib import Path
from test_sdk import Fake, world
from sdk import PolytopiaSDK, SDKError
from agentcli import main


class ObservationRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.f = Fake()
        self.s = PolytopiaSDK(self.f, player_id=2)

    def pending(self):
        self.s.unresolved = {'ticket': 8, 'binding': {'episodeId': 'ep-one', 'playerId': 2,
            'streamId': 'stream-one', 'stateVersion': 1}}
        self.s._resolution_required = True
        return copy.deepcopy(self.s.unresolved)

    def test_mixed_detailed_observation_is_discarded_without_retry(self):
        original = self.pending()
        versions = iter([world(1), world(2)])
        self.f.handlers['world'] = lambda _: next(versions)
        r = self.s.observe(details=True)
        self.assertEqual(r['status'], 'state_changed')
        self.assertFalse(r['ok'])
        self.assertIsNone(r['world'])
        self.assertNotIn('cities', r)
        self.assertNotIn('popup', r)
        self.assertFalse(r['retryAllowed'])
        self.assertTrue(r['retryObservationAllowed'])
        self.assertEqual(r['pendingReceipt'], original)
        r['pendingReceipt']['binding']['stateVersion'] = 999
        self.assertEqual(self.s.unresolved, original)
        self.assertTrue(self.s._resolution_required)
        self.assertEqual(sum(c == 'world' for c, _ in self.f.calls), 2)
        self.assertFalse(any(c in {'action', 'popup', 'handoff_start'} for c, _ in self.f.calls))

    def test_native_race_response_keeps_ticket_queryable(self):
        original = self.pending()
        self.f.handlers['world'] = {'ok': False, 'errorCode': 'STATE_CHANGED', 'error': 'moving bot'}
        self.assertEqual(self.s.observe()['pendingReceipt'], original)
        self.assertEqual(self.s.wait_result(8, binding=original['binding'], timeout=0)['status'], 'confirmed')
        self.assertIsNone(self.s.unresolved)
        self.assertEqual(sum(c == 'settlement' for c, _ in self.f.calls), 1)

    def test_basic_observation_discards_popup_from_new_version(self):
        original = self.pending()
        versions = iter([world(1), world(2)])
        self.f.handlers['world'] = lambda _: next(versions)
        self.f.handlers['popup_info'] = {'ok': True, 'showing': True, 'popupType': 'RewardPopup'}
        result = self.s.observe()
        self.assertEqual(result['status'], 'state_changed')
        self.assertIsNone(result['world'])
        self.assertNotIn('popup', result)
        self.assertEqual(result['pendingReceipt'], original)
        self.assertEqual(self.s.unresolved, original)
        self.assertTrue(self.s._resolution_required)
        self.assertEqual([c for c, _ in self.f.calls], ['handoff_status', 'world', 'popup_info', 'world'])

    def test_basic_observation_discards_same_version_turn_change(self):
        later = world()
        later['currentPlayerId'] = 3
        versions = iter([world(), later])
        self.f.handlers['world'] = lambda _: next(versions)
        self.assertEqual(self.s.observe()['status'], 'state_changed')
        self.assertFalse(any(c in {'popup', 'action', 'handoff_start'} for c, _ in self.f.calls))

    def test_basic_observation_keeps_stable_popup_and_binding(self):
        self.f.handlers['popup_info'] = {'ok': True, 'showing': True, 'popupType': 'RewardPopup'}
        result = self.s.observe()
        self.assertEqual(result['status'], 'observed')
        self.assertEqual(result['binding']['stateVersion'], 1)
        self.assertEqual(result['popup'], self.f.handlers['popup_info'])
        self.assertEqual(sum(c == 'world' for c, _ in self.f.calls), 2)

    def test_explicit_new_read_checks_human_curtain_again(self):
        self.f.handlers['world'] = {'ok': False, 'errorCode': 'STATE_CHANGED'}
        self.assertEqual(self.s.observe()['status'], 'state_changed')
        self.f.calls.clear()
        self.f.handlers['handoff_status'] = {'ok': True, 'phase': 'human_turn', 'ready': False}
        self.assertEqual(self.s.observe(details=True)['status'], 'handoff')
        self.assertEqual([c for c, _ in self.f.calls], ['handoff_status'])

    def test_recovery_does_not_hide_wrong_seat_episode_or_stream(self):
        self.s.observe()
        for key, value, code in [('localPlayerId', 1, 'SEAT_CHANGED'), ('episodeId', 'other', 'EPISODE_CHANGED')]:
            self.f.handlers['world'] = world()
            self.f.handlers['world'][key] = value
            with self.assertRaises(SDKError) as e: self.s.observe()
            self.assertEqual(e.exception.code, code)
        self.f.handlers['world'] = world()
        self.f.handlers['world']['state']['streamId'] = 'other'
        with self.assertRaises(SDKError) as e: self.s.observe()
        self.assertEqual(e.exception.code, 'STREAM_CHANGED')

    def test_cli_returns_retryable_read_status_and_no_write(self):
        self.f.handlers['world'] = {'ok': False, 'errorCode': 'STATE_CHANGED'}
        with contextlib.redirect_stdout(io.StringIO()) as output:
            code = main(['--player', '2', 'observe', '--details'], sdk_factory=lambda **_: self.s)
        self.assertEqual(code, 6)
        self.assertEqual(json.loads(output.getvalue())['status'], 'state_changed')
        self.assertFalse(any(c in {'action', 'popup'} for c, _ in self.f.calls))

    def test_log_records_recoverable_race_and_preserved_receipt(self):
        with tempfile.TemporaryDirectory() as d:
            self.s.log_path = Path(d) / 'sdk.jsonl'
            original = self.pending()
            self.f.handlers['world'] = {'ok': False, 'errorCode': 'STATE_CHANGED'}
            self.s.observe()
            row = json.loads(self.s.log_path.read_text(encoding='utf-8'))
            self.assertEqual(row['response']['status'], 'state_changed')
            self.assertEqual(row['response']['pendingReceipt'], original)

    def test_preview_never_computes_from_mixed_observation(self):
        self.f.handlers['world'] = {'ok': False, 'errorCode': 'STATE_CHANGED'}
        self.assertEqual(self.s.attack_preview()['status'], 'state_changed')
        self.assertFalse(any(c == 'attack_preview' for c, _ in self.f.calls))

    def test_reward_callback_exception_is_unknown_and_not_replayed(self):
        popup = {'ok': True, 'showing': True, 'popupType': 'IconRewardPopup', 'buttons': {
            'buttons': [{'id': 17, 'enabled': True, 'source': 'native_IconRewardPopup_RewardButton'}]}}
        self.f.handlers['popup_info'] = popup
        self.f.handlers['popup'] = {'ok': False, 'errorCode': 'POPUP_CALLBACK_UNCERTAIN', 'callbackAttempted': True}
        r = self.s.choose_popup(17, expected_popup=popup)
        self.assertEqual(r['status'], 'unknown')
        self.assertFalse(r['retryAllowed'])
        self.assertEqual(sum(c == 'popup' for c, _ in self.f.calls), 1)


if __name__ == '__main__': unittest.main()
