import copy
import sys
from pathlib import Path
import unittest

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from observation import normalize_world, ObservationRejected
from test_observation import world_frame, unit

class AquarionProjectionTests(unittest.TestCase):
    def catalog(self):
        return {'schema':'tribe-catalog-1','status':'ok','tribe':'Aquarion',
                'techs':[{'type':'MarineLife','baseCost':5}],
                'units':[{'type':'Jelly','baseCost':8,'abilities':['Tentacles']}],
                'improvements':[{'type':'Atoll','baseCost':5}]}

    def test_special_native_catalog_survives_the_planner_projection(self):
        frame=world_frame();frame['tribeCatalog']=self.catalog()
        result=normalize_world(frame)['tribeCatalog']
        self.assertEqual(result['techs'][0]['type'],'MarineLife')
        self.assertEqual(result['units'][0]['abilities'],['Tentacles'])
        self.assertEqual(result['improvements'][0]['type'],'Atoll')

    def test_unknown_catalog_is_not_promoted_to_empty_complete(self):
        frame=world_frame();frame['tribeCatalog']={'schema':'tribe-catalog-1','status':'unavailable',
                                               'techs':None,'units':None,'improvements':None}
        result=normalize_world(frame)['tribeCatalog']
        self.assertEqual(result['status'],'unavailable');self.assertIsNone(result['units'])

    def test_unknown_extra_catalog_fields_are_not_forwarded(self):
        frame=world_frame();catalog=self.catalog();catalog['privateState']={'x':9}
        catalog['units'][0]['hiddenUnitId']=12;frame['tribeCatalog']=catalog
        result=normalize_world(frame)['tribeCatalog']
        self.assertNotIn('privateState',result);self.assertNotIn('hiddenUnitId',result['units'][0])

    def test_native_technology_unlock_links_survive_with_strict_projection(self):
        frame=world_frame();catalog=self.catalog()
        catalog['techs'][0]['unlocks']={'units':['Shark','Shark'],'improvements':['Atoll'],'private':42}
        frame['tribeCatalog']=catalog
        result=normalize_world(frame)['tribeCatalog']['techs'][0]['unlocks']
        self.assertEqual(result,{'units':['Shark'],'improvements':['Atoll']})

    def test_unreadable_unlock_links_are_not_promoted_to_empty(self):
        for invalid in (None,{'units':None,'improvements':[]},{'units':[12],'improvements':[]}):
            with self.subTest(value=invalid),self.assertRaises(ObservationRejected):
                frame=world_frame();catalog=self.catalog();catalog['techs'][0]['unlocks']=invalid
                frame['tribeCatalog']=catalog;normalize_world(frame)

    def test_owned_tile_flood_effect_survives(self):
        frame=world_frame(me=1);frame['tiles'][0].update(owner=1,explored=True,
            terrainEffects=['Flooded'],terrainEffectsScope='owned-explored-tile')
        self.assertEqual(normalize_world(frame)['tiles'][0]['terrainEffects'],['Flooded'])

    def test_enemy_or_unowned_tile_effects_are_rejected(self):
        for owner in (0,2,None):
            with self.subTest(owner=owner),self.assertRaises(ObservationRejected):
                frame=world_frame(me=1);frame['tiles'][0].update(owner=owner,explored=True,
                    terrainEffects=['Tentacle'],terrainEffectsScope='owned-explored-tile')
                normalize_world(frame)

    def test_withheld_effects_stay_unknown(self):
        frame=world_frame(me=1);frame['tiles'][0].update(owner=2,terrainEffects=None,terrainEffectsScope='withheld')
        self.assertIsNone(normalize_world(frame)['tiles'][0]['terrainEffects'])

    def test_own_bubble_and_amphibious_ability_survive_standard_observation(self):
        own=unit(owner=1)
        own['properties']={'abilities':['Swim','Amphibious'],'effects':['Bubble']}
        result=normalize_world(world_frame(units=[own]))['ownUnits'][0]
        self.assertEqual(result['effects'],['Bubble'])
        self.assertEqual(result['abilities'],['Amphibious','Swim'])

    def test_enemy_mechanical_effects_are_not_forwarded(self):
        enemy=unit(owner=2)
        enemy['properties']={'abilities':['Swim'],'effects':['Bubble']}
        result=normalize_world(world_frame(units=[enemy]))['units'][0]
        self.assertNotIn('effects',result);self.assertNotIn('abilities',result)

    def test_unknown_own_effects_stay_unknown_and_malformed_effects_are_rejected(self):
        own=unit(owner=1);own['properties']={'effects':None}
        self.assertIsNone(normalize_world(world_frame(units=[own]))['ownUnits'][0]['effects'])
        own['properties']['effects']=[12]
        with self.assertRaises(ObservationRejected):normalize_world(world_frame(units=[own]))

    def test_visible_foreign_flood_bit_is_public_without_effect_list(self):
        frame=world_frame();frame['tiles'][0].update(owner=3,explored=True,inSight=True,
            flooded=True,floodedScope='visible-explored-tile',terrainEffects=None,terrainEffectsScope='withheld')
        tile=normalize_world(frame)['tiles'][0]
        self.assertIs(tile['flooded'],True);self.assertIsNone(tile['terrainEffects'])

    def test_hidden_or_unexplored_flood_bits_are_rejected(self):
        for visible,explored in [(False,True),(True,False)]:
            with self.subTest(visible=visible,explored=explored),self.assertRaises(ObservationRejected):
                frame=world_frame();frame['tiles'][0].update(owner=3,explored=explored,inSight=visible,
                    flooded=True,floodedScope='visible-explored-tile')
                normalize_world(frame)

    def test_withheld_public_flood_bit_stays_unknown(self):
        frame=world_frame();frame['tiles'][0].update(owner=3,explored=True,inSight=False,
            flooded=None,floodedScope='withheld')
        self.assertIsNone(normalize_world(frame)['tiles'][0]['flooded'])

    def test_own_unit_discloses_foreign_tile_without_a_native_visibility_method(self):
        frame=world_frame(units=[unit(owner=1,coords='(1,1)')])
        frame['tiles'][0].update(owner=3,explored=True,inSight=None,
            flooded=True,floodedScope='own-unit-tile')
        self.assertIs(normalize_world(frame)['tiles'][0]['flooded'],True)

    def test_own_unit_scope_requires_an_actual_own_unit_on_that_tile(self):
        for occupant in (unit(owner=2,coords='(1,1)'),unit(owner=1,coords='(2,2)')):
            with self.subTest(occupant=occupant),self.assertRaises(ObservationRejected):
                frame=world_frame(units=[occupant])
                frame['tiles'][0].update(owner=3,explored=True,inSight=None,
                    flooded=True,floodedScope='own-unit-tile')
                normalize_world(frame)

if __name__=='__main__':unittest.main()
