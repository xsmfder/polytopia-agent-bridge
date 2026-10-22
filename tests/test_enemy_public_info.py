"""Native panel projection and player-note persistence; no live game calls."""
import copy
import json
import math
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from observation import normalize_world, public_unit_info, PUBLIC_UNIT_STATS
from sdk_memory import ObservedEnemyMemory
from test_observation import world_frame, unit


def panel():
    return {'schema': 'unit-public-info-1', 'status': 'ok',
            'kills': 2, 'killsStatus': 'ok', 'veteran': False, 'veteranStatus': 'ok',
            'progressKind': 'veteran', 'progressLimit': 3, 'progressTarget': None,
            'passengerType': 'Swordsman', 'passengerStatus': 'ok',
            'homeCityName': '城市名', 'homeCityStatus': 'ok',
            'stats': {'health': 7, 'maxHealth': 10, 'attack': 3, 'attackBonus': 0,
                      'defence': 2, 'defenceMultiplier': 1.5, 'movement': 2,
                      'movementBonus': -1, 'range': 1}}


def visuals():
    return {'schema': 'unit-visual-effects-1', 'status': 'ok',
            'particles': {'status': 'ok', 'types': ['FutureGlow', 'Poisoned']},
            'overlay': {'status': 'ok', 'color': [0.2, 0.5, 1, 1], 'strength': 0.75}}


class EnemyPublicInfoTests(unittest.TestCase):
    def setUp(self):
        self.actor = unit()
        self.actor['properties'] = {'publicInfo': panel()}
        self.frame = world_frame(units=[self.actor])
        self.binding = {'episodeId': self.frame['matchContext'], 'playerId': 1,
                        'streamId': 'panel-stream', 'stateVersion': 10}
        self.memory = ObservedEnemyMemory()

    def normalized(self):
        return normalize_world(self.frame)['units'][0]['publicInfo']

    def update(self, units=None):
        self.frame['observationRevision'] += 1
        if units is not None:
            self.frame['units'] = units
        return self.memory.update(self.frame, self.binding)['records']

    def test_actual_render_cues_preserve_future_names_and_strip_private_details(self):
        source = visuals()
        source['particles']['poolId'] = 'private-pool'
        source['overlay']['nativePointer'] = 'private-renderer'
        self.actor['properties']['publicInfo']['visualEffects'] = source
        result = self.normalized()['visualEffects']
        self.assertEqual(result, visuals())
        self.assertNotIn('private', json.dumps(result))

    def test_visual_unavailability_partial_and_zero_are_distinct(self):
        source = visuals()
        self.actor['properties']['publicInfo']['visualEffects'] = source
        source['particles'].update(types=[])
        source['overlay']['strength'] = 0
        self.assertEqual(self.normalized()['visualEffects']['particles']['types'], [])
        self.assertEqual(self.normalized()['visualEffects']['overlay']['strength'], 0)
        source['status'] = 'partial'
        source['particles'].update(status='unavailable', types=['must-not-survive'])
        result = self.normalized()['visualEffects']
        self.assertIsNone(result['particles']['types'])
        self.assertEqual(result['overlay']['strength'], 0)
        source['status'] = 'withheld'
        result = self.normalized()['visualEffects']
        self.assertEqual(result['status'], 'withheld')
        self.assertIsNone(result['particles']['types'])
        self.assertIsNone(result['overlay']['color'])

    def test_invalid_visual_numbers_and_names_do_not_corrupt_other_panel_fields(self):
        for bad in (True, '0.5', float('nan'), float('inf'), 1e200, 10**1000):
            with self.subTest(bad=bad):
                source = visuals()
                source['overlay']['color'][0] = bad
                source['particles']['types'] += ['Hidden.Type', 'Poisoned', 7]
                self.actor['properties']['publicInfo']['visualEffects'] = source
                result = self.normalized()
                self.assertEqual(result['kills'], 2)
                self.assertEqual(result['visualEffects']['particles']['types'], ['FutureGlow', 'Poisoned'])
                self.assertEqual(result['visualEffects']['particles']['status'], 'partial')
                self.assertEqual(result['visualEffects']['overlay']['status'], 'unavailable')

    def test_render_cues_update_only_while_visible_and_freeze_in_notes(self):
        source = visuals()
        self.actor['properties']['publicInfo']['visualEffects'] = source
        first = self.update()[0]
        self.assertEqual(first['publicInfo']['visualEffects'], visuals())
        source['overlay']['strength'] = 0.25
        current = self.update()[0]
        self.assertEqual(current['publicInfo']['visualEffects']['overlay']['strength'], 0.25)
        self.assertEqual(first['publicInfo']['visualEffects']['overlay']['strength'], 0.75)
        frozen = self.update(units=[])[0]
        source['overlay']['strength'] = 0.9
        self.assertEqual(frozen['publicInfo']['visualEffects']['overlay']['strength'], 0.25)
        restored = ObservedEnemyMemory()
        restored.restore_state(self.memory.export_state())
        self.assertEqual(restored.export_state()['records'][0]['publicInfo']['visualEffects'], frozen['publicInfo']['visualEffects'])

    def test_persisted_render_cues_reject_nested_ids_and_noncanonical_values(self):
        self.actor['properties']['publicInfo']['visualEffects'] = visuals()
        self.update()
        saved = self.memory.export_state()
        mutations = [lambda v: v['particles'].update(poolId=77),
                     lambda v: v['overlay'].update(unitId=51),
                     lambda v: v['overlay'].update(color=[True, 0, 0, 1]),
                     lambda v: v['particles'].update(types=['Poisoned', 'FutureGlow']),
                     lambda v: v.update(status='withheld')]
        for change in mutations:
            with self.subTest(change=change):
                broken = copy.deepcopy(saved)
                change(broken['records'][0]['publicInfo']['visualEffects'])
                with self.assertRaises(ValueError):
                    ObservedEnemyMemory().restore_state(broken)

    def test_legacy_panels_remain_readable_with_or_without_optional_render_cues(self):
        legacy = panel()
        for field in ('progressKind', 'progressLimit', 'progressTarget'):
            legacy.pop(field)
        self.assertNotIn('visualEffects', public_unit_info(legacy, persisted=True))
        legacy['visualEffects'] = visuals()
        self.assertEqual(public_unit_info(legacy, persisted=True)['visualEffects'], visuals())

    def test_normalizer_keeps_native_display_values_without_raw_health_scaling(self):
        result = self.normalized()
        self.assertEqual(result, panel())
        self.assertEqual(result['stats']['health'], 7)
        self.assertEqual(result['stats']['defenceMultiplier'], 1.5)
        self.assertEqual(result['stats']['movementBonus'], -1)
        self.assertEqual(result['passengerType'], 'Swordsman')
        self.assertNotIn('executionId', normalize_world(self.frame)['units'][0])

    def test_unknown_fields_are_not_projected_from_panel_or_old_relations(self):
        self.actor['properties']['relations'] = {'passenger': {'id': 'secret-passenger'}}
        self.actor['properties']['publicInfo'].update(
            authority='secret-authority', passenger={'id': 'secret-passenger'},
            homeCity={'coords': '(99,99)'})
        self.actor['properties']['publicInfo']['stats']['nativeSource'] = 'secret-pointer'
        result = json.dumps(self.normalized())
        self.assertNotIn('secret', result)
        self.assertNotIn('99,99', result)
        self.assertEqual(self.normalized(), panel())

    def test_missing_panel_and_future_schema_remain_unknown(self):
        del self.actor['properties']['publicInfo']
        self.assertIsNone(self.normalized())
        self.actor['properties']['publicInfo'] = {'schema': 'future-panel'}
        self.assertIsNone(self.normalized())
        del self.actor['properties']
        self.assertIsNone(self.normalized())

    def test_not_displayed_kills_do_not_fall_back_to_old_raw_xp(self):
        self.actor['xp'] = 99
        self.actor['properties']['publicInfo'].update(kills=99, killsStatus='not-displayed',
                                                     veteran=True)
        result = self.normalized()
        self.assertEqual(result['status'], 'ok')
        self.assertIsNone(result['kills'])
        self.assertTrue(result['veteran'])
        self.assertEqual(result['killsStatus'], 'not-displayed')
        for field in ('progressKind', 'progressLimit', 'progressTarget'):
            self.assertIsNone(result[field])

    def test_none_passenger_and_city_differ_from_unreadable_values(self):
        self.actor['properties']['publicInfo'].update(
            passengerType=None, passengerStatus='none', homeCityName=None, homeCityStatus='none')
        self.assertEqual(self.normalized()['status'], 'ok')
        self.actor['properties']['publicInfo']['passengerStatus'] = 'unavailable'
        self.assertEqual(self.normalized()['status'], 'partial')

    def test_invalid_scalars_withhold_only_panel_field_not_complete_map(self):
        for field, values in {'kills': [True, -1, 2.5, '2'],
                              'veteran': [1, 'false'], 'passengerType': [[], 1, ''],
                              'homeCityName': [{}, 7]}.items():
            for bad in values:
                with self.subTest(field=field, bad=bad):
                    self.actor['properties']['publicInfo'] = panel()
                    self.actor['properties']['publicInfo'][field] = bad
                    result = self.normalized()
                    self.assertEqual(result['status'], 'partial')
                    self.assertIsNone(result[field])
                    self.assertEqual(result['stats'], panel()['stats'])

    def test_invalid_stats_never_round_truncate_or_default_to_zero(self):
        for field in PUBLIC_UNIT_STATS:
            for bad in [True, '2', [], math.inf, -math.inf, math.nan]:
                with self.subTest(field=field, bad=bad):
                    self.actor['properties']['publicInfo'] = panel()
                    self.actor['properties']['publicInfo']['stats'][field] = bad
                    result = self.normalized()
                    self.assertIsNone(result['stats'][field])
                    self.assertEqual(result['status'], 'partial')
        self.actor['properties']['publicInfo'] = panel()
        self.actor['properties']['publicInfo']['stats']['attack'] = -1
        self.assertIsNone(self.normalized()['stats']['attack'])

    def test_unavailable_panel_never_preserves_appended_values(self):
        self.actor['properties']['publicInfo']['status'] = 'unavailable'
        result = self.normalized()
        self.assertEqual(result['status'], 'unavailable')
        self.assertTrue(all(value is None for value in result['stats'].values()))
        for field in ['kills', 'veteran', 'passengerType', 'homeCityName']:
            self.assertIsNone(result[field])
        for field in ('progressKind', 'progressLimit', 'progressTarget'):
            self.assertIsNone(result[field])

    def test_visible_refresh_then_loss_freezes_entire_panel_and_returns_copies(self):
        first = self.update()[0]
        first['publicInfo']['stats']['attack'] = 999
        self.actor['properties']['publicInfo']['kills'] = 3
        second = self.update()[0]
        self.assertEqual(second['sightingId'], first['sightingId'])
        self.assertEqual(second['publicInfo']['kills'], 3)
        self.assertEqual(second['publicInfo']['stats']['attack'], 3)
        frozen = self.update([])[0]
        self.actor['properties']['publicInfo']['homeCityName'] = 'Unseen change'
        self.assertEqual(frozen['publicInfo'], second['publicInfo'])
        self.assertEqual(frozen['status'], 'unobserved')
        again = self.update([self.actor])
        self.assertEqual(again[0]['publicInfo'], frozen['publicInfo'])
        self.assertEqual(again[1]['publicInfo']['homeCityName'], 'Unseen change')
        self.assertEqual(again[1]['candidateOf'], frozen['sightingId'])

    def test_restart_preserves_panel_and_breaks_continuity(self):
        self.update()
        saved = self.memory.export_state()
        restored = ObservedEnemyMemory()
        restored.restore_state(json.loads(json.dumps(saved)))
        saved['records'][0]['publicInfo']['stats']['range'] = 999
        historical = restored.export_state()['records'][0]
        self.assertEqual(historical['publicInfo'], panel())
        self.assertEqual(historical['status'], 'unobserved')
        self.actor['properties']['publicInfo']['kills'] = 5
        records = restored.update(self.frame, self.binding)['records']
        self.assertEqual(records[0]['publicInfo']['kills'], 2)
        self.assertEqual(records[1]['publicInfo']['kills'], 5)

    def test_old_state_1_records_without_public_info_still_restore(self):
        self.update()
        saved = self.memory.export_state()
        del saved['records'][0]['publicInfo']
        restored = ObservedEnemyMemory()
        restored.restore_state(saved)
        record = restored.export_state()['records'][0]
        self.assertIsNone(record['publicInfo'])
        self.assertEqual(record['status'], 'unobserved')

    def test_invalid_persisted_panel_rejected_atomically_including_nested_extra_fields(self):
        self.update()
        good = self.memory.export_state()
        corruptions = [
            lambda p: p.update(passenger={'id': 9}),
            lambda p: p.update(homeCity={'coords': [9, 9]}),
            lambda p: p['stats'].update(nativeUnitId=9),
            lambda p: p.update(kills=True),
            lambda p: p.update(veteran=1),
            lambda p: p.update(homeCityName=['city']),
            lambda p: p['stats'].update(attack=math.inf),
            lambda p: p['stats'].update(attack=True),
            lambda p: p['stats'].update(attack=-1),
            lambda p: p.update(passengerStatus='none'),
            lambda p: p.update(schema='future-schema'),
            lambda p: p.pop('kills'),
        ]
        for corrupt in corruptions:
            saved = copy.deepcopy(good)
            corrupt(saved['records'][0]['publicInfo'])
            with self.subTest(panel=saved['records'][0]['publicInfo']):
                with self.assertRaises(ValueError):
                    self.memory.restore_state(saved)
                self.assertEqual(self.memory.export_state(), good)

    def test_evolution_progress_keeps_public_limit_and_target(self):
        self.actor['properties']['publicInfo'].update(progressKind='evolve',
                                                     progressLimit=7, progressTarget='DragonEgg')
        result = self.normalized()
        self.assertEqual(result['status'], 'ok')
        self.assertEqual((result['progressKind'], result['progressLimit'], result['progressTarget']),
                         ('evolve', 7, 'DragonEgg'))
        self.assertEqual(self.update()[0]['publicInfo'], result)

    def test_incomplete_or_invalid_progress_withholds_only_missing_fields(self):
        for field, bad in [('progressKind', 'unknown'), ('progressLimit', True),
                           ('progressLimit', -1), ('progressLimit', '3')]:
            with self.subTest(field=field, bad=bad):
                self.actor['properties']['publicInfo'] = panel()
                self.actor['properties']['publicInfo'][field] = bad
                result = self.normalized()
                self.assertEqual(result['status'], 'partial')
                self.assertIsNone(result[field])
                self.assertEqual(result['kills'], 2)
        self.actor['properties']['publicInfo'] = panel()
        self.actor['properties']['publicInfo'].update(progressKind='evolve', progressTarget=None)
        self.assertEqual(self.normalized()['status'], 'partial')
        self.assertEqual(self.normalized()['progressLimit'], 3)

    def test_no_progress_is_inferred_when_kills_are_not_displayed(self):
        for status in ['not-displayed', 'unavailable']:
            self.actor['properties']['publicInfo'] = panel()
            self.actor['properties']['publicInfo']['killsStatus'] = status
            result = self.normalized()
            for field in ('progressKind', 'progressLimit', 'progressTarget'):
                self.assertIsNone(result[field])

    def test_earlier_panel_shape_restores_and_exports_canonical_progress_unknown(self):
        self.update()
        saved = self.memory.export_state()
        for field in ('progressKind', 'progressLimit', 'progressTarget'):
            del saved['records'][0]['publicInfo'][field]
        restored = ObservedEnemyMemory()
        restored.restore_state(saved)
        result = restored.export_state()['records'][0]['publicInfo']
        self.assertEqual(result['status'], 'partial')
        self.assertEqual(result['kills'], 2)
        for field in ('progressKind', 'progressLimit', 'progressTarget'):
            self.assertIsNone(result[field])
        # The upgraded canonical record must itself remain restorable.
        again = ObservedEnemyMemory()
        again.restore_state(restored.export_state())
        self.assertEqual(again.export_state()['records'][0]['publicInfo'], result)

    def test_persisted_progress_cannot_carry_hidden_objects_or_invalid_values(self):
        self.update()
        good = self.memory.export_state()
        corruptions = [
            lambda p: p.update(progressKind={'nativeUnitId': 9}),
            lambda p: p.update(progressLimit=True),
            lambda p: p.update(progressTarget={'cityCoords': [9, 9]}),
            lambda p: p.update(progressTarget='HiddenTargetForVeteran'),
            lambda p: p.update(progressKind='evolve'),
            lambda p: p.update(progressState={'targetCity': [9, 9]}),
            lambda p: p.update(kills=None, killsStatus='not-displayed'),
        ]
        for corrupt in corruptions:
            saved = copy.deepcopy(good)
            corrupt(saved['records'][0]['publicInfo'])
            with self.subTest(panel=saved['records'][0]['publicInfo']):
                with self.assertRaises(ValueError):
                    self.memory.restore_state(saved)
                self.assertEqual(self.memory.export_state(), good)


if __name__ == '__main__':
    unittest.main()
