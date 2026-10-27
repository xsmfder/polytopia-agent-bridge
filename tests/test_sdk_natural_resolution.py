"""Natural-game regressions, with explicit native transport responses and no gameplay."""
import contextlib
import copy
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from sdk import PolytopiaSDK, SDKError
from agentcli import main, exit_code
from test_sdk import Fake, world


def notice():
    return {"ok": True, "schema": "popup-info-1", "status": "observed", "showing": True,
            "popupType": "IconRewardPopup", "title": "Tribe encountered", "body": "You encountered Elyrion.",
            "visibleTexts": [{"text": "7"}, {"text": "7"}], "truncated": False,
            "buttons": {"complete": True, "buttons": [{"id": 0, "hasCallback": True}]}}


class NaturalResolutionTests(unittest.TestCase):
    def setUp(self):
        self.f = Fake()
        self.sdk = PolytopiaSDK(self.f, player_id=2)
        self.ticks = [0.0]
        self.sdk.clock = lambda: self.ticks[0]
        self.sdk.sleep = lambda dt: self.ticks.__setitem__(0, self.ticks[0] + dt)

    def test_icon_auxiliary_text_can_disappear_or_reappear(self):
        observed = notice()
        for old, new in [(observed, {**observed, 'visibleTexts': []}),
                         ({**observed, 'visibleTexts': []}, observed)]:
            self.f.handlers['popup_info'] = new
            self.assertEqual(self.sdk.choose_popup(0, expected_popup=old)['status'], 'submitted')
        self.assertEqual(sum(c == 'popup' for c, _ in self.f.calls), 2)
        self.assertEqual(len(observed['visibleTexts']), 2)

    def test_frozen_natural_elyrion_encounter_accepts_only_auxiliary_sampling_change(self):
        fixture = json.loads((Path(__file__).parent/'fixtures/natural-popup-change.json').read_text(encoding='utf-8'))
        expected = fixture['exception']['kwargs']['expected_popup']
        current = fixture['nativeReads'][-1]['response']
        self.assertEqual(expected['visibleTexts'], [])
        self.assertEqual([row['text'] for row in current['visibleTexts']], ['7', '7'])
        self.f.handlers['popup_info'] = current
        result = self.sdk.choose_popup(0, expected_popup=expected)
        self.assertEqual(result['status'], 'submitted')
        self.assertEqual(sum(c == 'popup' for c, _ in self.f.calls), 1)

    def test_missing_auxiliary_text_never_ignores_real_decision_changes(self):
        expected = notice()
        for change in [{'title': 'Other tribe'}, {'body': 'Other reward'}, {'popupType': 'RewardPopup'},
                       {'buttons': {'complete': False, 'buttons': [{'id': 0}]}},
                       {'buttons': {'complete': True, 'buttons': [{'id': 1}]}},
                       {'truncated': True}]:
            self.f.handlers['popup_info'] = {**expected, 'visibleTexts': [], **change}
            with self.assertRaises(SDKError) as raised:
                self.sdk.choose_popup(0, expected_popup=expected)
            self.assertEqual(raised.exception.code, 'POPUP_CHANGED')
        self.assertFalse(any(c == 'popup' for c, _ in self.f.calls))

    def test_two_nonempty_auxiliary_samples_with_different_amounts_are_rejected(self):
        expected = notice()
        self.f.handlers['popup_info'] = {**expected, 'visibleTexts': [{'text': '8'}]}
        with self.assertRaises(SDKError):
            self.sdk.choose_popup(0, expected_popup=expected)
        self.assertFalse(any(c == 'popup' for c, _ in self.f.calls))

    def test_other_popup_types_and_ambiguous_options_remain_strict(self):
        for typ, buttons in [('BasicPopup', {'complete': True, 'buttons': [{'id': -1}]}),
                             ('IconRewardPopup', {'complete': True, 'buttons': [{'id': 0}, {'id': 1}]})]:
            expected = {**notice(), 'popupType': typ, 'buttons': buttons}
            self.f.handlers['popup_info'] = {**expected, 'visibleTexts': []}
            with self.assertRaises(SDKError):
                self.sdk.choose_popup(0 if typ == 'IconRewardPopup' else -1, expected_popup=expected)

    def pending(self):
        self.f.handlers['world'].update(canAct=False, pendingTriggerCountMine=1, popupShowing=False)
        return self.sdk.observe()

    def test_queue_is_bound_and_checkpointed_before_native_write(self):
        observed = self.pending()
        def advance(args):
            self.assertEqual(args['expectedEpisodeId'], 'ep-one')
            self.assertEqual(args['expectedPlayerId'], '2')
            self.assertEqual(args['expectedStreamId'], 'stream-one')
            self.assertEqual(args['expectedStateVersion'], '1')
            self.assertEqual(self.sdk._pending_writes[-1]['command'], 'popup')
            self.assertEqual(self.sdk._pending_writes[-1]['state'], 'uncertain')
            return {'ok': True, 'queueAdvanceRequested': True, 'callbackAttempted': True, 'outcome': 'invoked'}
        self.f.handlers['popup'] = advance
        result = self.sdk.advance_pending_popup(expected_observation=observed)
        self.assertEqual(result['status'], 'submitted')
        self.assertFalse(result['retryAllowed'])
        self.assertEqual(self.sdk._pending_writes, [])

    def test_queue_timeout_retains_durable_intent_and_cannot_be_replayed(self):
        observed = self.pending()
        self.f.handlers['popup'] = TimeoutError('reply lost')
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1]) as tmp:
            self.sdk = PolytopiaSDK(self.f, player_id=2, session_path=Path(tmp)/'state.json')
            result = self.sdk.advance_pending_popup(expected_observation=observed)
            self.assertEqual(result['status'], 'unknown')
            self.assertEqual(len(self.sdk._pending_writes), 1)
            self.sdk.close()
            self.sdk = PolytopiaSDK(self.f, player_id=2, session_path=Path(tmp)/'state.json')
            with self.assertRaises(SDKError) as raised:
                self.sdk.advance_pending_popup(expected_observation=observed)
            self.assertEqual(raised.exception.code, 'RECOVERY_REQUIRED')
            self.sdk.close()
        self.assertEqual(sum(c == 'popup' for c, _ in self.f.calls), 1)

    def test_queue_callback_failure_is_unknown_not_a_retryable_rejection(self):
        observed = self.pending()
        self.f.handlers['popup'] = {'ok': False, 'outcome': 'unknown', 'callbackAttempted': True,
                                  'errorCode': 'PENDING_POPUP_CALLBACK_FAILED'}
        result = self.sdk.advance_pending_popup(expected_observation=observed)
        self.assertEqual(result['status'], 'unknown')
        self.assertEqual(self.sdk._pending_writes[0]['state'], 'uncertain')

    def test_queue_must_not_cross_seat_state_popup_or_unknown_count(self):
        observed = self.pending()
        for change in [{'myTurn': False}, {'pendingTriggerCountMine': None}, {'pendingTriggerCountMine': 0},
                       {'matchEnded': True}, {'state': {'streamId': 'stream-one', 'stateVersion': 2}}]:
            self.f.handlers['world'] = {**world(), 'canAct': False, 'pendingTriggerCountMine': 1, **change}
            with self.assertRaises(SDKError):
                self.sdk.advance_pending_popup(expected_observation=observed)
        self.f.handlers['world'] = {**world(), 'pendingTriggerCountMine': 1}
        self.f.handlers['popup_info'] = notice()
        with self.assertRaises(SDKError):
            self.sdk.advance_pending_popup(expected_observation=observed)
        self.assertFalse(any(c == 'popup' for c, _ in self.f.calls))

    def test_driver_advances_queue_then_stops_at_actual_reward_choice(self):
        self.pending()
        reward = {'ok': True, 'showing': True, 'popupType': 'RewardPopup',
                  'buttons': {'complete': True, 'buttons': []},
                  'rewardDisplay': {'city': '(2,3)', 'canDismiss': False}}
        def advance(args):
            self.f.handlers['popup_info'] = reward
            return {'ok': True, 'queueAdvanceRequested': True}
        self.f.handlers['popup'] = advance
        result = self.sdk.resolve_pending()
        self.assertEqual(result['status'], 'needs_resolution')
        self.assertEqual(result['nextStep'], 'choose_city_reward')
        self.assertEqual(result['observation']['popup'], reward)
        self.assertEqual(sum(c == 'popup' for c, _ in self.f.calls), 1)
        self.assertFalse(any(c == 'action' for c, _ in self.f.calls))

    def test_driver_dismisses_selected_city_then_advances_next_city(self):
        self.pending()
        self.f.handlers['popup_info'] = {'ok': True, 'showing': True, 'popupType': 'RewardPopup',
            'buttons': {'complete': True, 'buttons': []}, 'rewardDisplay': {
                'city': '(2,3)', 'selectedReward': 'SuperUnit', 'canDismiss': True}}
        def ui(args):
            if args['op'] == 'dismiss_resolved_reward':
                self.assertEqual(args['reward'], 'SuperUnit')
                self.f.handlers['popup_info'] = {'ok': True, 'showing': False}
                return {'ok': True, 'dismissRequested': True, 'rewardResubmitted': False}
            self.f.handlers['popup_info'] = {'ok': True, 'showing': True, 'popupType': 'RewardPopup',
                                           'buttons': {'complete': True, 'buttons': []}}
            return {'ok': True, 'queueAdvanceRequested': True}
        self.f.handlers['popup'] = ui
        result = self.sdk.resolve_pending()
        self.assertEqual([s['operation'] for s in result['steps']], ['dismiss', 'next'])
        self.assertEqual(result['nextStep'], 'choose_city_reward')
        self.assertFalse(any(c == 'action' for c, _ in self.f.calls))

    def test_driver_never_replays_queue_when_native_state_is_unchanged(self):
        self.pending()
        self.f.handlers['popup'] = {'ok': True, 'queueAdvanceRequested': True}
        result = self.sdk.resolve_pending()
        self.assertEqual(result['status'], 'needs_resolution')
        self.assertEqual(sum(c == 'popup' for c, _ in self.f.calls), 1)

    def test_driver_never_replays_visible_display_with_new_request_id(self):
        self.f.handlers['popup_info'] = lambda args: {**notice(), 'reqId': len(self.f.calls)}
        result = self.sdk.resolve_pending()
        self.assertEqual(result['status'], 'needs_resolution')
        self.assertEqual(sum(c == 'popup' for c, _ in self.f.calls), 1)

    def test_driver_does_not_replay_acknowledgement_after_auxiliary_animation(self):
        self.f.handlers['popup_info'] = notice()
        def unchanged(args):
            self.f.handlers['popup_info'] = {**notice(), 'visibleTexts': []}
            return {'ok': True, 'invokedCallback': True}
        self.f.handlers['popup'] = unchanged
        result = self.sdk.resolve_pending()
        self.assertEqual(result['status'], 'needs_resolution')
        self.assertEqual(sum(c == 'popup' for c, _ in self.f.calls), 1)

    def test_driver_never_replays_reward_hide_with_new_request_id(self):
        self.f.handlers['popup_info'] = lambda args: {'ok': True, 'showing': True, 'reqId': len(self.f.calls),
            'popupType': 'RewardPopup', 'rewardDisplay': {
                'city': '(2,3)', 'selectedReward': 'SuperUnit', 'canDismiss': True}}
        result = self.sdk.resolve_pending()
        self.assertEqual(result['status'], 'needs_resolution')
        self.assertEqual(sum(c == 'popup' for c, _ in self.f.calls), 1)

    def test_driver_waits_original_ticket_and_preserves_unknown(self):
        action = next(a for a in self.sdk.list_actions()['actions'] if a['args'].get('name') == 'research')
        self.sdk.execute(action)
        original = copy.deepcopy(self.sdk.unresolved)
        self.f.handlers['settlement'].update(settlementComplete=True, effectConfirmation='unknown', actionCompleted=None)
        result = self.sdk.resolve_pending()
        self.assertEqual(result['status'], 'unknown')
        self.assertEqual(self.sdk.unresolved, original)
        self.assertEqual(sum(c == 'action' for c, _ in self.f.calls), 1)
        self.assertTrue(any(c == 'settlement' for c, _ in self.f.calls))

    def test_driver_returns_ready_only_after_receipts_and_fresh_world(self):
        action = next(a for a in self.sdk.list_actions()['actions'] if a['args'].get('name') == 'research')
        self.sdk.execute(action)
        result = self.sdk.resolve_pending()
        self.assertEqual(result['status'], 'ready')
        self.assertIsNone(self.sdk.unresolved)
        self.assertEqual(sum(c == 'action' for c, _ in self.f.calls), 1)

    def test_driver_does_not_read_a_hidden_hotseat_world(self):
        self.f.handlers['handoff_status'] = {'ok': True, 'ready': False, 'phase': 'human_turn'}
        result = self.sdk.resolve_pending()
        self.assertEqual(result['status'], 'handoff')
        self.assertEqual([c for c, _ in self.f.calls], ['handoff_status'])

    def test_driver_does_not_select_multi_option_notifications(self):
        self.f.handlers['popup_info'] = {**notice(), 'buttons': {'complete': True, 'buttons': [{'id': 0}, {'id': 1}]}}
        result = self.sdk.resolve_pending()
        self.assertEqual(result['nextStep'], 'choose_popup')
        self.assertFalse(any(c == 'popup' for c, _ in self.f.calls))

    def test_cli_exposes_resolution_and_single_advance(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            main(['--player', '2', 'resolve_pending', '--max-steps', '1'], sdk_factory=lambda **kwargs: self.sdk)
        self.assertEqual(json.loads(output.getvalue())['status'], 'ready')
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1]) as tmp:
            source = Path(tmp)/'observation.json'
            source.write_text(json.dumps(self.pending()), encoding='utf-8')
            self.f.handlers['popup'] = {'ok': True, 'queueAdvanceRequested': True}
            with contextlib.redirect_stdout(io.StringIO()):
                code = main(['--player', '2', 'advance_pending_popup', str(source)], sdk_factory=lambda **kwargs: self.sdk)
            self.assertEqual(code, exit_code({'status': 'submitted'}))

    def test_driver_limits_are_validated_before_io(self):
        for kwargs in [{'timeout': -1}, {'timeout': float('nan')}, {'interval': 0}, {'max_steps': True}, {'max_steps': 0}]:
            with self.assertRaises(SDKError):
                self.sdk.resolve_pending(**kwargs)
        self.assertEqual(self.f.calls, [])


if __name__ == '__main__':
    unittest.main()
