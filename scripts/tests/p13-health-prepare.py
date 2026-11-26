#!/usr/bin/env python3
"""Build isolated candidate + actual v24 repository source, never a live checkout."""
import hashlib
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import urllib.request

repo = Path(__file__).resolve().parents[2]
out = Path(sys.argv[1])
tree = out / 'build'
tree.mkdir()
for name in ('src', 'tests', 'scripts', 'Directory.Build.props', 'NuGet.config'):
    source, target = repo / name, tree / name
    if source.is_dir():
        shutil.copytree(source, target, ignore=shutil.ignore_patterns('bin', 'obj'))
    else:
        shutil.copy2(source, target)
archive = out / 'jellyfin.tar.gz'
urllib.request.urlretrieve('https://codeload.github.com/jellyfin/jellyfin/tar.gz/refs/tags/v10.11.9', archive)
with tarfile.open(archive) as tar:
    tar.extractall(tree, filter='data')
(tree / 'jellyfin-10.11.9').rename(tree / 'jellyfin')
for patch in sorted((repo / 'scripts/jellyfin-patches').glob('*.patch')):
    subprocess.run(['patch', '--batch', '--forward', '-p1', '-i', str(patch)], cwd=tree / 'jellyfin', check=True)
legacy = out / 'PhantomDb.v24.cs'
urllib.request.urlretrieve('https://raw.githubusercontent.com/spencerharmon/phantom-library/be60223881316da24bb9b402af4b5457ddef5bb6/src/Jellyfin.Plugin.PhantomLibrary/State/PhantomDb.cs', legacy)
assert hashlib.sha256(legacy.read_bytes()).hexdigest() == '0802be3889e58ed726afb0f63b52c7e62cfffefc2b86f047be3458ce54bd1843', 'legacy source integrity'
print('Prepared candidate sources, patched Jellyfin v10.11.9, and verified actual v24 PhantomDb')
