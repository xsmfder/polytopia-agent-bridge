"""Source staging must exclude live artifacts and preserve existing review trees."""
import json
from pathlib import Path
import tempfile
import unittest

import prepare_release as release
import verify_bridge as verification

class SourceReviewTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1])
        self.addCleanup(self.temp.cleanup)
        self.work = Path(self.temp.name)
        for name in (*verification.SDK_FILES, *verification.LEGACY_ENTRY_FILES,
                     'src/Test.cs', 'tests/FixtureTests.cs', 'tests/test_sample.py',
                     'tests/fixtures/data.json', 'tools/verify_bridge.py',
                     str(verification.CAMPAIGN / 'test_settled_unknown.py'),
                     str(verification.CAMPAIGN / 'attacker-disappeared-ticket34.json')):
            self.put(name, '{}' if name.endswith('.json') else '')
        for name in release.TEMPLATES:
            self.put('public/' + name, 'from bridge_transport import Bridge, wlog\n' if name == 'client.py' else 'draft\n')

    def put(self, name, value):
        path = self.work / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(value, encoding='utf-8')
        return path

    def test_live_artifacts_and_binaries_never_enter_source_tree(self):
        for name in ('out.jsonl', 'cmd.jsonl', 'controlled-seat.json', 'results/private.json',
                     'repairs/private.json', 'GameAssembly.dll', 'Polytopia.exe', 'src/leaked.dll'):
            self.put(name, 'private')
        destination = self.work / 'releases/review'
        result = release.stage(self.work, destination)
        self.assertTrue(result['ok'])
        self.assertFalse(result['published'])
        self.assertFalse(result['releaseReady'])
        for name in ('out.jsonl', 'cmd.jsonl', 'controlled-seat.json', 'results', 'repairs', 'GameAssembly.dll', 'Polytopia.exe', 'src/leaked.dll'):
            self.assertFalse((destination / name).exists(), name)
        self.assertEqual((destination / 'client.py').read_text(), 'from bridge_transport import Bridge, wlog\n')

    def test_scan_does_not_print_secret_values_and_refuses_staging(self):
        secret = 'ghp_' + 'a' * 36
        self.put('sdk.py', 'credential = "' + secret + '"\n')
        report = release.inventory(self.work)
        self.assertFalse(report['ok'])
        self.assertNotIn(secret, json.dumps(report))
        destination = self.work / 'releases/rejected'
        with self.assertRaises(ValueError):
            release.stage(self.work, destination)
        self.assertFalse(destination.exists())

    def test_existing_or_external_output_is_never_overwritten(self):
        destination = self.work / 'releases/review'
        release.stage(self.work, destination)
        before = {p.relative_to(destination).as_posix(): p.read_bytes() for p in destination.rglob('*') if p.is_file()}
        for output in (destination, self.work / 'outside', self.work.parent / 'external-review', self.work / 'releases'):
            with self.subTest(output=output), self.assertRaises(ValueError):
                release.stage(self.work, output)
        self.assertEqual(before, {p.relative_to(destination).as_posix(): p.read_bytes() for p in destination.rglob('*') if p.is_file()})

    def test_missing_template_or_escaping_source_is_refused(self):
        with self.assertRaises(ValueError):
            release.checked(self.work.parent / 'outside', self.work)
        (self.work / 'public/README.md').unlink()
        with self.assertRaises(ValueError):
            release.inventory(self.work)

    def test_every_public_document_requires_its_english_counterpart(self):
        report = release.inventory(self.work)
        selected = {row['path'] for row in report['files']}
        for name in release.DOCUMENTS:
            self.assertIn(name, selected)
            self.assertIn(name[:-3] + '.en.md', selected)
        (self.work / 'public/INSTALL.en.md').unlink()
        with self.assertRaises(ValueError):
            release.inventory(self.work)

    def test_manifest_uses_relative_paths_and_exact_content_hashes(self):
        destination = self.work / 'releases/review'
        report = release.stage(self.work, destination)
        for row in report['files']:
            self.assertFalse(Path(row['path']).is_absolute())
            self.assertEqual(release.hashlib.sha256((destination / row['path']).read_bytes()).hexdigest(), row['sha256'])
        self.assertFalse(report['licensePresent'])

    def test_documentation_cannot_depend_on_private_history(self):
        self.put('public/README.md', '[history](../repairs/private.json)\n')
        report = release.inventory(self.work)
        self.assertFalse(report['ok'])
        self.assertIn('unselected-doc-link', [row['rule'] for row in report['issues']])
        with self.assertRaises(ValueError):
            release.stage(self.work, self.work / 'releases/private-link')

    def test_selected_public_documentation_links_and_anchors_are_allowed(self):
        self.put('public/README.md', '[install](INSTALL.md#setup) [source](src/Test.cs) [upstream](https://example.com/)\n')
        self.assertTrue(release.inventory(self.work)['ok'])

if __name__ == '__main__':
    unittest.main()
