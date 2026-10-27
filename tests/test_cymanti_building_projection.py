import copy
import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from observation import normalize_world, ObservationRejected
from test_observation import world_frame


class CymantiBuildingProjectionTests(unittest.TestCase):
    def state(self, decomposing=False):
        return {'schema':'cymanti-owned-building-1',
                'authority':'owned-TileData.owner+improvement.type/level/effects',
                'owner':'1','type':'Fungi','level':2,
                'effects':None if decomposing is None else ['decomposing'] if decomposing else [],
                'decomposing':decomposing}

    def frame(self, state):
        frame=world_frame()
        frame['improvements'][0].update(improvementType='Fungi',ownedBuildingState=state)
        return frame

    def project(self, frame):
        return normalize_world(frame)['improvements'][0]

    def test_real_effect_presence_clear_and_unknown_survive(self):
        for flag in (True,False,None):
            with self.subTest(flag=flag):
                frame=self.frame(self.state(flag));row=self.project(frame)['ownedBuildingState']
                self.assertIs(row['decomposing'],flag)
                self.assertEqual(row['owner'],1)
                self.assertEqual(row['level'],2)
                self.assertEqual(row['effects'],frame['improvements'][0]['ownedBuildingState']['effects'])
        frame=self.frame(self.state(None));frame['improvements'][0]['ownedBuildingState']['level']=None
        self.assertIsNone(self.project(frame)['ownedBuildingState']['level'])

    def test_old_rows_and_explicit_null_do_not_invent_states(self):
        row=self.project(world_frame());self.assertNotIn('ownedBuildingState',row)
        self.assertIsNone(self.project(self.frame(None))['ownedBuildingState'])

    def test_decomposing_zero_is_not_a_boolean_or_an_enum_name(self):
        for value in (0,1,'true','decomposing',[],{}):
            frame=self.frame(self.state());frame['improvements'][0]['ownedBuildingState']['decomposing']=value
            with self.subTest(value=value),self.assertRaises(ObservationRejected):self.project(frame)
        for effects in ([0],['0'],[False],['ImprovementEffect.decomposing'],[''],[None],{}):
            frame=self.frame(self.state(True));frame['improvements'][0]['ownedBuildingState']['effects']=effects
            with self.subTest(effects=effects),self.assertRaises(ObservationRejected):self.project(frame)

    def test_bool_and_effect_list_must_agree(self):
        for effects,flag in (([],True),(['decomposing'],False),(None,False),(None,True),([],None),(['decomposing'],None)):
            frame=self.frame(self.state());frame['improvements'][0]['ownedBuildingState'].update(effects=effects,decomposing=flag)
            with self.subTest(effects=effects,flag=flag),self.assertRaises(ObservationRejected):self.project(frame)

    def test_native_effect_list_is_copied_sorted_and_keeps_other_actual_effects(self):
        frame=self.frame(self.state(True));state=frame['improvements'][0]['ownedBuildingState']
        state['effects']=['robbed','decomposing']
        projected=self.project(frame)['ownedBuildingState']
        self.assertEqual(projected['effects'],['decomposing','robbed'])
        projected['effects'].clear();self.assertEqual(state['effects'],['robbed','decomposing'])
        state['effects']=['robbed','robbed'];state['decomposing']=False
        with self.assertRaises(ObservationRejected):self.project(frame)

    def test_wrong_schema_authority_missing_fields_and_shape_are_rejected(self):
        for field in self.state():
            frame=self.frame(self.state());del frame['improvements'][0]['ownedBuildingState'][field]
            with self.subTest(missing=field),self.assertRaises(ObservationRejected):self.project(frame)
        for change in ({'schema':'future-state'},{'authority':'enemy-effects'}):
            frame=self.frame(self.state());frame['improvements'][0]['ownedBuildingState'].update(change)
            with self.assertRaises(ObservationRejected):self.project(frame)
        for malformed in (True,[],0,'state'):
            with self.assertRaises(ObservationRejected):self.project(self.frame(malformed))

    def test_state_and_outer_owner_must_certify_same_controlled_player(self):
        for owner in (None,False,0,2,'2',256):
            frame=self.frame(self.state());frame['improvements'][0]['ownedBuildingState']['owner']=owner
            with self.subTest(owner=owner),self.assertRaises(ObservationRejected):self.project(frame)
        for owner in (None,0,2):
            frame=self.frame(self.state());frame['improvements'][0]['owner']=owner
            with self.assertRaises(ObservationRejected):self.project(frame)
        frame=self.frame(self.state());frame['improvements'][0]['isMine']=False
        with self.assertRaises(ObservationRejected):self.project(frame)

    def test_enemy_nonnull_state_cannot_enter_projection(self):
        for value in (self.state(),{},[],False):
            frame=self.frame(value);frame['improvements'][0].update(owner=2,isMine=False)
            with self.subTest(value=value),self.assertRaises(ObservationRejected):self.project(frame)
        frame=self.frame(None);frame['improvements'][0].update(owner=2,isMine=False)
        self.assertNotIn('ownedBuildingState',self.project(frame))

    def test_type_and_level_are_native_strict_and_match_outer_row(self):
        for kind in ('Mycelium','Il2Cpp.Fungi','',None,0):
            frame=self.frame(self.state());frame['improvements'][0]['ownedBuildingState']['type']=kind
            with self.subTest(kind=kind),self.assertRaises(ObservationRejected):self.project(frame)
        for level in (-1,65536,False,2.0,'2',3):
            frame=self.frame(self.state());frame['improvements'][0]['ownedBuildingState']['level']=level
            with self.subTest(level=level),self.assertRaises(ObservationRejected):self.project(frame)
        frame=self.frame(self.state());frame['improvements'][0]['level']=None
        self.assertEqual(self.project(frame)['ownedBuildingState']['level'],2)

    def test_unknown_extra_data_is_never_forwarded(self):
        frame=self.frame(self.state());state=frame['improvements'][0]['ownedBuildingState']
        state['privateMap']={'hidden':42};before=copy.deepcopy(frame)
        row=self.project(frame)['ownedBuildingState']
        self.assertNotIn('privateMap',row);self.assertEqual(frame,before)

    def test_tile_owner_authority_cannot_be_replaced_by_internal_owner(self):
        frame=self.frame(self.state())
        frame['improvements'][0]['ownedBuildingState']['improvementOwner']=0
        projected=self.project(frame)['ownedBuildingState']
        self.assertEqual(projected['owner'],1)
        self.assertEqual(projected['authority'],'owned-TileData.owner+improvement.type/level/effects')
        self.assertNotIn('improvementOwner',projected)
        for authority in ('owned-TileData.improvement.type/owner/level/effects','owned-ImprovementState.owner'):
            bad=copy.deepcopy(frame);bad['improvements'][0]['ownedBuildingState']['authority']=authority
            with self.subTest(authority=authority),self.assertRaises(ObservationRejected):self.project(bad)


if __name__=='__main__':unittest.main()
