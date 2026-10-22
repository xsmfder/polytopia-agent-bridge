"""Beta artifacts must match frozen source bytes and never inherit local runtime files."""
import json
import unittest

import package_prerelease as package
import test_prepare_release


class PrereleaseTests(unittest.TestCase):
    def setUp(self):
        self.fixture = test_prepare_release.SourceReviewTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.work = self.fixture.work
        self.fixture.put('public/CHANGELOG.md', '# 中文\n')
        self.fixture.put('public/CHANGELOG.en.md', '# English\n')
        import prepare_release
        self.source = self.work / 'releases/frozen'
        prepare_release.stage(self.work, self.source)
        manifest = json.loads((self.source / 'release-manifest.json').read_text(encoding='utf-8'))
        manifest.update(version='0.1.0-beta.1', prerelease=True)
        (self.source / 'release-manifest.json').write_text(json.dumps(manifest), encoding='utf-8')

    def test_reproducible_exact_selection_and_download_verification(self):
        (self.source / 'out.jsonl').write_text('private', encoding='utf-8')
        first = package.package(self.source, 'v0.1.0-beta.1', self.source / 'dist/one')
        second = package.package(self.source, 'v0.1.0-beta.1', self.source / 'dist/two')
        self.assertEqual(first['sha256'], second['sha256'])
        self.assertTrue(package.verify(self.source, 'v0.1.0-beta.1', self.source / 'dist/one')['ok'])
        notes = (self.source / 'dist/one/release-notes.md').read_text(encoding='utf-8')
        self.assertTrue(all(name in notes for name in ('Codex', 'DSH', 'xsmfder', '中文', 'English')))

    def test_wrong_version_or_changed_source_refuses_before_output(self):
        destination = self.source / 'dist/no'
        for tag in ('v0.1.0-beta.2', 'v1.0.0', '../invalid'):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                package.package(self.source, tag, destination)
        (self.source / 'sdk.py').write_text('changed', encoding='utf-8')
        with self.assertRaises(ValueError):
            package.package(self.source, 'v0.1.0-beta.1', destination)
        self.assertFalse(destination.exists())

    def test_existing_output_and_corrupt_download_are_rejected(self):
        destination = self.source / 'dist/one'
        package.package(self.source, 'v0.1.0-beta.1', destination)
        with self.assertRaises(ValueError):
            package.package(self.source, 'v0.1.0-beta.1', destination)
        (destination / (package.NAME + '.zip')).write_bytes(b'corrupt')
        with self.assertRaises(ValueError):
            package.verify(self.source, 'v0.1.0-beta.1', destination)


if __name__ == '__main__':
    unittest.main()
