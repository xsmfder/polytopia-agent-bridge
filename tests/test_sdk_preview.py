import contextlib
import io
import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from sdk import PolytopiaSDK, SDKError
from agentcli import main
from test_sdk import Fake


class PreviewTests(unittest.TestCase):
    def setUp(self):
        self.f = Fake()
        self.sdk = PolytopiaSDK(self.f, player_id=2)
        self.f.handlers['attack_preview'] = {
            'ok': True, 'status': 'ok', 'schema': 'attack-preview-1',
            'episodeId': 'ep-one', 'controlledPlayerId': 2, 'streamId': 'stream-one', 'stateVersion': 1,
            'preview': {'attackers': [{'unitId': '7', 'targets': [{
                'coords': '(2,4)', 'damageDealt': 35, 'damageReceived': None,
                'wouldDieToRetaliation': None, 'compositeEffects': {'status': 'partial'}}]}]}}

    def test_query_uses_normal_command_full_binding_and_keeps_native_unknowns(self):
        result = self.sdk.attack_preview(unit='007', at=' 2, 4 ')
        self.assertEqual(result['status'], 'observed')
        self.assertEqual(self.f.calls[-1], ('attack_preview', {'unit': '7', 'at': '(2,4)',
            'expectedEpisodeId': 'ep-one', 'expectedPlayerId': '2', 'expectedStreamId': 'stream-one',
            'expectedStateVersion': '1'}))
        target = result['native']['preview']['attackers'][0]['targets'][0]
        self.assertIsNone(target['damageReceived'])
        self.assertIsNone(target['wouldDieToRetaliation'])
        self.assertEqual(target['compositeEffects']['status'], 'partial')
        self.assertFalse(any(c in {'action','call','unit_action','popup'} for c,_ in self.f.calls))
        self.assertIsNone(self.sdk.unresolved)

    def test_all_units_query_omits_filters_and_empty_targets_remain_successful_read(self):
        self.f.handlers['attack_preview']['preview']['attackers'] = []
        result = self.sdk.attack_preview()
        self.assertEqual(result['native']['preview']['attackers'], [])
        self.assertNotIn('unit', self.f.calls[-1][1])
        self.assertNotIn('at', self.f.calls[-1][1])

    def test_partial_and_unavailable_preserve_explicit_status(self):
        self.f.handlers['attack_preview']['status'] = 'partial'
        self.assertEqual(self.sdk.attack_preview()['status'], 'partial')
        self.f.handlers['attack_preview'] = {'ok': False, 'status': 'unavailable', 'reason': 'native helper unbound',
            'errorCode':'REQUEST_FAILED'}
        r = self.sdk.attack_preview()
        self.assertFalse(r['ok'])
        self.assertEqual(r['status'], 'unavailable')

    def test_scope_or_version_mismatch_refuses_read(self):
        for key, wrong in [('episodeId','other'), ('controlledPlayerId',1), ('streamId','other'),
                           ('stateVersion',2), ('stateVersion',None)]:
            original = self.f.handlers['attack_preview'][key]
            self.f.handlers['attack_preview'][key] = wrong
            with self.assertRaises(SDKError) as error: self.sdk.attack_preview()
            self.assertEqual(error.exception.code, 'PREVIEW_SCOPE_CHANGED')
            self.f.handlers['attack_preview'][key] = original

    def test_stale_server_response_preserves_error_without_retry(self):
        self.f.handlers['attack_preview'] = {'ok': False, 'errorCode': 'STALE_ACTION_CONTEXT'}
        with self.assertRaises(SDKError) as error: self.sdk.attack_preview()
        self.assertEqual(error.exception.code, 'STALE_ACTION_CONTEXT')
        self.assertEqual(sum(c=='attack_preview' for c,_ in self.f.calls), 1)

    def test_human_curtain_stops_before_world_or_preview(self):
        self.f.handlers['handoff_status'] = {'ok': True, 'phase': 'human_turn', 'ready': False}
        self.assertEqual(self.sdk.attack_preview()['status'], 'handoff')
        self.assertEqual([c for c,_ in self.f.calls], ['handoff_status'])

    def test_decision_popup_blocks_native_preview(self):
        self.f.handlers['handoff_status'] = {'ok': True, 'phase': 'needs_decision', 'ready': False,
            'needsDecision': True, 'overlayActive': False, 'configuredPlayerId':2, 'targetPlayerId':2}
        self.f.handlers['world']['canAct'] = False
        self.assertEqual(self.sdk.attack_preview()['status'], 'needs_resolution')
        self.assertFalse(any(c=='attack_preview' for c,_ in self.f.calls))

    def test_busy_or_bot_turn_does_not_query(self):
        for key in ['canAct','myTurn']:
            self.f.handlers['world'][key] = False
            with self.assertRaises(SDKError) as error: self.sdk.attack_preview()
            self.assertEqual(error.exception.code, 'PREVIEW_NOT_READY')
            self.f.handlers['world'][key] = True
        self.assertFalse(any(c=='attack_preview' for c,_ in self.f.calls))

    def test_bad_arguments_fail_before_any_game_read(self):
        for unit in [True,-1,4294967296,1.2,'wrong']:
            with self.assertRaises(SDKError): self.sdk.attack_preview(unit=unit)
        for at in [True, '2', '(2,3', '2,3)', '2,3,4', '(2147483648,3)']:
            with self.assertRaises(SDKError): self.sdk.attack_preview(at=at)
        self.assertEqual(self.f.calls, [])

    def test_cli_routes_optional_filters_and_status_codes(self):
        with contextlib.redirect_stdout(io.StringIO()) as output:
            code = main(['--player','2','attack_preview','--unit','7','--at','(2,4)'],
                        sdk_factory=lambda **kw: self.sdk)
        self.assertEqual(code,0)
        self.assertEqual(json.loads(output.getvalue())['native']['preview'], self.f.handlers['attack_preview']['preview'])
        self.f.handlers['attack_preview'] = {'ok':False,'status':'unavailable'}
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(main(['--player','2','attack_preview'], sdk_factory=lambda **kw:self.sdk),5)


if __name__=='__main__': unittest.main()
