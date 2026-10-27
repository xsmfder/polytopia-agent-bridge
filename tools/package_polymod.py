"""Create/verify a deterministic PolyMod package from a verified same-source build.

No game/loader DLLs, generated interop, saves or runtime logs are packaged.
No upload, deployment, game launch or live request is performed.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import zipfile

sys.dont_write_bytecode = True
WORK = Path(__file__).resolve().parents[1]
DLL_NAME = 'PolytopiaAgentBridge.PolyMod.dll'


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def version(work):
    work = Path(work)
    value = (work/'VERSION').read_text(encoding='utf-8-sig').strip()
    if not re.fullmatch(r'(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)', value):
        raise ValueError('VERSION must be a three-part numeric PolyMod version')
    source = (work/'src/BridgeVersion.cs').read_text(encoding='utf-8-sig')
    if not re.search(r'const string Version\s*=\s*"'+re.escape(value)+r'"\s*;', source):
        raise ValueError('C# bridge version differs from VERSION')
    if not re.search(r'const string AssemblyVersion\s*=\s*"'+re.escape(value)+r'\.0"\s*;', source):
        raise ValueError('Assembly version differs from VERSION')
    manifest = read(work/'polymod/manifest.json')
    if manifest.get('version') != value:
        raise ValueError('PolyMod display version differs from VERSION')
    if not re.fullmatch(r'(?!polytopia$)[a-z_]+', manifest.get('id', '')):
        raise ValueError('Invalid PolyMod id')
    if not manifest.get('name') or not manifest.get('authors') or manifest.get('client') is not True:
        raise ValueError('Missing Bridge manifest metadata/client scope')
    if any(not isinstance(a, str) or not a for a in manifest['authors']):
        raise ValueError('Invalid manifest author')
    return value


def payload(work, build_path, verification_path):
    work = Path(work).resolve()
    value = version(work)
    build_path, verification_path = Path(build_path).resolve(), Path(verification_path).resolve()
    build, proof = read(build_path), read(verification_path)
    if (build.get('ok') is not True or build.get('targetFramework') != 'net6.0'
            or 'BRIDGE_POLYMOD' not in build.get('symbols', [])):
        raise ValueError('A successful native PolyMod net6 build is required')
    if proof.get('ok') is not True or proof.get('scope', {}).get('all') is not True:
        raise ValueError('A successful full offline regression is required')
    if any(proof.get(k) != [] for k in ('changedSnapshotFiles', 'changedWorkingFiles', 'addedWorkingFiles')):
        raise ValueError('Regression inputs drifted')
    if not proof.get('steps') or any(s.get('exitCode') != 0 for s in proof['steps']):
        raise ValueError('Regression has no steps or a failing step')
    expected = proof.get('inputSha256', {})
    actual = {p.relative_to(work).as_posix(): sha(p) for p in (work/'src').glob('*.cs')}
    built = {row['path']: row['sha256'] for row in build['sources']}
    if actual != built or len(built) != len(build['sources']) or not actual:
        raise ValueError('PolyMod build source set differs from working source')
    for name, digest in actual.items():
        if expected.get(name) != digest or sha(build_path.parent/'inputs'/name) != digest:
            raise ValueError('PolyMod source differs from regression/build snapshot: '+name)
    # Validate the entire input closure, not just the C# files.
    for name, digest in expected.items():
        relative = Path(name)
        if relative.is_absolute() or '..' in relative.parts or not (work/relative).is_file() or sha(work/relative) != digest:
            raise ValueError('Working input differs from full regression: '+name)
    for reference in build.get('references', []):
        if sha(reference['path']) != reference['sha256']:
            raise ValueError('Local build reference changed')
    dll = Path(build['candidate']).resolve()
    if dll.parent != build_path.parent or dll.name != DLL_NAME or sha(dll) != build['dllSha256']:
        raise ValueError('PolyMod binary differs from its build record')
    manifest = (work/'polymod/manifest.json').read_bytes()
    if expected.get('polymod/manifest.json') != hashlib.sha256(manifest).hexdigest():
        raise ValueError('Manifest was not covered by regression snapshot')
    license_bytes = (work/'LICENSE').read_bytes()
    if expected.get('LICENSE') != hashlib.sha256(license_bytes).hexdigest():
        raise ValueError('License was not covered by regression snapshot')
    return value, {'manifest.json': manifest, DLL_NAME: dll.read_bytes(), 'LICENSE': license_bytes}


def package(work, build, verification, out):
    value, files = payload(work, build, verification)
    out = Path(out).resolve()
    if out.exists():
        raise ValueError('Output exists; previous release evidence is preserved')
    out.mkdir(parents=True)
    target = out/f'agent_bridge-{value}.polymod'
    with zipfile.ZipFile(target, 'x', zipfile.ZIP_DEFLATED) as z:
        for name, data in sorted(files.items()):
            info = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            z.writestr(info, data)
    (out/(target.name+'.sha256')).write_text(sha(target)+'  '+target.name+'\n', encoding='utf-8')
    return verify(work, build, verification, out)


def verify(work, build, verification, directory):
    value, files = payload(work, build, verification)
    target = Path(directory)/f'agent_bridge-{value}.polymod'
    with zipfile.ZipFile(target) as z:
        if len(z.namelist()) != len(files) or set(z.namelist()) != set(files) or z.testzip():
            raise ValueError('Unexpected PolyMod package contents')
        if any(z.read(name) != data for name, data in files.items()):
            raise ValueError('PolyMod package bytes differ from verified build')
    digest = sha(target)
    if target.with_name(target.name+'.sha256').read_text(encoding='utf-8') != digest+'  '+target.name+'\n':
        raise ValueError('PolyMod checksum differs')
    return {'ok': True, 'version': value, 'tag': 'v'+value, 'package': str(target),
            'sha256': digest, 'files': sorted(files), 'published': False, 'deployed': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build', type=Path, required=True)
    parser.add_argument('--verification', type=Path, required=True)
    target = parser.add_mutually_exclusive_group(required=True)
    target.add_argument('--out', type=Path)
    target.add_argument('--verify-directory', type=Path)
    args = parser.parse_args()
    try:
        report = package(WORK, args.build, args.verification, args.out) if args.out else verify(WORK, args.build, args.verification, args.verify_directory)
        print(json.dumps(report))
        return 0
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        print(json.dumps({'ok': False, 'error': str(error)}))
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
