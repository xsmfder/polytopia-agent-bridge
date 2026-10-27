"""Native training locations retain scope, validation and ordinary SDK execution."""
import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from observation import normalize_world
from sdk import PolytopiaSDK, SDKError
from test_sdk import Fake
import test_observation as legacy


def site(owner=1, at="(3,3)", kind="owned-water", valid=True):
    return {"coords": at, "owner": owner, "explored": True, "siteKind": kind,
            "status": "ok", "options": [{"unitType": "Island", "cost": 5,
                "trainable": True, "nativeValid": valid}]}


class WaterTrainingProjectionTests(unittest.TestCase):
    def frame(self):
        frame = legacy.world_frame()
        frame["tiles"][0]["explored"] = True
        frame["tiles"].append({"c": "(3,3)", "owner": 1, "explored": True,
                               "terrain": "Water", "water": True, "land": False})
        frame["trainOptions"]["sites"] = [site()]
        return frame

    def test_non_city_training_preserves_city_list(self):
        result = normalize_world(self.frame())["trainOptions"]
        self.assertEqual(result["status"], "ok")
        self.assertEqual(result["cities"][0]["coords"], [1, 1])
        self.assertEqual(result["sites"][0]["coords"], [3, 3])
        self.assertTrue(result["sites"][0]["options"][0]["nativeValid"])

    def test_known_typed_algae_scope_is_independent_of_water(self):
        frame = self.frame()
        frame["tiles"][-1].update(terrain="Field", water=False, terrainEffects=["Algae"],
                                  terrainEffectsScope="owned-explored-tile")
        frame["trainOptions"]["sites"][0]["siteKind"] = "owned-algae"
        self.assertEqual(normalize_world(frame)["trainOptions"]["sites"][0]["siteKind"], "owned-algae")

    def test_foreign_unexplored_and_unproven_water_are_filtered(self):
        for case in ("foreign-tile", "foreign-site", "missing-tile", "unexplored-certificate", "unknown-water", "invented-algae"):
            frame = self.frame()
            tile, row = frame["tiles"][-1], frame["trainOptions"]["sites"][0]
            if case == "foreign-tile": tile["owner"] = 2
            elif case == "foreign-site": row["owner"] = 2
            elif case == "missing-tile": frame["tiles"].pop()
            elif case == "unexplored-certificate": row["explored"] = False
            elif case == "unknown-water": tile["water"] = None
            else: row["siteKind"] = "owned-algae"
            with self.subTest(case=case):
                projected = normalize_world(frame)["trainOptions"]
                self.assertEqual(projected["sites"], [])
                self.assertEqual(projected["status"], "partial")

    def test_native_false_and_unknown_never_publish_executable_site(self):
        for valid in (False, None, "true"):
            frame = self.frame()
            frame["trainOptions"]["sites"][0]["options"][0]["nativeValid"] = valid
            with self.subTest(valid=valid):
                self.assertEqual(normalize_world(frame)["trainOptions"]["sites"][0]["options"], [])

    def test_duplicate_sites_and_options_keep_one_known_candidate(self):
        frame = self.frame()
        frame["trainOptions"]["sites"][0]["options"] *= 2
        frame["trainOptions"]["sites"] *= 2
        result = normalize_world(frame)["trainOptions"]
        self.assertEqual(len(result["sites"]), 1)
        self.assertEqual(len(result["sites"][0]["options"]), 1)

    def test_unavailable_domain_does_not_republish_sites(self):
        frame = self.frame()
        frame["trainOptions"]["status"] = "unavailable"
        self.assertEqual(normalize_world(frame)["trainOptions"]["sites"], [])


class WaterTrainingSDKTests(unittest.TestCase):
    def setUp(self):
        self.transport = Fake()
        self.sdk = PolytopiaSDK(self.transport, player_id=2)
        self.transport.handlers["world"]["trainOptions"]["sites"] = [site(owner=2)]

    def trains(self):
        return [a for a in self.sdk.list_actions()["actions"] if a["args"].get("name") == "train"]

    def test_non_city_action_uses_existing_train_execution(self):
        action = next(a for a in self.trains() if a["args"]["at"] == "(3,3)")
        self.assertTrue(action["eligible"])
        self.assertEqual(self.sdk.execute(action, wait=True)["status"], "confirmed")
        writes = [args for command, args in self.transport.calls if command == "action"]
        self.assertEqual(len(writes), 1)
        self.assertEqual({k: writes[0][k] for k in ("name", "at", "kind")},
                         {"name": "train", "at": "(3,3)", "kind": "Island"})

    def test_city_and_site_duplicate_is_one_descriptor(self):
        world = self.transport.handlers["world"]
        old = copy.deepcopy(world["trainOptions"]["cities"][0])
        old.update(owner=2, explored=True, siteKind="city")
        old["options"][0]["nativeValid"] = True
        world["trainOptions"]["sites"].extend([old, copy.deepcopy(old)])
        trains = self.trains()
        self.assertEqual(sum(a["args"]["at"] == "(2,3)" for a in trains), 1)
        self.assertEqual(sum(a["args"]["at"] == "(3,3)" for a in trains), 1)

    def test_foreign_unexplored_and_unknown_validation_not_offered(self):
        for key, value in (("owner", 3), ("explored", False), ("siteKind", "anything"), ("nativeValid", None)):
            row = self.transport.handlers["world"]["trainOptions"]["sites"][0]
            baseline = copy.deepcopy(row)
            if key == "nativeValid": row["options"][0][key] = value
            else: row[key] = value
            with self.subTest(key=key):
                self.assertFalse(any(a["args"]["at"] == "(3,3)" for a in self.trains()))
            self.transport.handlers["world"]["trainOptions"]["sites"][0] = baseline

    def test_live_revalidation_drops_now_invalid_water_command(self):
        action = next(a for a in self.trains() if a["args"]["at"] == "(3,3)")
        self.transport.handlers["world"]["trainOptions"]["sites"][0]["options"][0]["nativeValid"] = False
        with self.assertRaises(SDKError):
            self.sdk.execute(action)
        self.assertFalse(any(command == "action" for command, _ in self.transport.calls))


if __name__ == "__main__":
    unittest.main()
