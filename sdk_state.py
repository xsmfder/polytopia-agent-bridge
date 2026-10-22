"""Atomic SDK state guarded by a process-lifetime operating system lock."""
from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path

SCHEMA = "polytopia-sdk-session-1"


class SessionStateError(RuntimeError):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


class SessionStore:
    def __init__(self, path):
        self.path = Path(path).resolve()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.lock_path = self.path.with_name(self.path.name + ".lock")
        self._lock = self.lock_path.open("a+b")
        self._closed = False
        try:
            self._lock.seek(0, os.SEEK_END)
            if self._lock.tell() == 0:
                self._lock.write(b"\0")
                self._lock.flush()
            self._lock.seek(0)
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self._lock.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self._lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except (OSError, BlockingIOError) as exc:
            self._lock.close()
            self._closed = True
            raise SessionStateError("SESSION_LOCKED", "another process owns this SDK session") from exc

    def load(self):
        if self._closed:
            raise SessionStateError("SESSION_CLOSED", "SDK session lock is closed")
        if not self.path.exists():
            return None
        try:
            value = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as exc:
            raise SessionStateError("SESSION_INVALID", "SDK session file cannot be read") from exc
        if not isinstance(value, dict) or value.get("schema") != SCHEMA:
            raise SessionStateError("SESSION_INVALID", "unsupported SDK session schema")
        return value

    def save(self, value):
        if self._closed:
            raise SessionStateError("SESSION_CLOSED", "SDK session lock is closed")
        temporary = None
        try:
            with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=self.path.parent,
                                             prefix=self.path.name + ".", suffix=".tmp", delete=False) as fh:
                temporary = Path(fh.name)
                json.dump(value, fh, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
                fh.flush()
                os.fsync(fh.fileno())
            os.replace(temporary, self.path)
            temporary = None
            if os.name != "nt":
                descriptor = os.open(self.path.parent, os.O_RDONLY)
                try:
                    os.fsync(descriptor)
                finally:
                    os.close(descriptor)
        except (OSError, ValueError, TypeError) as exc:
            raise SessionStateError("SESSION_IO_ERROR", "SDK state could not be atomically persisted") from exc
        finally:
            if temporary is not None:
                temporary.unlink(missing_ok=True)

    def close(self):
        if self._closed:
            return
        try:
            self._lock.seek(0)
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self._lock.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(self._lock.fileno(), fcntl.LOCK_UN)
        finally:
            self._lock.close()
            self._closed = True
        # Keep the inode/path: unlinking a lock file permits two independent locks.
