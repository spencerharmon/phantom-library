#!/usr/bin/env python3
"""Negative control: disconnect admission in the disposable build, never real source."""
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

out = Path(sys.argv[1]).resolve()
root = out / 'build'
files = [root / 'src/Jellyfin.Plugin.PhantomLibrary/Clients' / f'{name}Client.cs' for name in ('Torrentio', 'Prowlarr')]
originals = {p: p.read_text() for p in files}
try:
    for path, text in originals.items():
        assert text.count('_health = health;') == 1
        path.write_text(text.replace('_health = health;', '_health = null;'))
    with (out / 'negative.log').open('w') as log:
        result = subprocess.run(['dotnet', 'test', str(root / 'tests/IndexerHealth.Tests/IndexerHealth.Tests.csproj'),
            '-m:2', '-p:UseSharedCompilation=false', '-nodeReuse:false',
            '-p:LegacyPhantomDbSource=' + str(out / 'PhantomDb.v24.cs'),
            '--filter', 'FullyQualifiedName~RetryAfter_SkipsOtherColour|FullyQualifiedName~SharedBudget_AndInteractivePreemption|FullyQualifiedName~ProwlarrDisabledUntil',
            '--logger', 'trx;LogFileName=negative.trx', '--results-directory', str(out)], stdout=log, stderr=subprocess.STDOUT)
    assert result.returncode != 0, 'disconnected admission unexpectedly passed'
    results = ET.parse(out / 'negative.trx').findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
    assert len(results) == 6 and all(r.attrib['outcome'] == 'Failed' for r in results), 'expected six behavioral assertion failures, not a build failure'
    print('PASS negative control: disconnected admission fails all 6 movie/episode backoff, disabled-status and shared-budget assertions')
finally:
    for path, text in originals.items():
        path.write_text(text)
