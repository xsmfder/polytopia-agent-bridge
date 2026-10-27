import copy
import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from observation import normalize_world, ObservationRejected
from test_observation import world_frame, unit
from test_enemy_public_info import panel, visuals


class CymantiProjectionTests(unittest.TestCase):
    def normalized(self, properties, owner=1):
        actor=unit(owner=owner, uid='8');actor['properties']=properties
        return normalize_world(world_frame(units=[actor]))['ownUnits' if owner==1 else 'units'][0]

    def status(self, field='poisoned', value=True, owner=1):
        effect={'poisoned':'Poisoned','swift':'Swift','doubleReady':'DoubleReady'}[field]
        properties={field:value, field+'Authority':'owned-native-effects'}
        if owner==1:
            properties['effects']=None if value is None else [effect] if value else []
        else:
            properties[field+'Authority']='visible-renderer-'+effect+'-particle'
            visual=visuals();visual['particles']['types']=[] if not value else [effect]
            properties['publicInfo']=panel();properties['publicInfo']['visualEffects']=visual
        return properties

    def relations(self):
        return {'linkAuthority':'owned-native-HasLeader/HasFollower','status':'ok',
                'hasLeader':True,'leaderId':'9','hasFollower':False,'followerId':None,
                'leaderRaw':9,'followerRaw':0,'passenger':{'id':'private-passenger'}}

    def test_owned_status_preserves_true_false_unknown(self):
        for field in ('poisoned','swift','doubleReady'):
            for value in (True,False,None):
                with self.subTest(field=field,value=value):
                    row=self.normalized(self.status(field,value))
                    self.assertIs(row[field],value)
                    self.assertEqual(row[field+'Authority'],'owned-native-effects')

    def test_status_does_not_coerce_numbers_or_accept_wrong_source(self):
        for field in ('poisoned','swift','doubleReady'):
            for value in (0,1,'true',[],{}):
                properties=self.status(field,None);properties[field]=value
                with self.subTest(field=field,value=value),self.assertRaises(ObservationRejected):self.normalized(properties)
            properties=self.status(field);properties[field+'Authority']='visible-renderer-Poisoned-particle'
            with self.assertRaises(ObservationRejected):self.normalized(properties)
            for key in (field,field+'Authority'):
                properties=self.status(field);del properties[key]
                with self.assertRaises(ObservationRejected):self.normalized(properties)

    def test_owned_known_status_requires_matching_native_effects(self):
        for effects in (None,[],['Poison'],[1],['Il2Cpp.Poisoned']):
            properties=self.status();properties['effects']=effects
            with self.assertRaises(ObservationRejected):self.normalized(properties)
        properties=self.status('swift');properties['effects']=['Boosted']
        with self.assertRaises(ObservationRejected):self.normalized(properties)
        properties=self.status();properties['poisoned']=None;properties['effects']=None
        self.assertIsNone(self.normalized(properties)['poisoned'])

    def test_enemy_status_uses_complete_actual_visuals_only(self):
        for field in ('poisoned','swift'):
            for value in (True,False,None):
                properties=self.status(field,value,2);properties['effects']=['private']
                row=self.normalized(properties,2)
                self.assertIs(row[field],value)
                self.assertNotIn('effects',row);self.assertNotIn('executionId',row)
            properties=self.status(field,True,2)
            for particle_status in ('partial','unavailable','withheld'):
                altered=copy.deepcopy(properties)
                altered['publicInfo']['visualEffects']['particles']['status']=particle_status
                with self.assertRaises(ObservationRejected):self.normalized(altered,2)
            properties[field]=None
            properties['publicInfo']['visualEffects']['particles'].update(status='unavailable',types=None)
            self.assertIsNone(self.normalized(properties,2)[field])

    def test_enemy_private_double_ready_is_rejected_even_when_null(self):
        for value in (True,False,None):
            properties=self.status('doubleReady',value)
            with self.assertRaises(ObservationRejected):self.normalized(properties,2)
        row=self.normalized({'effects':['DoubleReady']},2)
        self.assertNotIn('doubleReady',row)

    def test_public_swift_is_not_inferred_from_boosted(self):
        properties=self.status('swift',True,2)
        properties['publicInfo']['visualEffects']['particles']['types']=['Boosted']
        with self.assertRaises(ObservationRejected):self.normalized(properties,2)

    def test_owned_structured_relations_preserve_ids_without_private_payload(self):
        row=self.normalized({'relations':self.relations()})['relations']
        self.assertEqual(row['leaderId'],'9');self.assertFalse(row['hasFollower'])
        self.assertIsNone(row['followerId']);self.assertEqual(row['status'],'ok')
        self.assertNotIn('passenger',row);self.assertNotIn('leaderRaw',row)
        self.assertIsNone(self.normalized({'relations':{'leaderRaw':9}})['relations'])
        self.assertNotIn('relations',self.normalized({'relations':self.relations()},2))

    def test_relation_unknown_is_not_absence(self):
        relations=self.relations();relations.update(hasLeader=None,leaderId=None,status='partial')
        row=self.normalized({'relations':relations})['relations']
        self.assertIsNone(row['hasLeader']);self.assertIsNone(row['leaderId'])
        relations.update(hasLeader=True)
        self.assertTrue(self.normalized({'relations':relations})['relations']['hasLeader'])

    def test_relation_invalid_ids_flags_and_authorities_are_rejected(self):
        for linked in (0,'0',-1,4294967296,True,1.5,'8','not-an-id'):
            relations=self.relations();relations['leaderId']=linked
            with self.subTest(linked=linked),self.assertRaises(ObservationRejected):self.normalized({'relations':relations})
        for has in (False,None,1,'true'):
            relations=self.relations();relations['hasLeader']=has
            with self.assertRaises(ObservationRejected):self.normalized({'relations':relations})
        for change in ({'linkAuthority':'raw-fields'},{'status':'partial'}):
            relations=self.relations();relations.update(change)
            with self.assertRaises(ObservationRejected):self.normalized({'relations':relations})
        relations=self.relations();del relations['hasFollower']
        with self.assertRaises(ObservationRejected):self.normalized({'relations':relations})

    def test_frozen_projection_is_still_independent(self):
        properties=self.status();properties.update(frozen=False,frozenAuthority='owned-native-effects')
        row=self.normalized(properties);self.assertTrue(row['poisoned']);self.assertFalse(row['frozen'])


if __name__=='__main__':unittest.main()
