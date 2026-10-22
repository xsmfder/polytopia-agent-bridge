"""Value boundaries: no lossy observation coercion or requests for bad selectors."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from observation import normalize_world, ObservationRejected, parse_coords
from sdk import PolytopiaSDK, SDKError
from sdk_values import integer
from test_observation import world_frame, unit
from test_sdk import Fake


class ObservationValueTests(unittest.TestCase):
    def test_unknown_owned_technologies_are_not_an_empty_set(self):
        for mode in ("missing", "null"):
            with self.subTest(mode=mode):
                frame = world_frame()
                frame.pop("ownedTechs", None)
                frame.pop("availableTechs", None)
                if mode == "null":
                    frame["ownedTechs"] = None
                result = normalize_world(frame)
                self.assertIsNone(result["ownedTechs"])
                self.assertEqual(result["ownedTechsStatus"], "unavailable")
        frame = world_frame(ownedTechs=[])
        result = normalize_world(frame)
        self.assertEqual(result["ownedTechs"], [])
        self.assertEqual(result["ownedTechsStatus"], "ok")

    def test_explicit_unknown_technology_list_does_not_reuse_legacy_alias(self):
        frame = world_frame(ownedTechs=None, availableTechs=["StaleTech"])
        result = normalize_world(frame)
        self.assertIsNone(result["ownedTechs"])
        self.assertEqual(result["ownedTechsStatus"], "unavailable")
        del frame["ownedTechs"]
        result = normalize_world(frame)
        self.assertEqual(result["ownedTechs"], ["StaleTech"])
        self.assertEqual(result["ownedTechsStatus"], "ok")

    def test_technology_list_completeness_controls_its_meaning(self):
        for status, expected, expected_status in (("partial", ["KnownTech"], "partial"),
                ("unavailable", None, "unavailable"), ("unexpected", None, "unavailable"),
                (["ok"], None, "unavailable"), (None, None, "unavailable")):
            with self.subTest(status=status):
                result = normalize_world(world_frame(ownedTechs=["KnownTech"], ownedTechsStatus=status))
                self.assertEqual(result["ownedTechs"], expected)
                self.assertEqual(result["ownedTechsStatus"], expected_status)

    def test_training_city_projection_matches_native_city_and_ownership_rule(self):
        for kind, owner in (("CityWall", 1), ("FutureCity", 1), ("City", 2)):
            with self.subTest(kind=kind, owner=owner):
                frame = world_frame(me=1)
                frame["improvements"][0].update(improvementType=kind, owner=owner, isMine=True)
                result = normalize_world(frame)
                self.assertEqual(result["trainOptions"]["cities"], [])
                self.assertEqual(result["trainOptions"]["status"], "partial")

    def test_raw_hp_and_integer_strings_preserved_without_display_scaling(self):
        actor = unit(owner=" +1 ", hp="075")
        actor.update(xp="02", maxHealth="100", promotionLevel="1")
        result = normalize_world(world_frame(me=" +1 ", units=[actor], stars=" +0 "))
        self.assertEqual(result["ownUnits"][0]["hp"], 75)
        self.assertEqual(result["ownUnits"][0]["xp"], 2)
        self.assertEqual(result["ownUnits"][0]["maxHealth"], 100)
        self.assertEqual(result["stars"], 0)

    def test_null_health_and_economy_stay_unknown(self):
        result = normalize_world(world_frame(units=[unit(hp=None)], stars=None))
        self.assertIsNone(result["units"][0]["hp"])
        self.assertIsNone(result["units"][0]["xp"])
        self.assertIsNone(result["stars"])

    def test_bool_float_and_non_decimal_unit_values_rejected(self):
        for field in ("health", "xp", "maxHealth", "promotionLevel", "owner"):
            for bad in (True, False, 1.0, 1.9, "1_0", "--1"):
                with self.subTest(field=field, bad=bad):
                    actor = unit(owner=1)
                    actor[field] = bad
                    with self.assertRaises(ObservationRejected):
                        normalize_world(world_frame(units=[actor]))

    def test_local_player_identity_and_filter_count_do_not_accept_bool(self):
        for bad in (True, False, 1.0, 1.9, "", "--1"):
            with self.subTest(identity=bad), self.assertRaises(ObservationRejected):
                normalize_world(world_frame(me=bad))
        frame = world_frame()
        frame["visibilityFilters"]["unit"]["attempted"] = True
        with self.assertRaises(ObservationRejected):
            normalize_world(frame)

    def test_owner_fields_do_not_accept_truncation_or_bool(self):
        for collection in ("resources", "tiles", "improvements", "destroyed"):
            for bad in (True, 2.9, "--1"):
                with self.subTest(collection=collection, bad=bad):
                    frame = world_frame()
                    item = {"owner": bad, "coords": "(1,1)", "c": "(1,1)",
                            "resourceType": "Fruit", "unitType": "Warrior"}
                    frame[collection] = [item]
                    with self.assertRaises(ObservationRejected):
                        normalize_world(frame)

    def test_optional_numbers_still_reject_invalid_values(self):
        for field in ("turn", "stars", "pendingTriggerCountMine"):
            for bad in (True, 1.5):
                with self.subTest(field=field, bad=bad), self.assertRaises(ObservationRejected):
                    normalize_world(world_frame(**{field: bad}))
        frame = world_frame()
        frame["improvements"][0]["level"] = 1.5
        with self.assertRaises(ObservationRejected):
            normalize_world(frame)

    def test_bad_economy_cost_withholds_domain_instead_of_crashing(self):
        for bad in ("--1", True, 1.5, "1_0"):
            with self.subTest(cost=bad):
                frame = world_frame()
                frame["trainOptions"]["cities"][0]["options"][0]["cost"] = bad
                frame["researchableTechs"]["items"][0]["cost"] = bad
                result = normalize_world(frame)
                self.assertEqual(result["trainOptions"]["status"], "partial")
                self.assertEqual(result["trainOptions"]["cities"][0]["options"], [])
                self.assertEqual(result["researchableTechs"]["status"], "partial")
                self.assertEqual(result["researchableTechs"]["items"], [])

    def test_coords_are_native_integer_pairs_not_python_numeric_literals(self):
        self.assertEqual(parse_coords(" ( +2, -3 ) "), [2, -3])
        for bad in ("(1_0,2)", "(2147483648,0)", "(1.0,2)", "2,3"):
            with self.subTest(coords=bad), self.assertRaises(ObservationRejected):
                parse_coords(bad)


class SDKSelectorValueTests(unittest.TestCase):
    def setUp(self):
        self.fake = Fake()
        self.sdk = PolytopiaSDK(self.fake, player_id=2)

    def test_every_selector_entry_rejects_invalid_units_before_requests(self):
        for bad in (True, False, -1, 4294967296, 1.0, 1.9, "1_0", "--1", [], {}):
            calls = (lambda: self.sdk.list_actions("unit", unit=bad),
                     lambda: self.sdk.attack_preview(unit=bad),
                     lambda: self.sdk.composite_preview(unit=bad, at="(2,3)"))
            for call in calls:
                with self.subTest(unit=bad), self.assertRaises(SDKError) as caught:
                    call()
                self.assertEqual(caught.exception.code, "INVALID_ARGUMENT")
        self.assertEqual(self.fake.calls, [])

    def test_every_selector_entry_rejects_invalid_coords_before_requests(self):
        for bad in (True, 1, [], {}, "(2,3", "2,3)", "1_0,2", "1.1,2", "2147483648,0"):
            calls = (lambda: self.sdk.list_actions("tile", at=bad),
                     lambda: self.sdk.attack_preview(at=bad),
                     lambda: self.sdk.composite_preview(unit=7, at=bad))
            for call in calls:
                with self.subTest(at=bad), self.assertRaises(SDKError) as caught:
                    call()
                self.assertEqual(caught.exception.code, "INVALID_ARGUMENT")
        self.assertEqual(self.fake.calls, [])

    def test_missing_tile_selector_or_bad_scope_fails_before_queries(self):
        for scope in ("tile", [], {}, None, "bad"):
            with self.subTest(scope=scope), self.assertRaises(SDKError) as caught:
                self.sdk.list_actions(scope)
            self.assertEqual(caught.exception.code, "INVALID_ARGUMENT")
        self.assertEqual(self.fake.calls, [])

    def test_list_selectors_are_canonical_native_strings(self):
        self.sdk.list_actions("unit", unit=" +007 ")
        self.assertIn(("unit_actions", {"unit": "7"}), self.fake.calls)
        self.fake.calls.clear()
        result = self.sdk.list_actions("tile", at=" +2, -03 ")
        self.assertIn(("tile_actions", {"at": "(2,-3)"}), self.fake.calls)
        self.assertEqual(result["actions"][0]["args"]["at"], "(2,-3)")

    def test_shared_decimal_parser_preserves_legal_values(self):
        for value, expected in [(0, 0), (" +007 ", 7), ("-003", -3)]:
            self.assertEqual(integer(value), expected)
        for bad in (True, False, None, 1.0, "1_0", "--1"):
            with self.subTest(value=bad), self.assertRaises(ValueError):
                integer(bad)


if __name__ == "__main__":
    unittest.main()
