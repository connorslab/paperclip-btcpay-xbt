#!/usr/bin/env python3
"""Prepare pinned upstream source in the disposable .build directory."""
import hashlib
import json
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def run(*args, cwd=None):
    subprocess.run(args, cwd=cwd, check=True)

def prepare(name, settings):
    target = ROOT / '.build' / name
    patch = ROOT / 'patches' / (name + '.patch')
    inputs = [patch, ROOT / 'upstream.json']
    if name == 'btcpay':
        inputs += sorted(p for p in (ROOT / 'src').rglob('*') if p.is_file())
    digest = hashlib.sha256()
    for path in inputs:
        digest.update(str(path.relative_to(ROOT)).encode())
        digest.update(path.read_bytes())
    stamp = target / '.xbtpay-source-hash'
    expected = digest.hexdigest()
    if target.exists():
        if stamp.exists() and stamp.read_text() == expected:
            return
        raise SystemExit(f'{target} contains different/incomplete source. Move it aside before rebuilding.')
    target.mkdir(parents=True)
    run('git', 'init', '-q', str(target))
    run('git', 'remote', 'add', 'origin', settings['repository'], cwd=target)
    run('git', 'fetch', '--depth=1', 'origin', settings['revision'], cwd=target)
    run('git', 'checkout', '--detach', settings['revision'], cwd=target)
    run('git', 'apply', '--check', str(patch), cwd=target)
    run('git', 'apply', str(patch), cwd=target)
    if name == 'btcpay':
        shutil.copytree(ROOT / 'src', target, dirs_exist_ok=True)
        (target / 'XBTPay' / 'nuget').mkdir(parents=True, exist_ok=True)
    stamp.write_text(expected)

if __name__ == '__main__':
    manifest = json.loads((ROOT / 'upstream.json').read_text())
    for name in ['btcpay', 'nbxplorer']:
        prepare(name, manifest[name])
