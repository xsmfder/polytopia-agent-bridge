"""Keep already disclosed decision clues when projecting a raw legal world."""
import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from observation import normalize_world, ObservationRejected
from test_observation import world_frame, unit


class PublicChannelProjectionTests(unittest.TestCase):
    def test_explored_tile_style_is_preserved_independently_of_owner(self):
        frame = world_frame()
        frame["tiles"][0].update(owner=0, explored=True, land=True, currentlyVisible=None,
            lastObservedTurn=0, lastObservedRevision=7,
            visualStyle={"tribe": "Bardur", "skin": "Default", "status": "ok", "hiddenCapital": "(9,9)"})
        tile = normalize_world(frame)["tiles"][0]
        self.assertEqual(tile["visualStyle"], {"tribe": "Bardur", "skin": "Default", "status": "ok"})
        self.assertEqual(tile["owner"], 0)
        self.assertIs(tile["land"], True)
        self.assertIsNone(tile["currentlyVisible"])
        self.assertEqual((tile["lastObservedTurn"], tile["lastObservedRevision"]), (0, 7))

    def test_only_own_native_invisible_threat_boolean_survives(self):
        hint = {"status": "ok", "present": True, "targetCoords": "(9,9)", "owner": 3, "unitId": 99}
        own, enemy = unit(owner=1), unit(owner=2, uid="43")
        own["properties"] = enemy["properties"] = {"adjacentInvisibleThreat": hint}
        view = normalize_world(world_frame(me=1, units=[own, enemy]))
        self.assertEqual(view["ownUnits"][0]["adjacentInvisibleThreat"], {"present": True, "status": "ok"})
        self.assertNotIn("adjacentInvisibleThreat", view["units"][0])

    def test_unknown_and_known_false_hints_are_distinct(self):
        for value, status, expected in ((False, "ok", False), (1, "ok", None), (True, "unavailable", None)):
            with self.subTest(value=value, status=status):
                actor = unit(owner=1)
                actor["properties"] = {"adjacentInvisibleThreat": {"present": value, "status": status}}
                hint = normalize_world(world_frame(me=1, units=[actor]))["ownUnits"][0]["adjacentInvisibleThreat"]
                self.assertIs(hint["present"], expected)
                self.assertEqual(hint["status"], "ok" if type(expected) is bool else "unavailable")

    def test_explicit_unexplored_tile_cannot_publish_terrain_or_style(self):
        frame = world_frame()
        frame["tiles"][0].update(explored=False, visualStyle={"tribe": "Bardur", "skin": "Default", "status": "ok"})
        with self.assertRaises(ObservationRejected):
            normalize_world(frame)

    def test_terminal_phase_and_existing_max_health_are_no_longer_called_unimplemented(self):
        actor = unit(owner=1)
        actor["maxHealth"] = "100"
        view = normalize_world(world_frame(me=1, units=[actor], currentState="Ended", matchEnded=True))
        self.assertEqual((view["currentState"], view["matchEnded"]), ("Ended", True))
        self.assertEqual(view["ownUnits"][0]["maxHealth"], 100)
        self.assertNotIn("unitMaxHealth", view["unimplemented"])
        self.assertNotIn("terminalState", view["unimplemented"])
        self.assertNotIn("winnerId", view)


if __name__ == "__main__":
    unittest.main()
