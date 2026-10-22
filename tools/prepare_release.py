"""Read-only source inventory; --out creates a new local source review tree, never a GitHub upload."""
import argparse
import hashlib
import json
import posixpath
from pathlib import Path
import re
import sys
from urllib.parse import unquote, urlsplit

sys.dont_write_bytecode = True
from verify_bridge import input_files

WORK = Path(__file__).resolve().parents[1]
DOCUMENTS = ('README.md', 'CONTRIBUTING.md', 'RELEASE_CHECKLIST.md', 'COMPATIBILITY.md',
             'CHANGELOG.md', 'INSTALL.md', 'USAGE.md', 'SOURCE_PROVENANCE.md')
TEMPLATES = DOCUMENTS + tuple(name[:-3] + '.en.md' for name in DOCUMENTS) + (
             '.github/workflows/ci.yml', '.gitignore', 'client.py')
TOKEN_PATTERNS = (
    ('github-token', re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})\b')),
    ('api-secret', re.compile(r'\bsk-[A-Za-z0-9_-]{32,}\b')),
    ('private-key', re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----')),
    ('personal-path', re.compile(r'[A-Za-z]:[\\/]+Users[\\/]+(?!Public\b)[^\s"\n]+', re.I)),
)

def checked(path, work):
    path = Path(path)
    if path.is_symlink() or not path.resolve().is_relative_to(work.resolve()):
        raise ValueError('source path is a link or escapes the workspace: ' + path.name)
    if not path.is_file():
        raise ValueError('missing selected source: ' + path.name)
    return path

def source_map(work):
    work = Path(work).resolve()
    selected = {p.relative_to(work).as_posix(): checked(p, work) for p in input_files(work)}
    # input_files is the source/test closure, not a walk of logs or game assets.
    if any(n.startswith(('repairs/', 'campaigns/', 'analysis/')) for n in selected):
        raise ValueError('verification still depends on private history')
    template_root = work / 'public'
    if not template_root.is_dir():
        marker = checked(work / 'release-manifest.json', work)
        if json.loads(marker.read_text(encoding='utf-8')).get('schema') != 'bridge-source-review-1':
            raise ValueError('missing public templates or validated source-review marker')
        template_root = work
    for name in TEMPLATES:
        selected[name] = checked(template_root / name, work)
    if (work / 'LICENSE').is_file():
        selected['LICENSE'] = checked(work / 'LICENSE', work)
    for name in selected:
        if Path(name).suffix.lower() in {'.dll', '.exe', '.pdb', '.jsonl', '.zip', '.png', '.jpg'}:
            raise ValueError('binary or runtime artifact selected: ' + name)
    return selected

def inventory(work=WORK):
    selected = source_map(work)
    files, issues = [], []
    for name, path in sorted(selected.items()):
        data = path.read_bytes()
        text = data.decode('utf-8-sig')
        files.append({'path': name, 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data)})
        for line, content in enumerate(text.splitlines(), 1):
            for rule, pattern in TOKEN_PATTERNS:
                if pattern.search(content):
                    # Never print matching contents or credential material.
                    issues.append({'path': name, 'line': line, 'rule': rule})
            if name.endswith('.md'):
                for link in re.findall(r'\]\(([^)]+)\)', content):
                    target = urlsplit(link.strip('<>'))
                    if target.scheme or target.netloc or not target.path:
                        continue
                    destination = posixpath.normpath(posixpath.join(posixpath.dirname(name), unquote(target.path)))
                    if destination not in selected:
                        issues.append({'path': name, 'line': line, 'rule': 'unselected-doc-link'})
    return {'schema': 'bridge-source-review-1', 'ok': not issues, 'files': files, 'fileCount': len(files),
            'totalBytes': sum(row['bytes'] for row in files), 'issues': issues,
            'licensePresent': 'LICENSE' in selected, 'published': False, 'releaseReady': False,
            'remainingGates': ['source provenance and license decision', 'clean source-tree regression',
                               'first-install acceptance', 'public version and compatibility policy'],
            'excluded': ['game binaries/assets and decompiled dumps', 'runtime queues/logs/saves/configuration',
                         'private repairs/campaigns and native dependencies', 'desktop screenshot/input helpers']}

def stage(work, target):
    work, target = Path(work).resolve(), Path(target).resolve()
    base = (work / 'releases').resolve()
    if not base.is_relative_to(work) or target == base or not target.is_relative_to(base) or target.exists():
        raise ValueError('output must be a new subdirectory of releases; existing reviews are preserved')
    report = inventory(work)
    if not report['ok']:
        raise ValueError('source scan has findings; no files staged')
    selected = source_map(work)
    # Validate all content before creating the target; copy only the reviewed bytes.
    buffers = {}
    for row in report['files']:
        raw = selected[row['path']].read_bytes()
        if hashlib.sha256(raw).hexdigest() != row['sha256']:
            raise ValueError('source changed during preparation: ' + row['path'])
        buffers[row['path']] = raw
    target.mkdir(parents=True)
    for name, data in buffers.items():
        path = target / name
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open('xb') as writer:
            writer.write(data)
    (target / 'release-manifest.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    return report

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, help='explicit new source-review directory under releases')
    args = parser.parse_args(argv)
    try:
        report = stage(WORK, args.out) if args.out else inventory()
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 0 if report['ok'] else 1
    except (OSError, ValueError) as error:
        print(json.dumps({'ok': False, 'published': False, 'error': str(error)}, ensure_ascii=False))
        return 1

if __name__ == '__main__':
    raise SystemExit(main())
