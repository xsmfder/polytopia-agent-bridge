import unittest
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from observation import normalize_world, ObservationRejected
from test_observation import world_frame, unit


class PublicAlgaeTests(unittest.TestCase):
    def frame(self):
        frame=world_frame()
        tile=frame['tiles'][0]
        tile.update(explored=True,algae=True,algaeScope='owned-explored-tile')
        return frame

    def test_owned_public_effect_is_retained(self):
        self.assertIs(normalize_world(self.frame())['tiles'][0]['algae'],True)

    def test_currently_visible_foreign_tile_only_exports_named_effect(self):
        frame=self.frame();frame['tiles'][0].update(owner=2,inSight=True,algaeScope='visible-explored-tile')
        tile=normalize_world(frame)['tiles'][0]
        self.assertTrue(tile['algae']);self.assertIsNone(tile.get('terrainEffects'))

    def test_stale_or_withheld_state_cannot_assert_algae(self):
        for scope in ('visible-explored-tile','withheld'):
            frame=self.frame();frame['tiles'][0].update(owner=2,inSight=False,algaeScope=scope)
            with self.subTest(scope=scope),self.assertRaises(ObservationRejected):normalize_world(frame)

    def test_own_unit_on_foreign_tile_is_a_valid_public_scope(self):
        frame=self.frame();frame['tiles'][0].update(owner=2,algaeScope='own-unit-tile')
        frame['units']=[unit(owner=1,coords='(1,1)')]
        self.assertTrue(normalize_world(frame)['tiles'][0]['algae'])

    def test_unknown_is_not_absence(self):
        frame=self.frame();frame['tiles'][0].update(algae=None,algaeScope='withheld')
        self.assertIsNone(normalize_world(frame)['tiles'][0]['algae'])

    def test_owned_effects_must_agree(self):
        frame=self.frame();frame['tiles'][0].update(terrainEffects=[],terrainEffectsScope='owned-explored-tile')
        with self.assertRaises(ObservationRejected):normalize_world(frame)


if __name__=='__main__':unittest.main()
