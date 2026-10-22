import copy, json, sys, unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[3]))
from sdk import PolytopiaSDK, SDKError
HERE=Path(__file__).resolve().parent
class Replayed:
 def __init__(self):
  self.native=json.loads((HERE/'attacker-disappeared-ticket34.json').read_text(encoding='utf8'));self.calls=[]
 def request(self,command,**kw):
  self.calls.append(command)
  if command=='settlement': return copy.deepcopy(self.native)
  if command=='world':return {'ok':True,'controlledIdentityVerified':True,'episodeId':self.native['episodeId'],'localPlayerId':1,'canAct':True,'state':{'streamId':self.native['streamId'],'stateVersion':self.native['stateVersion']}}
  raise AssertionError(command)
class Cases(unittest.TestCase):
 def setUp(self):
  self.f=Replayed();n=self.f.native
  self.b={'episodeId':n['episodeId'],'playerId':1,'streamId':n['streamId'],'stateVersion':n['beforeStateVersion']}
  self.s=PolytopiaSDK(self.f,player_id=1);self.s.unresolved={'ticket':n['ticketId'],'binding':self.b};self.t=n['ticketId']
 def ack(self):return self.s.acknowledge_settled_unknown(self.t,binding=self.b,accept_unknown_effect=True)
 def test_attacker_disappearance_keeps_unknown_and_never_writes(self):
  r=self.ack();self.assertEqual(r['status'],'settled_effect_unknown');self.assertFalse(r['ok']);self.assertEqual(r['native']['effectConfirmation'],'unknown');self.assertIsNone(self.s.unresolved);self.assertEqual(self.f.calls,['settlement','world'])
 def test_pending_or_non_idle_never_unlocks(self):
  for k,v in [('settlementComplete',False),('pipelineIdle',False),('completionAuthority',None)]:
   with self.subTest(k=k):
    old=self.f.native[k];self.f.native[k]=v
    with self.assertRaises(SDKError):self.ack()
    self.assertIsNotNone(self.s.unresolved);self.f.native[k]=old
 def test_foreign_identity_never_unlocks(self):
  self.f.native['episodeId']='foreign'
  with self.assertRaises(SDKError):self.ack()
  self.assertIsNotNone(self.s.unresolved)
 def test_explicit_decision_required(self):
  with self.assertRaises(SDKError):self.s.acknowledge_settled_unknown(self.t,binding=self.b)
  self.assertIsNotNone(self.s.unresolved);self.assertEqual(self.f.calls,[])
 def test_missing_settled_version_never_unlocks(self):
  self.f.native['stateVersion']=None
  with self.assertRaises(SDKError):self.ack()
  self.assertIsNotNone(self.s.unresolved)
if __name__=='__main__':unittest.main()
