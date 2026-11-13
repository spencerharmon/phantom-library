#!/usr/bin/env bash
# Offline regression test for gostream-oom-acceptance.sh --drift (neutral fixtures).
set -euo pipefail
exec python3 - "${BASH_SOURCE[0]}" <<'PY'
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

here = Path(sys.argv[1]).resolve().parent
engine = here.parent / 'gostream-oom-acceptance.sh'
assert os.access(engine, os.X_OK), f'missing executable: {engine}'
container = {'name': 'stream', 'image': 'example.com/stream:v2',
             'env': [{'name': 'GOMEMLIMIT', 'value': '1800MiB'},
                     {'name': 'MKV_PROXY_CONCURRENCY', 'value': '6'}]}
deployment = {'kind': 'Deployment', 'metadata': {'name': 'stream', 'namespace': 'media', 'generation': 11,
              'labels': {'kustomize.toolkit.fluxcd.io/name': 'stream',
                         'kustomize.toolkit.fluxcd.io/namespace': 'flux-system'}},
              'spec': {'template': {'spec': {'containers': [container]}}}}
expected = {'container': 'stream', 'rollout_at': None,
            'env': {'GOMEMLIMIT': '1800MiB', 'MKV_PROXY_CONCURRENCY': '6'},
            'config': {'read_ahead_mib': 64}}
config = {'read_ahead_mib': 64}
flux = {'kind': 'Kustomization', 'metadata': {'name': 'stream', 'namespace': 'flux-system'},
        'status': {'lastAppliedRevision': 'main@sha1:0123abcd'}}
failures = 0


def run(name, code, verdict, files, extra=(), contains=()):
    global failures
    with tempfile.TemporaryDirectory() as tmp:
        argv = [str(engine), '--drift', '--namespace', 'media', '--name', 'stream',
                '--expected-image', 'example.com/stream:v2']
        for flag, data in files.items():
            path = Path(tmp) / (flag + '.json')
            path.write_text(data if isinstance(data, str) else json.dumps(data))
            argv += ['--' + flag, str(path)]
        out = subprocess.run(argv + list(extra), capture_output=True, text=True)
    ok = out.returncode == code and out.stdout.startswith(verdict + ' ') and all(c in out.stdout for c in contains)
    ok = ok and '"rollout_at": null' in out.stdout if out.stdout.startswith('DRIFT') else ok
    print(('ok   ' if ok else 'FAIL ') + name + f' rc={out.returncode} {out.stdout.strip()[:200]}')
    failures += not ok


full = {'deployment': deployment, 'expected-tuning': dict(expected, config={}), 'flux': flux}
run('full match', 0, 'DRIFT_MATCH', full, contains=('main@sha1:0123abcd',))
d = json.loads(json.dumps(deployment))
d['spec']['template']['spec']['containers'][0]['env'][0]['value'] = '2200MiB'
run('env mismatch 2200MiB', 3, 'DRIFT_MISMATCH', dict(full, deployment=d),
    contains=('"observed": "2200MiB"', '"state": "mismatch"'))
run('mounted config unobservable', 2, 'DRIFT_INCOMPLETE', dict(full, config=config, **{'expected-tuning': expected}),
    contains=('config.read_ahead_mib', '"state": "unobservable"'))
run('config unobservable', 2, 'DRIFT_INCOMPLETE', dict(full, **{'expected-tuning': expected}),
    contains=('"state": "unobservable"',))
run('missing flux input unobservable', 2, 'DRIFT_INCOMPLETE', {k: v for k, v in full.items() if k != 'flux'},
    contains=('"binding": "unobservable"', '"state": "unobservable"'))
d = json.loads(json.dumps(deployment)); d['metadata']['namespace'] = 'other'
run('foreign namespace', 2, 'INCOMPLETE', dict(full, deployment=d), contains=('foreign',))
d = json.loads(json.dumps(deployment)); d['metadata']['name'] = 'other'
run('foreign name', 2, 'INCOMPLETE', dict(full, deployment=d), contains=('foreign',))
run('malformed deployment JSON', 2, 'INCOMPLETE', dict(full, deployment='{not json'))
run('malformed flux kind', 2, 'INCOMPLETE', dict(full, flux={'kind': 'ConfigMap'}))
d = json.loads(json.dumps(deployment))
d['spec']['template']['spec']['containers'][0]['image'] = 'example.com/stream:v1'
run('image mismatch', 3, 'DRIFT_MISMATCH', dict(full, deployment=d))
for suite in ('gostream-oom-acceptance.test.sh', 'gostream-oom-prerollout.test.sh'):
    r = subprocess.run([str(here / suite)], capture_output=True, text=True)
    print(('ok   ' if r.returncode == 0 else 'FAIL ') + suite)
    failures += r.returncode != 0
    if r.returncode:
        print(r.stdout[-2000:], r.stderr[-2000:])
sys.exit(1 if failures else 0)
PY
