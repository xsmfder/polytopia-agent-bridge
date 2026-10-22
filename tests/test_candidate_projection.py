"""Native menu facts survive projection; unavailable domains never supply actions."""
import contextlib
import io
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import test_observation as legacy
from observation import normalize_world
from sdk import PolytopiaSDK
from test_sdk import Fake


class CandidateProjectionTests(unittest.TestCase):
    def test_native_new_unit_survives_without_static_type_table(self):
        frame = legacy.world_frame()
        frame['trainOptions']['cities'][0]['options'][0]['unitType'] = 'FutureNative_Unit2'
        result = normalize_world(frame)['trainOptions']
        self.assertEqual(result['status'], 'ok')
        self.assertEqual(result['cities'][0]['options'][0]['unitType'], 'FutureNative_Unit2')

    def test_training_still_checks_city_cost_and_native_permission(self):
        for field, value in [('cost', True), ('cost', -1), ('trainable', False),
                             ('unitType', 'Type.With.Namespace'), ('unitType', 'bad name')]:
            frame = legacy.world_frame()
            frame['trainOptions']['cities'][0]['options'][0][field] = value
            with self.subTest(field=field, value=value):
                self.assertEqual(normalize_world(frame)['trainOptions']['cities'][0]['options'], [])

    def test_bad_research_rows_do_not_break_other_domains(self):
        frame = legacy.world_frame()
        frame['researchableTechs']['items'].insert(0, None)
        frame['researchableTechs']['items'].append('unreadable')
        result = normalize_world(frame)
        self.assertEqual(result['researchableTechs']['status'], 'partial')
        self.assertEqual([r['tech'] for r in result['researchableTechs']['items']], ['Organization'])
        self.assertTrue(result['tiles'])

    def test_unavailable_training_cannot_republish_residual_options(self):
        frame = legacy.world_frame()
        frame['trainOptions']['status'] = 'unavailable'
        result = normalize_world(frame)['trainOptions']
        self.assertEqual(result['status'], 'unavailable')
        self.assertEqual(result['cities'], [])

    def test_native_identifier_format_is_shared_by_tech_and_units(self):
        for name in ('Namespace.Tech', 'bad name', '-1', '12', '', None):
            frame = legacy.world_frame()
            frame['researchableTechs']['items'][0]['tech'] = name
            with self.subTest(name=name):
                self.assertEqual(normalize_world(frame)['researchableTechs']['items'], [])

    def test_legacy_normalizer_acceptance_suite(self):
        with contextlib.redirect_stdout(io.StringIO()) as output:
            code = legacy.main()
        self.assertEqual(code, 0, output.getvalue())


class SDKCandidateDomainTests(unittest.TestCase):
    def setUp(self):
        self.transport = Fake()
        self.sdk = PolytopiaSDK(self.transport, player_id=2)

    def actions(self):
        return self.sdk.list_actions()['actions']

    def test_unavailable_domains_cannot_offer_old_native_rows(self):
        world = self.transport.handlers['world']
        for domain in ('researchableTechs', 'trainOptions', 'legalMoveTargets'):
            world[domain]['status'] = 'unavailable'
        actions = self.actions()
        self.assertFalse(any(a['args'].get('name') in ('research', 'train', 'move') for a in actions))
        self.assertTrue(any(a['args'].get('name') == 'end_turn' for a in actions))

    def test_failed_city_does_not_hide_other_city_training(self):
        world = self.transport.handlers['world']
        world['trainOptions']['cities'].insert(0, {
            'status': 'failed', 'coords': '(1,1)',
            'options': [{'unitType': 'FutureUnit', 'cost': 1, 'trainable': True}]})
        actions = [a for a in self.actions() if a['args'].get('name') == 'train']
        self.assertEqual([a['args']['at'] for a in actions], ['(2,3)'])

    def test_partial_menu_preserves_known_current_options(self):
        world = self.transport.handlers['world']
        world['researchableTechs']['status'] = 'partial'
        world['researchableTechs']['items'].insert(0, None)
        world['trainOptions']['status'] = 'partial'
        world['trainOptions']['cities'].insert(0, None)
        world['trainOptions']['cities'][1]['options'].insert(0, 'unreadable')
        actions = self.actions()
        self.assertEqual(sum(a['args'].get('name') == 'research' for a in actions), 1)
        self.assertEqual(sum(a['args'].get('name') == 'train' for a in actions), 1)

    def test_revalidation_cannot_submit_a_now_unavailable_candidate(self):
        from sdk import SDKError
        action = next(a for a in self.actions() if a['args'].get('name') == 'research')
        self.transport.handlers['world']['researchableTechs']['status'] = 'unavailable'
        with self.assertRaisesRegex(SDKError, 'absent or not positively executable'):
            self.sdk.execute(action)
        self.assertFalse(any(cmd == 'action' for cmd, _ in self.transport.calls))

    def test_unavailable_actor_targets_do_not_become_move_actions(self):
        actor = self.transport.handlers['world']['legalMoveTargets']['units'][0]
        for status in ('unavailable', 'partial', 'withheld', None):
            actor['status'] = status
            with self.subTest(status=status):
                self.assertFalse(any(a['args'].get('name') == 'move' for a in self.actions()))
        actor['status'] = 'ok'
        self.transport.handlers['world']['legalMoveTargets']['status'] = 'partial'
        self.assertTrue(any(a['args'].get('name') == 'move' for a in self.actions()))

    def test_unavailable_pending_reward_cannot_offer_residual_choices(self):
        for status in ('unavailable', 'none', 'trigger-present-popup-unreadable'):
            with self.subTest(status=status):
                self.transport.handlers['city_options']['pendingCityReward'] = {
                    'status': status, 'city': '(2,3)', 'choices': [{'reward': 'Workshop'}]}
                self.assertFalse(any(a['args'].get('name') == 'city_reward' for a in self.actions()))

    def test_partial_pending_reward_keeps_only_known_native_rows(self):
        self.transport.handlers['city_options']['pendingCityReward'] = {
            'status': 'offered-partial', 'city': '(2,3)',
            'choices': [None, 'unreadable', {'reward': 'Workshop'}]}
        rewards = [a for a in self.actions() if a['args'].get('name') == 'city_reward']
        self.assertEqual([a['args']['reward'] for a in rewards], ['Workshop'])

    def test_reward_revalidation_does_not_submit_an_unavailable_offer(self):
        from sdk import SDKError
        self.transport.handlers['city_options']['pendingCityReward'] = {
            'status': 'offered', 'city': '(2,3)', 'choices': [{'reward': 'Workshop'}]}
        action = next(a for a in self.actions() if a['args'].get('name') == 'city_reward')
        self.transport.handlers['city_options']['pendingCityReward']['status'] = 'unavailable'
        self.transport.calls.clear()
        with self.assertRaises(SDKError):
            self.sdk.execute(action)
        self.assertFalse(any(command == 'action' for command, _ in self.transport.calls))


if __name__ == '__main__':
    unittest.main()
