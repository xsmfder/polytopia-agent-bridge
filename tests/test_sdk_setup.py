"""Normal menu administration through the production SDK, with explicit fake transport."""
import contextlib
import copy
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from sdk import PolytopiaSDK, SDKError
from agentcli import main, exit_code


def menu(revision=1, phase="settings_menu", action="select:map_preset:77"):
    return {"ok": True, "schema": "singleplayer-setup-1", "setupSessionId": "menu-session",
            "menuRevision": revision, "configuredPlayerId": 1, "phase": phase, "ready": False,
            "creationSubmitted": False, "settings": {"mapPreset": "FutureMap"},
            "actions": [{"actionId": action, "executable": True, "enabled": True}]}


class MenuTransport:
    def __init__(self):
        self.calls = []
        self.state = menu()
        self.reply = None

    def request(self, command, timeout=None, **args):
        self.calls.append((command, args))
        if command == "setup_status":
            return copy.deepcopy(self.state)
        if command != "setup_action":
            raise AssertionError("menu administration must not read world or invoke gameplay")
        if isinstance(self.reply, Exception):
            raise self.reply
        if self.reply is not None:
            return copy.deepcopy(self.reply)
        return {"ok": True, "callbackSubmitted": True, "status": "configured", "settingVerified": True,
                "setupSessionId": args["setupSessionId"], "menuRevision": int(args["menuRevision"]),
                "actionId": args["actionId"], "retryAllowed": False}


class SetupTests(unittest.TestCase):
    def setUp(self):
        self.f = MenuTransport()
        self.now = 0
        self.sdk = PolytopiaSDK(self.f, player_id=1, clock=lambda: self.now,
                                sleep=lambda delay: setattr(self, "now", self.now + delay))

    def submitted(self, action="continue"):
        self.f.state = menu(action=action)
        self.f.reply = {"ok": True, "callbackSubmitted": True, "status": "submitted",
                        "setupSessionId": "menu-session", "menuRevision": 1, "actionId": action}
        return self.sdk.setup_action(action, expected_setup=self.sdk.setup_status())

    def test_observe_menu_never_reads_world_or_binds_match(self):
        status = self.sdk.setup_status()
        self.assertEqual(status["status"], "observed")
        self.assertEqual(status["native"]["actions"][0]["actionId"], "select:map_preset:77")
        self.assertIsNone(self.sdk.episode_id)
        self.assertEqual(self.f.calls, [("setup_status", {})])

    def test_dynamic_native_choice_and_verified_configuration(self):
        reply = self.sdk.setup_action("select:map_preset:77", expected_setup=self.sdk.setup_status())
        self.assertEqual(reply["status"], "configured")
        self.assertTrue(reply["ok"])
        self.assertFalse(reply["retryAllowed"])
        self.assertIsNone(self.sdk._setup_unresolved)
        self.assertEqual(self.f.calls[-1], ("setup_action", {"actionId": "select:map_preset:77",
            "setupSessionId": "menu-session", "menuRevision": "1"}))

    def test_wrong_sdk_seat_and_active_binding_refuse_before_write(self):
        status = self.sdk.setup_status()
        for sdk in (PolytopiaSDK(self.f, player_id=2), PolytopiaSDK(self.f, player_id=1, episode_id="old")):
            with self.assertRaises(SDKError):
                sdk.setup_action("select:map_preset:77", expected_setup=status)
        self.assertEqual([c for c, _ in self.f.calls], ["setup_status"])

    def test_original_unresolved_gameplay_receipt_is_preserved(self):
        self.sdk.unresolved = {"ticket": 9}
        with self.assertRaises(SDKError):
            self.sdk.setup_action("select:map_preset:77", expected_setup=self.sdk.setup_status())
        self.assertEqual(self.sdk.unresolved, {"ticket": 9})

    def test_write_gate_refusal_cannot_create_a_phantom_setup_receipt(self):
        for durable in (False, True):
            with self.subTest(durable=durable), tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1]) as tmp:
                path = Path(tmp) / 'session.json' if durable else None
                transport = MenuTransport()
                with contextlib.closing(PolytopiaSDK(transport, player_id=1, session_path=path)) as sdk:
                    original = sdk._begin_write('handoff_start', {'playerId': '1', 'expectedHandoffId': 'original'})
                    before = copy.deepcopy(sdk._pending_writes)
                    with self.assertRaises(SDKError) as caught:
                        sdk.setup_action('select:map_preset:77', expected_setup=menu())
                    self.assertEqual(caught.exception.code, 'RECOVERY_REQUIRED')
                    self.assertIsNone(sdk._setup_unresolved)
                    self.assertEqual(sdk._pending_writes, before)
                    if durable:
                        result = sdk.resume_pending(timeout=0)
                        self.assertEqual(result['status'], 'unknown')
                        self.assertEqual(result['pendingWrites'][0]['id'], original['id'])
                    self.assertEqual(transport.calls, [])
                if durable:
                    with contextlib.closing(PolytopiaSDK(transport, player_id=1, session_path=path)) as restored:
                        self.assertIsNone(restored._setup_unresolved)
                        self.assertEqual(restored._pending_writes, before)

    def test_failed_checkpoint_gate_preserves_original_recovery_target(self):
        self.sdk._state_save_failed = True
        with self.assertRaises(SDKError) as caught:
            self.sdk.setup_action('select:map_preset:77', expected_setup=menu())
        self.assertEqual(caught.exception.code, 'SESSION_IO_ERROR')
        self.assertIsNone(self.sdk._setup_unresolved)
        self.assertEqual(self.sdk._pending_writes, [])
        self.assertEqual(self.f.calls, [])

    def test_setup_receipt_and_intent_are_present_before_transport_dispatch(self):
        request = self.f.request
        def inspect(command, **args):
            if command == 'setup_action':
                self.assertEqual(self.sdk._setup_unresolved['actionId'], args['actionId'])
                self.assertEqual(len(self.sdk._pending_writes), 1)
                self.assertEqual(self.sdk._pending_writes[0]['kind'], 'setup')
            return request(command, **args)
        self.f.request = inspect
        self.assertEqual(self.sdk.setup_action('select:map_preset:77', expected_setup=menu())['status'], 'configured')
        self.assertIsNone(self.sdk._setup_unresolved)
        self.assertEqual(self.sdk._pending_writes, [])

    def test_missing_or_disabled_or_duplicate_action_is_not_sent(self):
        for actions in ([], [{"actionId": "x", "executable": True, "enabled": None}],
                        [{"actionId": "x", "executable": True, "enabled": True}] * 2):
            self.f.state["actions"] = actions
            with self.assertRaises(SDKError):
                self.sdk.setup_action("x", expected_setup=self.sdk.setup_status())
        self.assertFalse(any(c == "setup_action" for c, _ in self.f.calls))

    def test_transport_timeout_retains_original_setup_and_blocks_retry(self):
        self.f.reply = TimeoutError("lost reply")
        reply = self.sdk.setup_action("select:map_preset:77", expected_setup=self.sdk.setup_status())
        self.assertEqual(reply["status"], "unknown")
        with self.assertRaises(SDKError) as caught:
            self.sdk.setup_action("select:map_preset:77", expected_setup=self.sdk.setup_status())
        self.assertEqual(caught.exception.code, "SETUP_PENDING")
        self.assertEqual(sum(c == "setup_action" for c, _ in self.f.calls), 1)

    def test_fresh_menu_after_uncertain_navigation_permits_new_decision(self):
        receipt = self.submitted("start")
        self.f.state = menu(2, "mode_menu", "mode:FutureMode")
        self.f.state.update(optionsComplete=True, lastAction=copy.deepcopy(self.f.reply))
        state = self.sdk.wait_setup(receipt, timeout=0)
        self.assertEqual(state["status"], "observed")
        self.assertIsNone(self.sdk._setup_unresolved)
        self.assertFalse(state["retryAllowed"])
        self.assertEqual(sum(c == "setup_action" for c, _ in self.f.calls), 1)
        self.assertEqual(self.sdk._pending_writes, [])
        self.f.reply = None
        self.assertEqual(self.sdk.setup_action("mode:FutureMode", expected_setup=state)["status"], "configured")

    def test_in_memory_menu_change_without_completion_cannot_unlock_setup(self):
        receipt = self.submitted("start")
        self.f.state = menu(2, "mode_menu", "mode:FutureMode")
        state = self.sdk.wait_setup(receipt, timeout=0)
        self.assertEqual(state["status"], "timeout")
        self.assertIsNotNone(self.sdk._setup_unresolved)
        self.assertEqual(len(self.sdk._pending_writes), 1)

    def test_saved_navigation_receipt_recovers_in_fresh_sdk_without_resending(self):
        receipt = self.submitted("start")
        self.f.state = menu(2, "mode_menu", "mode:FutureMode")
        self.f.state.update(optionsComplete=True, lastAction=copy.deepcopy(self.f.reply))
        restored = PolytopiaSDK(self.f, player_id=1)
        self.assertEqual(restored.wait_setup(receipt, timeout=0)["status"], "observed")
        self.assertIsNone(restored._setup_unresolved)
        self.f.reply = None
        self.assertEqual(restored.setup_action("mode:FutureMode", expected_setup=restored.setup_status())["status"], "configured")
        self.assertEqual([a["actionId"] for c, a in self.f.calls if c == "setup_action"], ["start", "mode:FutureMode"])

    def test_lost_navigation_reply_recovers_only_from_original_native_completion(self):
        for durable in (False, True):
            with self.subTest(durable=durable), tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1]) as tmp:
                transport = MenuTransport()
                transport.state = menu(action="start")
                transport.reply = TimeoutError("lost reply")
                sdk = PolytopiaSDK(transport, player_id=1, session_path=Path(tmp) / "session.json" if durable else None)
                with contextlib.closing(sdk):
                    receipt = sdk.setup_action("start", expected_setup=sdk.setup_status())
                    self.assertEqual(receipt["status"], "unknown")
                    transport.state = menu(2, "mode_menu", "mode:FutureMode")
                    self.assertEqual(sdk.wait_setup(receipt, timeout=0)["status"], "timeout")
                    transport.state.update(optionsComplete=True, lastAction={"ok": True, "callbackSubmitted": True,
                        "status": "submitted", "setupSessionId": "menu-session", "menuRevision": 1, "actionId": "start"})
                    self.assertEqual(sdk.wait_setup(receipt, timeout=0)["status"], "observed")
                    self.assertIsNone(sdk._setup_unresolved)
                    self.assertEqual(sdk._pending_writes, [])
                self.assertEqual(sum(c == "setup_action" for c, _ in transport.calls), 1)

    def test_foreign_refusal_preserves_setup_across_restart(self):
        for change in ({"setupSessionId": "other"}, {"menuRevision": 2},
                       {"menuRevision": True}, {"actionId": "other"}):
            with self.subTest(change=change), tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1]) as tmp:
                transport = MenuTransport()
                transport.reply = {"ok": False, "callbackSubmitted": False, "status": "rejected", **change}
                path = Path(tmp) / "session.json"
                with contextlib.closing(PolytopiaSDK(transport, player_id=1, session_path=path)) as sdk:
                    result = sdk.setup_action("select:map_preset:77", expected_setup=menu())
                    self.assertEqual(result["status"], "unknown")
                    self.assertEqual(len(sdk._pending_writes), 1)
                with contextlib.closing(PolytopiaSDK(transport, player_id=1, session_path=path)) as restored:
                    self.assertEqual(restored._setup_unresolved["actionId"], "select:map_preset:77")
                    with self.assertRaises(SDKError):
                        restored.setup_action("select:map_preset:77", expected_setup=menu())
                self.assertEqual(sum(c == "setup_action" for c, _ in transport.calls), 1)

    def test_contradictory_refusal_cannot_clear_original_request(self):
        for change in ({"callbackAttempted": True}, {"dispatched": True}, {"accepted": True},
                       {"outcome": "unknown"}, {"status": "unknown"},
                       {"ok": True, "status": "configured", "settingVerified": True}):
            with self.subTest(change=change):
                transport = MenuTransport()
                transport.reply = {"ok": False, "callbackSubmitted": False, "status": "rejected", **change}
                sdk = PolytopiaSDK(transport, player_id=1)
                self.assertEqual(sdk.setup_action("select:map_preset:77", expected_setup=menu())["status"], "unknown")
                self.assertIsNotNone(sdk._setup_unresolved)
                self.assertEqual(len(sdk._pending_writes), 1)

    def test_configured_reply_with_dispatch_conflict_is_not_success(self):
        for change in ({"dispatched": False}, {"callbackAttempted": False}, {"outcome": "unknown"},
                       {"error": "callback failed"}):
            with self.subTest(change=change):
                transport = MenuTransport()
                transport.reply = {"ok": True, "callbackSubmitted": True, "status": "configured", "settingVerified": True,
                    "setupSessionId": "menu-session", "menuRevision": 1, "actionId": "select:map_preset:77", **change}
                sdk = PolytopiaSDK(transport, player_id=1)
                self.assertEqual(sdk.setup_action("select:map_preset:77", expected_setup=menu())["status"], "unknown")
                self.assertIsNotNone(sdk._setup_unresolved)
                self.assertEqual(len(sdk._pending_writes), 1)

    def test_malformed_reply_keeps_uncertain_setup_and_never_replays(self):
        for reply in ([], "bad reply"):
            with self.subTest(reply=reply), tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1]) as tmp:
                transport = MenuTransport()
                transport.reply = reply
                path = Path(tmp) / "session.json"
                with contextlib.closing(PolytopiaSDK(transport, player_id=1, session_path=path)) as sdk:
                    self.assertEqual(sdk.setup_action("select:map_preset:77", expected_setup=menu())["status"], "unknown")
                    self.assertIsNotNone(sdk._setup_unresolved)
                with contextlib.closing(PolytopiaSDK(transport, player_id=1, session_path=path)) as restored:
                    self.assertEqual(restored.resume_pending(timeout=0)["status"], "timeout")
                    with self.assertRaises(SDKError):
                        restored.setup_action("select:map_preset:77", expected_setup=menu())
                self.assertEqual(sum(c == "setup_action" for c, _ in transport.calls), 1)

    def test_loading_revision_change_cannot_finish_creation(self):
        receipt = self.submitted()
        self.f.state = menu(3, "loading")
        self.f.state["creationSubmitted"] = True
        state = self.sdk.wait_setup(receipt, timeout=.5, interval=.25)
        self.assertEqual(state["status"], "timeout")
        self.assertIsNotNone(self.sdk._setup_unresolved)
        self.assertEqual(sum(c == "setup_action" for c, _ in self.f.calls), 1)

    def test_durable_navigation_recovers_after_cli_restart_and_allows_next_menu(self):
        with tempfile.TemporaryDirectory(prefix="setup-", dir=Path(__file__).resolve().parents[1]) as tmp:
            path = Path(tmp) / "session.json"
            self.f.state = menu(phase="start_menu", action="start")
            self.f.reply = {"ok": True, "callbackSubmitted": True, "status": "submitted",
                            "setupSessionId": "menu-session", "menuRevision": 1, "actionId": "start"}
            first = PolytopiaSDK(self.f, player_id=1, session_path=path)
            first.setup_action("start", expected_setup=first.setup_status())
            first.close()
            self.f.state = menu(2, "mode_menu", "mode:FutureMode")
            self.f.state.update(optionsComplete=True, lastAction=copy.deepcopy(self.f.reply))
            with contextlib.closing(PolytopiaSDK(self.f, player_id=1, session_path=path)) as restored:
                result = restored.resume_pending(timeout=0)
                self.assertEqual(result["status"], "observed")
                self.assertIsNone(restored._setup_unresolved)
                self.assertEqual(restored._pending_writes, [])
                self.f.reply = None
                restored.setup_action("mode:FutureMode", expected_setup=restored.setup_status())
            self.assertEqual([a['actionId'] for c, a in self.f.calls if c == 'setup_action'], ['start', 'mode:FutureMode'])
            self.assertEqual(json.loads(path.read_text())["pendingWrites"], [])

    def test_durable_navigation_requires_matching_native_completion_evidence(self):
        for change in ({'lastAction': None}, {'optionsComplete': False}, {'menuRevision': 1},
                       {'configuredPlayerId': 2}, {'phase': 'loading'},
                       {'lastAction': {'ok': False}}, {'lastAction': {'actionId': 'other'}}):
            with self.subTest(change=change), tempfile.TemporaryDirectory(prefix="setup-", dir=Path(__file__).resolve().parents[1]) as tmp:
                self.f = MenuTransport()
                path = Path(tmp) / "session.json"
                self.f.state = menu(phase="start_menu", action="start")
                self.f.reply = {"ok": True, "callbackSubmitted": True, "status": "submitted",
                                "setupSessionId": "menu-session", "menuRevision": 1, "actionId": "start"}
                with contextlib.closing(PolytopiaSDK(self.f, player_id=1, session_path=path)) as sdk:
                    receipt = sdk.setup_action("start", expected_setup=sdk.setup_status())
                    self.f.state = menu(2, "mode_menu", "mode:FutureMode")
                    self.f.state.update(optionsComplete=True, lastAction=copy.deepcopy(self.f.reply))
                    self.f.state.update(change)
                    result = sdk.wait_setup(receipt, timeout=0)
                    self.assertEqual(result['status'], 'timeout')
                    self.assertIsNotNone(sdk._setup_unresolved)
                    self.assertEqual(len(sdk._pending_writes), 1)
                self.assertEqual(sum(c == 'setup_action' for c, _ in self.f.calls), 1)

    def test_durable_creation_cannot_finish_at_an_advanced_menu(self):
        with tempfile.TemporaryDirectory(prefix="setup-", dir=Path(__file__).resolve().parents[1]) as tmp:
            path = Path(tmp) / 'session.json'
            self.f.state = menu(action='continue')
            self.f.reply = {'ok': True, 'callbackSubmitted': True, 'status': 'submitted',
                            'setupSessionId': 'menu-session', 'menuRevision': 1, 'actionId': 'continue'}
            with contextlib.closing(PolytopiaSDK(self.f, player_id=1, session_path=path)) as sdk:
                receipt = sdk.setup_action('continue', expected_setup=sdk.setup_status())
                self.f.state = menu(2, 'mode_menu', 'mode:FutureMode')
                self.f.state.update(optionsComplete=True, lastAction=copy.deepcopy(self.f.reply))
                self.assertEqual(sdk.wait_setup(receipt, timeout=0)['status'], 'timeout')
                self.assertIsNotNone(sdk._setup_unresolved)
                self.assertEqual(len(sdk._pending_writes), 1)

    def test_game_ready_binds_only_verified_singleplayer_identity(self):
        receipt = self.submitted()
        self.f.state = menu(4, "game_ready")
        self.f.state.update(ready=True, episodeId="new-episode", creationSubmitted=True)
        state = self.sdk.wait_setup(receipt, timeout=0)
        self.assertEqual(state["status"], "confirmed")
        self.assertEqual(self.sdk.episode_id, "new-episode")
        self.assertIsNone(self.sdk._setup_unresolved)

    def test_durable_creation_can_acknowledge_intro_then_confirm_original_setup(self):
        from test_sdk import Fake
        gameplay = Fake()
        gameplay.handlers['world'].update(localPlayerId=1, currentPlayerId=1)
        popup = {'ok': True, 'showing': True, 'popupType': 'BasicPopup',
                 'buttons': {'buttons': [{'id': '-1', 'closesPopup': 'True'}]}}
        gameplay.handlers['popup_info'] = popup
        gameplay.handlers['popup'] = {'ok': True, 'invokedCallback': True, 'popupStillShowing': 'False'}
        original_request = self.f.request
        self.f.request = lambda command, **args: (original_request(command, **args) if command.startswith('setup_')
                                                else gameplay.request(command, **args))
        self.f.state = menu(action='continue')
        self.f.reply = {'ok': True, 'callbackSubmitted': True, 'status': 'submitted',
                        'setupSessionId': 'menu-session', 'menuRevision': 1, 'actionId': 'continue'}
        with tempfile.TemporaryDirectory(prefix='setup-', dir=Path(__file__).resolve().parents[1]) as tmp:
            path = Path(tmp) / 'session.json'
            with contextlib.closing(PolytopiaSDK(self.f, player_id=1, session_path=path)) as sdk:
                sdk.setup_action('continue', expected_setup=sdk.setup_status())
                self.assertEqual(sdk.choose_popup(-1, expected_popup=popup)['status'], 'submitted')
                self.assertTrue(sdk._setup_unresolved['creationSubmitted'])
                self.assertEqual(len(sdk._pending_writes), 1)
                self.assertEqual(sdk._pending_writes[0]['kind'], 'setup')
                self.f.state = menu(3, 'game_ready')
                self.f.state.update(ready=True, episodeId='ep-one', creationSubmitted=True)
                self.assertEqual(sdk.resume_pending(timeout=0)['status'], 'confirmed')
                self.assertIsNone(sdk._setup_unresolved)
                self.assertEqual(sdk._pending_writes, [])
            self.assertEqual(sum(c == 'setup_action' for c, _ in self.f.calls), 1)
            self.assertEqual(sum(c == 'popup' for c, _ in gameplay.calls), 1)

    def test_durable_intro_resolution_cannot_bypass_navigation_or_unknown_creation(self):
        from test_sdk import Fake
        for action, lost in (('start', False), ('continue', True)):
            with self.subTest(action=action, lost=lost), tempfile.TemporaryDirectory(prefix='setup-', dir=Path(__file__).resolve().parents[1]) as tmp:
                gameplay = Fake()
                gameplay.handlers['world'].update(localPlayerId=1, currentPlayerId=1)
                popup = {'ok': True, 'showing': True, 'popupType': 'BasicPopup'}
                gameplay.handlers['popup_info'] = popup
                self.f = MenuTransport()
                original_request = self.f.request
                self.f.request = lambda command, **args: (original_request(command, **args) if command.startswith('setup_')
                                                        else gameplay.request(command, **args))
                self.f.state = menu(action=action)
                self.f.reply = TimeoutError('lost') if lost else {'ok': True, 'callbackSubmitted': True, 'status': 'submitted',
                    'setupSessionId': 'menu-session', 'menuRevision': 1, 'actionId': action}
                with contextlib.closing(PolytopiaSDK(self.f, player_id=1, session_path=Path(tmp) / 'session.json')) as sdk:
                    sdk.setup_action(action, expected_setup=sdk.setup_status())
                    with self.assertRaises(SDKError): sdk.choose_popup(-1, expected_popup=popup)
                    self.assertIsNotNone(sdk._setup_unresolved)
                    self.assertEqual(len(sdk._pending_writes), 1)
                self.assertFalse(any(c == 'popup' for c, _ in gameplay.calls))

    def test_ready_missing_creation_proof_or_wrong_seat_is_not_confirmed(self):
        for patch in ({"creationSubmitted": False}, {"configuredPlayerId": 2}, {"episodeId": None}):
            self.f.state = menu(4, "game_ready")
            self.f.state.update(ready=True, episodeId="new", creationSubmitted=True)
            self.f.state.update(patch)
            with self.assertRaises(SDKError):
                self.sdk.setup_status()
            self.assertIsNone(self.sdk.episode_id)

    def test_changed_game_episode_cannot_replace_existing_sdk(self):
        self.sdk.episode_id = "old"
        self.f.state = menu(4, "game_ready")
        self.f.state.update(ready=True, episodeId="new", creationSubmitted=True)
        with self.assertRaises(SDKError) as caught:
            self.sdk.setup_status()
        self.assertEqual(caught.exception.code, "EPISODE_CHANGED")
        self.assertEqual(self.sdk.episode_id, "old")

    def test_scope_mismatch_reply_keeps_uncertainty(self):
        self.f.reply = {"ok": True, "callbackSubmitted": True, "status": "configured", "settingVerified": True,
                        "setupSessionId": "other", "menuRevision": 1, "actionId": "select:map_preset:77"}
        reply = self.sdk.setup_action("select:map_preset:77", expected_setup=self.sdk.setup_status())
        self.assertEqual(reply["status"], "unknown")
        self.assertIsNotNone(self.sdk._setup_unresolved)

    def test_explicit_pre_callback_rejection_releases_only_setup_pending(self):
        self.f.reply = {"ok": False, "callbackSubmitted": False, "status": "rejected", "errorCode": "STALE_SETUP_CONTEXT"}
        reply = self.sdk.setup_action("select:map_preset:77", expected_setup=self.sdk.setup_status())
        self.assertEqual(reply["status"], "rejected")
        self.assertIsNone(self.sdk._setup_unresolved)
        self.assertEqual(self.sdk._pending_writes, [])
        self.f.reply = None
        self.assertEqual(self.sdk.setup_action("select:map_preset:77", expected_setup=menu())["status"], "configured")

    def test_not_confirmed_setting_keeps_pending_for_explicit_observation(self):
        self.f.reply = {"ok": False, "callbackSubmitted": True, "status": "not_confirmed", "settingVerified": False,
                        "setupSessionId": "menu-session", "menuRevision": 1, "actionId": "select:map_preset:77"}
        reply = self.sdk.setup_action("select:map_preset:77", expected_setup=self.sdk.setup_status())
        self.assertEqual(reply["status"], "not_confirmed")
        self.assertIsNotNone(self.sdk._setup_unresolved)
        self.assertEqual(exit_code(reply), 5)

    def test_changed_native_session_keeps_original_uncertain_receipt(self):
        receipt = self.submitted()
        self.f.state["setupSessionId"] = "restart"
        state = self.sdk.wait_setup(receipt, timeout=0)
        self.assertEqual(state["status"], "unknown")
        self.assertEqual(state["errorCode"], "SETUP_SESSION_CHANGED")
        self.assertEqual(self.sdk._setup_unresolved["setupBinding"]["setupSessionId"], "menu-session")

    def test_popup_during_creation_requests_resolution_without_replay(self):
        receipt = self.submitted()
        self.f.state = menu(4, "needs_resolution")
        state = self.sdk.wait_setup(receipt, timeout=0)
        self.assertEqual(state["status"], "needs_resolution")
        self.assertEqual(exit_code(state), 6)
        self.assertIsNotNone(self.sdk._setup_unresolved)
        self.assertEqual(sum(c == "setup_action" for c, _ in self.f.calls), 1)

    def test_wait_transport_failure_does_not_clear_creation(self):
        receipt = self.submitted()
        def fail(*args, **kwargs):
            raise TimeoutError("read lost")
        self.f.request = fail
        state = self.sdk.wait_setup(receipt, timeout=0)
        self.assertEqual(state["status"], "unknown")
        self.assertIsNotNone(self.sdk._setup_unresolved)

    def test_saved_resolution_response_can_resume_original_creation(self):
        receipt = self.submitted()
        self.f.state = menu(4, "needs_resolution")
        state = self.sdk.wait_setup(receipt, timeout=0)
        self.assertEqual(state["setupBinding"], receipt["setupBinding"])
        self.assertEqual(state["actionId"], "continue")
        self.f.state = menu(5, "game_ready")
        self.f.state.update(ready=True, episodeId="new", creationSubmitted=True)
        recovered = PolytopiaSDK(self.f, player_id=1).wait_setup(state, timeout=0)
        self.assertEqual(recovered["status"], "confirmed")
        self.assertEqual(sum(c == "setup_action" for c, _ in self.f.calls), 1)

    def test_session_change_response_preserves_recoverable_binding(self):
        receipt = self.submitted()
        self.f.state = menu(4)
        self.f.state["setupSessionId"] = "another-session"
        state = self.sdk.wait_setup(receipt, timeout=0)
        self.assertEqual(state["status"], "unknown")
        self.assertEqual(state["setupBinding"], receipt["setupBinding"])
        self.assertEqual(state["actionId"], "continue")

    def test_malformed_context_never_sends(self):
        for patch in ({"schema": "other"}, {"menuRevision": True}, {"menuRevision": 0},
                      {"configuredPlayerId": 2}, {"setupSessionId": ""}):
            state = menu()
            state.update(patch)
            with self.assertRaises(SDKError):
                self.sdk.setup_action("select:map_preset:77", expected_setup=state)
        self.assertFalse(self.f.calls)

    def test_invalid_polling_limits_rejected_before_menu_write(self):
        for timeout in (-1, float("nan"), float("inf"), True):
            with self.assertRaises(SDKError):
                self.sdk.setup_action("select:map_preset:77", expected_setup=menu(), wait=True, timeout=timeout)
        self.assertFalse(self.f.calls)

    def test_failed_native_status_cannot_bind_game_ready(self):
        self.f.state = menu(4, "game_ready")
        self.f.state.update(ok=False, ready=True, episodeId="new", creationSubmitted=True)
        self.assertEqual(self.sdk.setup_status()["status"], "unavailable")
        self.assertIsNone(self.sdk.episode_id)

    def test_missing_callback_evidence_keeps_uncertain_receipt(self):
        self.f.reply = {"ok": True, "status": "configured", "settingVerified": True}
        state = self.sdk.setup_action("select:map_preset:77", expected_setup=menu())
        self.assertEqual(state["status"], "unknown")
        self.assertIsNotNone(self.sdk._setup_unresolved)

    def test_cli_menu_status_and_configuration_share_sdk(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "menu.json"
            path.write_text(json.dumps({"native": menu()}), encoding="utf-8")
            factory = lambda **args: PolytopiaSDK(self.f, **args)
            with contextlib.redirect_stdout(io.StringIO()) as output:
                code = main(["--player", "1", "setup_status"], sdk_factory=factory)
            self.assertEqual(code, 0)
            self.assertEqual(json.loads(output.getvalue())["native"]["phase"], "settings_menu")
            with contextlib.redirect_stdout(io.StringIO()) as output:
                code = main(["--player", "1", "setup_action", str(path), "select:map_preset:77"], sdk_factory=factory)
            self.assertEqual(code, 0)
            self.assertEqual(json.loads(output.getvalue())["status"], "configured")

    def test_cli_wait_saved_creation_receipt_only_queries(self):
        receipt = self.submitted()
        self.f.state = menu(4, "game_ready")
        self.f.state.update(ready=True, episodeId="new", creationSubmitted=True)
        before = len(self.f.calls)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "receipt.json"
            path.write_text(json.dumps(receipt), encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()) as output:
                code = main(["--player", "1", "wait_setup", str(path), "--timeout", "0"],
                            sdk_factory=lambda **args: PolytopiaSDK(self.f, **args))
            self.assertEqual(code, 0)
            self.assertEqual(json.loads(output.getvalue())["status"], "confirmed")
        self.assertEqual(self.f.calls[before:], [("setup_status", {})])


if __name__ == "__main__":
    unittest.main()
