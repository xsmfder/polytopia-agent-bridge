import contextlib
import copy
import io
import json
import sys
import unittest
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from sdk import PolytopiaSDK, SDKError
from agentcli import main, exit_code


def world(version=1):
    return {"ok": True, "controlledIdentityVerified": True, "episodeId": "ep-one",
            "localPlayerId": 2, "currentPlayerId": 2, "myTurn": True, "canAct": True,
            "stars": "10", "state": {"streamId": "stream-one", "stateVersion": version},
            "researchableTechs": {"status": "ok", "items": [{"tech": "FutureTech", "cost": 3}]},
            "trainOptions": {"cities": [{"coords": "(2,3)", "options": [{"unitType": "FutureUnit", "trainable": True, "cost": 4}]}]},
            "legalMoveTargets": {"units": [{"from": "(2,3)", "targets": ["(2,4)"]}]},
            "legalAttackTargets": {"units": []}}


class Fake:
    def __init__(self):
        self.calls = []
        self.handlers = {
            "world": world(), "handoff_status": {"ok": True, "ready": True, "phase": "map_ready"},
            "popup_info": {"ok": True, "showing": False},
            "city_options": {"status": "ok", "controlledPlayerId": 2, "tileOptions": [
                {"coords": "(2,3)", "improvement": "FutureBuilding", "buildableNow": True}],
                "ownedEconomicTiles": [{"coords": "(2,3)"}], "pendingCityReward": {"choices": []}},
            "unit_actions": {"ok": True, "units": [{"unitId": "7", "actions": [
                {"actionId": "FutureSkill:custom", "executable": True, "nativeValid": True, "parameters": {"NativeField": 123}},
                {"actionId": "Unreadable", "executable": None}]}]},
            "tile_actions": {"ok": True, "actions": [{"actionId": "FutureTileSkill", "executable": True, "nativeValid": True}]},
            "tribe_relations": {"ok": True, "tribes": []},
            "leaderboard": {"ok": False, "status": "unavailable", "reason": "not displayed yet"},
            "notifications": {"ok": True, "status": "ok", "episodeId": "ep-one", "controlledPlayerId": 2,
                              "streamId": "notice-stream", "events": [], "historyGap": False},
            "action": {"ok": True, "accepted": True, "settlementTicket": 8},
            "unit_action": {"ok": True, "accepted": True, "settlementTicket": 8},
            "settlement": {"ok": True, "streamId": "stream-one", "settlementStatus": "complete",
                           "settlementComplete": True, "effectConfirmation": "confirmed", "actionCompleted": True},
            "events": {"ok": True, "streamId": "stream-one", "historyGap": False, "events": []},
            "popup": lambda args: ({"ok": True, "dismissRequested": True, "rewardResubmitted": False}
                if args.get("op") == "dismiss_resolved_reward" else {"ok": True, "invokedCallback": True}),
            "result": {"ok": True, "status": "archived"},
            "handoff_start": {"ok": True, "accepted": True, "callbackSubmitted": True}}

    def request(self, command, timeout=None, **args):
        self.calls.append((command, args))
        value = self.handlers[command]
        if callable(value):
            value = value(args)
        if isinstance(value, Exception):
            raise value
        value = copy.deepcopy(value)
        if command == "settlement":
            value.setdefault("ticketId", int(args["ticket"]))
            value.setdefault("episodeId", "ep-one")
            value.setdefault("controlledPlayerId", "2")
            value.setdefault("beforeStateVersion", 1)
        return value


class SDKTests(unittest.TestCase):
    def setUp(self):
        self.f = Fake()
        self.sdk = PolytopiaSDK(self.f, player_id=2)

    def action(self):
        return next(a for a in self.sdk.list_actions()["actions"] if a["args"].get("name") == "research")

    def test_ruins_applied_feedback_survives_original_ticket_queries(self):
        binding = {"episodeId": "ep-one", "playerId": 2, "streamId": "stream-one", "stateVersion": 1}
        for kind, unit, extra in (
            ("PopulationGrowth", "population", {"populationGains": [{"city": "(1,1)", "amount": 1}]}),
            ("SuperUnit", "units", {"generatedUnits": [{"unitId": "20", "type": "FutureNativeUnit", "coords": "(2,3)"}]}),
            ("City", "cities", {"createdCity": {"coords": "(2,3)", "type": "City"}}),
            ("Explorer", "tiles", {"exploration": {"status": "applied", "newlyExploredCount": 1,
                                                        "newlyExploredTiles": ["(1,1)"]}}),
        ):
            with self.subTest(kind=kind):
                outcome = {"nativeReward": kind, "rewardStatus": "applied", "rewardApplied": True,
                           "rewardAmount": 1, "rewardUnit": unit, **extra}
                self.f.handlers["settlement"]["ruinsOutcome"] = outcome
                result = self.sdk.wait_result(8, binding=binding)
                self.assertEqual(result["status"], "confirmed")
                self.assertEqual(result["native"]["ruinsOutcome"], outcome)
                self.assertFalse(result["retryAllowed"])
        self.assertEqual([c for c, _ in self.f.calls], ["settlement"] * 4)

    def test_ruins_interaction_completion_does_not_promote_partial_reward(self):
        self.f.handlers["settlement"]["ruinsOutcome"] = {
            "nativeReward": "PopulationGrowth", "rewardStatus": "partially_applied",
            "rewardApplied": None, "rewardAmount": None, "observedAppliedAmount": 1,
            "rewardUnit": "population", "populationGains": [{"city": "(1,1)", "amount": 1}]}
        result = self.sdk.wait_result(8, binding={"episodeId": "ep-one", "playerId": 2,
                                                "streamId": "stream-one", "stateVersion": 1})
        self.assertEqual(result["status"], "confirmed")
        self.assertIsNone(result["native"]["ruinsOutcome"]["rewardApplied"])
        self.assertEqual(result["native"]["ruinsOutcome"]["rewardStatus"], "partially_applied")

    def test_leaderboard_preserves_anonymous_snapshot_and_history(self):
        self.f.handlers["leaderboard"] = {"ok": True, "status": "historical", "episodeId": "ep-one",
            "controlledPlayerId": 2, "fresh": False, "rows": [{"label": "Unknown tribe", "position": 1, "score": 100}]}
        result = self.sdk.leaderboard()
        self.assertEqual(result["status"], "historical")
        self.assertNotIn("playerId", result["native"]["rows"][0])
        self.assertEqual(self.f.calls[-1], ("leaderboard", {"mode": "read"}))

    def test_leaderboard_modes_and_unavailable_are_not_success(self):
        self.f.handlers["leaderboard"] = {"ok": False, "status": "unavailable"}
        self.assertFalse(self.sdk.leaderboard()["ok"])
        with self.assertRaises(SDKError):
            self.sdk.leaderboard("populate_private")
        self.f.handlers["leaderboard"] = {"ok": True, "status": "opening"}
        self.assertEqual(self.sdk.leaderboard("open")["status"], "opening")

    def test_leaderboard_wrong_scope_rejected(self):
        self.f.handlers["leaderboard"] = {"ok": True, "status": "ok", "episodeId": "other", "controlledPlayerId": 2}
        with self.assertRaises(SDKError): self.sdk.leaderboard()
        self.f.handlers["leaderboard"]["episodeId"] = "ep-one"
        self.f.handlers["leaderboard"]["controlledPlayerId"] = 1
        with self.assertRaises(SDKError): self.sdk.leaderboard()

    def test_notification_history_uses_its_own_cursor_stream(self):
        self.f.handlers["notifications"] = {"ok": True, "status": "ok", "episodeId": "ep-one", "controlledPlayerId": 2,
            "streamId": "notice-stream", "events": [{"eventId": 8, "observedTurn": 9, "message": "Peace ended"}], "historyGap": True}
        result = self.sdk.notifications(2, stream_id="notice-stream", limit=5)
        self.assertEqual(result["status"], "history_gap")
        self.assertEqual(self.f.calls[-1], ("notifications", {"after": "2", "limit": "5", "stream": "notice-stream"}))
        self.assertNotIn("currentRelation", result["native"]["events"][0])

    def test_notification_missing_text_coverage_survives_direct_and_detailed_reads(self):
        self.f.handlers["notifications"].update(captureComplete=False, unreadableNoticeCount=1,
                                               events=[], historyGap=False)
        direct = self.sdk.notifications()
        detailed = self.sdk.observe(details=True)
        self.assertFalse(direct["native"]["captureComplete"])
        self.assertEqual(direct["native"]["unreadableNoticeCount"], 1)
        self.assertFalse(detailed["notifications"]["captureComplete"])
        self.assertEqual(detailed["notifications"]["unreadableNoticeCount"], 1)
        self.assertEqual(detailed["notifications"]["events"], [])
        self.f.handlers["notifications"].update(captureComplete=True, unreadableNoticeCount=0)
        fresh = self.sdk.observe(details=True)["notifications"]
        self.assertTrue(fresh["captureComplete"])
        self.assertEqual(fresh["unreadableNoticeCount"], 0)

    def test_notification_bad_cursor_and_cross_scope_rejected(self):
        for cursor, limit in [(True, 1), (-1, 1), (0, 129)]:
            with self.assertRaises(SDKError): self.sdk.notifications(cursor, limit=limit)
        self.f.handlers["notifications"] = {"ok": True, "status": "ok", "episodeId": "ep-one", "controlledPlayerId": 1, "streamId": "notice-stream"}
        with self.assertRaises(SDKError): self.sdk.notifications()
        self.f.handlers["notifications"]["controlledPlayerId"] = 2
        with self.assertRaises(SDKError): self.sdk.notifications(stream_id="another-stream")

    def test_notification_observer_unavailable_is_not_empty_success(self):
        self.f.handlers["notifications"] = {"ok": True, "status": "unavailable", "episodeId": "ep-one",
            "controlledPlayerId": 2, "streamId": "notice-stream", "observerInstalled": False, "events": []}
        self.assertEqual(self.sdk.notifications()["status"], "unavailable")

    def test_public_display_cli_commands_route_without_actions(self):
        self.f.handlers["leaderboard"] = {"ok": True, "status": "historical"}
        self.f.handlers["notifications"] = {"ok": True, "status": "ok", "episodeId": "ep-one", "controlledPlayerId": 2, "streamId": "notice-stream", "events": []}
        with contextlib.redirect_stdout(io.StringIO()) as output:
            code = main(["--player", "2", "leaderboard"], sdk_factory=lambda **kwargs: self.sdk)
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(output.getvalue())["status"], "historical")
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(main(["--player", "2", "notifications"], sdk_factory=lambda **kwargs: self.sdk), 0)
        self.assertFalse(any(command in {"action", "unit_action"} for command, _ in self.f.calls))

    def test_observe_preserves_public_hints_styles_and_own_tasks(self):
        hint = {"present": True, "status": "ok", "authority": "native indicator"}
        self.f.handlers["world"]["myUnits"] = [{"properties": {"adjacentInvisibleThreat": hint}}]
        self.f.handlers["world"]["tiles"] = [{"c": "(2,3)", "visualStyle": {
            "tribe": "Imperius", "skin": "Default", "status": "ok"}}]
        task = {"task": "FutureTask", "started": True, "completed": False, "improvementUnlocks": None,
                "completionText": "3 / 10", "completionTextStatus": "ok"}
        self.f.handlers["city_options"]["tasks"] = [task]
        observed = self.sdk.observe(details=True)
        self.assertEqual(observed["world"]["myUnits"][0]["properties"]["adjacentInvisibleThreat"], hint)
        self.assertEqual(observed["world"]["tiles"], self.f.handlers["world"]["tiles"])
        self.assertEqual(observed["cities"]["tasks"], [task])
        self.assertNotIn("progress", observed["cities"]["tasks"][0])
        self.f.handlers["world"]["myUnits"][0]["properties"]["adjacentInvisibleThreat"]["present"] = None
        self.f.handlers["world"]["myUnits"][0]["properties"]["adjacentInvisibleThreat"]["status"] = "unavailable"
        fresh = self.sdk.observe()
        self.assertIsNone(fresh["world"]["myUnits"][0]["properties"]["adjacentInvisibleThreat"]["present"])
        self.assertIs(observed["world"]["myUnits"][0]["properties"]["adjacentInvisibleThreat"]["present"], True)

    def test_detailed_observation_passive_display_reads_and_partial_availability(self):
        observed = self.sdk.observe(details=True)
        self.assertTrue(observed["ok"])
        self.assertFalse(observed["leaderboard"]["ok"])
        self.assertEqual(observed["notifications"]["streamId"], "notice-stream")
        self.assertEqual(sum(c == "world" for c, _ in self.f.calls), 2)
        self.assertIn(("leaderboard", {"mode": "read"}), self.f.calls)
        self.assertFalse(any(args.get("mode") == "open" for _, args in self.f.calls))
        self.f.calls.clear()
        self.sdk.observe()
        self.assertFalse(any(c in {"leaderboard", "notifications"} for c, _ in self.f.calls))

    def test_detailed_observation_rejects_cross_scope_and_unidentified_display(self):
        for native in [
            {"ok": True, "status": "ok", "episodeId": "other", "controlledPlayerId": 2},
            {"ok": True, "status": "historical", "episodeId": "ep-one", "controlledPlayerId": 1},
            {"ok": True, "status": "ok", "rows": []},
        ]:
            self.f.handlers["leaderboard"] = native
            with self.assertRaises(SDKError): self.sdk.observe(details=True)
        self.f.handlers["leaderboard"] = {"ok": False, "status": "unavailable"}
        self.f.handlers["notifications"]["controlledPlayerId"] = 1
        with self.assertRaises(SDKError): self.sdk.observe(details=True)

    def test_detailed_observation_preserves_history_gap_and_version_check(self):
        self.f.handlers["notifications"]["historyGap"] = True
        self.assertTrue(self.sdk.observe(details=True)["notifications"]["historyGap"])
        calls = [0]
        def changing_world(args):
            calls[0] += 1
            return world(calls[0])
        self.f.handlers["world"] = changing_world
        changed = self.sdk.observe(details=True)
        self.assertEqual(changed["status"], "state_changed")
        self.assertIsNone(changed["world"])

    def test_dynamic_diplomacy_candidates_response_variants_and_unimplemented_adapter(self):
        self.f.handlers["tribe_relations"] = {"ok": True, "tribes": [{"playerId": 3, "operations": [
            {"actionId": "peace_response", "accept": False, "canExecute": True, "adapterSupported": True},
            {"actionId": "peace_response", "accept": True, "canExecute": True, "adapterSupported": True},
            {"actionId": "native:FutureDiplomacy", "canExecute": True, "adapterSupported": False}]}]}
        actions = self.sdk.list_actions("tribe")["actions"]
        self.assertEqual(len(actions), 3)
        self.assertEqual([a["args"].get("accept") for a in actions[:2]], ["false", "true"])
        self.assertFalse(actions[-1]["eligible"])
        self.assertEqual(len({a["actionId"] for a in actions}), 3)

    def test_city_native_potential_hint_is_preserved_without_inventing_reward_actions(self):
        semantics = {"canCityBeUpgradedRole": "cached-native-construction-potential",
                     "canCityBeUpgradedPersistence": "not-guaranteed-across-save-resume",
                     "populationRequirementField": "populationNeededToUpgrade",
                     "executableChoiceField": "pendingCityReward"}
        city = self.f.handlers["city_options"]
        city["cityUpgradeSemantics"] = semantics
        city["pendingCityReward"] = {"status": "none", "choices": []}
        for hint in (True, False, None):
            with self.subTest(hint=hint):
                city["myCities"] = [{"coords": "(2,3)", "canCityBeUpgraded": hint,
                                     "populationNeededToUpgrade": 5}]
                observed = self.sdk.observe(details=True)["cities"]
                self.assertIs(observed["myCities"][0]["canCityBeUpgraded"], hint)
                self.assertEqual(observed["cityUpgradeSemantics"], semantics)
                self.assertFalse(any(a["args"].get("name") == "city_reward"
                                     for a in self.sdk.list_actions("city")["actions"]))

    def test_actual_city_reward_choice_does_not_depend_on_cached_potential_hint(self):
        city = self.f.handlers["city_options"]
        city["myCities"] = [{"coords": "(2,3)", "canCityBeUpgraded": False,
                             "populationNeededToUpgrade": 5}]
        city["pendingCityReward"] = {"status": "offered", "city": "(2,3)",
                                     "choices": [{"reward": "FutureActualReward"}]}
        rewards = [a for a in self.sdk.list_actions("city")["actions"]
                   if a["args"].get("name") == "city_reward"]
        self.assertEqual(len(rewards), 1)
        self.assertEqual(rewards[0]["args"]["reward"], "FutureActualReward")
        self.assertTrue(rewards[0]["eligible"])

    def test_break_peace_and_destroy_embassy_execute_once_and_wait_for_native_effect(self):
        self.f.handlers["tribe_relations"] = {"ok": True, "tribes": [{"playerId": 3, "operations": [
            {"actionId": "break_peace", "canExecute": True, "adapterSupported": True},
            {"actionId": "destroy_embassy", "canExecute": True, "adapterSupported": True}]}]}
        self.f.handlers["tribe_action"] = {"ok": True, "nativeExecution": {
            "accepted": True, "settlementTicket": 8}}
        for operation in ("break_peace", "destroy_embassy"):
            proposal = next(a for a in self.sdk.list_actions("tribe")["actions"]
                            if a["args"]["operation"] == operation)
            self.assertEqual(self.sdk.execute(proposal, wait=True)["status"], "confirmed")
        sent = [args for command, args in self.f.calls if command == "tribe_action"]
        self.assertEqual(len(sent), 2)
        self.assertEqual([a["operation"] for a in sent], ["break_peace", "destroy_embassy"])
        self.assertTrue(all(a["expectedPlayerId"] == "2" and a["opponent"] == "3" for a in sent))

    def test_diplomacy_disappearing_candidate_and_uncertain_effect_do_not_resend(self):
        self.f.handlers["tribe_relations"] = {"ok": True, "tribes": [{"playerId": 3, "operations": [
            {"actionId": "destroy_embassy", "canExecute": True, "adapterSupported": True}]}]}
        proposal = self.sdk.list_actions("tribe")["actions"][0]
        self.f.handlers["tribe_relations"]["tribes"][0]["operations"] = []
        with self.assertRaises(SDKError): self.sdk.execute(proposal)
        self.assertFalse(any(command == "tribe_action" for command, _ in self.f.calls))
        self.f.handlers["tribe_relations"]["tribes"][0]["operations"] = [proposal["native"]]
        self.f.handlers["tribe_action"] = {"ok": True, "nativeExecution": {"accepted": True, "settlementTicket": 8}}
        self.f.handlers["settlement"]["effectConfirmation"] = "unknown"
        self.assertEqual(self.sdk.execute(proposal, wait=True)["status"], "unknown")
        with self.assertRaises(SDKError): self.sdk.execute(proposal)
        self.assertEqual(sum(command == "tribe_action" for command, _ in self.f.calls), 1)

    def test_opponent_turn_notification_acknowledgement_unblocks_end_turn(self):
        self.f.handlers["world"].update(myTurn=False, currentPlayerId=1, canAct=False, popupNeedsChoice=False)
        self.f.handlers["handoff_status"] = {"ok": True, "phase": "not_hotseat"}
        popup = {"ok": True, "showing": True, "popupType": "BasicPopupLegacy", "title": "Peace treaty rejected",
                 "buttons": {"buttons": [{"id": "-1", "closesPopup": "True"}]}}
        self.f.handlers["popup_info"] = popup
        self.assertEqual(self.sdk.choose_popup(-1, expected_popup=popup)["status"], "submitted")
        commands = [cmd for cmd, _ in self.f.calls]
        self.assertLess(commands.index("handoff_status"), commands.index("popup"))
        self.assertEqual(commands.count("popup"), 1)

    def test_opponent_turn_acknowledgement_rejects_hotseat_and_unknown_mode(self):
        self.f.handlers["world"].update(myTurn=False, canAct=False, popupNeedsChoice=False)
        popup = {"ok": True, "showing": True, "popupType": "BasicPopupLegacy",
                 "buttons": {"buttons": [{"id": "-1", "closesPopup": True}]}}
        self.f.handlers["popup_info"] = popup
        for phase in ["human_turn", "needs_decision", "curtain", None]:
            self.f.handlers["handoff_status"] = {"ok": True, "phase": phase}
            with self.assertRaises(SDKError):
                self.sdk.choose_popup(-1, expected_popup=popup)
        self.assertFalse(any(cmd == "popup" for cmd, _ in self.f.calls))

    def test_opponent_turn_actual_choices_unknowns_and_rewards_remain_blocked(self):
        base = {"ok": True, "showing": True, "popupType": "BasicPopupLegacy",
                "buttons": {"buttons": [{"id": "-1", "closesPopup": True}]}}
        self.f.handlers["handoff_status"] = {"ok": True, "phase": "not_hotseat"}
        cases = [(False, None, base, -1), (None, False, base, -1),
                 (False, True, base, -1), (False, False, base, 0),
                 (False, False, {**base, "popupType": "RewardPopup"}, -1),
                 (False, False, {**base, "buttons": {"buttons": [{"id": "-1"}]}}, -1),
                 (False, False, {**base, "buttons": {"buttons": [{"id": "-1", "closesPopup": True}, {"id": "0"}]}}, -1)]
        for my_turn, needs_choice, popup, index in cases:
            self.f.handlers["world"].update(myTurn=my_turn, canAct=False, popupNeedsChoice=needs_choice)
            self.f.handlers["popup_info"] = popup
            with self.assertRaises(SDKError):
                self.sdk.choose_popup(index, expected_popup=popup)
        self.assertFalse(any(cmd == "popup" for cmd, _ in self.f.calls))

    def test_owned_visible_hotseat_recap_notice_can_be_acknowledged(self):
        self.f.handlers["world"].update(myTurn=False, currentPlayerId=1, canAct=False, popupNeedsChoice=False)
        self.f.handlers["handoff_status"] = {
            "ok": True, "phase": "needs_decision", "needsDecision": True, "recapping": True,
            "overlayActive": False, "overlayPresented": False, "configuredPlayerId": 2, "targetPlayerId": 2}
        popup = {"ok": True, "showing": True, "popupType": "BasicPopupLegacy", "title": "Broke Peace",
                 "buttons": {"popupType": "DiplomacyPopup", "buttons": [{"id": "-1", "closesPopup": "True"}]}}
        self.f.handlers["popup_info"] = popup
        observation = self.sdk.watch_handoff(timeout=0)
        self.assertEqual(observation["status"], "needs_resolution")
        self.assertEqual(observation["popup"]["title"], "Broke Peace")
        for wrapper in ("BasicPopupLegacy", "DiplomacyPopup"):
            self.f.handlers["popup_info"] = {**popup, "popupType": wrapper}
            self.assertEqual(self.sdk.choose_popup(-1, expected_popup=observation["popup"])["status"], "submitted")
        self.assertEqual(sum(cmd == "popup" for cmd, _ in self.f.calls), 2)
        self.assertFalse(any(cmd in {"action", "handoff_start"} for cmd, _ in self.f.calls))

    def test_hotseat_recap_acknowledgement_requires_owned_unconcealed_recap(self):
        self.f.handlers["world"].update(myTurn=False, currentPlayerId=1, canAct=False, popupNeedsChoice=False)
        popup = {"ok": True, "showing": True, "popupType": "BasicPopupLegacy",
                 "buttons": {"buttons": [{"id": "-1", "closesPopup": True}]}}
        self.f.handlers["popup_info"] = popup
        status = {"ok": True, "phase": "needs_decision", "needsDecision": True, "recapping": True,
                  "overlayActive": False, "overlayPresented": False, "configuredPlayerId": 2, "targetPlayerId": 2}
        for change in ({"phase": "human_turn"}, {"phase": "recap"}, {"recapping": False},
                       {"recapping": None}, {"recapping": 1}, {"overlayActive": True},
                       {"overlayPresented": True}, {"overlayPresented": None},
                       {"configuredPlayerId": 1}, {"targetPlayerId": 1}, {"needsDecision": None}):
            with self.subTest(change=change):
                self.f.handlers["handoff_status"] = {**status, **change}
                with self.assertRaises(SDKError):
                    self.sdk.choose_popup(-1, expected_popup=popup)
        self.assertFalse(any(cmd == "popup" for cmd, _ in self.f.calls))

    def test_popup_base_wrapper_alias_keeps_same_concrete_buttons_and_text(self):
        expected = {"ok": True, "showing": True, "popupType": "BasicPopupLegacy", "title": "Embassy Established",
                    "body": "Established", "buttons": {"popupType": "DiplomacyPopup", "buttons": [{"id": "-1"}]}}
        self.f.handlers["popup_info"] = {**expected, "popupType": "DiplomacyPopup", "reqId": 123}
        self.assertEqual(self.sdk.choose_popup(-1, expected_popup=expected)["status"], "submitted")
        self.assertEqual(sum(c == "popup" for c, _ in self.f.calls), 1)
        self.assertEqual(expected["popupType"], "BasicPopupLegacy")

    def test_popup_alias_normalization_still_rejects_actual_content_or_concrete_type_change(self):
        expected = {"ok": True, "showing": True, "popupType": "BasicPopupLegacy", "title": "Embassy Established",
                    "body": "Established", "buttons": {"popupType": "DiplomacyPopup", "buttons": [{"id": "-1"}]}}
        for change in [{"title": "Another notice"}, {"body": "Other reward"}, {"popupType": "IconRewardPopup"},
                       {"buttons": {"popupType": "DiplomacyPopup", "buttons": [{"id": "0"}]}}]:
            self.f.handlers["popup_info"] = {**expected, **change}
            with self.assertRaises(SDKError): self.sdk.choose_popup(-1, expected_popup=expected)
        self.assertFalse(any(c == "popup" for c, _ in self.f.calls))

    def test_end_turn_transient_unknown_waits_only_original_ticket(self):
        end = next(a for a in self.sdk.list_actions()["actions"] if a["args"].get("name") == "end_turn")
        ticks = [0.0]
        self.sdk.clock = lambda: ticks[0]
        self.sdk.sleep = lambda seconds: ticks.__setitem__(0, ticks[0] + seconds)
        count = [0]
        def settlement(args):
            count[0] += 1
            if count[0] < 3:
                return {"ok": True, "streamId": "stream-one", "action": "end_turn",
                        "settlementStatus": "unknown", "settlementComplete": False,
                        "actionCompleted": False, "effectConfirmation": "unknown",
                        "pipelineIdle": None, "publicHandoff": None}
            return {"ok": True, "streamId": "stream-one", "action": "end_turn",
                    "settlementStatus": "complete", "settlementComplete": True,
                    "actionCompleted": True, "effectConfirmation": "confirmed",
                    "pipelineIdle": None, "publicHandoff": {"turn": "22", "targetPlayerId": "1"}}
        self.f.handlers["settlement"] = settlement
        result = self.sdk.execute(end, wait=True, timeout=2)
        self.assertEqual(result["status"], "confirmed")
        self.assertEqual(count[0], 3)
        self.assertIsNone(self.sdk.unresolved)
        sent = [c for c in self.f.calls if c[0] == "action"]
        self.assertEqual(len(sent), 1)
        self.assertEqual([c[0] for c in self.f.calls[self.f.calls.index(sent[0])+1:]], ["settlement"] * 3)

    def test_end_turn_unknown_deadline_retains_receipt_and_blocks_writes(self):
        receipt = self.sdk.execute(self.action())
        ticks = [0.0]
        self.sdk.clock = lambda: ticks[0]
        self.sdk.sleep = lambda seconds: ticks.__setitem__(0, ticks[0] + seconds)
        self.f.handlers["settlement"].update({"action": "end_turn", "settlementStatus": "unknown",
            "settlementComplete": False, "actionCompleted": False, "effectConfirmation": "unknown"})
        result = self.sdk.wait_result(receipt["ticket"], timeout=.5)
        self.assertEqual(result["status"], "timeout")
        self.assertEqual(result["native"]["settlementStatus"], "unknown")
        self.assertIsNotNone(self.sdk.unresolved)
        self.assertFalse(result["retryAllowed"])
        self.assertEqual(ticks[0], .5)
        with self.assertRaises(SDKError):
            self.sdk.execute(self.action())

    def test_non_handoff_unknown_or_transport_failure_returns_immediately(self):
        receipt = self.sdk.execute(self.action())
        self.sdk.sleep = lambda seconds: self.fail("generic unknown must not wait")
        self.f.handlers["settlement"].update({"action": "research", "settlementStatus": "unknown",
            "settlementComplete": False, "actionCompleted": False, "effectConfirmation": "unknown"})
        self.assertEqual(self.sdk.wait_result(receipt["ticket"])["status"], "unknown")
        self.f.handlers["settlement"] = TimeoutError("read uncertain")
        self.assertEqual(self.sdk.wait_result(receipt["ticket"])["status"], "unknown")
        self.assertIsNotNone(self.sdk.unresolved)

    def test_native_new_candidates_and_parameters_retained(self):
        discovery = self.sdk.list_actions("all")
        values = [a["args"].get("kind") or a["args"].get("action_id") for a in discovery["actions"]]
        for candidate in ("FutureTech", "FutureUnit", "FutureBuilding", "FutureSkill:custom", "FutureTileSkill"):
            self.assertIn(candidate, values)
        self.assertIsNone(next(a for a in discovery["actions"] if a["args"].get("action_id") == "Unreadable")["eligible"])

    def test_native_affordability_not_invented(self):
        self.f.handlers["world"]["stars"] = None
        self.assertIsNone(self.action()["eligible"])

    def test_no_hidden_or_wrong_seat_menu_reads(self):
        self.f.handlers["world"]["localPlayerId"] = 1
        with self.assertRaisesRegex(SDKError, "another player"):
            self.sdk.list_actions()
        self.assertEqual([c[0] for c in self.f.calls], ["world"])

    def test_dark_observe_only_public_handoff(self):
        self.f.handlers["handoff_status"] = {"ok": True, "ready": False, "phase": "curtain"}
        self.assertEqual(self.sdk.observe()["status"], "handoff")
        self.assertEqual([c[0] for c in self.f.calls], ["handoff_status"])

    def test_state_change_during_discovery(self):
        self.f.handlers["world"] = lambda _: world(len([c for c in self.f.calls if c[0] == "world"]))
        with self.assertRaises(SDKError) as caught:
            self.sdk.list_actions()
        self.assertEqual(caught.exception.code, "STATE_CHANGED")

    def test_tampered_or_stale_action_never_dispatched(self):
        action = self.action()
        action["args"]["kind"] = "HiddenTech"
        with self.assertRaises(SDKError):
            self.sdk.execute(action)
        self.f.handlers["world"]["state"]["stateVersion"] = 2
        with self.assertRaises(SDKError) as caught:
            self.sdk.execute(self.action() | {"binding": {**action["binding"], "stateVersion": 1}})
        self.assertEqual(caught.exception.code, "STALE_ACTION")
        self.assertFalse(any(c[0] == "action" for c in self.f.calls))

    def test_dispatch_accepted_wait_confirmed_and_guards(self):
        receipt = self.sdk.execute(self.action())
        self.assertEqual(receipt["status"], "accepted")
        dispatch = next(args for cmd, args in self.f.calls if cmd == "action")
        self.assertEqual(dispatch["expectedPlayerId"], "2")
        self.assertEqual(dispatch["expectedEpisodeId"], "ep-one")
        self.assertEqual(self.sdk.wait_result(8)["status"], "confirmed")
        self.assertIsNone(self.sdk.unresolved)

    def test_unknown_blocks_resend(self):
        action = self.action()
        self.f.handlers["action"] = TimeoutError("late result")
        self.assertEqual(self.sdk.execute(action)["status"], "unknown")
        with self.assertRaises(SDKError) as caught:
            self.sdk.execute(action)
        self.assertEqual(caught.exception.code, "UNRESOLVED_ACTION")
        self.assertEqual(len([c for c in self.f.calls if c[0] == "action"]), 1)

    def test_complete_unknown_is_not_success(self):
        receipt = self.sdk.execute(self.action())
        self.f.handlers["settlement"]["effectConfirmation"] = "unknown"
        self.f.handlers["settlement"]["actionCompleted"] = False
        self.assertEqual(self.sdk.wait_result(8)["status"], "unknown")
        self.assertIsNotNone(self.sdk.unresolved)

    def test_wait_timeout_and_wrong_stream(self):
        self.sdk.execute(self.action())
        self.f.handlers["settlement"]["settlementStatus"] = "pending"
        self.assertEqual(self.sdk.wait_result(8, timeout=0)["status"], "timeout")
        self.f.handlers["settlement"]["streamId"] = "other"
        with self.assertRaises(SDKError):
            self.sdk.wait_result(8)

    def test_needs_resolution_allows_reward_only_then_original_ticket(self):
        receipt = self.sdk.execute(self.action())
        self.f.handlers["settlement"]["settlementStatus"] = "needs_resolution"
        self.assertEqual(self.sdk.wait_result(8)["status"], "needs_resolution")
        self.f.handlers["city_options"]["pendingCityReward"] = {"city": "(2,3)", "choices": [{"reward": "FutureReward"}]}
        self.f.handlers["world"]["canAct"] = False
        reward = next(a for a in self.sdk.list_actions("city")["actions"] if a["args"].get("name") == "city_reward")
        self.f.handlers["action"]["settlementTicket"] = 9
        self.f.handlers["settlement"]["settlementStatus"] = "complete"
        self.assertEqual(self.sdk.execute(reward, wait=True)["status"], "confirmed")
        self.assertEqual(self.sdk.unresolved["ticket"], 8)
        self.assertEqual(self.sdk.wait_result(8)["status"], "confirmed")

    def test_event_cursor_gap_and_stream(self):
        self.f.handlers["events"]["historyGap"] = True
        self.assertEqual(self.sdk.events()["status"], "history_gap")
        with self.assertRaises(SDKError):
            self.sdk.events(stream_id="old")

    def test_events_retain_transient_own_invisible_hints_and_unknowns(self):
        changes = [(False, True), (True, False), (False, None), (None, False)]
        events = [{"eventId": index, "kind": "own_invisible_threat_changed", "unitId": "own",
                   "coords": "(4,4)", "actorId": None, "nativeAction": None,
                   "nativeTargetId": None, "nativeTargetCoord": None,
                   "before": {"present": old, "status": "unavailable" if old is None else "ok"},
                   "after": {"present": new, "status": "unavailable" if new is None else "ok"}}
                  for index, (old, new) in enumerate(changes, 1)]
        self.f.handlers["events"].update(events=events, nextEventId=4)
        result = self.sdk.events()
        self.assertEqual(result["native"]["events"], events)
        self.assertIs(result["native"]["events"][0]["after"]["present"], True)
        self.assertIsNone(result["native"]["events"][2]["after"]["present"])
        self.assertFalse(any(command in {"action", "unit_action"} for command, _ in self.f.calls))

    def test_events_preserve_per_type_hook_evidence_without_promoting_coverage(self):
        hooks = {"schema": "visible-native-hook-evidence-1", "streamId": "stream-one",
                 "installationStatus": "partial", "completeActionCoverage": False,
                 "observedVisibleTypes": [{"nativeAction": "AttackAction", "visibleEventCount": 2,
                                            "firstEventId": 1, "lastEventId": 2}],
                 "unobservedTypes": ["MoveAction"]}
        self.f.handlers["events"].update(nativeHooks=hooks, nativeHookCoverage="partial-or-unavailable")
        result = self.sdk.events()
        self.assertEqual(result["native"]["nativeHooks"], hooks)
        self.assertFalse(result["native"]["nativeHooks"]["completeActionCoverage"])
        self.assertFalse(any(command in {"action", "unit_action"} for command, _ in self.f.calls))

    def test_events_future_cursor_error_preserves_native_and_never_restarts(self):
        self.f.handlers["events"] = {"ok": False, "errorCode": "EVENT_CURSOR_AHEAD",
                                    "streamId": "stream-one", "latestEventId": 3}
        with self.assertRaises(SDKError) as failure:
            self.sdk.events(20)
        self.assertEqual(failure.exception.code, "EVENT_CURSOR_AHEAD")
        self.assertEqual(failure.exception.details["native"]["latestEventId"], 3)
        self.assertEqual([args for command, args in self.f.calls if command == "events"],
                         [{"afterEventId": "20", "limit": "100"}])

    def test_events_stream_change_during_read_refuses_old_coverage(self):
        self.f.handlers["events"]["streamId"] = "new-stream"
        self.f.handlers["events"]["nativeHooks"] = {"observedVisibleTypes": [{"nativeAction": "AttackAction"}]}
        with self.assertRaises(SDKError) as failure:
            self.sdk.events()
        self.assertEqual(failure.exception.code, "STREAM_CHANGED")

    def test_popup_changed_refused_and_no_effect_success(self):
        current = {"ok": True, "showing": True, "popupType": "BasicPopup", "title": "A"}
        self.f.handlers["popup_info"] = current
        self.assertEqual(self.sdk.choose_popup(-1, expected_popup={**current, "reqId": 1})["status"], "submitted")
        with self.assertRaises(SDKError):
            self.sdk.choose_popup(0, expected_popup={**current, "title": "B"})

    def test_handoff_callback_once_and_human_private_pause(self):
        self.f.handlers["handoff_status"] = {"ok": True, "ready": False, "canStart": True,
            "configuredPlayerId": 2, "targetPlayerId": 2, "handoffId": "h1"}
        self.assertEqual(self.sdk.watch_handoff(timeout=0)["status"], "timeout")
        self.assertEqual(len([c for c in self.f.calls if c[0] == "handoff_start"]), 1)
        self.f.handlers["handoff_status"] = {"ok": True, "phase": "human_turn"}
        self.assertEqual(self.sdk.watch_handoff(return_on_human=True)["status"], "human_turn")
        self.assertFalse(any(c[0] == "world" for c in self.f.calls))

    def test_archive_needs_no_live_world(self):
        self.assertEqual(self.sdk.result("old-episode")["status"], "archived")
        self.assertEqual(self.f.calls, [("result", {"episode": "old-episode"})])

    def test_cli_json_and_exit_codes(self):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = main(["--player", "2", "list_actions"], sdk_factory=lambda **kw: PolytopiaSDK(self.f, **kw))
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(out.getvalue())["status"], "discovered")
        for status, code in [("accepted", 6), ("needs_resolution", 6), ("rejected", 3), ("timeout", 4), ("unknown", 5), ("confirmed", 0)]:
            self.assertEqual(exit_code({"status": status}), code)

    def test_strict_integer_ids_and_cursor(self):
        for player in (True, 1.5, 2.0, "1.5"):
            with self.assertRaises(SDKError):
                PolytopiaSDK(self.f, player_id=player)
        for cursor in (True, 1.5):
            with self.assertRaises(SDKError):
                self.sdk.events(cursor)

    def test_native_validity_false_or_unknown(self):
        for native_valid, expected in ((False, False), (None, None), (True, True)):
            self.f.handlers["unit_actions"]["units"][0]["actions"][0]["nativeValid"] = native_valid
            menu = self.sdk.list_actions("unit", unit=7)
            self.assertIs(menu["actions"][0]["eligible"], expected)

    def test_missing_native_stream_or_wrong_ticket_never_confirmed(self):
        receipt = self.sdk.execute(self.action())
        del self.f.handlers["settlement"]["streamId"]
        self.assertEqual(self.sdk.wait_result(8)["status"], "unknown")
        self.f.handlers["settlement"]["ticketId"] = 999
        self.assertEqual(self.sdk.wait_result(8)["status"], "unknown")
        with self.assertRaises(SDKError):
            self.sdk.wait_result(8, binding={"playerId": 2})

    def test_result_wrong_seat_refused(self):
        self.f.handlers["result"]["controlledPlayerId"] = 1
        with self.assertRaises(SDKError):
            self.sdk.result()
        self.f.handlers["result"] = {"ok": True, "status": "complete", "result": {"controlledPlayerId": 1}}
        with self.assertRaises(SDKError):
            self.sdk.result()

    def test_solo_observation_not_blocked_by_hotseat_status(self):
        self.f.handlers["handoff_status"] = {"ok": True, "phase": "not_hotseat", "ready": False}
        self.assertEqual(self.sdk.observe()["status"], "observed")

    def test_watch_waits_human_endturn_and_callback_once(self):
        statuses = iter([
            {"ok": True, "phase": "human_turn", "ready": False},
            {"ok": True, "phase": "handoff_overlay", "ready": False, "canStart": True,
             "configuredPlayerId": 2, "targetPlayerId": 2, "handoffId": "h1"},
            {"ok": True, "phase": "map_ready", "ready": True},
            {"ok": True, "phase": "map_ready", "ready": True}])
        self.f.handlers["handoff_status"] = lambda _: next(statuses)
        self.sdk.sleep = lambda _: None
        self.assertEqual(self.sdk.watch_handoff()["status"], "observed")
        self.assertEqual(len([c for c in self.f.calls if c[0] == "handoff_start"]), 1)

    def test_sdk_exception_log_preserves_original_receipt_and_error_code(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "exceptions.jsonl"
            sdk = PolytopiaSDK(self.f, player_id=2, log_path=path)
            original = {"ticket": 8, "binding": {"episodeId": "ep-one"}}
            sdk.unresolved = original
            self.f.handlers["world"] = {"ok": False, "errorCode": "READ_UNAVAILABLE", "error": "visible frame unavailable"}
            with self.assertRaises(SDKError) as caught:
                sdk.observe()
            self.assertEqual(caught.exception.code, "READ_UNAVAILABLE")
            row = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(row["method"], "observe")
            self.assertEqual(row["exception"]["errorCode"], "READ_UNAVAILABLE")
            self.assertEqual(row["unresolved"], original)
            self.assertIs(sdk.unresolved, original)
            self.assertFalse(any(cmd == "action" for cmd, _ in self.f.calls))

    def test_transport_exception_is_logged_and_raised_unchanged(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "exceptions.jsonl"
            sdk = PolytopiaSDK(self.f, player_id=2, log_path=path)
            failure = TimeoutError("read timed out")
            self.f.handlers["world"] = failure
            with self.assertRaises(TimeoutError) as caught:
                sdk.events()
            self.assertIs(caught.exception, failure)
            row = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(row["method"], "events")
            self.assertEqual(row["exception"]["type"], "TimeoutError")

    def test_failed_logging_does_not_mask_original_exception(self):
        with tempfile.TemporaryDirectory() as directory:
            sdk = PolytopiaSDK(self.f, player_id=2)
            sdk.log_path = Path(directory)  # Opening a directory as a file fails.
            failure = RuntimeError("original transport failure")
            self.f.handlers["world"] = failure
            with self.assertRaises(RuntimeError) as caught:
                sdk.events()
            self.assertIs(caught.exception, failure)

    def test_failed_logging_keeps_accepted_action_receipt_without_resend(self):
        proposal = self.action()
        with tempfile.TemporaryDirectory() as directory:
            self.sdk.log_path = Path(directory)
            receipt = self.sdk.execute(proposal)
            self.assertEqual(receipt["status"], "accepted")
            self.assertEqual(receipt["ticket"], 8)
            self.assertIn("logError", receipt)
            self.assertEqual(sum(cmd == "action" for cmd, _ in self.f.calls), 1)
            self.assertEqual(self.sdk.unresolved["ticket"], 8)

    def test_cli_explicit_unknown_ack_requeries_original_and_keeps_effect_unknown(self):
        with tempfile.TemporaryDirectory() as directory:
            p = Path(directory) / "receipt.json"
            binding = {"episodeId": "ep-one", "playerId": 2, "streamId": "stream-one", "stateVersion": 1}
            p.write_text(json.dumps({"ticket": 8, "binding": binding}), encoding="utf-8")
            self.f.handlers["settlement"].update(effectConfirmation="unknown", actionCompleted=False,
                settlementComplete=True, pipelineIdle=True, completionAuthority="native pipeline idle", stateVersion=1)
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                code = main(["--player", "2", "acknowledge_settled_unknown", str(p), "--accept-unknown-effect"],
                            sdk_factory=lambda **kw: self.sdk)
            result = json.loads(output.getvalue())
            self.assertEqual(code, 5)
            self.assertEqual(result["status"], "settled_effect_unknown")
            self.assertFalse(result["ok"])
            self.assertTrue(result["canContinue"])
            self.assertFalse(result["retryAllowed"])
            self.assertIsNone(self.sdk.unresolved)
            self.assertFalse(any(cmd == "action" for cmd, _ in self.f.calls))

    def test_cli_unknown_ack_cannot_release_incomplete_action(self):
        with tempfile.TemporaryDirectory() as directory:
            p = Path(directory) / "receipt.json"
            p.write_text(json.dumps({"ticket": 8, "binding": {"episodeId": "ep-one", "playerId": 2,
                "streamId": "stream-one", "stateVersion": 1}}), encoding="utf-8")
            self.f.handlers["settlement"].update(effectConfirmation="unknown", actionCompleted=False,
                settlementComplete=False, pipelineIdle=False)
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                code = main(["--player", "2", "acknowledge_settled_unknown", str(p), "--accept-unknown-effect"],
                            sdk_factory=lambda **kw: self.sdk)
            self.assertEqual(code, 3)
            self.assertEqual(json.loads(output.getvalue())["errorCode"], "ACTION_NOT_SETTLED")
            self.assertEqual(self.sdk.unresolved["ticket"], 8)

    def test_dry_episode_flow_and_business_log(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "trace.jsonl"
            sdk = PolytopiaSDK(self.f, player_id=2, log_path=path)
            sdk.observe(details=True)
            discovery = sdk.list_actions()
            proposal = next(a for a in discovery["actions"] if a["args"].get("name") == "research")
            receipt = sdk.execute(proposal, wait=True)
            self.assertEqual(receipt["status"], "confirmed")
            sdk.events()
            self.f.handlers["result"] = {"ok": True, "status": "final", "controlledPlayerId": 2, "nativeFinalTotal": 1000}
            self.assertEqual(sdk.result()["status"], "final")
            rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
            self.assertIn("execute", [r["method"] for r in rows])
            self.assertIn("result", [r["method"] for r in rows])

    def test_handoff_invocation_uncertain_not_rejected(self):
        self.f.handlers["handoff_status"] = {"ok": True, "ready": False, "canStart": True,
            "configuredPlayerId": 2, "targetPlayerId": 2, "handoffId": "h1"}
        self.f.handlers["handoff_start"] = {"ok": False, "callbackSubmitted": True,
            "errorCode": "HANDOFF_INVOCATION_UNCERTAIN"}
        self.assertEqual(self.sdk.watch_handoff()["status"], "unknown")

    def test_frozen_real_native_menu_shape_and_archived_result(self):
        fixtures = Path(__file__).resolve().parent / "fixtures"
        self.f.handlers["unit_actions"] = json.loads((fixtures / "sdk-native-unit-menu.json").read_text(encoding="utf-8"))
        menu = self.sdk.list_actions("unit", unit=6)
        self.assertIn("Capture", [a["args"]["action_id"] for a in menu["actions"]])
        final = json.loads((fixtures / "sdk-native-final.json").read_text(encoding="utf-8"))
        self.f.handlers["result"] = {"ok": True, "status": "complete", "archived": True, "result": final}
        sdk = PolytopiaSDK(self.f, player_id=1)
        self.assertEqual(sdk.result(final["episodeId"])["native"]["result"]["scores"]["nativeFinalTotal"], 4746)

    def test_dismiss_resolved_reward_guard_and_native_pending_refusal(self):
        popup = {"ok": True, "showing": True, "popupType": "RewardPopup", "title": "Upgrade"}
        self.f.handlers["popup_info"] = popup
        result = self.sdk.dismiss_resolved_reward("Workshop", expected_popup=popup)
        self.assertEqual(result["status"], "submitted")
        args = next(args for cmd, args in self.f.calls if cmd == "popup")
        self.assertEqual(args["op"], "dismiss_resolved_reward")
        self.assertEqual(args["reward"], "Workshop")
        self.assertEqual(args["expectedPlayerId"], "2")
        self.f.handlers["popup"] = {"ok": False, "errorCode": "REWARD_NOT_PROVEN_RESOLVED", "outcome": "not_dispatched"}
        self.assertEqual(self.sdk.dismiss_resolved_reward("Workshop", expected_popup=popup)["status"], "rejected")
        self.assertFalse(any(cmd == "action" for cmd, _ in self.f.calls))

    def test_dismiss_wrong_popup_and_uncertain_hide(self):
        popup = {"ok": True, "showing": True, "popupType": "BasicPopup"}
        self.f.handlers["popup_info"] = popup
        with self.assertRaises(SDKError):
            self.sdk.dismiss_resolved_reward("Workshop", expected_popup=popup)
        popup["popupType"] = "RewardPopup"
        self.f.handlers["popup"] = {"ok": False, "outcome": "unknown"}
        self.assertEqual(self.sdk.dismiss_resolved_reward("Workshop", expected_popup=popup)["status"], "unknown")

    def test_wait_native_episode_seat_and_before_version_binding(self):
        self.sdk.execute(self.action())
        for key, wrong in (("episodeId", "ep-other"), ("controlledPlayerId", "1"), ("beforeStateVersion", 2)):
            self.f.handlers["settlement"][key] = wrong
            with self.assertRaises(SDKError):
                self.sdk.wait_result(8)
            del self.f.handlers["settlement"][key]
        self.f.handlers["settlement"]["episodeId"] = None
        self.assertEqual(self.sdk.wait_result(8)["status"], "unknown")

    def test_string_ticket_resolves_same_pending_action(self):
        self.sdk.execute(self.action())
        self.assertEqual(self.sdk.wait_result("8")["status"], "confirmed")
        self.assertIsNone(self.sdk.unresolved)

    def test_reward_display_dismiss_then_original_receipt_confirmation(self):
        original = self.sdk.execute(self.action())
        self.sdk = PolytopiaSDK(self.f, player_id=2)
        self.f.handlers["settlement"]["settlementStatus"] = "needs_resolution"
        self.sdk.wait_result(original["ticket"], binding=original["binding"])
        self.assertTrue(self.sdk._resolution_required)
        self.f.handlers["world"]["state"]["stateVersion"] = 2
        self.f.handlers["world"]["canAct"] = False
        self.f.handlers["city_options"]["pendingCityReward"] = {
            "status": "offered", "city": "(2,3)", "choices": [{"reward": "Workshop"}]}
        reward = next(a for a in self.sdk.list_actions("city")["actions"]
                      if a["args"].get("name") == "city_reward")
        self.f.handlers["action"]["settlementTicket"] = 9
        choice = self.sdk.execute(reward)
        self.assertEqual(self.sdk.unresolved["ticket"], original["ticket"])
        popup = {"ok": True, "showing": True, "popupType": "RewardPopup"}
        self.f.handlers["popup_info"] = popup
        self.f.handlers["city_options"]["pendingCityReward"] = {"status": "none", "choices": []}
        display = self.sdk.observe(details=True)
        self.assertEqual(self.sdk.dismiss_resolved_reward(
            "Workshop", expected_popup=display["popup"])["status"], "submitted")
        self.f.handlers["settlement"] = lambda args: {
            "ok": True, "streamId": "stream-one", "settlementStatus": "complete", "settlementComplete": True,
            "effectConfirmation": "confirmed", "actionCompleted": True,
            "beforeStateVersion": 2 if args["ticket"] == "9" else 1}
        self.assertEqual(self.sdk.wait_result(choice["ticket"], binding=choice["binding"])["status"], "confirmed")
        self.assertEqual(self.sdk.unresolved["ticket"], original["ticket"])
        self.assertEqual(self.sdk.wait_result(original["ticket"], binding=original["binding"])["status"], "confirmed")
        self.assertIsNone(self.sdk.unresolved)

    def test_reconnected_pending_receipt_blocks_new_write_until_confirmed(self):
        original = self.sdk.execute(self.action())
        resumed = PolytopiaSDK(self.f, player_id=2)
        self.f.handlers["settlement"]["settlementStatus"] = "pending"
        self.assertEqual(resumed.wait_result("8", binding=original["binding"], timeout=0)["status"], "timeout")
        with self.assertRaises(SDKError) as caught:
            resumed.execute(self.action())
        self.assertEqual(caught.exception.code, "UNRESOLVED_ACTION")
        self.f.handlers["settlement"]["settlementStatus"] = "complete"
        self.assertEqual(resumed.wait_result(8)["status"], "confirmed")
        self.assertIsNone(resumed.unresolved)

    def test_hotseat_owned_visible_decision_exposes_normal_observation(self):
        self.f.handlers["handoff_status"] = {
            "ok": True, "phase": "needs_decision", "needsDecision": True, "ready": False,
            "overlayActive": False, "configuredPlayerId": 2, "targetPlayerId": 2}
        self.f.handlers["world"]["canAct"] = False
        self.f.handlers["popup_info"] = {"ok": True, "showing": True, "popupType": "BasicPopup"}
        observation = self.sdk.observe(details=True)
        self.assertEqual(observation["status"], "needs_resolution")
        self.assertEqual(observation["world"]["localPlayerId"], 2)
        self.assertEqual(observation["popup"]["popupType"], "BasicPopup")
        self.assertIn("cities", observation)
        self.assertFalse(any(cmd in {"popup", "handoff_start", "action"} for cmd, _ in self.f.calls))

    def test_hotseat_decision_does_not_bypass_curtain_or_other_seat(self):
        status = {"ok": True, "phase": "needs_decision", "needsDecision": True, "ready": False,
                  "overlayActive": False, "configuredPlayerId": 2, "targetPlayerId": 2}
        for changed in ({"phase": "transition"}, {"phase": "recap"}, {"phase": "human_turn"},
                        {"overlayActive": True}, {"overlayActive": None}, {"needsDecision": None},
                        {"configuredPlayerId": 1}, {"targetPlayerId": 1}):
            with self.subTest(changed=changed):
                self.f.calls.clear()
                self.f.handlers["handoff_status"] = {**status, **changed}
                self.assertEqual(self.sdk.observe()["status"], "handoff")
                self.assertEqual([cmd for cmd, _ in self.f.calls], ["handoff_status"])

    def test_watch_hotseat_decision_returns_popup_and_rechecks_phase(self):
        decision = {"ok": True, "phase": "needs_decision", "needsDecision": True, "ready": False,
                    "overlayActive": False, "configuredPlayerId": 2, "targetPlayerId": 2}
        self.f.handlers["handoff_status"] = decision
        self.f.handlers["popup_info"] = {"ok": True, "showing": True, "popupType": "RewardPopup"}
        result = self.sdk.watch_handoff(timeout=0)
        self.assertEqual(result["status"], "needs_resolution")
        self.assertEqual(result["popup"]["popupType"], "RewardPopup")
        self.assertFalse(any(cmd == "handoff_start" for cmd, _ in self.f.calls))
        self.f.calls.clear()
        statuses = iter([decision, {"ok": True, "phase": "human_turn", "ready": False}])
        self.f.handlers["handoff_status"] = lambda _: next(statuses)
        self.assertEqual(self.sdk.watch_handoff(timeout=0)["status"], "handoff")
        self.assertEqual([cmd for cmd, _ in self.f.calls], ["handoff_status", "handoff_status"])

    def test_watch_ready_waits_for_world_can_act_bootstrap(self):
        def handoff(_):
            if len([cmd for cmd, _ in self.f.calls if cmd == "handoff_status"]) == 1:
                return {"ok": True, "phase": "handoff_overlay", "ready": False, "canStart": True,
                        "configuredPlayerId": 2, "targetPlayerId": 2, "handoffId": "h-bootstrap"}
            return {"ok": True, "phase": "map_ready", "ready": True}
        self.f.handlers["handoff_status"] = handoff
        busy = {**world(), "canAct": False, "canActReason": "command pipeline is busy"}
        states = iter([busy, busy, world(), world()])
        self.f.handlers["world"] = lambda _: next(states)
        self.sdk.sleep = lambda _: None
        observation = self.sdk.watch_handoff()
        self.assertEqual(observation["status"], "observed")
        self.assertIs(observation["world"]["canAct"], True)
        self.assertEqual(len([cmd for cmd, _ in self.f.calls if cmd == "world"]), 4)
        self.assertEqual(len([cmd for cmd, _ in self.f.calls if cmd == "handoff_start"]), 1)

    def test_watch_ready_but_world_busy_times_out(self):
        self.f.handlers["world"]["canAct"] = False
        self.assertEqual(self.sdk.watch_handoff(timeout=0)["status"], "timeout")
        self.assertEqual(len([cmd for cmd, _ in self.f.calls if cmd == "world"]), 2)
        self.assertFalse(any(cmd == "handoff_start" for cmd, _ in self.f.calls))

    def test_watch_ready_world_unknown_or_popup_not_misreported_ready(self):
        self.f.handlers["world"]["canAct"] = None
        self.assertEqual(self.sdk.watch_handoff(timeout=0)["status"], "unknown")
        self.f.handlers["world"]["canAct"] = False
        self.f.handlers["popup_info"] = {"ok": True, "showing": True, "popupType": "BasicPopup"}
        observation = self.sdk.watch_handoff(timeout=0)
        self.assertEqual(observation["status"], "needs_resolution")
        self.assertEqual(observation["popup"]["popupType"], "BasicPopup")

    def test_end_turn_public_handoff_confirmation_without_private_pipeline(self):
        end_turn = next(a for a in self.sdk.list_actions()["actions"]
                        if a["args"].get("name") == "end_turn")
        receipt = self.sdk.execute(end_turn)
        self.f.handlers["handoff_status"] = {"ok": True, "phase": "human_turn", "ready": False}
        self.f.handlers["world"] = RuntimeError("human private sampling forbidden")
        self.f.handlers["settlement"].update({
            "episodeId": receipt["binding"]["episodeId"],
            "controlledPlayerId": str(receipt["binding"]["playerId"]),
            "beforeStateVersion": receipt["binding"]["stateVersion"],
            "pipelineIdle": None, "completionAuthority": "public native hotseat handoff",
            "action": "end_turn", "settlementComplete": True})
        self.f.calls.clear()
        outcome = self.sdk.wait_result(receipt["ticket"], binding=receipt["binding"])
        self.assertEqual(outcome["status"], "confirmed")
        self.assertIsNone(outcome["native"]["pipelineIdle"])
        self.assertIsNone(self.sdk.unresolved)
        self.assertEqual([cmd for cmd, _ in self.f.calls], ["settlement"])


if __name__ == "__main__":
    unittest.main()
