import copy
import json
import unittest
from test_sdk import Fake
from test_observation import world_frame
from sdk import PolytopiaSDK
from sdk_memory import ObservedEnemyMemory

FRAME = world_frame(me=1, rev=200, units=[], episodeId='ep-memory-test',
                    state={'streamId': 'memory-stream', 'stateVersion': 10})


class MemoryTests(unittest.TestCase):
    def setUp(self):
        self.f = Fake()
        self.s = PolytopiaSDK(self.f, player_id=1)
        self.frame = copy.deepcopy(FRAME)
        self.enemy = {'id': 'hidden-internal-999', 'owner': '2', 'type': 'Warrior', 'coords': '(4,3)',
                      'health': '70', 'xp': '1', 'currentlyVisible': True, 'inSight': True}
        self.frame['units'].append(copy.deepcopy(self.enemy))
        self.f.handlers['world'] = self.frame
        self.f.handlers['handoff_status'] = {'ok': True, 'phase': 'not_hotseat'}

    def observe(self, revision, units=None):
        self.frame['observationRevision'] = revision
        if units is not None:
            self.frame['units'] = copy.deepcopy(units)
        return self.s.observe()['enemyMemory']

    def test_sdk_surfaces_player_notes_without_extra_requests_or_internal_id(self):
        result = self.observe(300)
        record = result['records'][0]
        self.assertEqual((record['unitType'], record['owner'], record['hp']), ('Warrior', 2, 70))
        self.assertEqual(record['lastSeenPosition'], [4, 3])
        self.assertEqual(record['lastSeenObservationRevision'], 300)
        self.assertNotIn('hidden-internal-999', json.dumps(result))
        self.assertEqual([c for c, _ in self.f.calls], ['handoff_status', 'world', 'popup_info', 'world'])

    def test_loss_freezes_hp_position_and_time_without_claiming_death(self):
        old = self.observe(300)['records'][0]
        self.frame['turn'] += 1
        result = self.observe(400, units=[])['records'][0]
        for field in ['hp', 'owner', 'unitType', 'lastSeenPosition', 'lastSeenTurn', 'lastSeenObservationRevision']:
            self.assertEqual(result[field], old[field])
        self.assertEqual(result['status'], 'unobserved')
        self.assertTrue(result['observationLost'])

    def test_bookend_and_menu_revision_gaps_do_not_duplicate_continuous_note(self):
        first = self.observe(300)['records'][0]
        self.frame['units'][-1]['health'] = '60'
        second = self.observe(500)['records']
        self.assertEqual(len(second), 1)
        self.assertEqual(second[0]['sightingId'], first['sightingId'])
        self.assertEqual(second[0]['hp'], 60)
        self.assertEqual(second[0]['association'], 'assumed_continuation')

    def test_reappearance_after_loss_is_candidate_not_proven_identity(self):
        first = self.observe(300)['records'][0]
        self.observe(400, units=[])
        records = self.observe(500, units=[self.enemy])['records']
        self.assertEqual(len(records), 2)
        self.assertEqual(records[0]['status'], 'unobserved')
        self.assertEqual(records[1]['candidateOf'], first['sightingId'])
        self.assertEqual(records[1]['association'], 'candidate')

    def test_partial_frame_does_not_erase_notes_or_link_across_gap(self):
        self.observe(300)
        self.frame['completeness'] = 'incomplete'
        failed = self.observe(400, units=[])
        self.assertEqual(failed['status'], 'unavailable')
        self.assertIsNone(failed['records'])
        self.frame['completeness'] = 'complete'
        records = self.observe(500, units=[self.enemy])['records']
        self.assertEqual(len(records), 2)
        self.assertEqual(records[0]['hp'], 70)
        self.assertEqual(records[1]['association'], 'candidate')

    def test_curtain_and_mixed_frame_do_not_consume_private_notes(self):
        self.observe(300)
        self.f.handlers['handoff_status'] = {'ok': True, 'phase': 'curtain', 'ready': False}
        self.f.calls.clear()
        self.assertEqual(self.s.observe()['status'], 'handoff')
        self.assertEqual(self.f.calls, [('handoff_status', {})])
        self.f.handlers['handoff_status'] = {'ok': True, 'phase': 'not_hotseat'}
        versions = [copy.deepcopy(self.frame), copy.deepcopy(self.frame)]
        versions[1]['state']['stateVersion'] += 1
        self.f.handlers['world'] = lambda _: versions.pop(0)
        self.assertEqual(self.s.observe()['status'], 'state_changed')
        self.f.handlers['world'] = self.frame
        records = self.observe(500)['records']
        self.assertEqual(len(records), 2)
        self.assertEqual(records[1]['association'], 'candidate')

    def test_backward_revision_rejected_and_returned_notes_are_copies(self):
        result = self.observe(500)
        result['records'][0]['hp'] = 999
        self.assertEqual(self.observe(400)['status'], 'unavailable')
        self.assertEqual(self.observe(600)['records'][0]['hp'], 70)

    def test_transport_failure_preserves_notes_and_breaks_assumed_continuity(self):
        self.observe(300)
        self.f.handlers['popup_info'] = TimeoutError('read lost')
        with self.assertRaises(TimeoutError):
            self.s.observe()
        self.f.handlers['popup_info'] = {'ok': True, 'showing': False}
        records = self.observe(500)['records']
        self.assertEqual(len(records), 2)
        self.assertEqual(records[0]['hp'], 70)
        self.assertEqual(records[1]['association'], 'candidate')

    def test_scope_reset_and_old_history_bound_are_explicit(self):
        memory = ObservedEnemyMemory(max_records=1)
        binding = {'episodeId': self.frame['episodeId'], 'playerId': 1, 'streamId': 'one', 'stateVersion': 1}
        memory.update(self.frame, binding)
        memory.update({**self.frame, 'units': []}, binding)
        result = memory.update(self.frame, binding)
        self.assertEqual(len(result['records']), 1)
        self.assertTrue(result['historyTruncated'])
        self.assertEqual(result['discardedRecordCount'], 1)
        result = memory.update(self.frame, {**binding, 'streamId': 'two'})
        self.assertEqual(result['records'][0]['association'], 'new')
        self.assertFalse(result['historyTruncated'])

    def test_visible_population_can_exceed_soft_limit_then_old_notes_are_bounded(self):
        memory = ObservedEnemyMemory(max_records=512)
        binding = {'episodeId': self.frame['episodeId'], 'playerId': 1,
                   'streamId': 'large-visible-set', 'stateVersion': 10}
        units = [{**self.enemy, 'id': str(index), 'coords': f'({index % 30},{index // 30})'}
                 for index in range(600)]
        frame = {**self.frame, 'units': units}
        visible = memory.update(frame, binding)
        self.assertEqual(len(visible['records']), 600)
        self.assertTrue(all(x['status'] == 'visible' for x in visible['records']))
        self.assertFalse(visible['historyTruncated'])
        frozen = memory.update({**frame, 'observationRevision': 201, 'units': []}, binding)
        self.assertEqual(len(frozen['records']), 512)
        self.assertEqual(frozen['discardedRecordCount'], 88)
        self.assertTrue(all(x['status'] == 'unobserved' for x in frozen['records']))
        restored = ObservedEnemyMemory(max_records=512)
        restored.restore_state(memory.export_state())
        again = restored.update({**frame, 'observationRevision': 202}, binding)
        self.assertEqual(len(again['records']), 600)
        self.assertTrue(all(x['status'] == 'visible' for x in again['records']))
        self.assertTrue(all(x['association'] != 'assumed_continuation' for x in again['records']))
        self.assertEqual(again['discardedRecordCount'], 600)
        self.assertEqual(len(restored._metadata), len(restored._memory.memory))

    def test_turnover_keeps_latest_observations_and_eviction_metadata_consistent(self):
        memory = ObservedEnemyMemory(max_records=8)
        binding = {'episodeId': self.frame['episodeId'], 'playerId': 1,
                   'streamId': 'turnover', 'stateVersion': 10}
        last_id = 0
        for sample in range(100):
            frame = {**self.frame, 'observationRevision': 200 + sample,
                     'units': [{**self.enemy, 'coords': f'({sample},0)', 'health': str(sample)}]}
            result = memory.update(frame, binding)
            current = [x for x in result['records'] if x['status'] == 'visible']
            self.assertEqual(len(current), 1)
            self.assertEqual((current[0]['lastSeenPosition'], current[0]['hp']), ([sample, 0], sample))
            self.assertGreater(current[0]['sightingId'], last_id)
            last_id = current[0]['sightingId']
            self.assertLessEqual(len(result['records']), 8)
            self.assertEqual(set(memory._metadata), set(memory._memory.memory))
        self.assertEqual(result['discardedRecordCount'], 92)
        restored = ObservedEnemyMemory(max_records=8)
        restored.restore_state(memory.export_state())
        self.assertEqual(restored.export_state()['discardedRecordCount'], 92)
        reset = restored.update(frame, {**binding, 'streamId': 'new-stream'})
        self.assertEqual(len(reset['records']), 1)
        self.assertFalse(reset['historyTruncated'])


if __name__ == '__main__':
    unittest.main()
