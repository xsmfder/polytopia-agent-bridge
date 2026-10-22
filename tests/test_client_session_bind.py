"""Exercise the actual Bridge class without importing its Win32 desktop helpers."""
import ast
import json
import functools
import sys
import tempfile
import time
import unittest
from pathlib import Path


WORK = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORK))
CLIENT = WORK / "client.py"


def load_bridge(work):
    tree = ast.parse(CLIENT.read_text(encoding="utf-8-sig"), filename=str(CLIENT))
    nodes = [n for n in tree.body if isinstance(n, ast.ImportFrom)
             and n.module == "bridge_transport" and any(a.name == "Bridge" for a in n.names)]
    if len(nodes) != 1:
        raise AssertionError("client must export the shared pure transport")
    namespace = {}
    exec(compile(ast.Module(body=nodes, type_ignores=[]), str(CLIENT), "exec"), namespace)
    return functools.partial(namespace["Bridge"], work=work, liveness=lambda: True)



class BridgeSessionBindTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.work = Path(self.tmp.name)
        self.ready = self.work / "bridge-ready.json"
        self.write_ready(6972)
        self.Bridge = load_bridge(self.work)

    def write_ready(self, pid):
        self.ready.write_text(json.dumps({"pid": pid}), encoding="utf-8")

    def test_create_binds_current_ready_pid_and_serializes_it(self):
        bridge = self.Bridge()
        self.assertEqual(bridge._game_pid(), 6972)
        rid = bridge.send("unit_actions", unit="19")
        self.assertGreaterEqual(rid, 1)
        request = json.loads(bridge.cmd_path.read_text(encoding="utf-8"))
        self.assertEqual(request, {"id": rid, "cmd": "unit_actions", "bridgePid": 6972, "unit": "19"})
        self.assertEqual(bridge._next_id, rid + 1)

    def test_pid_switch_rejects_before_append_and_keeps_cached_pid_and_id(self):
        bridge = self.Bridge()
        bridge.send("ping")
        before = bridge.cmd_path.read_bytes()
        bridge._next_id = 999
        self.write_ready(33184)
        with self.assertRaisesRegex(RuntimeError, "bound PID 6972, ready PID 33184"):
            bridge.send("unit_action", unit="19", action_id="Recover")
        self.assertEqual(bridge._next_id, 999)
        self.assertEqual(bridge.cmd_path.read_bytes(), before)
        self.assertEqual(bridge._game_pid(), 6972)

    def test_new_client_automatically_binds_new_instance(self):
        old = self.Bridge()
        self.write_ready(33184)
        fresh = self.Bridge()
        self.assertEqual(fresh._game_pid(), 33184)
        fresh.send("ping")
        self.assertEqual(json.loads(fresh.cmd_path.read_text())['bridgePid'], 33184)
        with self.assertRaises(RuntimeError):
            old.send("ping")

    def test_pid_switch_blocks_matching_old_reqid_response(self):
        bridge = self.Bridge()
        rid = bridge.send("ping")
        self.write_ready(33184)
        bridge.out_path.write_text(json.dumps({"reqId": rid, "ok": True}) + "\n", encoding="utf-8")
        with self.assertRaises(RuntimeError):
            bridge.wait_for(rid, timeout=0.1)
        self.assertEqual(bridge._offset, 0)

    def test_missing_or_malformed_ready_does_not_append_or_advance(self):
        bridge = self.Bridge()
        initial_id = bridge._next_id
        for bad in ["{", '{"pid":null}', '{"pid":true}', '{"pid":-2}', '{"pid":6972.5}']:
            self.ready.write_text(bad, encoding="utf-8")
            with self.assertRaises(RuntimeError):
                bridge.send("ping")
            self.assertEqual(bridge._next_id, initial_id)
            self.assertEqual(bridge.cmd_path.read_bytes(), b"")
        self.ready.unlink()
        with self.assertRaises(RuntimeError):
            bridge.send("ping")
        self.assertEqual(bridge._next_id, initial_id)

    def test_unbound_client_never_silently_adopts_later_ready(self):
        self.ready.unlink()
        bridge = self.Bridge()
        initial_id = bridge._next_id
        self.write_ready(33184)
        with self.assertRaises(RuntimeError):
            bridge.send("ping")
        self.assertIsNone(bridge._game_pid())
        self.assertEqual(bridge._next_id, initial_id)

    def test_reserved_fields_still_reject_without_append_or_advance(self):
        bridge = self.Bridge()
        initial_id = bridge._next_id
        for kw in [{"id": "unit-id"}, {"cmd": "spoof"}, {"bridgePid": 33184}]:
            with self.assertRaises((ValueError, TypeError)):
                bridge.send("ping", **kw)
            self.assertEqual(bridge._next_id, initial_id)
            self.assertEqual(bridge.cmd_path.read_bytes(), b"")

    def test_unit_popupid_reward_are_still_regular_request_fields(self):
        bridge = self.Bridge()
        bridge.send("popup", unit="3", popupId="8", reward="Workshop")
        request = json.loads(bridge.cmd_path.read_text())
        self.assertEqual(request["unit"], "3")
        self.assertEqual(request["popupId"], "8")
        self.assertEqual(request["reward"], "Workshop")

    def test_bad_frame_accounting_and_normal_reply_read_still_work(self):
        bridge = self.Bridge()
        bridge.out_path.write_text('{broken\n{"reqId":1,"ok":true}\n', encoding="utf-8")
        self.assertEqual(bridge._read_new(), [{"reqId": 1, "ok": True}])
        self.assertEqual(bridge.bad_frames, 1)
        self.assertIn("1 malformed frame", bridge.bad_frame_report())


if __name__ == "__main__":
    unittest.main(verbosity=2)
