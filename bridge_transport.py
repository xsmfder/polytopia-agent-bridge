"""Polytopia agent bridge: pure-stdlib file-queue transport (no desktop code).

This is the whole client side of the queue protocol, extracted from client.py so the
SDK can ``import bridge_transport`` without loading user32/gdi32, creating a shots
directory or touching any window.  Only the file queue and (lazily) kernel32 liveness
are used here.

Protocol
    agent -> cmd.jsonl : {"id":N,"cmd":"...","bridgePid":<ready pid>, ...}
    game  -> out.jsonl : {"reqId":N, ...}
``bridge-ready.json`` names the process that owns the queue (pid + started).  A Bridge
binds that pair once, stamps every request with the PID, and never follows later
ready-file changes.
"""
import ctypes
import json
import random
import threading
import time
from collections import OrderedDict
from pathlib import Path

# --------------------------------------------------------------- queue layout
WORK = Path(__file__).resolve().parent          # .offline-work/agent-bridge
ROOT = WORK.parents[1]
CMD_NAME = "cmd.jsonl"
OUT_NAME = "out.jsonl"
READY_NAME = "bridge-ready.json"

MAX_BAD_FRAME_SAMPLES = 5
BAD_FRAME_PREVIEW = 200
MAX_DEFERRED_REQ_IDS = 64
MAX_DEFERRED_FRAMES = 256
MAX_UNSOLICITED_FRAMES = 256
POLL_INTERVAL = 0.05
LIVENESS_INTERVAL = 2.0
# Bytes of the already-consumed region kept as a fingerprint. A replaced out.jsonl
# (same length or longer) is spotted by re-reading this small tail instead of the file.
FINGERPRINT_BYTES = 64
# Bound one polling batch when a client catches up after a long pause. A single
# frame may span batches; it is decoded only after its newline arrives.
READ_CHUNK_BYTES = 1024 * 1024

# Request ids are random so two clients started at the same moment cannot both claim
# the same reply line; stay inside the range every JSON reader represents exactly.
ID_MIN = 1
ID_MAX = 2 ** 53 - 1


def _valid_request_id(value):
    # Python compares True and 1.0 equal to 1; only protocol integers are tickets.
    return type(value) is int and ID_MIN <= value <= ID_MAX

# kernel32 liveness constants
STILL_ACTIVE = 259
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
ERROR_FILE_NOT_FOUND = 2
ERROR_INVALID_PARAMETER = 87

_KERNEL32 = None                                # loaded on first liveness probe only


def _kernel32():
    """kernel32 with 64-bit-safe handle signatures, or None if it cannot be loaded.

    Handles are declared as c_void_p: with the implicit c_int restype a real 64-bit
    HANDLE value would be truncated, and liveness would then be unreliable.
    """
    global _KERNEL32
    if _KERNEL32 is None:
        try:
            k32 = ctypes.WinDLL("kernel32", use_last_error=True)
            k32.OpenProcess.restype = ctypes.c_void_p
            k32.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
            k32.GetExitCodeProcess.restype = ctypes.c_int
            k32.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
            k32.CloseHandle.restype = ctypes.c_int
            k32.CloseHandle.argtypes = [ctypes.c_void_p]
            _KERNEL32 = k32
        except Exception:
            _KERNEL32 = False
    return _KERNEL32 or None


def alive_from_open_failure(err):
    """OpenProcess failed with `err`: only "no such process" means dead.

    Access denied (5) and anything unknown prove nothing, so they count as alive and
    the caller waits out its own timeout instead of being told a live game died.
    """
    return err not in (ERROR_INVALID_PARAMETER, ERROR_FILE_NOT_FOUND)


def wlog(msg):
    print(msg, flush=True)


class Bridge:
    """File-queue transport client bound to one bridge session (PID + started)."""

    def __init__(self, timeout=20.0, work=None, liveness=None, rng=None):
        self.work = Path(work) if work is not None else WORK
        self.cmd_path = self.work / CMD_NAME
        self.out_path = self.work / OUT_NAME
        self.ready_path = self.work / READY_NAME
        self.timeout = timeout
        self.liveness = liveness              # test seam: () -> bool; None = ask kernel32
        self._rng = rng if rng is not None else random.Random()
        self._bound_pid, self._bound_started = self._read_ready()
        self.cmd_path.parent.mkdir(parents=True, exist_ok=True)
        if not self.cmd_path.exists():
            self.cmd_path.write_text("", encoding="utf-8")
        self._next_id = self._draw_start_id()
        self._offset = self.out_path.stat().st_size if self.out_path.exists() else 0
        self._read_tail = self._tail_bytes(min(FINGERPRINT_BYTES, self._offset))
        self._pending = b""                   # bytes after the last newline (unfinished line)
        self._read_has_more = False           # unread bytes from the last size snapshot
        self.bad_frames = 0
        self.bad_frames_sample = []
        self.deferred_dropped = 0
        self.unsolicited_dropped = 0
        self._deferred = OrderedDict()        # reqId -> frames kept for other requests
        self._deferred_count = 0
        self._unsolicited = []                # frames the game emitted without a reqId
        self._closed = False
        self._request_lock = threading.RLock()  # one in-flight send+wait per process
        self._write_lock = threading.Lock()     # atomic id allocation + append

    # ------------------------------------------------------------- readiness
    def _read_ready(self):
        """(pid, started) of the queue owner, read fresh; never changes this binding.

        `started` distinguishes a restarted instance that kept its PID; a fixture without
        the field simply binds started=None and is still accepted.
        """
        try:
            data = json.loads(self.ready_path.read_text(encoding="utf-8-sig"))
            raw = data.get("pid")
            if isinstance(raw, bool) or not isinstance(raw, (int, str)):
                return (None, None)
            pid = int(raw)
            if pid <= 0:
                return (None, None)
            started = data.get("started")
            return (pid, started if isinstance(started, str) and started else None)
        except Exception:
            return (None, None)

    def _read_ready_pid(self):
        return self._read_ready()[0]

    def _assert_session(self):
        pid, started = self._read_ready()
        if self._bound_pid is None or pid != self._bound_pid:
            raise RuntimeError(
                "bridge session changed or unavailable (bound PID %s, ready PID %s); "
                "no command written. Create a new Bridge after the intended instance is ready."
                % (self._bound_pid, pid))
        if self._bound_started is not None and started is None:
            raise RuntimeError(
                "bridge session start marker unavailable (bound PID %s); no command "
                "written. Recheck the intended instance before creating a new Bridge."
                % self._bound_pid)
        if (self._bound_started is not None and started is not None
                and started != self._bound_started):
            raise RuntimeError(
                "bridge session restarted with the same PID %s (start %s -> %s); no command "
                "written. Create a new Bridge after the intended instance is ready."
                % (self._bound_pid, self._bound_started, started))

    def _game_pid(self):
        """The PID fixed when this Bridge was created; never follow ready-file changes."""
        return self._bound_pid

    # ---------------------------------------------------------------- writing
    def _draw_start_id(self):
        return self._rng.randint(ID_MIN, ID_MAX)

    def send(self, cmd, **kw):
        if "id" in kw or "cmd" in kw or "bridgePid" in kw:
            raise ValueError("id/cmd/bridgePid are reserved request envelope fields; "
                             "use unit, popupId or reward")
        if self._closed:
            raise RuntimeError("bridge client is closed; create a new Bridge")
        with self._write_lock:
            # Never silently retarget a cached client after another game takes the queue.
            # The PID envelope also lets the server reject a switch after this check.
            self._assert_session()
            rid = self._next_id
            payload = {"id": rid, "cmd": cmd, "bridgePid": self._bound_pid}
            payload.update(kw)
            with self.cmd_path.open("a", encoding="utf-8") as fh:
                fh.write(json.dumps(payload) + "\n")
            self._next_id = rid + 1 if rid < ID_MAX else self._draw_start_id()
        return rid

    # ---------------------------------------------------------------- reading
    def _record_bad_frame(self, pos, msg, raw):
        self.bad_frames += 1
        if len(self.bad_frames_sample) < MAX_BAD_FRAME_SAMPLES:
            preview = raw if isinstance(raw, str) else raw.decode("utf-8", errors="replace")
            self.bad_frames_sample.append(
                {"pos": pos, "msg": msg, "preview": preview[:BAD_FRAME_PREVIEW]})

    def bad_frame_report(self):
        """Bounded, printable summary of malformed frames seen so far."""
        if not self.bad_frames:
            return ""
        parts = []
        for s in self.bad_frames_sample:
            parts.append("  col=%s %s | %s" % (s["pos"], s["msg"], s["preview"]))
        return ("%d malformed frame(s) discarded; first %d:\n%s"
                % (self.bad_frames, len(self.bad_frames_sample), "\n".join(parts)))

    def _tail_bytes(self, want):
        """Read `want` bytes ending at the current read position (bounded, small)."""
        if want <= 0:
            return b""
        try:
            with self.out_path.open("rb") as fh:
                fh.seek(self._offset - want)
                return fh.read(want)
        except OSError:
            return b""

    def _sync_position(self):
        """Detect a superseded out.jsonl and void everything read from the old file.

        Two shapes are detected without reading the whole file:
          * the file is shorter than our read position (truncate / rotate);
          * the file was replaced by an equally long or longer one: the small tail we
            already consumed no longer sits at the same absolute position.
        Both reset the read position and drop the unfinished line, the parked frames and
        the tail fingerprint, so a ticket from the old file can never be served as a
        result of the new one.  Detection boundary: a replacement that reproduces the
        last FINGERPRINT_BYTES consumed bytes at the same offset is indistinguishable
        from the original file.
        """
        size = self.out_path.stat().st_size if self.out_path.exists() else 0
        replaced = size < self._offset
        if not replaced and self._offset > 0 and self._read_tail:
            replaced = self._tail_bytes(len(self._read_tail)) != self._read_tail
        if not replaced:
            return False
        self._offset = 0
        self._pending = b""
        self._read_has_more = False
        self._read_tail = b""
        self._deferred.clear()
        self._deferred_count = 0
        self._unsolicited = []
        return True

    def _poll(self):
        """Return complete frames from one bounded batch appended to out.jsonl.

        Reads bytes (never text) so `_offset` stays a true byte offset and a multi-byte
        UTF-8 character split across two reads is not corrupted; the part after the last
        newline is held in `_pending` until the game finishes the line.  Decoding is
        strict per line: a line that is not valid UTF-8 is a bad frame, never repaired
        into something that looks like a legitimate response.
        """
        self._assert_session()
        self._sync_position()
        self._read_has_more = False
        if not self.out_path.exists():
            return []
        size = self.out_path.stat().st_size
        if size == self._offset:
            return []
        with self.out_path.open("rb") as fh:
            fh.seek(self._offset)
            chunk = fh.read(READ_CHUNK_BYTES)
            self._offset = fh.tell()
        self._read_has_more = self._offset < size
        if len(chunk) >= FINGERPRINT_BYTES:
            self._read_tail = chunk[-FINGERPRINT_BYTES:]
        else:
            self._read_tail = (self._read_tail + chunk)[-FINGERPRINT_BYTES:]
        data = self._pending + chunk
        cut = data.rfind(b"\n")
        if cut < 0:
            self._pending = data
            return []
        block = data[:cut]
        self._pending = data[cut + 1:]
        frames = []
        for raw_line in block.split(b"\n"):
            line = raw_line.strip()
            if not line:
                continue
            try:
                text = line.decode("utf-8")
            except UnicodeDecodeError as exc:
                self._record_bad_frame(exc.start, "invalid utf-8", line)
                continue
            try:
                frame = json.loads(text)
            except ValueError as exc:
                self._record_bad_frame(getattr(exc, "pos", -1), getattr(exc, "msg", str(exc)), line)
                continue
            if not isinstance(frame, dict):
                self._record_bad_frame(-1, "frame is not a JSON object", line)
                continue
            frames.append(frame)
        return frames

    def _read_new(self):
        """One bounded batch of newly read frames (bad ones are counted)."""
        return self._poll()

    # ---------------------------------------------------- reply bookkeeping
    def _park(self, frame, rid):
        """Keep a frame that is not the answer to the request being waited on."""
        if _valid_request_id(rid):
            bucket = self._deferred.get(rid)
            if bucket is None:
                bucket = []
                self._deferred[rid] = bucket
            bucket.append(frame)
            self._deferred_count += 1
            while (self._deferred_count > MAX_DEFERRED_FRAMES
                   or len(self._deferred) > MAX_DEFERRED_REQ_IDS):
                _rid, dropped = self._deferred.popitem(last=False)
                self._deferred_count -= len(dropped)
                self.deferred_dropped += 1
        else:
            self._unsolicited.append(frame)
            if len(self._unsolicited) > MAX_UNSOLICITED_FRAMES:
                del self._unsolicited[0]
                self.unsolicited_dropped += 1

    def _take_deferred(self, req_id, pred):
        bucket = self._deferred.get(req_id)
        if not bucket:
            return None
        for index, frame in enumerate(bucket):
            if pred is None or pred(frame):
                del bucket[index]
                self._deferred_count -= 1
                if not bucket:
                    del self._deferred[req_id]
                return frame
        return None

    def _flush_parked(self):
        frames = []
        for bucket in self._deferred.values():
            frames.extend(bucket)
        self._deferred.clear()
        self._deferred_count = 0
        frames.extend(self._unsolicited)
        del self._unsolicited[:]
        return frames

    # -------------------------------------------------------------- requests
    def wait_for(self, req_id, pred=None, timeout=None):
        """Wait for the reply to `req_id`; never returns another request's frame."""
        if self._closed:
            raise RuntimeError("bridge client is closed; create a new Bridge")
        if not _valid_request_id(req_id):
            raise ValueError("req_id must be an integer from %d to %d" % (ID_MIN, ID_MAX))
        with self._request_lock:
            self._assert_session()
            self._sync_position()
            frame = self._take_deferred(req_id, pred)
            if frame is not None:
                return frame
            end = time.time() + (timeout if timeout is not None else self.timeout)
            seen = 0
            next_alive_check = time.time()
            while time.time() < end:
                result = None
                for frame in self._read_new():
                    seen += 1
                    rid = frame.get("reqId")
                    if result is None and _valid_request_id(rid) and rid == req_id:
                        if pred is None or pred(frame):
                            # Do not return yet: the rest of this batch may hold replies for
                            # other in-flight requests and must be kept, not dropped.
                            result = frame
                            continue
                        continue          # same ticket, not the answer we want yet
                    self._park(frame, rid)
                if result is not None:
                    return result
                now = time.time()
                if now >= next_alive_check:
                    next_alive_check = now + LIVENESS_INTERVAL
                    # A dead game cannot ever answer: surface that instead of a bare timeout.
                    if not self._game_alive():
                        raise RuntimeError(
                            "bridge process %s is no longer running; %d frame(s) seen. "
                            "The game was closed - relaunch it before sending commands."
                            % (self._game_pid(), seen))
                if not self._read_has_more:
                    time.sleep(POLL_INTERVAL)
            report = self.bad_frame_report()
            detail = ("; %s" % report) if report else "; no malformed frames seen"
            raise TimeoutError(
                "no reply for reqId=%s (already written to cmd.jsonl, not resent; "
                "saw %d frame(s), %d kept for other requests)%s"
                % (req_id, seen, self._deferred_count, detail))

    def request(self, cmd, pred=None, timeout=None, **kw):
        """Send once and wait; a timeout never re-sends and names the committed reqId."""
        if self._closed:
            raise RuntimeError("bridge client is closed; create a new Bridge")
        with self._request_lock:
            rid = self.send(cmd, **kw)
            try:
                return self.wait_for(rid, pred=pred, timeout=timeout)
            except TimeoutError as exc:
                exc.reqId = rid
                raise

    def drain(self, seconds=1.0):
        """Collect everything readable for `seconds` (including parked pushes)."""
        if self._closed:
            raise RuntimeError("bridge client is closed; create a new Bridge")
        with self._request_lock:
            self._assert_session()
            self._sync_position()
            end = time.time() + seconds
            frames = self._flush_parked()
            while time.time() < end:
                for frame in self._read_new():
                    self._park(frame, frame.get("reqId"))
                frames += self._flush_parked()
                if not self._read_has_more:
                    time.sleep(POLL_INTERVAL)
            return frames

    # -------------------------------------------------------------- liveness
    def _game_alive(self, kernel32=None):
        """False only when the bound process is provably gone.

        Unknown outcomes (no kernel32, access denied, a probe that raises) count as
        alive so a running game is never reported dead.
        """
        if self.liveness is not None:
            return bool(self.liveness())
        pid = self._game_pid()
        if pid is None:
            return True          # nothing to compare against; fall back to the timeout
        k32 = kernel32 if kernel32 is not None else _kernel32()
        if k32 is None:
            return True
        try:
            handle = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
            if not handle:
                return alive_from_open_failure(ctypes.get_last_error())
            try:
                code = ctypes.c_ulong()
                if not k32.GetExitCodeProcess(handle, ctypes.byref(code)):
                    return True
                return code.value == STILL_ACTIVE
            finally:
                k32.CloseHandle(handle)
        except Exception:
            return True

    def bound_process_exited(self, pid):
        """Prove the original queue owner exited; unavailable probes never mean exit."""
        if type(pid) is not int or self._bound_pid is None or pid != self._bound_pid:
            return None
        return not self._game_alive()

    def lifecycle_receipt(self, operation_id):
        """Read only this queue owner's durable final quit progress, never another run."""
        if (not isinstance(operation_id, str) or len(operation_id) != 32
                or any(c not in "0123456789abcdef" for c in operation_id)
                or not self._bound_started or not self._bound_pid):
            return None
        path = self.work / "lifecycle-receipts" / (operation_id + ".json")
        try:
            if path.is_symlink() or path.stat().st_size > 65536:
                return None
            value = json.loads(path.read_text(encoding="utf-8-sig"))
            if (not isinstance(value, dict) or value.get("schema") != "game-lifecycle-1"
                    or type(value.get("pid")) is not int or value["pid"] != self._bound_pid
                    or value.get("bridgeStarted") != self._bound_started
                    or value.get("operationId") != operation_id
                    or value.get("operation") != "quit" or value.get("phase") != "quit_requested"
                    or value.get("quitScheduled") is not True):
                return None
            return value
        except (OSError, ValueError):
            return None

    def close(self):
        """Idempotent; later sends/waits are rejected instead of hitting a stale queue."""
        self._closed = True
