"""Actual SDK discovery/dispatch with counted native-shaped transport responses."""
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
from agentcli import main
from test_sdk import Fake, world

WRITES = {"action", "unit_action", "tile_action", "tribe_action"}


class MenuTransport(Fake):
    def __init__(self, tiles=20):
        super().__init__()
        self.handlers["world"]["legalAttackTargets"] = {"units": [{"from": "(2,3)", "targets": ["(2,5)"]}]}
        self.handlers["city_options"]["ownedEconomicTiles"] = [{"coords": f"({i},3)"} for i in range(tiles)]
        self.handlers["unit_actions"]["units"].append({"unitId": "9", "actions": [
            {"actionId": "FutureOtherSkill", "executable": True, "nativeValid": True}]})
        self.handlers["tribe_relations"] = {"ok": True, "tribes": [{"playerId": 3, "operations": [
            {"actionId": "peace_request", "adapterSupported": True, "canExecute": True},
            {"actionId": "peace_response", "accept": False, "adapterSupported": True, "canExecute": True},
            {"actionId": "peace_response", "accept": True, "adapterSupported": True, "canExecute": True}]}]}
        for command in ("tile_action", "tribe_action"):
            self.handlers[command] = {"ok": True, "accepted": True, "settlementTicket": 8}

    def request(self, command, timeout=None, **args):
        result = super().request(command, timeout=timeout, **args)
        if command == "unit_actions" and args.get("unit") is not None:
            result["units"] = [u for u in result["units"] if str(u["unitId"]) == args["unit"]]
        return result


def proposal(sdk, name=None, command="action", scope="all", **kwargs):
    return next(a for a in sdk.list_actions(scope, **kwargs)["actions"]
                if a["command"] == command and (name is None or a["args"].get("name") == name))


class TargetedExecutionTests(unittest.TestCase):
    def setUp(self):
        self.transport = MenuTransport()
        self.sdk = PolytopiaSDK(self.transport, player_id=2)

    def run_action(self, name=None, command="action", scope="all", **kwargs):
        action = proposal(self.sdk, name, command, scope, **kwargs)
        self.transport.calls.clear()
        result = self.sdk.execute(action)
        self.assertEqual(result["status"], "accepted")
        return action, result

    def commands(self):
        return [command for command, _ in self.transport.calls]

    def test_move_from_all_only_rechecks_world(self):
        self.run_action("move")
        self.assertEqual(self.commands(), ["world", "world", "action"])

    def test_attack_from_all_only_rechecks_world(self):
        self.run_action("attack")
        self.assertEqual(self.commands(), ["world", "world", "action"])

    def test_research_train_and_end_turn_skip_other_domains(self):
        for name in ("research", "train", "end_turn"):
            with self.subTest(name=name):
                self.setUp()
                self.run_action(name)
                self.assertEqual(self.commands(), ["world", "world", "action"])

    def test_build_only_rechecks_city_domain(self):
        self.run_action("build")
        self.assertEqual(self.commands(), ["world", "city_options", "world", "action"])

    def test_unit_from_all_rechecks_selected_native_unit(self):
        action, _ = self.run_action(command="unit_action")
        self.assertEqual(self.commands(), ["world", "unit_actions", "world", "unit_action"])
        self.assertEqual(self.transport.calls[1][1], {"unit": action["args"]["unit"]})
        self.assertEqual(action["scope"], "all")

    def test_tile_from_all_rechecks_one_native_tile(self):
        action, _ = self.run_action(command="tile_action")
        self.assertEqual(self.commands(), ["world", "tile_actions", "world", "tile_action"])
        self.assertEqual(self.transport.calls[1][1], {"at": action["args"]["at"]})

    def test_tribe_from_all_skips_unit_city_and_tile_domains(self):
        self.run_action(command="tribe_action")
        self.assertEqual(self.commands(), ["world", "tribe_relations", "world", "tribe_action"])

    def test_existing_narrow_scope_descriptors_remain_usable(self):
        for scope, command, kwargs in [("core", "action", {}), ("city", "action", {}),
                                       ("unit", "unit_action", {"unit": 7}),
                                       ("tile", "tile_action", {"at": "(2,3)"}),
                                       ("tribe", "tribe_action", {})]:
            with self.subTest(scope=scope):
                self.setUp()
                action, _ = self.run_action(command=command, scope=scope, **kwargs)
                self.assertEqual(action["scope"], scope)
                self.assertEqual(sum(c in WRITES for c in self.commands()), 1)

    def test_unrelated_menu_failure_does_not_block_move(self):
        action = proposal(self.sdk, "move")
        for command in ("unit_actions", "city_options", "tribe_relations", "tile_actions"):
            self.transport.handlers[command] = TimeoutError("unrelated menu unavailable")
        self.transport.calls.clear()
        self.assertEqual(self.sdk.execute(action)["status"], "accepted")
        self.assertEqual(self.commands(), ["world", "world", "action"])

    def test_stale_binding_stops_before_reading_private_menus(self):
        action = proposal(self.sdk, command="unit_action")
        self.transport.handlers["world"]["state"]["stateVersion"] = 2
        self.transport.calls.clear()
        with self.assertRaises(SDKError) as failure:
            self.sdk.execute(action)
        self.assertEqual(failure.exception.code, "STALE_ACTION")
        self.assertEqual(self.commands(), ["world"])

    def test_second_world_change_prevents_dispatch(self):
        action = proposal(self.sdk, command="tile_action")
        self.transport.calls.clear()
        self.transport.handlers["world"] = lambda _: world(sum(c == "world" for c in self.commands()))
        with self.assertRaises(SDKError) as failure:
            self.sdk.execute(action)
        self.assertEqual(failure.exception.code, "STATE_CHANGED")
        self.assertFalse(any(c in WRITES for c in self.commands()))

    def test_disappeared_or_unknown_native_skill_never_dispatches(self):
        action = proposal(self.sdk, command="unit_action")
        for actions in ([], [{"actionId": action["args"]["action_id"], "executable": None, "nativeValid": True}]):
            with self.subTest(actions=actions):
                self.transport.handlers["unit_actions"]["units"][0]["actions"] = actions
                self.transport.calls.clear()
                with self.assertRaises(SDKError) as failure:
                    self.sdk.execute(action)
                self.assertEqual(failure.exception.code, "ACTION_NOT_AVAILABLE")
                self.assertFalse(any(c in WRITES for c in self.commands()))

    def test_selected_menu_failure_never_sends_write(self):
        action = proposal(self.sdk, command="tile_action")
        self.transport.handlers["tile_actions"] = TimeoutError("selected menu failed")
        self.transport.calls.clear()
        with self.assertRaises(TimeoutError):
            self.sdk.execute(action)
        self.assertEqual(self.commands(), ["world", "tile_actions"])
        self.assertIsNone(self.sdk.unresolved)

    def test_tampered_descriptor_cannot_add_arguments_or_change_action(self):
        original = proposal(self.sdk, "move")
        for changes in ({"to": "(9,9)"}, {"debug": "private"}, {"audit": "1"},
                        {"name": "research", "kind": "FutureTech"}):
            with self.subTest(changes=changes):
                action = copy.deepcopy(original)
                action["args"].update(changes)
                self.transport.calls.clear()
                with self.assertRaises(SDKError):
                    self.sdk.execute(action)
                self.assertFalse(any(c in WRITES for c in self.commands()))

    def test_native_context_guards_are_preserved(self):
        action, _ = self.run_action("move")
        args = self.transport.calls[-1][1]
        self.assertEqual({k: v for k, v in args.items() if k.startswith("expected")}, {
            "expectedEpisodeId": action["binding"]["episodeId"], "expectedPlayerId": "2",
            "expectedStreamId": action["binding"]["streamId"], "expectedStateVersion": "1"})

    def test_native_final_rejection_is_not_accepted_or_retried(self):
        action = proposal(self.sdk, "move")
        self.transport.handlers["action"] = {"ok": False, "errorCode": "STALE_ACTION_CONTEXT", "accepted": False}
        self.transport.calls.clear()
        self.assertEqual(self.sdk.execute(action)["status"], "rejected")
        self.assertEqual(self.commands().count("action"), 1)
        self.assertIsNone(self.sdk.unresolved)

    def test_uncertain_dispatch_still_locks_and_preserves_receipt(self):
        action = proposal(self.sdk, "move")
        self.transport.handlers["action"] = TimeoutError("write may have executed")
        self.transport.calls.clear()
        self.assertEqual(self.sdk.execute(action)["status"], "unknown")
        original = copy.deepcopy(self.sdk.unresolved)
        with self.assertRaises(SDKError) as failure:
            self.sdk.execute(action)
        self.assertEqual(failure.exception.code, "UNRESOLVED_ACTION")
        self.assertEqual(self.sdk.unresolved, original)
        self.assertEqual(self.commands().count("action"), 1)

    def test_dynamic_future_unit_skill_uses_its_native_id(self):
        action = proposal(self.sdk, command="unit_action")
        self.transport.handlers["unit_actions"]["units"][0]["actions"][0]["actionId"] = "NeverSeenSkill:mode=4"
        action = proposal(self.sdk, command="unit_action")
        self.transport.calls.clear()
        self.assertEqual(self.sdk.execute(action)["status"], "accepted")
        self.assertEqual(self.transport.calls[-1][1]["action_id"], "NeverSeenSkill:mode=4")

    def test_changed_native_prices_invalidate_old_research_and_training(self):
        for name in ('research', 'train'):
            with self.subTest(name=name):
                transport = MenuTransport()
                sdk = PolytopiaSDK(transport, player_id=2)
                action = proposal(sdk, name)
                world = transport.handlers['world']
                row = (world['researchableTechs']['items'][0] if name == 'research'
                       else world['trainOptions']['cities'][0]['options'][0])
                row['cost'] = int(world['stars']) + 1
                transport.calls.clear()
                with self.assertRaises(SDKError) as caught:
                    sdk.execute(action)
                self.assertEqual(caught.exception.code, 'ACTION_NOT_AVAILABLE')
                self.assertFalse(any(c in WRITES for c, _ in transport.calls))
                self.assertIsNone(sdk.unresolved)

    def test_changed_native_permission_invalidates_old_candidates_in_each_menu(self):
        for command in ('action', 'unit_action', 'tile_action', 'tribe_action'):
            with self.subTest(command=command):
                transport = MenuTransport()
                sdk = PolytopiaSDK(transport, player_id=2)
                action = proposal(sdk, 'build' if command == 'action' else None, command=command)
                if command == 'action':
                    transport.handlers['city_options']['tileOptions'][0]['buildableNow'] = False
                elif command == 'unit_action':
                    transport.handlers['unit_actions']['units'][0]['actions'][0]['nativeValid'] = False
                elif command == 'tile_action':
                    transport.handlers['tile_actions']['actions'][0]['executable'] = False
                else:
                    transport.handlers['tribe_relations']['tribes'][0]['operations'][0]['canExecute'] = False
                transport.calls.clear()
                with self.assertRaises(SDKError) as caught:
                    sdk.execute(action)
                self.assertEqual(caught.exception.code, 'ACTION_NOT_AVAILABLE')
                self.assertFalse(any(c in WRITES for c, _ in transport.calls))
                self.assertEqual(sdk._pending_writes, [])

    def test_changed_native_legal_targets_remove_old_move_and_attack(self):
        for name, key in (('move', 'legalMoveTargets'), ('attack', 'legalAttackTargets')):
            with self.subTest(name=name):
                transport = MenuTransport()
                sdk = PolytopiaSDK(transport, player_id=2)
                action = proposal(sdk, name)
                transport.handlers['world'][key]['units'][0]['targets'] = []
                transport.calls.clear()
                with self.assertRaises(SDKError) as caught:
                    sdk.execute(action)
                self.assertEqual(caught.exception.code, 'ACTION_NOT_AVAILABLE')
                self.assertFalse(any(c in WRITES for c, _ in transport.calls))

    def test_city_reward_can_resolve_parent_without_replacing_original_ticket(self):
        self.transport.handlers["world"]["canAct"] = False
        self.transport.handlers["city_options"]["pendingCityReward"] = {"city": "(2,3)", "choices": [{"reward": "FutureReward"}]}
        action = proposal(self.sdk, "city_reward")
        parent = {"ticket": 6, "binding": action["binding"]}
        self.sdk.unresolved = copy.deepcopy(parent)
        self.sdk._resolution_required = True
        self.transport.calls.clear()
        self.assertEqual(self.sdk.execute(action)["status"], "accepted")
        self.assertEqual(self.sdk.unresolved, parent)
        self.assertEqual(self.commands(), ["world", "city_options", "world", "action"])

    def test_wrong_scope_adapter_is_rejected_without_native_reads(self):
        action = proposal(self.sdk, command="tile_action")
        action["scope"] = "unit"
        self.transport.calls.clear()
        with self.assertRaises(SDKError) as failure:
            self.sdk.execute(action)
        self.assertEqual(failure.exception.code, "ACTION_NOT_AVAILABLE")
        self.assertEqual(self.commands(), [])

    def test_cli_uses_same_targeted_path_for_existing_discovery_file(self):
        discovery = self.sdk.list_actions("all")
        action = next(a for a in discovery["actions"] if a["args"].get("name") == "move")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "actions.json"
            path.write_text(json.dumps(discovery), encoding="utf-8")
            self.transport.calls.clear()
            with contextlib.redirect_stdout(io.StringIO()) as output:
                code = main(["--player", "2", "execute", str(path), action["actionId"]], sdk_factory=lambda **_: self.sdk)
            self.assertEqual(code, 6)
            self.assertEqual(json.loads(output.getvalue())["status"], "accepted")
            self.assertEqual(self.commands(), ["world", "world", "action"])

    def test_sdk_log_keeps_only_relevant_fresh_source_evidence(self):
        action = proposal(self.sdk, command="unit_action")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "sdk.jsonl"
            self.sdk.log_path = path
            self.transport.calls.clear()
            self.sdk.execute(action)
            rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
        refresh = next(r for r in rows if r["method"] == "_revalidate_action")["response"]
        self.assertEqual(list(refresh["sources"]), ["unit"])
        self.assertEqual(len(refresh["actions"]), 1)
        self.assertEqual(refresh["actions"][0]["actionId"], action["actionId"])
        self.assertEqual(self.commands(), ["world", "unit_actions", "world", "unit_action"])


if __name__ == "__main__":
    unittest.main()
