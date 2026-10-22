"""Tests for the standalone file-queue transport: temp dirs and a fake game only.

Run:  python tests\test_bridge_transport.py -v        (from .offline-work/agent-bridge)
No game, no window, no desktop API and no real queue files are touched.
"""
import ast
import ctypes
import importlib.util
import json
import os
import pathlib
import random
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORK = HERE.parent
MODULE_PATH = WORK / "bridge_transport.py"
sys.path.insert(0, str(WORK))

import bridge_transport                                    # noqa: E402
from bridge_transport import Bridge                        # noqa: E402


def fresh_module():
    """Load bridge_transport.py again without registering it in sys.modules."""
    spec = importlib.util.spec_from_file_location("bridge_transport_import_probe", MODULE_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class FakeKernel32:
    """Stands in for kernel32 so liveness classification needs no real process."""

    def __init__(self, handle=1, err=0, exit_ok=True, code=bridge_transport.STILL_ACTIVE):
        self.handle = handle
        self.err = err
        self.exit_ok = exit_ok
        self.code = code
        self.closed = False

    def OpenProcess(self, access, inherit, pid):
        if not self.handle:
            ctypes.set_last_error(self.err)
        return self.handle

    def GetExitCodeProcess(self, handle, ref):
        if self.exit_ok:
            ref._obj.value = self.code
        return self.exit_ok

    def CloseHandle(self, handle):
        self.closed = True
        return True


class BrokenKernel32:
    def OpenProcess(self, access, inherit, pid):
        raise OSError("kernel32 is unavailable")


class ReplyServer(threading.Thread):
    """Tiny fake game: echo every complete cmd.jsonl line back into out.jsonl."""

    def __init__(self, cmd_path, out_path, poll=0.005):
        super().__init__(daemon=True)
        self.cmd_path = Path(cmd_path)
        self.out_path = Path(out_path)
        self.poll = poll
        self.before_first_reply = 0     # requests already queued when the first reply went out
        self._replied = 0
        self._consumed = 0
        self._stop_event = threading.Event()

    def stop(self):
        self._stop_event.set()
        self.join(timeout=2.0)

    def run(self):
        while not self._stop_event.is_set():
            try:
                data = self.cmd_path.read_bytes().decode("utf-8", errors="replace")
            except OSError:
                data = ""
            cut = data.rfind("\n")
            complete = data[:cut + 1] if cut >= 0 else ""
            lines = [line for line in complete.split("\n") if line.strip()]
            if len(lines) > self._consumed:
                batch = lines[self._consumed:]
                self._consumed = len(lines)
                if self._replied == 0:
                    self.before_first_reply = len(batch)
                for line in batch:
                    self._reply(json.loads(line))
            time.sleep(self.poll)

    def _reply(self, request):
        with self.out_path.open("a", encoding="utf-8") as fh:
            fh.write(json.dumps({"reqId": request["id"], "echo": request.get("echo")}) + "\n")
        self._replied += 1


class BridgeTestCase(unittest.TestCase):
    PID = 6972
    OTHER_PID = 33184

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.work = Path(self.tmp.name)
        self.ready = self.work / "bridge-ready.json"
        self.cmd = self.work / "cmd.jsonl"
        self.out = self.work / "out.jsonl"
        self.write_ready(self.PID)

    def write_ready(self, pid, started=None):
        payload = {"pid": pid}
        if started is not None:
            payload["started"] = started
        self.ready.write_text(json.dumps(payload), encoding="utf-8")

    def make_bridge(self, **kw):
        kw.setdefault("liveness", lambda: True)     # never depends on a real process
        bridge = Bridge(work=self.work, **kw)
        self.addCleanup(bridge.close)
        return bridge

    def append_out(self, *frames):
        with self.out.open("a", encoding="utf-8") as fh:
            for frame in frames:
                fh.write(json.dumps(frame, ensure_ascii=False) + "\n")

    def cmd_lines(self):
        text = self.cmd.read_text(encoding="utf-8") if self.cmd.exists() else ""
        return [json.loads(line) for line in text.splitlines() if line.strip()]


# --------------------------------------------------------------- binding
class SessionBindingTests(BridgeTestCase):
    def test_binds_ready_pid_and_stamps_bridge_pid(self):
        bridge = self.make_bridge()
        self.assertEqual(bridge._game_pid(), self.PID)
        rid = bridge.send("unit_actions", unit="19")
        line = self.cmd_lines()[0]
        self.assertEqual(line, {"id": rid, "cmd": "unit_actions", "bridgePid": self.PID, "unit": "19"})
        self.assertEqual(bridge._next_id, rid + 1)

    def test_request_ids_are_random_json_safe_and_increasing(self):
        expected = random.Random(20261010).randint(bridge_transport.ID_MIN, bridge_transport.ID_MAX)
        bridge = self.make_bridge(rng=random.Random(20261010))   # same seed, independent draw
        self.assertEqual(bridge._next_id, expected)
        self.assertGreater(expected, 1)                       # not the old shared start at 1
        self.assertLessEqual(expected, 2 ** 53 - 1)           # exactly representable in JSON
        ids = [bridge.send("ping") for _ in range(3)]
        self.assertEqual(ids, [expected, expected + 1, expected + 2])
        for rid in ids:
            self.assertEqual(json.loads(json.dumps(rid)), rid)

    def test_new_clients_do_not_share_a_starting_id(self):
        starts = [Bridge(work=self.work, liveness=lambda: True)._next_id for _ in range(8)]
        self.assertGreater(len(set(starts)), 1)
        for start in starts:
            self.assertGreaterEqual(start, bridge_transport.ID_MIN)
            self.assertLessEqual(start, bridge_transport.ID_MAX)

    def test_reserved_envelope_fields_are_rejected_without_append(self):
        bridge = self.make_bridge()
        start = bridge._next_id
        for kw in ({"id": "unit-id"}, {"cmd": "spoof"}, {"bridgePid": self.OTHER_PID}):
            with self.assertRaises((ValueError, TypeError)):
                bridge.send("ping", **kw)
            self.assertEqual(bridge._next_id, start)
        self.assertEqual(self.cmd.read_bytes(), b"")
        bridge.send("popup", unit="3", popupId="8", reward="Workshop")
        line = self.cmd_lines()[0]
        self.assertEqual((line["unit"], line["popupId"], line["reward"]), ("3", "8", "Workshop"))

    def test_ready_pid_change_rejects_before_append_and_keeps_binding(self):
        bridge = self.make_bridge()
        bridge.send("ping")
        before = self.cmd.read_bytes()
        bridge._next_id = 999
        self.write_ready(self.OTHER_PID)
        with self.assertRaisesRegex(RuntimeError, "bound PID 6972, ready PID 33184"):
            bridge.send("unit_action", unit="19", action_id="Recover")
        self.assertEqual(bridge._next_id, 999)
        self.assertEqual(self.cmd.read_bytes(), before)
        self.assertEqual(bridge._game_pid(), self.PID)

    def test_unbound_client_never_adopts_later_ready(self):
        self.ready.unlink()
        bridge = self.make_bridge()
        self.write_ready(self.OTHER_PID)
        with self.assertRaises(RuntimeError):
            bridge.send("ping")
        self.assertIsNone(bridge._game_pid())
        self.assertEqual(self.cmd.read_bytes(), b"")

    def test_malformed_ready_file_is_not_a_binding(self):
        for bad in ("{", '{"pid":null}', '{"pid":true}', '{"pid":-2}', '{"pid":6972.5}', "{}"):
            self.ready.write_text(bad, encoding="utf-8")
            bridge = Bridge(work=self.work, liveness=lambda: True)
            self.assertIsNone(bridge._game_pid())
            with self.assertRaises(RuntimeError):
                bridge.send("ping")

    def test_new_ready_session_cannot_reuse_an_old_ticket(self):
        bridge = self.make_bridge()
        rid = bridge.send("ping")
        self.write_ready(self.OTHER_PID)
        self.append_out({"reqId": rid, "ok": True})
        with self.assertRaises(RuntimeError):
            bridge.wait_for(rid, timeout=0.3)
        self.assertEqual(bridge._offset, 0)          # nothing consumed from the new session
        self.assertEqual(bridge.bad_frames, 0)

    def test_same_pid_restarted_session_rejects_old_ticket(self):
        self.write_ready(self.PID, started="2026-10-10T02:06:12")
        bridge = self.make_bridge()
        self.assertEqual(bridge._bound_started, "2026-10-10T02:06:12")
        rid = bridge.send("ping")
        self.write_ready(self.PID, started="2026-10-10T03:00:00")
        self.append_out({"reqId": rid, "ok": True})
        with self.assertRaisesRegex(RuntimeError, "restarted with the same PID 6972"):
            bridge.wait_for(rid, timeout=0.3)
        with self.assertRaisesRegex(RuntimeError, "restarted with the same PID 6972"):
            bridge.send("ping")
        self.assertEqual(bridge._offset, 0)
        self.assertEqual(len(self.cmd_lines()), 1)

    def test_ready_without_started_still_binds(self):
        bridge = self.make_bridge()                  # fixture ready file has pid only
        self.assertEqual(bridge._game_pid(), self.PID)
        self.assertIsNone(bridge._bound_started)
        bridge.send("ping")
        # A fixture that later gains `started` for the same PID must not break the binding.
        self.write_ready(self.PID, started="2026-10-10T02:06:12")
        bridge.send("ping")
        self.assertEqual(len(self.cmd_lines()), 2)

    def test_known_start_marker_cannot_be_lost_before_send_or_read(self):
        for started in (None, "", True, 123):
            with self.subTest(started=started):
                self.write_ready(self.PID, started="original-start")
                bridge = self.make_bridge()
                before = len(self.cmd_lines())
                rid = bridge.send("ping")
                # Include a cached reply: session checks must precede cache lookup too.
                bridge._park({"reqId": rid, "ok": True}, rid)
                self.write_ready(self.PID, started=started)
                with self.assertRaises(RuntimeError):
                    bridge.send("ping")
                with self.assertRaises(RuntimeError):
                    bridge.wait_for(rid, timeout=0.1)
                with self.assertRaises(RuntimeError):
                    bridge.drain(seconds=0)
                self.assertEqual(len(self.cmd_lines()), before + 1)
                self.assertEqual(bridge._deferred_count, 1)
                self.assertEqual(bridge._offset, 0)


# --------------------------------------------------------------- reading
class FrameReadingTests(BridgeTestCase):
    def test_half_line_is_held_until_the_newline_arrives(self):
        bridge = self.make_bridge()
        rid = bridge.send("ping")
        line = json.dumps({"reqId": rid, "note": "文化"}, ensure_ascii=False) + "\n"
        raw = line.encode("utf-8")
        self.out.write_bytes(raw[:len(raw) // 2])
        self.assertEqual(bridge._read_new(), [])
        self.assertEqual(bridge.bad_frames, 0)
        self.out.write_bytes(raw)
        self.assertEqual(bridge._read_new(), [{"reqId": rid, "note": "文化"}])
        self.assertEqual(bridge.bad_frames, 0)
        self.assertEqual(bridge._read_new(), [])
        self.assertEqual(bridge._offset, len(raw))

    def test_utf8_split_inside_a_multibyte_character_is_not_corrupted(self):
        bridge = self.make_bridge()
        rid = bridge.send("ping")
        text = json.dumps({"reqId": rid, "msg": "文化🎉abc"}, ensure_ascii=False)
        raw = (text + "\n").encode("utf-8")
        cut = raw.index("🎉".encode("utf-8")) + 2          # inside the 4-byte code point
        self.out.write_bytes(raw[:cut])
        self.assertEqual(bridge._read_new(), [])
        self.out.write_bytes(raw)
        self.assertEqual(bridge._read_new(), [{"reqId": rid, "msg": "文化🎉abc"}])
        self.assertEqual(bridge.bad_frames, 0)
        second = bridge.send("pong")
        self.append_out({"reqId": second, "msg": "第二"})
        self.assertEqual(bridge._read_new(), [{"reqId": second, "msg": "第二"}])
        self.assertEqual(bridge._offset, self.out.stat().st_size)

    def test_invalid_utf8_frame_is_never_served_as_a_reply(self):
        bridge = self.make_bridge()
        rid = bridge.send("ping")
        # A corrupt line must become a bad frame, not a repaired-but-valid-looking reply.
        self.out.write_bytes(b'{"reqId":%d,"ok":true,"note":"\xff\xfe"}\n' % rid)
        self.assertEqual(bridge._read_new(), [])
        self.assertEqual(bridge.bad_frames, 1)
        self.assertEqual(bridge.bad_frames_sample[0]["msg"], "invalid utf-8")
        with self.assertRaises(TimeoutError):
            bridge.wait_for(rid, timeout=0.2)
        self.append_out({"reqId": rid, "ok": True})
        self.assertEqual(bridge.wait_for(rid, timeout=0.5), {"reqId": rid, "ok": True})
        self.assertEqual(bridge.bad_frames, 1)

    def test_same_length_replacement_is_not_swallowed(self):
        bridge = self.make_bridge()
        first = bridge.send("ping")
        self.append_out({"reqId": first, "ok": True, "pad": "p" * 40})
        self.assertEqual(len(bridge._read_new()), 1)
        consumed = self.out.stat().st_size
        second = bridge.send("ping")
        base_len = len(json.dumps({"reqId": second, "ok": True, "pad": ""}).encode("utf-8")) + 1
        padding = consumed - base_len
        self.assertGreaterEqual(padding, 0)
        replacement = json.dumps({"reqId": second, "ok": True, "pad": "x" * padding}) + "\n"
        self.assertEqual(len(replacement.encode("utf-8")), consumed)   # exactly as long as before
        self.out.write_bytes(replacement.encode("utf-8"))
        self.assertEqual(bridge._read_new(), [{"reqId": second, "ok": True, "pad": "x" * padding}])
        self.assertEqual(bridge._offset, consumed)

    def test_longer_replacement_is_not_swallowed(self):
        bridge = self.make_bridge()
        first = bridge.send("ping")
        self.append_out({"reqId": first, "ok": True})
        self.assertEqual(len(bridge._read_new()), 1)
        consumed = self.out.stat().st_size
        second = bridge.send("ping")
        # The new ticket sits BEFORE the old read position, so only a replacement check
        # can see it; the rest of the longer file is a harmless non-reply frame.
        ticket = json.dumps({"reqId": second, "ok": True}) + "\n"
        filler = json.dumps({"event": "filler", "pad": "y" * consumed}) + "\n"
        replacement = (ticket + filler).encode("utf-8")
        self.assertGreater(len(replacement), consumed)
        self.out.write_bytes(replacement)
        frames = bridge._read_new()
        self.assertIn({"reqId": second, "ok": True}, frames)
        self.assertEqual([f["event"] for f in frames if "event" in f], ["filler"])
        self.assertEqual(bridge.bad_frames, 0)

    def test_replacement_that_reproduces_the_consumed_tail_is_the_documented_blind_spot(self):
        bridge = self.make_bridge()
        first = bridge.send("ping")
        old_line = json.dumps({"reqId": first, "ok": True, "pad": ""}) + "\n"
        tail_frame = json.dumps({"event": "tail", "pad": "t" * bridge_transport.FINGERPRINT_BYTES}) + "\n"
        self.out.write_bytes((old_line + tail_frame).encode("utf-8"))
        self.assertEqual(len(bridge._read_new()), 2)
        second = bridge.send("ping")
        new_base = json.dumps({"reqId": second, "ok": True, "pad": ""}) + "\n"
        padding = len(old_line.encode("utf-8")) - len(new_base.encode("utf-8"))
        self.assertGreaterEqual(padding, 0)
        new_line = json.dumps({"reqId": second, "ok": True, "pad": "b" * padding}) + "\n"
        self.assertEqual(len(new_line.encode("utf-8")), len(old_line.encode("utf-8")))
        self.out.write_bytes((new_line + tail_frame).encode("utf-8"))   # same length, new ticket
        # Bounded fingerprint: the replaced file reproduces the last consumed bytes, so the
        # swap is undetectable by design and the unseen ticket is not delivered.
        self.assertEqual(bridge._read_new(), [])
        with self.assertRaises(TimeoutError):
            bridge.wait_for(second, timeout=0.15)
        self.assertEqual(bridge.bad_frames, 0)

    def test_plain_append_after_a_read_is_not_treated_as_replacement(self):
        bridge = self.make_bridge()
        first = bridge.send("ping")
        self.append_out({"reqId": first, "ok": True})
        self.assertEqual(len(bridge._read_new()), 1)
        second = bridge.send("ping")
        self.append_out({"reqId": second, "ok": True})
        self.assertEqual([f["reqId"] for f in bridge._read_new()], [second])
        self.assertEqual(bridge.bad_frames, 0)

    def test_malformed_frames_are_counted_and_the_report_is_bounded(self):
        bridge = self.make_bridge()
        rid = bridge.send("ping")
        bad_lines = ["x" * 5000, "{", "not json", '{"a":', "[]", "123", '"text"', "null"]
        bad_lines = (bad_lines * 3)[:20]
        self.out.write_bytes(("\n".join(bad_lines) + "\n").encode("utf-8"))
        self.append_out({"reqId": rid, "ok": True})
        self.assertEqual(bridge._read_new(), [{"reqId": rid, "ok": True}])
        self.assertEqual(bridge.bad_frames, 20)
        self.assertEqual(len(bridge.bad_frames_sample), 5)
        report = bridge.bad_frame_report()
        self.assertIn("20 malformed frame(s) discarded; first 5:", report)
        self.assertEqual(len(report.splitlines()), 6)
        self.assertLess(len(report), 2000)
        self.assertEqual(len(bridge.bad_frames_sample[0]["preview"]), bridge_transport.BAD_FRAME_PREVIEW)
        with self.out.open("a", encoding="utf-8") as fh:
            fh.write("garbage\n" * 10)
        self.assertEqual(bridge._read_new(), [])
        self.assertEqual(bridge.bad_frames, 30)
        self.assertEqual(len(bridge.bad_frames_sample), 5)      # samples stay bounded too

    def test_truncation_drops_an_unfinished_old_ticket(self):
        bridge = self.make_bridge()
        rid = bridge.send("ping")
        self.out.write_bytes(('{"reqId":%d,"ok":true}' % rid).encode("utf-8"))   # no newline
        self.assertEqual(bridge._read_new(), [])
        self.out.write_bytes(b"")                                  # game rotated out.jsonl
        self.assertEqual(bridge._read_new(), [])
        with self.assertRaises(TimeoutError):
            bridge.wait_for(rid, timeout=0.3)
        self.assertEqual(bridge._offset, 0)
        self.assertEqual(bridge.bad_frames, 0)

    def test_truncation_then_new_content_reads_only_the_new_ticket(self):
        bridge = self.make_bridge()
        first = bridge.send("ping")
        self.append_out({"reqId": first, "ok": True})
        self.assertEqual([f["reqId"] for f in bridge._read_new()], [first])
        self.out.write_bytes(b"")
        second = bridge.send("ping")
        self.append_out({"reqId": second, "ok": True})
        frames = bridge._read_new()
        self.assertEqual([f["reqId"] for f in frames], [second])
        self.assertNotIn(first, [f["reqId"] for f in frames])

    def test_rotation_voids_parked_frames_pending_line_and_tail(self):
        bridge = self.make_bridge()
        mine = bridge.send("ping")
        other = bridge.send("ping")
        self.append_out({"reqId": other, "event": "old"}, {"event": "push"})
        with self.assertRaises(TimeoutError):
            bridge.wait_for(mine, timeout=0.15)      # parks the other reply and the push
        self.assertGreater(bridge._deferred_count, 0)
        self.assertEqual(len(bridge._unsolicited), 1)
        self.out.write_bytes(('{"reqId":%d,"ok":tr' % other).encode("utf-8"))   # half old ticket
        self.assertEqual(bridge._read_new(), [])
        self.assertNotEqual(bridge._pending, b"")
        self.out.write_bytes(b"")                    # game rotates out.jsonl
        self.assertEqual(bridge._read_new(), [])
        self.assertEqual(bridge._deferred_count, 0)
        self.assertEqual(bridge._unsolicited, [])
        self.assertEqual(bridge._pending, b"")
        self.assertEqual(bridge._read_tail, b"")
        with self.assertRaises(TimeoutError):
            bridge.wait_for(other, timeout=0.1)      # the parked old ticket is not reusable
        self.assertEqual(bridge.bad_frames, 0)


# --------------------------------------------------------------- requests
class RequestIsolationTests(BridgeTestCase):
    def test_non_integer_reply_ids_cannot_impersonate_a_ticket(self):
        bridge = self.make_bridge()
        bridge._next_id = 1
        rid = bridge.send("ping")
        invalid = [{"reqId": value, "kind": "invalid"}
                   for value in (True, 1.0, "1")]
        self.append_out(*invalid, {"reqId": rid, "kind": "real"})
        self.assertEqual(bridge.wait_for(rid, timeout=0.5)["kind"], "real")
        self.assertEqual(bridge.drain(seconds=0), invalid)

    def test_float_reply_cannot_impersonate_random_ticket(self):
        bridge = self.make_bridge()
        rid = bridge.send("ping")
        self.append_out({"reqId": float(rid), "kind": "invalid"},
                        {"reqId": rid, "kind": "real"})
        self.assertEqual(bridge.wait_for(rid, timeout=0.5)["kind"], "real")

    def test_invalid_wait_id_cannot_consume_parked_integer_reply(self):
        bridge = self.make_bridge()
        bridge._park({"reqId": 1, "ok": True}, 1)
        for value in (True, 1.0, "1", None, 0, -1, bridge_transport.ID_MAX + 1):
            with self.subTest(req_id=value):
                with self.assertRaises(ValueError):
                    bridge.wait_for(value, timeout=0.01)
                self.assertEqual(bridge._deferred_count, 1)
        self.assertTrue(bridge.wait_for(1, timeout=0.1)["ok"])
        self.assertEqual(self.cmd_lines(), [])

    def test_out_of_range_reply_id_is_not_cached_as_a_ticket(self):
        bridge = self.make_bridge()
        frame = {"reqId": bridge_transport.ID_MAX + 1}
        bridge._park(frame, frame["reqId"])
        self.assertEqual(bridge._deferred_count, 0)
        self.assertEqual(bridge.drain(seconds=0), [frame])

    def test_timeout_names_the_committed_request_id_and_does_not_resend(self):
        bridge = self.make_bridge(timeout=0.05)
        with self.assertRaises(TimeoutError) as caught:
            bridge.request("ping", echo="x", timeout=0.25)
        lines = self.cmd_lines()
        self.assertEqual(len(lines), 1)                            # exactly one send
        self.assertEqual(getattr(caught.exception, "reqId"), lines[0]["id"])
        message = str(caught.exception)
        self.assertIn("reqId=%d" % lines[0]["id"], message)
        self.assertIn("not resent", message)
        self.assertIn("no malformed frames seen", message)

    def test_late_reply_after_timeout_is_picked_up_without_resending(self):
        bridge = self.make_bridge()
        with self.assertRaises(TimeoutError) as caught:
            bridge.request("ping", timeout=0.2)
        rid = caught.exception.reqId
        self.append_out({"reqId": rid, "ok": True, "late": True})
        frame = bridge.wait_for(rid, timeout=0.5)
        self.assertTrue(frame["late"])
        self.assertEqual(len(self.cmd_lines()), 1)                 # still one send only

    def test_reply_for_another_request_is_kept_and_never_returned(self):
        bridge = self.make_bridge()
        rid_a = bridge.send("ping", echo="a")
        rid_b = bridge.send("ping", echo="b")
        self.assertNotEqual(rid_a, rid_b)
        self.append_out({"reqId": rid_b, "echo": "b"}, {"reqId": rid_a, "echo": "a"})
        frame_a = bridge.wait_for(rid_a, timeout=0.5)
        self.assertEqual((frame_a["reqId"], frame_a["echo"]), (rid_a, "a"))
        frame_b = bridge.wait_for(rid_b, timeout=0.5)
        self.assertEqual((frame_b["reqId"], frame_b["echo"]), (rid_b, "b"))
        self.assertEqual(bridge._deferred_count, 0)
        self.assertEqual(bridge.deferred_dropped, 0)

    def test_batch_tail_after_the_matching_reply_is_preserved(self):
        bridge = self.make_bridge()
        rid_a = bridge.send("ping", echo="a")
        rid_b = bridge.send("ping", echo="b")
        # Both replies arrive in ONE poll batch, with the wanted one first: returning
        # immediately would silently drop the second request's reply.
        self.append_out({"reqId": rid_a, "echo": "a"}, {"reqId": rid_b, "echo": "b"})
        frame_a = bridge.wait_for(rid_a, timeout=0.5)
        self.assertEqual((frame_a["reqId"], frame_a["echo"]), (rid_a, "a"))
        frame_b = bridge.wait_for(rid_b, timeout=0.5)
        self.assertEqual((frame_b["reqId"], frame_b["echo"]), (rid_b, "b"))
        self.assertEqual(bridge._deferred_count, 0)
        self.assertEqual(bridge.deferred_dropped, 0)

    def test_predicate_waits_for_the_matching_frame(self):
        bridge = self.make_bridge()
        rid = bridge.send("ping")
        self.append_out({"reqId": rid, "phase": "starting"})
        timer = threading.Timer(
            0.12, lambda: self.append_out({"reqId": rid, "phase": "ready"}))
        timer.start()
        self.addCleanup(timer.cancel)
        frame = bridge.wait_for(rid, pred=lambda f: f.get("phase") == "ready", timeout=2.0)
        self.assertEqual(frame["phase"], "ready")

    def test_unsolicited_push_frames_are_not_returned_as_replies(self):
        bridge = self.make_bridge()
        rid = bridge.send("ping")
        self.append_out({"event": "turn"}, {"reqId": rid, "ok": True})
        frame = bridge.wait_for(rid, timeout=0.5)
        self.assertEqual(frame["reqId"], rid)
        self.assertNotIn("event", frame)
        self.assertIn({"event": "turn"}, bridge.drain(seconds=0.01))

    def test_concurrent_requests_are_serialized_and_each_gets_its_own_reply(self):
        server = ReplyServer(self.cmd, self.out)
        server.start()
        self.addCleanup(server.stop)
        bridge = self.make_bridge(timeout=5.0)
        results = {}
        errors = []

        def worker(tag):
            try:
                results[tag] = bridge.request("ping", echo=tag)
            except Exception as exc:                              # noqa: BLE001
                errors.append((tag, exc))

        threads = [threading.Thread(target=worker, args=(tag,)) for tag in ("alpha", "beta")]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(timeout=8)
        self.assertEqual(errors, [])
        self.assertEqual(sorted(results), ["alpha", "beta"])
        by_echo = {line["echo"]: line["id"] for line in self.cmd_lines()}
        self.assertEqual(len(by_echo), 2)
        for tag, frame in results.items():
            self.assertEqual(frame["echo"], tag)                  # never the other request's reply
            self.assertEqual(frame["reqId"], by_echo[tag])
        self.assertEqual(server.before_first_reply, 1)            # send+wait is serialized

    def test_close_rejects_further_use(self):
        bridge = self.make_bridge()
        bridge.close()
        bridge.close()                                            # idempotent
        with self.assertRaisesRegex(RuntimeError, "closed"):
            bridge.send("ping")
        with self.assertRaisesRegex(RuntimeError, "closed"):
            bridge.wait_for(1, timeout=0.05)
        with self.assertRaisesRegex(RuntimeError, "closed"):
            bridge.request("ping", timeout=0.05)
        with self.assertRaisesRegex(RuntimeError, "closed"):
            bridge.drain(seconds=0.0)
        self.assertEqual(self.cmd.read_bytes(), b"")


# --------------------------------------------------------------- liveness
class LivenessTests(BridgeTestCase):
    def test_injected_dead_liveness_raises_instead_of_timing_out(self):
        bridge = self.make_bridge(liveness=lambda: False)
        rid = bridge.send("ping")
        with self.assertRaisesRegex(RuntimeError, "no longer running"):
            bridge.wait_for(rid, timeout=0.6)
        self.assertEqual(len(self.cmd_lines()), 1)                # a dead session never re-sends

    def test_denied_or_unknown_liveness_is_not_reported_dead(self):
        self.assertFalse(bridge_transport.alive_from_open_failure(87))
        self.assertFalse(bridge_transport.alive_from_open_failure(2))
        self.assertTrue(bridge_transport.alive_from_open_failure(5))      # access denied
        self.assertTrue(bridge_transport.alive_from_open_failure(0))      # unknown
        self.assertTrue(bridge_transport.alive_from_open_failure(1234))   # unknown
        bridge = self.make_bridge(liveness=None)
        self.assertTrue(bridge._game_alive(kernel32=FakeKernel32(handle=0, err=5)))
        self.assertFalse(bridge._game_alive(kernel32=FakeKernel32(handle=0, err=87)))
        self.assertTrue(bridge._game_alive(kernel32=FakeKernel32(code=bridge_transport.STILL_ACTIVE)))
        exited = FakeKernel32(code=0)
        self.assertFalse(bridge._game_alive(kernel32=exited))
        self.assertTrue(exited.closed)
        self.assertTrue(bridge._game_alive(kernel32=FakeKernel32(exit_ok=False)))
        self.assertTrue(bridge._game_alive(kernel32=BrokenKernel32()))

    def test_liveness_probe_is_lazy_and_unbound_client_counts_as_alive(self):
        saved = bridge_transport._KERNEL32
        bridge_transport._KERNEL32 = None
        self.addCleanup(setattr, bridge_transport, "_KERNEL32", saved)
        bridge = self.make_bridge(liveness=None)
        bridge.send("ping")
        self.assertIsNone(bridge_transport._KERNEL32)             # sending never probes
        other = tempfile.TemporaryDirectory()
        self.addCleanup(other.cleanup)
        ub = Bridge(work=Path(other.name), liveness=None)
        self.assertIsNone(ub._game_pid())
        self.assertTrue(ub._game_alive())
        self.assertIsNone(bridge_transport._KERNEL32)             # no pid -> no kernel32 call
        if os.name == "nt":
            bridge._bound_pid = os.getpid()
            self.assertTrue(bridge._game_alive())
            self.assertIsNotNone(bridge_transport._KERNEL32)      # lazy load, once
            bridge._bound_pid = 0xFFFFFFF0                        # not a live pid
            self.assertFalse(bridge._game_alive())

    def test_kernel32_probe_uses_64_bit_handle_signatures(self):
        if os.name != "nt":
            self.skipTest("kernel32 is Windows only")
        k32 = bridge_transport._kernel32()
        self.assertIsNotNone(k32)
        self.assertIs(k32.OpenProcess.restype, ctypes.c_void_p)       # HANDLE, not c_int
        self.assertEqual(list(k32.OpenProcess.argtypes),
                         [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong])
        self.assertIs(k32.GetExitCodeProcess.restype, ctypes.c_int)
        self.assertEqual(len(k32.GetExitCodeProcess.argtypes), 2)
        self.assertIs(k32.GetExitCodeProcess.argtypes[0], ctypes.c_void_p)
        self.assertIs(k32.CloseHandle.restype, ctypes.c_int)
        self.assertIs(k32.CloseHandle.argtypes[0], ctypes.c_void_p)


# --------------------------------------------------------------- packaging
class PackagingTests(BridgeTestCase):
    def test_work_override_never_writes_into_the_live_queue(self):
        live_cmd = bridge_transport.WORK / "cmd.jsonl"
        bridge = self.make_bridge()
        bridge.send("probe_074")
        self.assertEqual(bridge.work, self.work)
        self.assertEqual(bridge.cmd_path, self.cmd)
        self.assertEqual(bridge.out_path, self.out)
        self.assertEqual(bridge.ready_path, self.ready)
        after = live_cmd.read_text(encoding="utf-8") if live_cmd.exists() else ""
        self.assertNotIn("probe_074", after)

    def test_module_import_has_no_desktop_side_effects(self):
        calls = []
        original = pathlib.Path.mkdir

        def spy(self, *args, **kwargs):
            calls.append(str(self))
            return original(self, *args, **kwargs)

        pathlib.Path.mkdir = spy
        try:
            before = set(sys.modules)
            module = fresh_module()
            after = set(sys.modules)
        finally:
            pathlib.Path.mkdir = original
        self.assertEqual(calls, [])                               # import creates no directory
        added = after - before
        self.assertEqual(added & {"client", "PIL", "pyautogui", "uiautomation",
                                  "win32gui", "win32api", "mss", "ctypes.wintypes"}, set(),
                         "imported desktop modules: %s" % sorted(added))
        self.assertNotIn("client", after)
        self.assertNotIn("PIL", after)
        self.assertFalse(hasattr(module, "user32"))
        self.assertFalse(hasattr(module, "SHOTS"))
        self.assertEqual(module.WORK, WORK)
        self.assertEqual(module.ID_MAX, 2 ** 53 - 1)

    def test_source_has_no_window_capture_or_input_calls(self):
        tree = ast.parse(MODULE_PATH.read_text(encoding="utf-8"), filename=str(MODULE_PATH))
        names = {node.id for node in ast.walk(tree) if isinstance(node, ast.Name)}
        names |= {node.attr for node in ast.walk(tree) if isinstance(node, ast.Attribute)}
        forbidden = {"user32", "gdi32", "windll", "SendInput", "SetCursorPos", "SetForegroundWindow",
                     "BringWindowToTop", "BitBlt", "PrintWindow", "keybd_event", "mouse_event",
                     "pyautogui", "ImageGrab", "capture_screen", "capture", "click", "find_window",
                     "marker", "shots", "SHOTS"}
        self.assertEqual(names & forbidden, set())
        imported = set()
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                imported |= {alias.name.split(".")[0] for alias in node.names}
            elif isinstance(node, ast.ImportFrom) and node.module:
                imported.add(node.module.split(".")[0])
        self.assertEqual(imported & {"client", "PIL", "pyautogui", "uiautomation",
                                     "win32gui", "win32api", "mss"}, set())
        self.assertIn("ctypes", imported)                         # only for kernel32 liveness


if __name__ == "__main__":
    unittest.main(verbosity=2)
