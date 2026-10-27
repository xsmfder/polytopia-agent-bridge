"""Packaging rejects source/version drift and dependency contamination; no native execution."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

import package_polymod as package


class PolyModPackageTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1])
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.write('VERSION', '0.4.0\n')
        self.write('LICENSE', 'Owned fixture license notice\n')
        self.write('src/BridgeVersion.cs', 'const string Version = "0.4.0"; const string AssemblyVersion = "0.4.0.0";')
        self.write('polymod/manifest.json', json.dumps({'id': 'agent_bridge', 'name': 'Bridge', 'version': '0.4.0',
                                                     'authors': ['xsmfder'], 'client': True}))
        inputs = {name: package.sha(self.root/name) for name in ('VERSION','LICENSE','src/BridgeVersion.cs','polymod/manifest.json')}
        self.write('build/inputs/src/BridgeVersion.cs', (self.root/'src/BridgeVersion.cs').read_text())
        self.write('build/'+package.DLL_NAME, 'owned code fixture, not a real native build')
        self.build = {'ok': True, 'targetFramework': 'net6.0', 'symbols': ['BRIDGE_POLYMOD'],
            'sources': [{'path': 'src/BridgeVersion.cs', 'sha256': inputs['src/BridgeVersion.cs']}],
            'references': [], 'candidate': str(self.root/'build'/package.DLL_NAME),
            'dllSha256': package.sha(self.root/'build'/package.DLL_NAME)}
        self.proof = {'ok': True, 'scope': {'all': True}, 'steps': [{'exitCode': 0}], 'inputSha256': inputs,
                      'changedSnapshotFiles': [], 'changedWorkingFiles': [], 'addedWorkingFiles': []}
        self.sync()

    def write(self, name, text):
        p=self.root/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_text(text,encoding='utf-8')

    def sync(self):
        self.write('build/build.json',json.dumps(self.build))
        self.write('verification.json',json.dumps(self.proof))

    def pack(self, name='out'):
        return package.package(self.root,self.root/'build/build.json',self.root/'verification.json',self.root/name)

    def test_archive_is_reproducible_and_root_contains_only_owned_payload(self):
        self.write('GameAssembly.dll','private game')
        self.write('BepInEx/core/loader.dll','external loader')
        self.write('out.jsonl','private queue')
        one,two=self.pack('one'),self.pack('two')
        self.assertEqual(one['sha256'],two['sha256'])
        self.assertEqual(one['tag'],'v0.4.0')
        with zipfile.ZipFile(one['package']) as z:
            self.assertEqual(set(z.namelist()),{'manifest.json',package.DLL_NAME,'LICENSE'})
            self.assertEqual(z.read('LICENSE'),(self.root/'LICENSE').read_bytes())
            self.assertEqual(json.loads(z.read('manifest.json'))['version'],'0.4.0')

    def test_mismatched_manifest_and_unsupported_prerelease_suffix_fail(self):
        for value in ['0.3.1','0.4.0-beta.1','v0.4.0']:
            m=json.loads((self.root/'polymod/manifest.json').read_text());m['version']=value
            self.write('polymod/manifest.json',json.dumps(m))
            with self.assertRaises(ValueError):self.pack()
        self.assertFalse((self.root/'out').exists())

    def test_whole_input_closure_and_snapshot_must_match(self):
        for name in ('VERSION','LICENSE','src/BridgeVersion.cs','polymod/manifest.json','build/inputs/src/BridgeVersion.cs'):
            p=self.root/name;before=p.read_bytes();p.write_bytes(before+b' ')
            with self.subTest(name=name),self.assertRaises(ValueError):self.pack()
            p.write_bytes(before)
        self.write('src/New.cs','new unverified source')
        with self.assertRaises(ValueError):self.pack()

    def test_failed_partial_or_drifted_regression_never_packages(self):
        original=copy.deepcopy(self.proof)
        for change in [{'ok':False},{'scope':{'all':False}},{'steps':[{'exitCode':1}]},
                       {'steps':[]},{'changedWorkingFiles':['sdk.py']}]:
            self.proof={**original,**change};self.sync()
            with self.subTest(change=change),self.assertRaises(ValueError):self.pack()
        self.assertFalse((self.root/'out').exists())

    def test_changed_build_binary_or_reference_is_rejected(self):
        self.write('build/'+package.DLL_NAME,'changed')
        with self.assertRaises(ValueError):self.pack()
        self.write('build/'+package.DLL_NAME,'owned code fixture, not a real native build')
        self.write('external-reference.dll','original')
        self.build['references']=[{'path':str(self.root/'external-reference.dll'),'sha256':package.sha(self.root/'external-reference.dll')}]
        self.sync();self.write('external-reference.dll','changed')
        with self.assertRaises(ValueError):self.pack()

    def test_existing_output_and_tampered_container_fail(self):
        report=self.pack()
        with self.assertRaises(ValueError):self.pack()
        with zipfile.ZipFile(report['package'],'a') as z:z.writestr('GameAssembly.dll','not allowed')
        with self.assertRaises(ValueError):package.verify(self.root,self.root/'build/build.json',self.root/'verification.json',self.root/'out')


if __name__=='__main__':unittest.main()
