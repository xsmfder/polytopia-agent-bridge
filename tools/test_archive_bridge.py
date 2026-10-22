"""Archive tests use only temporary fake workspaces; no real queue is cleared."""
import contextlib
import gzip
import io
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import archive_bridge as archiver


class ArchiveTests(unittest.TestCase):
    def setUp(self):
        writable = Path(__file__).resolve().parents[1]
        self.temp = tempfile.TemporaryDirectory(dir=writable)
        assert Path(self.temp.name).resolve().is_relative_to(writable)
        self.addCleanup(self.temp.cleanup)
        self.work = Path(self.temp.name) / "game" / ".offline-work" / "agent-bridge"
        self.work.mkdir(parents=True)
        self.original = {"cmd.jsonl": b'\xef\xbb\xbf{"id":1}\r\npartial',
                         "out.jsonl": b'\x00\xff\x81' + b'{"ok":true}\n' * 500}
        for name, data in self.original.items():
            (self.work / name).write_bytes(data)
        self.stopped = lambda: []

    def run_archive(self, **kwargs):
        return archiver.archive(self.work, **({"probe": self.stopped} | kwargs))

    def originals_unchanged(self):
        for name, data in self.original.items():
            self.assertEqual((self.work / name).read_bytes(), data)

    def evidence(self):
        return next((self.work / "archives").glob("*/archive-manifest.json"))

    def test_default_plan_is_entirely_read_only(self):
        before = {str(p.relative_to(self.work)): p.read_bytes() for p in self.work.rglob("*") if p.is_file()}
        with patch.object(archiver, "WORK", self.work), patch.object(archiver, "game_processes", self.stopped), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(archiver.main([]), 0)
        result = json.loads(output.getvalue())
        self.assertEqual(result["operation"], "plan")
        self.assertTrue(result["canArchive"])
        self.assertEqual(before, {str(p.relative_to(self.work)): p.read_bytes()
                                  for p in self.work.rglob("*") if p.is_file()})
        self.assertFalse((self.work / "deployments").exists())
        self.assertFalse((self.work / "launches").exists())
        self.assertFalse((self.work / "archives").exists())

    def test_archive_preserves_exact_bytes_and_clears_only_fixed_queues(self):
        untouched = {"controlled-seat.json": b'{"playerId":2}', "bridge-ready.json": b'{}',
                     "session.json": b'sdk session', "repairs/historical/out.jsonl": b'historical',
                     "save.dat": b'fake save'}
        for name, data in untouched.items():
            path = self.work / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        result = self.run_archive()
        self.assertTrue(result["ok"])
        self.assertEqual(result["status"], "completed")
        self.assertEqual(result["cleared"], list(archiver.QUEUES))
        for name, data in self.original.items():
            self.assertEqual((self.work / name).read_bytes(), b"")
            with gzip.open(Path(result["manifest"]).parent / (name + ".gz"), "rb") as reader:
                self.assertEqual(reader.read(), data)
        for name, data in untouched.items():
            self.assertEqual((self.work / name).read_bytes(), data)
        self.assertTrue(archiver.verify(self.work, result["manifest"])["ok"])
        self.assertFalse((self.work / "deployments" / ".deployment.lock").exists())

    def test_one_missing_queue_is_not_created(self):
        (self.work / "out.jsonl").unlink()
        result = self.run_archive()
        self.assertTrue(result["ok"])
        self.assertEqual(result["cleared"], ["cmd.jsonl"])
        self.assertFalse((self.work / "out.jsonl").exists())
        proof = json.loads(Path(result["manifest"]).read_text())
        self.assertEqual(proof["sources"]["out.jsonl"], {"exists": False})

    def test_empty_queues_refuse_without_archive(self):
        for name in archiver.QUEUES:
            (self.work / name).write_bytes(b"")
        self.assertFalse(archiver.plan(self.work, self.stopped)["canArchive"])
        with self.assertRaisesRegex(ValueError, "already empty"):
            self.run_archive()
        self.assertFalse((self.work / "archives").exists())

    def test_running_game_refuses_all_writes(self):
        self.assertFalse(archiver.plan(self.work, lambda: [123])["canArchive"])
        with self.assertRaisesRegex(ValueError, "running"):
            self.run_archive(probe=lambda: [123])
        self.originals_unchanged()
        self.assertFalse((self.work / "deployments").exists())

    def test_process_query_failure_preserves_sources(self):
        def failed():
            raise OSError("probe failed")
        with self.assertRaisesRegex(OSError, "probe failed"):
            self.run_archive(probe=failed)
        self.originals_unchanged()

    def test_invalid_process_query_refuses(self):
        for value in (None, [True], [0], ["5"]):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "process query"):
                self.run_archive(probe=lambda: value)
        self.originals_unchanged()

    def test_deploy_lock_conflict_refuses(self):
        lock = self.work / "deployments" / ".deployment.lock"
        lock.parent.mkdir()
        lock.write_bytes(b"owned")
        with self.assertRaises(FileExistsError):
            self.run_archive()
        self.assertEqual(lock.read_bytes(), b"owned")
        self.originals_unchanged()

    def test_startup_lock_conflict_releases_deploy_lock(self):
        with contextlib.closing(archiver.SessionStore(self.work / "launches" / "startup-lock.json")):
            with self.assertRaisesRegex(RuntimeError, "another process"):
                self.run_archive()
        self.assertFalse((self.work / "deployments" / ".deployment.lock").exists())
        self.originals_unchanged()

    def test_queue_changes_before_locks_refuse_without_archive(self):
        calls = 0
        def probe():
            nonlocal calls
            calls += 1
            if calls == 2:
                (self.work / "out.jsonl").write_bytes(b"changed by another writer")
            return []
        with self.assertRaisesRegex(ValueError, "plan changed"):
            self.run_archive(probe=probe)
        self.assertEqual((self.work / "out.jsonl").read_bytes(), b"changed by another writer")
        self.assertEqual((self.work / "cmd.jsonl").read_bytes(), self.original["cmd.jsonl"])
        self.assertFalse((self.work / "archives").exists())

    def test_mutation_after_compression_keeps_new_source_and_verified_evidence(self):
        original_verify = archiver.verify
        def verify(*args):
            proof = original_verify(*args)
            (self.work / "out.jsonl").write_bytes(b"new content")
            return proof
        with patch.object(archiver, "verify", side_effect=verify):
            result = self.run_archive()
        self.assertFalse(result["ok"])
        self.assertTrue(result["archived"])
        self.assertEqual(result["cleared"], [])
        self.assertEqual((self.work / "out.jsonl").read_bytes(), b"new content")
        self.assertEqual((self.work / "cmd.jsonl").read_bytes(), self.original["cmd.jsonl"])
        self.assertTrue(original_verify(self.work, self.evidence())["ok"])

    def test_equal_bytes_different_identity_still_refuse_clear(self):
        original_verify = archiver.verify
        def verify(*args):
            proof = original_verify(*args)
            replacement = self.work / "replacement"
            replacement.write_bytes(self.original["out.jsonl"])
            os.replace(replacement, self.work / "out.jsonl")
            return proof
        with patch.object(archiver, "verify", side_effect=verify):
            result = self.run_archive()
        self.assertFalse(result["ok"])
        self.assertEqual(result["cleared"], [])
        self.originals_unchanged()

    def test_absent_source_appearing_refuses_all_clearing(self):
        (self.work / "out.jsonl").unlink()
        original_verify = archiver.verify
        def verify(*args):
            proof = original_verify(*args)
            (self.work / "out.jsonl").write_bytes(b"new queue")
            return proof
        with patch.object(archiver, "verify", side_effect=verify):
            result = self.run_archive()
        self.assertFalse(result["ok"])
        self.assertEqual(result["cleared"], [])
        self.assertEqual((self.work / "out.jsonl").read_bytes(), b"new queue")

    def test_game_appears_after_verification_sources_retained(self):
        calls = 0
        def probe():
            nonlocal calls
            calls += 1
            return [500] if calls >= 3 else []
        result = self.run_archive(probe=probe)
        self.assertFalse(result["ok"])
        self.assertTrue(result["archived"])
        self.assertEqual(result["cleared"], [])
        self.originals_unchanged()

    def test_partial_clear_stops_when_game_appears(self):
        calls = 0
        def probe():
            nonlocal calls
            calls += 1
            return [500] if calls >= 5 else []
        result = self.run_archive(probe=probe)
        self.assertFalse(result["ok"])
        self.assertEqual(result["status"], "partial")
        self.assertEqual(result["cleared"], ["cmd.jsonl"])
        self.assertEqual((self.work / "cmd.jsonl").read_bytes(), b"")
        self.assertEqual((self.work / "out.jsonl").read_bytes(), self.original["out.jsonl"])
        self.assertTrue(archiver.verify(self.work, result["manifest"])["ok"])

    def test_failed_compression_does_not_clear_either_source(self):
        with patch.object(archiver, "compressed_copy", side_effect=OSError("disk full")):
            result = self.run_archive()
        self.assertFalse(result["ok"])
        self.assertFalse(result["archived"])
        self.assertEqual(result["cleared"], [])
        self.originals_unchanged()

    def test_failed_archive_verification_preserves_sources(self):
        with patch.object(archiver, "verify", side_effect=ValueError("gzip damaged")):
            result = self.run_archive()
        self.assertFalse(result["ok"])
        self.assertFalse(result["archived"])
        self.originals_unchanged()

    def test_source_fsync_failure_reports_real_clear_and_preserves_evidence(self):
        before = (self.work / "cmd.jsonl").stat()
        original = os.fsync
        def fsync(descriptor):
            current = os.fstat(descriptor)
            if (current.st_dev, current.st_ino) == (before.st_dev, before.st_ino):
                raise OSError("source fsync failed after truncate")
            return original(descriptor)
        with patch.object(archiver.os, "fsync", side_effect=fsync):
            result = self.run_archive()
        self.assertFalse(result["ok"])
        self.assertEqual(result["status"], "partial")
        self.assertEqual(result["clearAttempted"], ["cmd.jsonl"])
        self.assertEqual(result["cleared"], ["cmd.jsonl"])
        self.assertEqual((self.work / "cmd.jsonl").read_bytes(), b"")
        self.assertEqual((self.work / "out.jsonl").read_bytes(), self.original["out.jsonl"])
        self.assertTrue(archiver.verify(self.work, result["manifest"])["ok"])
        disk_result = json.loads(Path(result["record"]).read_text())
        self.assertEqual(disk_result["status"], "partial")
        self.assertEqual(disk_result["clearAttempted"], ["cmd.jsonl"])
        self.assertEqual(disk_result["cleared"], ["cmd.jsonl"])

    def test_interrupt_after_verified_record_leaves_archive_and_sources(self):
        calls = 0
        def probe():
            nonlocal calls
            calls += 1
            if calls == 3:
                raise KeyboardInterrupt()
            return []
        with self.assertRaises(KeyboardInterrupt):
            self.run_archive(probe=probe)
        self.originals_unchanged()
        self.assertTrue(archiver.verify(self.work, self.evidence())["ok"])
        self.assertFalse((self.work / "deployments" / ".deployment.lock").exists())

    def test_corrupted_gzip_is_rejected(self):
        result = self.run_archive()
        compressed = Path(result["manifest"]).parent / "out.jsonl.gz"
        compressed.write_bytes(compressed.read_bytes()[:-4])
        with self.assertRaisesRegex(ValueError, "archive changed"):
            archiver.verify(self.work, result["manifest"])

    def test_corrupt_original_hash_is_rejected(self):
        result = self.run_archive()
        path = Path(result["manifest"])
        data = json.loads(path.read_text())
        data["sources"]["out.jsonl"]["sha256"] = "0" * 64
        path.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, "Original bytes"):
            archiver.verify(self.work, path)

    def test_verify_refuses_wrong_or_escaping_record(self):
        result = self.run_archive()
        with self.assertRaisesRegex(ValueError, "expected directory"):
            archiver.verify(self.work, self.work / "archive-manifest.json")
        with self.assertRaisesRegex(ValueError, "Expected archive-manifest"):
            archiver.verify(self.work, result["record"])

    def test_verify_is_read_only_and_never_probes_processes(self):
        result = self.run_archive()
        before = {str(p.relative_to(self.work)): p.read_bytes() for p in self.work.rglob("*") if p.is_file()}
        with patch.object(archiver, "game_processes", side_effect=AssertionError("no process query")):
            self.assertTrue(archiver.verify(self.work, result["manifest"])["ok"])
        self.assertEqual(before, {str(p.relative_to(self.work)): p.read_bytes()
                                  for p in self.work.rglob("*") if p.is_file()})

    def test_hard_link_source_is_rejected(self):
        source = self.work / "out.jsonl"
        try:
            os.link(source, self.work / "another-name")
        except PermissionError:
            # The desktop sandbox denies link creation. Inject the real stat
            # API's link count so the production rejection still runs.
            original = Path.lstat
            def lstat(path):
                info = original(path)
                if path != source:
                    return info
                names = ("st_mode", "st_file_attributes", "st_dev", "st_ino", "st_size", "st_mtime_ns")
                return SimpleNamespace(**{name: getattr(info, name, 0) for name in names}, st_nlink=2)
            with patch.object(Path, "lstat", lstat), self.assertRaisesRegex(ValueError, "hard links"):
                self.run_archive()
        else:
            with self.assertRaisesRegex(ValueError, "hard links"):
                self.run_archive()
        self.originals_unchanged()

    def test_link_source_is_rejected_even_when_target_is_inside_work(self):
        path = self.work / "out.jsonl"
        path.unlink()
        target = self.work / "target"
        target.write_bytes(b"untouched")
        try:
            path.symlink_to(target)
        except OSError:
            path.write_bytes(b"plain fixture standing in for link")
            original = Path.is_symlink
            with patch.object(Path, "is_symlink", lambda p: p == path or original(p)), \
                    self.assertRaisesRegex(ValueError, "plain file"):
                self.run_archive()
        else:
            with self.assertRaisesRegex(ValueError, "link"):
                self.run_archive()
        self.assertEqual(target.read_bytes(), b"untouched")

    def test_same_stat_different_bytes_refuses_clear(self):
        original_verify = archiver.verify
        source = self.work / "out.jsonl"
        before = source.stat()
        def verify(*args):
            proof = original_verify(*args)
            source.write_bytes(b"X" * len(self.original["out.jsonl"]))
            os.utime(source, ns=(before.st_atime_ns, before.st_mtime_ns))
            return proof
        with patch.object(archiver, "verify", side_effect=verify):
            result = self.run_archive()
        self.assertFalse(result["ok"])
        self.assertEqual(result["cleared"], [])
        self.assertIn("bytes changed", result["error"])

    def test_absent_source_appearing_during_final_probe_prevents_clear(self):
        (self.work / "out.jsonl").unlink()
        calls = 0
        def probe():
            nonlocal calls
            calls += 1
            if calls == 3:
                (self.work / "out.jsonl").write_bytes(b"late new queue")
            return []
        result = self.run_archive(probe=probe)
        self.assertFalse(result["ok"])
        self.assertEqual(result["cleared"], [])
        self.assertEqual((self.work / "cmd.jsonl").read_bytes(), self.original["cmd.jsonl"])

    @unittest.skipUnless(os.name == "nt", "Windows share-mode behavior")
    def test_windows_exclusive_handles_deny_external_writer(self):
        calls = 0
        def probe():
            nonlocal calls
            calls += 1
            if calls == 3:
                for name in archiver.QUEUES:
                    with self.assertRaises(PermissionError):
                        (self.work / name).write_bytes(b"external write must be denied")
            return []
        result = self.run_archive(probe=probe)
        self.assertTrue(result["ok"])

    def test_cli_archive_failure_is_nonzero(self):
        with patch.object(archiver, "WORK", self.work), patch.object(archiver, "game_processes", lambda: [45]), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(archiver.main(["archive"]), 1)
        self.assertEqual(json.loads(output.getvalue())["status"], "refused")

    def test_escape_in_any_output_or_lock_directory_refuses_before_writes(self):
        original = Path.resolve
        for name in ("archives", "deployments", "launches"):
            with self.subTest(name=name):
                def resolve(path, *args, **kwargs):
                    return self.work.parent / "outside" if path == self.work / name else original(path, *args, **kwargs)
                with patch.object(Path, "resolve", resolve), self.assertRaisesRegex(ValueError, "expected directory"):
                    self.run_archive()
                self.assertFalse((self.work / "archives").exists())
                self.assertFalse((self.work / "deployments").exists())
                self.assertFalse((self.work / "launches").exists())
                self.originals_unchanged()

    def test_verify_rejects_escaping_archive_root_before_reading(self):
        result = self.run_archive()
        original = Path.resolve
        def resolve(path, *args, **kwargs):
            return self.work.parent / "outside" if path == self.work / "archives" else original(path, *args, **kwargs)
        with patch.object(Path, "resolve", resolve), self.assertRaisesRegex(ValueError, "expected directory"):
            archiver.verify(self.work, result["manifest"])


if __name__ == "__main__":
    unittest.main()
