import unittest
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from observation import normalize_world, ObservationRejected
from test_observation import world_frame, unit


class PolarisProjectionTests(unittest.TestCase):
    def test_owned_frozen_and_explicit_clear(self):
        for value in (True,False,None):
            own=unit(owner=1);own['properties']={'frozen':value,'frozenAuthority':'owned-native-effects'}
            row=normalize_world(world_frame(units=[own]))['ownUnits'][0]
            self.assertIs(row['frozen'],value)

    def test_enemy_uses_only_public_renderer_authority(self):
        enemy=unit(owner=2)
        enemy['properties']={'frozen':True,'frozenAuthority':'visible-renderer-Frozen-particle','effects':['private']}
        row=normalize_world(world_frame(units=[enemy]))['units'][0]
        self.assertTrue(row['frozen']);self.assertNotIn('effects',row)
        enemy['properties']['frozenAuthority']='owned-native-effects'
        with self.assertRaises(ObservationRejected):normalize_world(world_frame(units=[enemy]))

    def test_no_integer_frozen_coercion(self):
        own=unit(owner=1);own['properties']={'frozen':0,'frozenAuthority':'owned-native-effects'}
        with self.assertRaises(ObservationRejected):normalize_world(world_frame(units=[own]))

    def test_ice_is_consistent_with_the_public_terrain(self):
        for terrain,ice in (('Ice',True),('Water',False),('Ocean',False),('Field',False)):
            frame=world_frame();frame['tiles'][0].update(terrain=terrain,ice=ice)
            self.assertIs(normalize_world(frame)['tiles'][0]['ice'],ice)
        frame['tiles'][0]['ice']=True
        with self.assertRaises(ObservationRejected):normalize_world(frame)

    def test_unknown_ice_remains_unknown(self):
        frame=world_frame();frame['tiles'][0].update(terrain=None,ice=None)
        self.assertIsNone(normalize_world(frame)['tiles'][0]['ice'])


if __name__=='__main__':unittest.main()
