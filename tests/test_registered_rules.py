import unittest
from test_sdk import Fake
from sdk import PolytopiaSDK,SDKError
from sdk_values import native_identifier,native_rule_identifier
from observation import normalize_world
from test_observation import world_frame

class RegisteredRuleTests(unittest.TestCase):
    def test_private_names_remain_strict_and_rule_tokens_canonical(self):
        for value in ('1000','1001','2147483647'):
            self.assertEqual(native_rule_identifier(value),value)
            self.assertIsNone(native_identifier(value))
        for value in (1000,True,'999','01000','+1000','1000.0','2147483648','1e3','1000,Warrior'):
            self.assertIsNone(native_rule_identifier(value))

    def test_dynamic_menu_discovery_execution_and_revocation(self):
        fake=Fake();w=fake.handlers['world']
        w['researchableTechs']['items'][0]['tech']='1000'
        w['trainOptions']['cities'][0]['options'][0]['unitType']='1001'
        fake.handlers['city_options']['tileOptions'][0]['improvement']='1002'
        sdk=PolytopiaSDK(fake,player_id=2)
        actions=sdk.list_actions()['actions']
        for name,kind in (('research','1000'),('train','1001'),('build','1002')):
            self.assertTrue(any(a['args'].get('name')==name and a['args'].get('kind')==kind for a in actions))
        research=next(a for a in actions if a['args'].get('name')=='research')
        w['researchableTechs']['items']=[]
        fake.calls.clear()
        with self.assertRaises(SDKError):sdk.execute(research)
        self.assertFalse(any(c=='action' for c,_ in fake.calls))

    def test_normalized_native_rules_keep_dynamic_tech_and_unit(self):
        w=world_frame()
        w['researchableTechs']['items'][0]['tech']='1000'
        w['trainOptions']['cities'][0]['options'][0]['unitType']='1001'
        out=normalize_world(w)
        self.assertEqual(out['researchableTechs']['items'][0]['tech'],'1000')
        self.assertEqual(out['trainOptions']['cities'][0]['options'][0]['unitType'],'1001')

if __name__=='__main__':unittest.main()

