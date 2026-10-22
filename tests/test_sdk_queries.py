"""Player-visible query integration: production SDK/CLI, explicit fake native transport."""
import contextlib
import copy
import io
import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from sdk import PolytopiaSDK, SDKError
from agentcli import main, exit_code
from test_sdk import Fake, world

PASSIVE = {'game_info':'game-info-1', 'unit_properties':'unit-properties-1',
           'score_snapshot':'score-snapshot-1', 'threat_map':'visible-threat-1'}

class QueryTests(unittest.TestCase):
    def setUp(self):
        self.f=Fake(); self.sdk=PolytopiaSDK(self.f, player_id=2)
        for command,schema in PASSIVE.items():
            self.f.handlers[command]={'ok':True,'schema':schema,'episodeId':'ep-one'}
        self.f.handlers['composite_preview']={'schema':'move-composite-preview-1','status':'partial',
            'unitId':'7','destination':'(2,4)','steps':[{'effects':{'status':'unknown','affectedUnits':None}}]}
        self.f.handlers['tribe_info']={'ok':True,'schema':'tribe-info-1','episodeId':'ep-one','playerId':3,
            'details':{'attitudeLabel':None,'thirdPartyRelations':None}}

    def test_passive_queries_use_context_and_bookends_without_writes(self):
        for command in PASSIVE:
            with self.subTest(command=command):
                self.f.calls=[]
                result=getattr(self.sdk,command)()
                self.assertEqual(result['status'],'observed')
                self.assertEqual(result['native'],self.f.handlers[command])
                self.assertEqual(result['binding']['playerId'],2)
                self.assertEqual([c for c,_ in self.f.calls],['handoff_status','world','popup_info','world',command,'world'])
                self.assertEqual(self.f.calls[-2][1],{'expectedEpisodeId':'ep-one','expectedPlayerId':'2',
                    'expectedStreamId':'stream-one','expectedStateVersion':'1'})

    def test_threat_empty_and_unknown_are_never_promoted_to_safe(self):
        self.f.handlers['threat_map'].update(status='partial',completeThreatMap=False,enemies=[],hiddenEnemyCoverage='unknown',
            reachableTiles=None,attackTargetsAfterMove=None)
        result=self.sdk.threat_map()
        self.assertTrue(result['ok']); self.assertEqual(result['status'],'partial')
        self.assertIsNone(result['native']['reachableTiles'])
        self.assertFalse(result['native']['completeThreatMap'])
        self.assertEqual(result['native']['hiddenEnemyCoverage'],'unknown')

    def test_score_provisional_and_terminal_only_rating(self):
        self.f.handlers['score_snapshot'].update(status='provisional',final=False,complete=False,scores={'total':None})
        result=self.sdk.score_snapshot()
        self.assertEqual(result['status'],'provisional'); self.assertIsNone(result['native']['scores']['total'])
        self.f.handlers['score_snapshot']['status']='unavailable_before_terminal'
        result=self.sdk.score_snapshot()
        self.assertFalse(result['ok']); self.assertEqual(exit_code(result),5)

    def test_human_turn_and_curtain_never_read_private_queries(self):
        for phase in ['human_turn','curtain','awaiting_handoff']:
            self.f.handlers['handoff_status']={'ok':True,'phase':phase,'ready':False}
            for command in list(PASSIVE)+['composite_preview','tribe_info']:
                self.f.calls=[]
                result=self.invoke(command)
                self.assertEqual(result['status'],'handoff')
                self.assertEqual(self.f.calls,[('handoff_status',{})])

    def invoke(self, command):
        if command=='composite_preview':return self.sdk.composite_preview(unit='7',at='(2,4)')
        if command=='tribe_info':return self.sdk.tribe_info(3)
        return getattr(self.sdk,command)()

    def test_bot_turn_refuses_query(self):
        self.f.handlers['world']['myTurn']=False
        for command in PASSIVE:
            with self.assertRaises(SDKError) as caught:self.invoke(command)
            self.assertEqual(caught.exception.code,'READ_NOT_READY')
        self.assertFalse(any(c in PASSIVE for c,_ in self.f.calls))

    def test_pending_choice_allows_passive_read_but_blocks_preview_and_open(self):
        self.f.handlers['world']['canAct']=False
        self.assertEqual(self.sdk.game_info()['status'],'observed')
        for call in [lambda:self.sdk.composite_preview(unit=7,at='(2,4)'),lambda:self.sdk.tribe_info(3,mode='open')]:
            with self.assertRaises(SDKError) as caught:call()
            self.assertEqual(caught.exception.code,'PREVIEW_NOT_READY')
        self.assertFalse(any(c in {'tribe_info','composite_preview'} for c,_ in self.f.calls))

    def test_visible_hotseat_choice_still_allows_passive_read(self):
        self.f.handlers['handoff_status']={'ok':True,'phase':'needs_decision','ready':False,'needsDecision':True,
            'overlayActive':False,'configuredPlayerId':2,'targetPlayerId':2}
        self.f.handlers['world']['canAct']=False
        self.assertEqual(self.sdk.unit_properties()['status'],'observed')

    def test_native_stale_discards_payload_preserves_original_receipt_no_retry(self):
        self.sdk.unresolved={'ticket':12,'binding':{'episodeId':'ep-one'}}
        self.f.handlers['threat_map']={'ok':False,'errorCode':'STALE_ACTION_CONTEXT','enemies':[{'private':'discard'}]}
        result=self.sdk.threat_map()
        self.assertEqual(result['status'],'state_changed');self.assertFalse(result['ok'])
        self.assertIsNone(result['native']);self.assertEqual(result['pendingReceipt'],self.sdk.unresolved)
        result['pendingReceipt']['ticket']=99;self.assertEqual(self.sdk.unresolved['ticket'],12)
        self.assertEqual(sum(c=='threat_map' for c,_ in self.f.calls),1)

    def test_bookend_version_change_discards_mixed_read(self):
        count=[0]
        def changing(args):
            count[0]+=1
            return world(1 if count[0]<=2 else 2)
        self.f.handlers['world']=changing
        result=self.sdk.unit_properties()
        self.assertEqual(result['status'],'state_changed'); self.assertIsNone(result['native'])
        self.assertTrue(result['retryObservationAllowed']);self.assertFalse(result['retryAllowed'])

    def test_bookend_current_player_change_discards_read(self):
        count=[0]
        def changing(args):
            count[0]+=1; w=world();w['currentPlayerId']=2 if count[0]==1 else 3;return w
        self.f.handlers['world']=changing
        self.assertEqual(self.sdk.game_info()['status'],'state_changed')

    def test_changed_epoch_or_stream_not_returned_as_current(self):
        for key,value in [('episodeId','other'),('localPlayerId',3),('state',{'streamId':'other','stateVersion':1})]:
            self.setUp();count=[0]
            def changing(args):
                count[0]+=1;w=world()
                if count[0]>1:w[key]=value
                return w
            self.f.handlers['world']=changing
            with self.assertRaises(SDKError):self.sdk.game_info()

    def test_native_foreign_headers_are_rejected(self):
        for key,value in [('episodeId','other'),('controlledPlayerId',1),('streamId','other'),('stateVersion',2)]:
            self.setUp();self.f.handlers['unit_properties'][key]=value
            with self.assertRaises(SDKError) as caught:self.sdk.unit_properties()
            self.assertEqual(caught.exception.code,'READ_SCOPE_CHANGED')

    def test_success_requires_schema_episode_and_native_evidence(self):
        for patch,remove in [({'schema':'wrong'},None),({},'episodeId'),({},'ok'),({'status':'invented'},None)]:
            self.setUp();self.f.handlers['game_info'].update(patch)
            if remove:self.f.handlers['game_info'].pop(remove)
            with self.assertRaises(SDKError):self.sdk.game_info()

    def test_missing_or_invalid_version_stops_before_query(self):
        for version in [None,True,0,-1]:
            self.setUp();self.f.handlers['world']['state']['stateVersion']=version
            with self.assertRaises(SDKError) as caught:self.sdk.threat_map()
            self.assertEqual(caught.exception.code,'VERSION_UNKNOWN')
            self.assertFalse(any(c=='threat_map' for c,_ in self.f.calls))

    def test_unavailable_and_request_error_preserve_native_reason(self):
        for native in [{'ok':False,'errorCode':'VISIBLE_UNITS_UNAVAILABLE'}, {'status':'unavailable','reason':'helper unbound'}]:
            self.f.handlers['composite_preview']=native
            result=self.sdk.composite_preview(unit=7,at='(2,4)')
            self.assertEqual(result['status'],'unavailable');self.assertFalse(result['ok']);self.assertEqual(result['native'],native)

    def test_transport_timeout_does_not_clear_pending_or_retry(self):
        self.sdk.unresolved={'ticket':12}
        self.f.handlers['game_info']=TimeoutError('read lost')
        result=self.sdk.game_info()
        self.assertEqual(result['status'],'unknown');self.assertEqual(self.sdk.unresolved,{'ticket':12})
        self.assertEqual(sum(c=='game_info' for c,_ in self.f.calls),1)

    def test_move_preview_normalizes_args_and_keeps_partial_effects(self):
        result=self.sdk.composite_preview(unit='007',at=' 2, 4 ')
        self.assertEqual(result['status'],'partial');self.assertTrue(result['ok'])
        self.assertIsNone(result['native']['steps'][0]['effects']['affectedUnits'])
        self.assertEqual(self.f.calls[-2][1]['unit'],'7');self.assertEqual(self.f.calls[-2][1]['at'],'(2,4)')
        self.assertNotIn('episodeId',result['native']) # SDK binding never fabricates native stamps.

    def test_bad_move_arguments_fail_without_reads(self):
        for unit in [True,-1,4294967296,1.2,'bad']:
            with self.assertRaises(SDKError):self.sdk.composite_preview(unit=unit,at='(2,4)')
        for at in [True,'2','(2,3','2,3)','(2147483648,0)']:
            with self.assertRaises(SDKError):self.sdk.composite_preview(unit=7,at=at)
        with self.assertRaises(SDKError):self.sdk.composite_preview(unit=7,at='(2,4)',kind='attack')
        self.assertFalse(self.f.calls)

    def test_tribe_read_is_passive_and_keeps_anonymization(self):
        result=self.sdk.tribe_info('003')
        self.assertEqual(result['status'],'observed');self.assertIsNone(result['native']['details']['attitudeLabel'])
        self.assertEqual(self.f.calls[-2][1]['mode'],'read');self.assertEqual(self.f.calls[-2][1]['opponent'],'3')

    def test_tribe_view_open_close_explicitly_allow_only_version_progress(self):
        for mode,status in [('open','opening'),('close','closed')]:
            self.setUp(); self.f.handlers['tribe_info']={'ok':True,'schema':'tribe-info-1','status':status}
            if mode=='open':self.f.handlers['tribe_info'].update(episodeId='ep-one',playerId=3)
            count=[0]
            def changing(args):count[0]+=1;return world(1 if count[0]<=2 else 2)
            self.f.handlers['world']=changing
            result=self.sdk.tribe_info(3,mode=mode)
            self.assertEqual(result['status'],status);self.assertEqual(result['binding']['stateVersion'],1)
            self.assertEqual(result['afterBinding']['stateVersion'],2)

    def test_tribe_wrong_target_cannot_supply_attitude(self):
        self.f.handlers['tribe_info']['playerId']=4
        with self.assertRaises(SDKError) as caught:self.sdk.tribe_info(3)
        self.assertEqual(caught.exception.code,'READ_TARGET_CHANGED')

    def test_view_accepts_native_integer_strings_without_type_error(self):
        self.f.handlers['tribe_info'].update(status='opening')
        count=[0]
        def changing(args):count[0]+=1;return world('1' if count[0]<=2 else '2')
        self.f.handlers['world']=changing
        result=self.sdk.tribe_info(3,mode='open')
        self.assertEqual(result['status'],'opening');self.assertEqual(result['afterBinding']['stateVersion'],'2')

    def test_view_rejects_version_rollback_and_keeps_operation_uncertain(self):
        self.f.handlers['tribe_info'].update(status='opening')
        count=[0]
        def changing(args):count[0]+=1;return world(2 if count[0]<=2 else 1)
        self.f.handlers['world']=changing
        result=self.sdk.tribe_info(3,mode='open')
        self.assertEqual(result['status'],'state_changed');self.assertIsNone(result['native'])
        self.assertTrue(result['viewOperationAttempted']);self.assertFalse(result['retryAllowed'])

    def test_bad_tribe_arguments_never_open_native_view(self):
        for opponent in [True,0,-1,255,2,1.3,'bad']:
            with self.assertRaises(SDKError):self.sdk.tribe_info(opponent)
        with self.assertRaises(SDKError):self.sdk.tribe_info(3,mode='refresh_private')
        self.assertFalse(self.f.calls)

    def test_cli_routes_all_six_methods_and_explicit_view_modes(self):
        commands=[[c] for c in PASSIVE]+[['composite_preview','--unit','7','--at','(2,4)'],['tribe_info','3']]
        for args in commands:
            with contextlib.redirect_stdout(io.StringIO()) as output:
                code=main(['--player','2',*args],sdk_factory=lambda **kw:self.sdk)
            self.assertEqual(code,0);self.assertTrue(json.loads(output.getvalue())['ok'])

    def test_cli_partial_empty_threat_and_unavailable_rating_exit_codes(self):
        self.f.handlers['threat_map'].update(status='partial',enemies=[],completeThreatMap=False)
        self.f.handlers['score_snapshot']['status']='unavailable_before_terminal'
        for command,expected in [('threat_map',0),('score_snapshot',5)]:
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(main(['--player','2',command],sdk_factory=lambda **kw:self.sdk),expected)

if __name__=='__main__':unittest.main()
