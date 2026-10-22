import copy
import json
import tempfile
import unittest
from pathlib import Path

from sdk_memory import ObservedEnemyMemory
from test_observation import world_frame


class EnemyMemoryStateTests(unittest.TestCase):
    def setUp(self):
        self.binding = {'episodeId': 'ep-notes', 'playerId': 1, 'streamId': 'stream-notes', 'stateVersion': 10}
        self.unit = {'id': 'internal-must-not-persist', 'owner': '2', 'type': 'Warrior',
                     'coords': '(4,3)', 'health': '70', 'xp': '1', 'currentlyVisible': True, 'inSight': True}
        self.frame = world_frame(me=1, rev=200, units=[self.unit], episodeId='ep-notes',
                                 state={'streamId': 'stream-notes', 'stateVersion': 10})
        self.original = ObservedEnemyMemory()
        self.original.update(self.frame, self.binding)

    def restored(self):
        memory = ObservedEnemyMemory()
        memory.restore_state(json.loads(json.dumps(self.original.export_state())))
        return memory

    def test_roundtrip_preserves_public_fields_and_freezes_former_visible_notes(self):
        restored = self.restored().export_state()
        record = restored['records'][0]
        self.assertEqual(restored['scope'], ['ep-notes', 1, 'stream-notes'])
        self.assertEqual((record['hp'], record['unitType'], record['owner']), (70, 'Warrior', 2))
        self.assertEqual(record['lastSeenPosition'], [4, 3])
        self.assertEqual(record['lastSeenObservationRevision'], 200)
        self.assertEqual(record['lastSeenStateVersion'], 10)
        self.assertEqual(record['status'], 'unobserved')
        self.assertTrue(record['observationLost'])
        self.assertNotIn('internal-must-not-persist', json.dumps(restored))

    def test_first_sample_after_restart_creates_candidate_without_proving_same_unit(self):
        memory = self.restored()
        self.frame['observationRevision'] = 250
        records = memory.update(self.frame, self.binding)['records']
        self.assertEqual(len(records), 2)
        self.assertEqual(records[0]['status'], 'unobserved')
        self.assertEqual(records[1]['candidateOf'], records[0]['sightingId'])
        self.assertEqual(records[1]['association'], 'candidate')

    def test_invalid_record_or_unknown_field_does_not_partially_replace_existing_notes(self):
        good = self.original.export_state()
        corruptions = [
            lambda d: d['records'][0].update({'nativeUnitId': 9}),
            lambda d: d['records'].append(copy.deepcopy(d['records'][0])),
            lambda d: d['records'][0].update({'sightingId': True}),
            lambda d: d['records'][0].update({'hp': -1}),
            lambda d: d['records'][0].update({'lastSeenSampleId': 999}),
            lambda d: d['records'][0].update({'candidateOf': 1}),
            lambda d: d['records'][0].update({'status': []}),
            lambda d: d.update({'scope': ['ep-notes', True, 'stream-notes']}),
            lambda d: d.update({'nextSightingId': 1}),
            lambda d: d.update({'schema': 'future-schema'}),
        ]
        for corrupt in corruptions:
            value = copy.deepcopy(good)
            corrupt(value)
            with self.subTest(value=value):
                with self.assertRaises(ValueError):
                    self.original.restore_state(value)
                self.assertEqual(self.original.export_state(), good)

    def test_copies_do_not_alias_and_null_hp_stays_unknown(self):
        self.frame['units'][0]['health'] = None
        self.original.update(self.frame, self.binding)
        saved = self.original.export_state()
        self.assertIsNone(saved['records'][0]['hp'])
        memory = ObservedEnemyMemory()
        memory.restore_state(saved)
        saved['records'][0]['lastSeenPosition'][0] = 999
        saved['records'][0]['hp'] = 999
        self.assertEqual(memory.export_state()['records'][0]['lastSeenPosition'], [4, 3])
        self.assertIsNone(memory.export_state()['records'][0]['hp'])

    def test_empty_and_absent_state_do_not_fabricate_sightings(self):
        fresh = ObservedEnemyMemory()
        self.assertIsNone(fresh.export_state())
        fresh.restore_state(None)
        self.frame['units'] = []
        fresh.update(self.frame, self.binding)
        restored = ObservedEnemyMemory()
        restored.restore_state(fresh.export_state())
        self.assertEqual(restored.update(self.frame, self.binding)['records'], [])

    def test_frozen_loss_and_truncation_metadata_survive_restart(self):
        limited = ObservedEnemyMemory(max_records=1)
        limited.update(self.frame, self.binding)
        limited.update({**self.frame, 'units': []}, self.binding)
        limited.update(self.frame, self.binding)
        restored = ObservedEnemyMemory(max_records=1)
        restored.restore_state(limited.export_state())
        self.assertEqual(restored.export_state()['discardedRecordCount'], 1)
        result = restored.update({**self.frame, 'units': []}, self.binding)
        self.assertTrue(result['historyTruncated'])
        self.assertEqual(result['records'][0]['status'], 'unobserved')

    def test_new_stream_resets_restored_notes(self):
        result = self.restored().update(self.frame, {**self.binding, 'streamId': 'new-stream'})
        self.assertEqual(len(result['records']), 1)
        self.assertEqual(result['records'][0]['association'], 'new')

    def test_confirmed_destruction_is_not_revived_by_restore(self):
        self.frame['destroyed'] = [{'owner': 2, 'unitType': 'Warrior', 'coords': '(4,3)'}]
        self.original.update(self.frame, self.binding)
        result = self.restored().export_state()['records'][0]
        self.assertEqual(result['status'], 'confirmedDestroyed')
        self.assertFalse(result['observationLost'])

    def test_real_sdk_session_restores_notes_without_querying_hidden_or_current_state(self):
        from sdk import PolytopiaSDK
        from test_sdk import Fake
        fake = Fake()
        fake.handlers['world'] = self.frame
        fake.handlers['handoff_status'] = {'ok': True, 'phase': 'not_hotseat'}
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1]) as directory:
            path = Path(directory) / 'notes.json'
            with PolytopiaSDK(fake, player_id=1, session_path=path) as sdk:
                first = sdk.observe()['enemyMemory']['records'][0]
            fake.calls.clear()
            self.frame['observationRevision'] = 300
            self.frame['units'][0]['health'] = '60'
            with PolytopiaSDK(fake, player_id=1, session_path=path) as sdk:
                self.assertEqual(fake.calls, [])
                historical = sdk._enemy_memory.export_state()['records'][0]
                self.assertEqual(historical['status'], 'unobserved')
                self.assertEqual(historical['hp'], 70)
                records = sdk.observe()['enemyMemory']['records']
                self.assertEqual(records[0]['hp'], 70)
                self.assertEqual(records[1]['hp'], 60)
                self.assertEqual(records[1]['candidateOf'], first['sightingId'])
            self.assertNotIn('internal-must-not-persist', path.read_text(encoding='utf-8'))

    def test_failed_sdk_read_does_not_erase_notes_saved_before_process_boundary(self):
        from sdk import PolytopiaSDK
        from test_sdk import Fake
        fake = Fake()
        fake.handlers['world'] = self.frame
        fake.handlers['handoff_status'] = {'ok': True, 'phase': 'not_hotseat'}
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1]) as directory:
            path = Path(directory) / 'notes.json'
            with PolytopiaSDK(fake, player_id=1, session_path=path) as sdk:
                sdk.observe()
                fake.handlers['popup_info'] = TimeoutError('read interrupted')
                with self.assertRaises(TimeoutError):
                    sdk.observe()
            with PolytopiaSDK(fake, player_id=1, session_path=path) as sdk:
                records = sdk._enemy_memory.export_state()['records']
                self.assertEqual(len(records), 1)
                self.assertEqual(records[0]['hp'], 70)
                self.assertEqual(records[0]['status'], 'unobserved')


if __name__ == '__main__':
    unittest.main()
