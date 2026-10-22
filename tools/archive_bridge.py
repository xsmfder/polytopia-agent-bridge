"""Explicit offline queue archiving. The default plan performs no writes.

Only cmd.jsonl/out.jsonl are archived. No game, save, SDK session or historical
evidence is changed. Verified gzip evidence is durable before queues are cleared.
"""
from __future__ import annotations

import argparse
from contextlib import ExitStack, closing, contextmanager
import gzip
import hashlib
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
from datetime import datetime, timezone
import uuid

sys.dont_write_bytecode = True
from deploy_bridge import WORK, game_processes, inside, require

sys.path.insert(0, str(WORK))
from sdk_state import SessionStore

SCHEMA = "bridge-queue-archive-1"
QUEUES = ("cmd.jsonl", "out.jsonl")


def stamp(info):
    return {"device": info.st_dev, "inode": info.st_ino, "size": info.st_size,
            "mtimeNs": info.st_mtime_ns}


def safe_source(path):
    """Reject links and multiple aliases; these are fixed work-directory queues."""
    require(path.resolve() == path.parent.resolve() / path.name, "Queue path is a link: " + str(path))
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and not path.is_symlink()
            and not (getattr(info, "st_file_attributes", 0) & 0x400), "Queue is not a plain file: " + str(path))
    require(info.st_nlink == 1, "Queue has multiple hard links: " + str(path))
    return info


def stream_digest(handle):
    digest, size = hashlib.sha256(), 0
    for chunk in iter(lambda: handle.read(1024 * 1024), b""):
        digest.update(chunk)
        size += len(chunk)
    return digest.hexdigest(), size


def snapshot(path):
    if not path.exists():
        require(not path.is_symlink(), "Broken queue symlink: " + str(path))
        return {"exists": False}
    before = safe_source(path)
    with path.open("rb") as source:
        opened = os.fstat(source.fileno())
        digest, size = stream_digest(source)
        after = os.fstat(source.fileno())
    require(stamp(before) == stamp(opened) == stamp(after) == stamp(safe_source(path))
            and size == before.st_size, "Queue changed while reading: " + str(path))
    return {"exists": True, "stat": stamp(before), "sha256": digest, "size": size}


def probe_stopped(probe):
    pids = probe()
    require(isinstance(pids, list) and all(type(pid) is int and pid > 0 for pid in pids),
            "Invalid game process query")
    return pids


def plan(work=WORK, probe=None):
    work = Path(work).resolve()
    require(work.is_dir(), "Missing bridge work directory")
    pids = probe_stopped(probe or game_processes)
    sources = {name: snapshot(work / name) for name in QUEUES}
    return {"operation": "plan", "work": str(work), "gamePids": pids,
            "canArchive": not pids and any(item.get("size", 0) for item in sources.values()),
            "sources": sources, "archived": False, "queuesCleared": False}


def write_json(path, value):
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=path.name + ".", suffix=".tmp",
                                         delete=False) as handle:
            temporary = Path(handle.name)
            handle.write((json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8"))
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
        temporary = None
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


@contextmanager
def coordination_locks(work):
    # Same fixed paths and order as the existing deploy/start tools. The deploy
    # O_EXCL lock deliberately remains fail-closed after a hard process crash.
    directory = inside(work / "deployments", work)
    startup = inside(work / "launches", work)
    directory.mkdir(exist_ok=True)
    path = directory / ".deployment.lock"
    descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    try:
        with closing(SessionStore(startup / "startup-lock.json")):
            yield
    finally:
        os.close(descriptor)
        path.unlink()


@contextmanager
def exclusive_source(path):
    """Windows share=0 denies append, replacement and deletion while clearing."""
    safe_source(path)
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes
        import msvcrt
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        create = kernel.CreateFileW
        create.argtypes = (wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID,
                           wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE)
        create.restype = wintypes.HANDLE
        handle = create(str(path), 0xC0000000, 0, None, 3, 0x80, None)
        if handle == ctypes.c_void_p(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            descriptor = msvcrt.open_osfhandle(handle, os.O_RDWR | os.O_BINARY)
        except BaseException:
            close = kernel.CloseHandle
            close.argtypes, close.restype = (wintypes.HANDLE,), wintypes.BOOL
            close(handle)
            raise
        with os.fdopen(descriptor, "r+b") as source:
            yield source
    else:
        import fcntl
        with path.open("r+b") as source:
            fcntl.flock(source.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            yield source


def compressed_copy(source, target, expected):
    require(snapshot(source) == expected, "Queue changed before compression: " + str(source))
    with source.open("rb") as reader, target.open("xb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as writer:
            for chunk in iter(lambda: reader.read(1024 * 1024), b""):
                writer.write(chunk)
        raw.flush()
        os.fsync(raw.fileno())
    require(snapshot(source) == expected, "Queue changed during compression: " + str(source))
    with gzip.open(target, "rb") as reader:
        digest, size = stream_digest(reader)
    require(digest == expected["sha256"] and size == expected["size"], "Compressed source verification failed")
    with target.open("rb") as reader:
        compressed_sha, compressed_size = stream_digest(reader)
    return {"gzip": target.name, "gzipSha256": compressed_sha, "gzipSize": compressed_size}


def verify(work, record_path):
    work = Path(work).resolve()
    archive_root = inside(work / "archives", work)
    record_path = inside(record_path, archive_root)
    require(record_path.name == "archive-manifest.json", "Expected archive-manifest.json")
    proof = json.loads(record_path.read_text(encoding="utf-8"))
    require(isinstance(proof, dict), "Invalid archive manifest")
    require(proof.get("schema") == SCHEMA and proof.get("work") == str(work), "Archive identity mismatch")
    require(isinstance(proof.get("sources"), dict) and set(proof["sources"]) == set(QUEUES), "Unexpected queue set")
    for name, entry in proof["sources"].items():
        require(isinstance(entry, dict), "Invalid queue record")
        require(type(entry.get("exists")) is bool, "Invalid queue presence")
        if not entry["exists"]:
            require(entry == {"exists": False}, "Unexpected absent queue data")
            continue
        require(entry.get("gzip") == name + ".gz", "Unexpected compressed filename")
        compressed = inside(record_path.parent / entry["gzip"], record_path.parent)
        with compressed.open("rb") as raw:
            digest, size = stream_digest(raw)
        require(digest == entry.get("gzipSha256") and size == entry.get("gzipSize"), "Compressed archive changed")
        with gzip.open(compressed, "rb") as reader:
            digest, size = stream_digest(reader)
        require(digest == entry.get("sha256") and size == entry.get("size"), "Original bytes verification failed")
    return {"ok": True, "operation": "verify", "manifest": str(record_path), "sources": proof["sources"]}


def archive(work=WORK, probe=None):
    work = Path(work).resolve()
    archive_root = inside(work / "archives", work)
    probe = probe or game_processes
    initial = plan(work, probe)
    require(initial["canArchive"], "Game is running or queues are already empty; archive refused")
    with coordination_locks(work):
        require(plan(work, probe) == initial, "Queue/process plan changed before acquiring locks")
        directory = archive_root / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ-") + uuid.uuid4().hex[:8])
        directory.mkdir(parents=True)
        manifest = directory / "archive-manifest.json"
        result_path = directory / "result.json"
        result = {"schema": SCHEMA, "operation": "archive", "ok": False, "status": "compressing",
                  "manifest": str(manifest), "record": str(result_path), "clearAttempted": [],
                  "cleared": [], "archived": False,
                  "queuesCleared": False}
        write_json(result_path, result)
        try:
            sources = {}
            for name, expected in initial["sources"].items():
                sources[name] = dict(expected)
                if expected["exists"]:
                    sources[name].update(compressed_copy(work / name, directory / (name + ".gz"), expected))
            write_json(manifest, {"schema": SCHEMA, "work": str(work), "sources": sources,
                                  "createdUtc": datetime.now(timezone.utc).isoformat()})
            verify(work, manifest)
            result.update(status="verified", archived=True)
            write_json(result_path, result)
            with ExitStack() as stack:
                opened = {}
                for name, expected in initial["sources"].items():
                    path = work / name
                    if expected["exists"]:
                        source = stack.enter_context(exclusive_source(path))
                        require(stamp(os.fstat(source.fileno())) == expected["stat"], "Queue identity changed: " + name)
                        digest, size = stream_digest(source)
                        require(digest == expected["sha256"] and size == expected["size"], "Queue bytes changed: " + name)
                        require(stamp(safe_source(path)) == expected["stat"], "Queue path changed: " + name)
                        opened[name] = source
                    else:
                        require(not path.exists() and not path.is_symlink(), "Absent queue appeared: " + name)
                require(not probe_stopped(probe), "Game appeared before queue clearing; sources retained")
                # Absent paths have no handle to lock. Recheck after the process
                # probe as well, before clearing any existing queue.
                for name, expected in initial["sources"].items():
                    if not expected["exists"]:
                        require(not (work / name).exists() and not (work / name).is_symlink(),
                                "Absent queue appeared: " + name)
                result["status"] = "clearing"
                write_json(result_path, result)
                for name, source in opened.items():
                    require(not probe_stopped(probe), "Game appeared during queue clearing; remaining sources retained")
                    result["clearAttempted"].append(name)
                    write_json(result_path, result)  # Durable intent precedes mutation.
                    source.seek(0)
                    source.truncate(0)
                    result["cleared"].append(name)  # A later fsync can fail after truncate took effect.
                    source.flush()
                    os.fsync(source.fileno())
                    write_json(result_path, result)
            result.update(status="completed", ok=True, queuesCleared=True)
        except Exception as exc:
            result.update(status="partial" if result["cleared"] else (
                              "clear_unknown" if result["clearAttempted"] else "sources_retained"),
                          error=type(exc).__name__ + ": " + str(exc))
        write_json(result_path, result)
        return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    subs = parser.add_subparsers(dest="operation")
    subs.add_parser("plan", help="read-only queue sizes/hashes and game status")
    subs.add_parser("archive", help="explicitly compress, verify, then clear stopped-game queues")
    sub = subs.add_parser("verify", help="read-only archive integrity verification")
    sub.add_argument("--record", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        result = verify(WORK, args.record) if args.operation == "verify" else (
            archive(WORK) if args.operation == "archive" else plan(WORK))
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0 if result.get("ok", True) else 1
    except (OSError, ValueError, RuntimeError) as exc:
        print(json.dumps({"ok": False, "status": "refused", "error": str(exc)}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
