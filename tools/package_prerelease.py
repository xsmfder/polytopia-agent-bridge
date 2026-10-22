"""Build or verify a reproducible source-only beta archive from a frozen manifest."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import zipfile

sys.dont_write_bytecode = True
import prepare_release

WORK = Path(__file__).resolve().parents[1]
NAME = 'polytopia-agent-bridge-source'


def source_bytes(work, tag):
    work = Path(work).resolve()
    if not re.fullmatch(r'v\d+\.\d+\.\d+-beta\.[1-9]\d*', tag):
        raise ValueError('Expected an explicit beta version tag')
    manifest_bytes = (work / 'release-manifest.json').read_bytes()
    manifest = json.loads(manifest_bytes.decode('utf-8'))
    if manifest.get('version') != tag[1:] or manifest.get('prerelease') is not True:
        raise ValueError('Tag does not match the prepared beta manifest')
    report = prepare_release.inventory(work)
    if not report['ok'] or manifest['files'] != report['files']:
        raise ValueError('Source selection or content differs from the frozen manifest')
    files = {}
    for row in report['files']:
        raw = (work / row['path']).read_bytes()
        if hashlib.sha256(raw).hexdigest() != row['sha256']:
            raise ValueError('Source changed while packaging: ' + row['path'])
        files[row['path']] = raw
    files['release-manifest.json'] = manifest_bytes
    return files


def release_notes(files, tag):
    base = 'https://github.com/xsmfder/polytopia-agent-bridge'
    parts = [f'# {tag} — First public beta / 首个公开测试版\n',
             f'[中文安装说明]({base}/blob/{tag}/INSTALL.md) | '
             f'[English installation]({base}/blob/{tag}/INSTALL.en.md)\n',
             'Download `polytopia-agent-bridge-source.zip` and its `.sha256` file. '
             'This is a source distribution: install the official loader and build locally.\n',
             '下载同名源码ZIP及SHA256文件；本包为源码发行，需要自行安装官方加载器并在本机构建。\n']
    for name in ('CHANGELOG.md', 'CHANGELOG.en.md'):
        text = files[name].decode('utf-8')
        text = re.sub(r'\]\(([^)]+)\)', lambda m: '](' + (
            m[1] if '://' in m[1] else base + '/blob/' + tag + '/' + m[1]) + ')', text)
        parts.append(text)
    parts.append('## Contributors / 贡献\n\n'
                 '- **xsmfder**: project direction, information boundaries, assisted testing and acceptance / 项目设计、信息边界、人工配合测试与验收。\n'
                 '- **Codex (OpenAI)**: implementation, integration, regression and release preparation / 代码实现、整合维护、回归与发布准备。\n'
                 '- **DSH agent**: collaborative development and independent installation/live acceptance / 协作开发与独立安装、实机验收。\n')
    return '\n'.join(parts)


def package(work, tag, output):
    files = source_bytes(work, tag)
    output = Path(output).resolve()
    if not output.is_relative_to(Path(work).resolve()) or output.exists():
        raise ValueError('Output must be a new directory within the source workspace')
    output.mkdir(parents=True)
    archive = output / (NAME + '.zip')
    with zipfile.ZipFile(archive, 'x', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for name, raw in sorted(files.items()):
            info = zipfile.ZipInfo(NAME + '/' + name, (1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, raw, compresslevel=9)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    (output / (NAME + '.zip.sha256')).write_bytes((digest + '  ' + archive.name + '\n').encode('utf-8'))
    (output / 'release-notes.md').write_bytes(release_notes(files, tag).encode('utf-8'))
    return verify(work, tag, output)


def verify(work, tag, directory):
    files = source_bytes(work, tag)
    directory = Path(directory)
    archive = directory / (NAME + '.zip')
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    expected_sum = digest + '  ' + archive.name + '\n'
    if (directory / (NAME + '.zip.sha256')).read_bytes() != expected_sum.encode('utf-8'):
        raise ValueError('Archive checksum mismatch')
    with zipfile.ZipFile(archive) as z:
        names = [NAME + '/' + name for name in files]
        if len(z.namelist()) != len(names) or set(z.namelist()) != set(names) or z.testzip():
            raise ValueError('Archive selection differs from the source manifest')
        if any(z.read(NAME + '/' + name) != raw for name, raw in files.items()):
            raise ValueError('Archive content differs from source')
    return {'ok': True, 'version': tag[1:], 'prerelease': True, 'files': len(files), 'sha256': digest}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tag', required=True)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('--out', type=Path)
    group.add_argument('--verify-directory', type=Path)
    args = parser.parse_args()
    try:
        result = package(WORK, args.tag, args.out) if args.out else verify(WORK, args.tag, args.verify_directory)
        print(json.dumps(result))
        return 0
    except (OSError, ValueError, KeyError, zipfile.BadZipFile) as error:
        print(json.dumps({'ok': False, 'error': str(error)}))
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
