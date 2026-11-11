#!/usr/bin/env bash
# Offline regression test for `gostream-oom-acceptance.sh --drift` (neutral fixtures).
set -euo pipefail
exec python3 - "${BASH_SOURCE[0]}" <<'PY'
import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile

here = Path(sys.argv[1]).resolve().parent
engine = here.parent / 'gostream-oom-acceptance.sh'
expected = {'container': 'stream', 'rollout_at': None, 'image': 'example.com/stream:v2',
            'env': {'GOMEMLIMIT': '2600MiB', 'MKV_PROXY_CONCURRENCY': '6'},
            'config': {'read_ahead_budget_mb': 64}}
container = {'name': 'stream', 'image': 'example.com/stream:v2',
             'env': [{'name': 'GOMEMLIMIT', 'value': '2600MiB'},
                     {'name': 'MKV_PROXY_CONCURRENCY', 'value': '6'}]}
deployment = {'kind': 'Deployment', 'metadata': {'namespace': 'media', 'name': 'stream', 'generation': 11},
              'spec': {'template': {'spec': {'containers': [container]}}}}
config = {'read_ahead_budget_mb': 64, 'other': True}
flux = {'kind': 'Kustomization', 'metadata': {'namespace': 'flux-system', 'name': 'apps'},
        'spec': {}, 'status': {'lastAppliedRevision': 'main@sha1:0123abcd',
        'conditions': [{'type': 'Ready', 'status': 'True'}],
        'inventory': {'entries': [{'id': 'media_stream_apps_Deployment', 'v': 'v1'}]}}}
base = {'expected-tuning': expected, 'deployment': deployment, 'config': config, 'flux': flux}


def run(files, raw=None):
    with tempfile.TemporaryDirectory() as tmp:
        argv = [str(engine), '--drift', '--namespace', 'media', '--name', 'stream']
        for key, value in files.items():
            if value is None:
                continue
            path = Path(tmp) / (key + '.json')
            path.write_text(raw[key] if raw and key in raw else json.dumps(value))
            argv += ['--' + key, str(path)]
        p = subprocess.run(argv, capture_output=True, text=True)
        return p.returncode, p.stdout


def report(out):
    return json.loads(out.split(' ', 1)[1])


def states(out):
    return {f['field']: f['state'] for f in report(out)['fields']}


failures = []


def check(name, cond, out):
    print(('ok   ' if cond else 'FAIL ') + name)
    if not cond:
        failures.append(name)
        print('     ' + out.strip())


code, out = run(base)
check('full match -> DRIFT_NONE exit 0', code == 0 and out.startswith('DRIFT_NONE ')
      and set(states(out).values()) == {'match'}
      and report(out)['flux']['last_applied_revision'] == 'main@sha1:0123abcd'
      and report(out)['rollout_at'] is None, out)

case = copy.deepcopy(base)
case['deployment']['spec']['template']['spec']['containers'][0]['env'] = [
    {'name': 'GOMEMLIMIT', 'value': '2200MiB'}]
code, out = run(case)
s = states(out) if out.startswith('DRIFT ') else {}
check('env mismatch (2200MiB) -> DRIFT exit 3', code == 3 and out.startswith('DRIFT ')
      and s.get('env.GOMEMLIMIT') == 'mismatch' and s.get('env.MKV_PROXY_CONCURRENCY') == 'mismatch'
      and any(f['observed'] == '2200MiB' for f in report(out)['fields']), out)

case = copy.deepcopy(base); case['config'] = {'other': True}
code, out = run(case)
check('missing config key -> mismatch exit 3', code == 3
      and states(out).get('config.read_ahead_budget_mb') == 'mismatch', out)

case = copy.deepcopy(base); case['config'] = None
code, out = run(case)
check('absent config receipt -> unobservable, never match', code == 2 and out.startswith('DRIFT_INCOMPLETE ')
      and states(out).get('config.read_ahead_budget_mb') == 'unobservable', out)

case = copy.deepcopy(base); case['flux'] = None
code, out = run(case)
check('missing Flux input -> unobservable exit 2', code == 2 and out.startswith('DRIFT_INCOMPLETE ')
      and report(out)['flux']['state'] == 'unobservable', out)

case = copy.deepcopy(base); case['deployment']['metadata']['namespace'] = 'other'
code, out = run(case)
check('foreign Deployment namespace fails closed', code == 2 and out.startswith('INCOMPLETE ')
      and 'foreign' in out, out)

case = copy.deepcopy(base); case['flux']['status']['inventory']['entries'][0]['id'] = 'other_stream_apps_Deployment'
code, out = run(case)
check('foreign Flux inventory fails closed', code == 2 and out.startswith('INCOMPLETE ') and 'foreign' in out, out)

code, out = run(base, raw={'deployment': '{"kind": "Deployment", '})
check('malformed JSON fails closed', code == 2 and out.startswith('INCOMPLETE '), out)

case = copy.deepcopy(base)
case['deployment']['spec']['template']['spec']['containers'][0]['env'][0] = {
    'name': 'GOMEMLIMIT', 'valueFrom': {'configMapKeyRef': {'name': 'x', 'key': 'y'}}}
code, out = run(case)
check('valueFrom env -> unobservable', code == 2 and states(out).get('env.GOMEMLIMIT') == 'unobservable', out)

for suite in ('gostream-oom-acceptance.test.sh', 'gostream-oom-prerollout.test.sh'):
    p = subprocess.run([str(here / suite)], capture_output=True, text=True)
    check('existing suite ' + suite + ' still passes', p.returncode == 0, p.stdout + p.stderr)

sys.exit(1 if failures else 0)
PY
