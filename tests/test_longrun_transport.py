"""Long backlog regression tests; fake queue files, no game or desktop imports."""
import ast
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

WORK = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(WORK))
import bridge_transport as transport


class LongrunTransportTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.work = Path(self.tmp.name)
        (self.work / transport.READY_NAME).write_text(
            json.dumps({'pid': 31, 'started': 'fixture'}), encoding='utf-8')
        self.bridge = transport.Bridge(work=self.work, liveness=lambda: True)
        self.addCleanup(self.bridge.close)
        self.out = self.work / transport.OUT_NAME

    def append(self, *frames):
        with self.out.open('ab') as fh:
            for frame in frames:
                fh.write((json.dumps(frame, ensure_ascii=False) + '\n').encode('utf-8'))

    def test_backlog_reads_one_bounded_batch_then_catches_up_without_loss(self):
        expected = [{'reqId': n + 1, 'text': 'payload'} for n in range(100)]
        self.append(*expected)
        with patch.object(transport, 'READ_CHUNK_BYTES', 128):
            first = self.bridge._read_new()
            self.assertEqual(self.bridge._offset, 128)
            self.assertTrue(self.bridge._read_has_more)
            seen = first
            while self.bridge._read_has_more:
                seen += self.bridge._read_new()
        self.assertEqual(seen, expected)
        self.assertEqual(self.bridge.bad_frames, 0)
        self.assertEqual(self.bridge._pending, b'')
        self.assertEqual(self.bridge._offset, self.out.stat().st_size)

    def test_utf8_character_across_batch_boundary_stays_intact(self):
        frame = {'reqId': 7, 'text': '\u5b66\u4e60\u4e2d'}
        self.append(frame)
        raw = self.out.read_bytes()
        chunk = raw.index('\u5b66'.encode('utf-8')) + 1
        with patch.object(transport, 'READ_CHUNK_BYTES', chunk):
            self.assertEqual(self.bridge._read_new(), [])
            seen = []
            while self.bridge._read_has_more:
                seen += self.bridge._read_new()
        self.assertEqual(seen, [frame])
        self.assertEqual(self.bridge.bad_frames, 0)

    def test_large_single_frame_is_preserved_not_truncated_by_batch_limit(self):
        frame = {'reqId': 8, 'snapshot': 'x' * 600}
        self.append(frame)
        with patch.object(transport, 'READ_CHUNK_BYTES', 64):
            seen = []
            while True:
                seen += self.bridge._read_new()
                if not self.bridge._read_has_more:
                    break
        self.assertEqual(seen, [frame])
        self.assertEqual(self.bridge.bad_frames, 0)

    def test_wait_catches_up_without_sleeping_between_backlog_batches(self):
        self.append(*[{'reqId': n + 1, 'text': 'x' * 15} for n in range(99)])
        expected = {'reqId': 999, 'ok': True}
        self.append(expected)
        with patch.object(transport, 'READ_CHUNK_BYTES', 128), \
                patch.object(transport.time, 'sleep', side_effect=AssertionError('backlog must be drained')):
            actual = self.bridge.wait_for(999, timeout=2)
        self.assertEqual(actual, expected)
        # Responses still use the existing bounded parking policy.
        self.assertLessEqual(self.bridge._deferred_count, transport.MAX_DEFERRED_FRAMES)
        self.assertLessEqual(len(self.bridge._deferred), transport.MAX_DEFERRED_REQ_IDS)
        self.assertGreater(self.bridge.deferred_dropped, 0)
        self.assertEqual(self.bridge.wait_for(99, timeout=.1)['reqId'], 99)

    def test_truncation_drops_partial_old_line_and_parked_replies(self):
        self.bridge._park({'reqId': 92, 'old': True}, 92)
        self.append({'reqId': 2, 'text': 'x' * 600})
        with patch.object(transport, 'READ_CHUNK_BYTES', 128):
            self.assertEqual(self.bridge._read_new(), [])
            self.assertTrue(self.bridge._read_has_more)
            fresh = {'reqId': 3, 'new': True}
            self.out.write_bytes((json.dumps(fresh) + '\n').encode('utf-8'))
            self.assertEqual(self.bridge._read_new(), [fresh])
        self.assertEqual(self.bridge._deferred_count, 0)
        self.assertEqual(self.bridge._pending, b'')
        self.assertFalse(self.bridge._read_has_more)

    def test_client_initialization_skips_existing_long_history(self):
        self.append(*[{'reqId': n + 1, 'old': 'x' * 500} for n in range(100)])
        b = transport.Bridge(work=self.work, liveness=lambda: True)
        self.addCleanup(b.close)
        old_size = self.out.stat().st_size
        self.assertEqual(b._offset, old_size)
        self.assertEqual(len(b._read_tail), transport.FINGERPRINT_BYTES)
        fresh = {'reqId': 9000, 'fresh': True}
        self.append(fresh)
        self.assertEqual(b._read_new(), [fresh])
        self.assertEqual(b._read_new(), [])

    def test_wait_preserves_legacy_read_new_audit_hook(self):
        seen = []
        read_new = self.bridge._read_new
        def audited_read():
            frames = read_new()
            seen.extend(frames)
            return frames
        self.bridge._read_new = audited_read
        first = {'reqId': 1, 'ok': True}
        other = {'reqId': 2, 'ok': True}
        self.append(first, other)
        self.assertEqual(self.bridge.wait_for(1, timeout=1), first)
        self.assertEqual(seen, [first, other])
        self.assertEqual(self.bridge.wait_for(2, timeout=1), other)
        self.assertEqual(seen, [first, other])

    def test_drain_preserves_read_new_audit_hook_without_duplicate_consumption(self):
        seen = []
        read_new = self.bridge._read_new
        def audited_read():
            frames = read_new()
            seen.extend(frames)
            return frames
        self.bridge._read_new = audited_read
        fresh = {'reqId': 4, 'ok': True}
        self.append(fresh)
        self.assertEqual(self.bridge.drain(seconds=.02), [fresh])
        self.assertEqual(seen, [fresh])
        self.assertEqual(self.bridge.drain(seconds=.02), [])
        self.assertEqual(seen, [fresh])

    def test_legacy_client_bridge_export_uses_same_transport(self):
        # Inspect and execute only the compatibility import, never the desktop module.
        tree = ast.parse((WORK / 'client.py').read_text(encoding='utf-8'))
        self.assertFalse(any(isinstance(n, ast.ClassDef) and n.name == 'Bridge' for n in tree.body))
        imports = [n for n in tree.body if isinstance(n, ast.ImportFrom)
                   and n.module == 'bridge_transport' and any(a.name == 'Bridge' for a in n.names)]
        self.assertEqual(len(imports), 1)
        scope = {}
        exec(compile(ast.Module(body=imports, type_ignores=[]), 'client-bridge-export', 'exec'), scope)
        self.assertIs(scope['Bridge'], transport.Bridge)


if __name__ == '__main__':
    unittest.main()
