#!/usr/bin/env bash
# Offline process-identity regression; shared gostream serves movies and episodes.
set -euo pipefail
exec python3 - "${BASH_SOURCE[0]}" <<'PY'
from copy import deepcopy
import json
from pathlib import Path
import subprocess
import sys
import tempfile

engine = Path(sys.argv[1]).resolve().parent.parent / 'gostream-oom-acceptance.sh'
values = {'master_concurrency_limit': 6, 'read_ahead_budget_mb': 64}
deployment = {'kind': 'Deployment', 'metadata': {
    'name': 'stream', 'namespace': 'media', 'uid': 'deployment-uid',
    'labels': {'kustomize.toolkit.fluxcd.io/name': 'stream',
               'kustomize.toolkit.fluxcd.io/namespace': 'controllers'}},
    'spec': {'replicas': 1, 'selector': {'matchLabels': {'app': 'stream'}},
             'template': {'spec': {'containers': [{'name': 'gostream',
                'image': 'example.com/stream:v2',
                'env': [{'name': 'GOMEMLIMIT', 'value': '1800MiB'}]}]}}}}
rs = {'kind': 'ReplicaSet', 'metadata': {'name': 'stream-rs', 'uid': 'rs-uid',
      'namespace': 'media', 'ownerReferences': [{'kind': 'Deployment',
      'name': 'stream', 'uid': 'deployment-uid', 'controller': True}]}}
pod = {'kind': 'Pod', 'metadata': {'name': 'stream-rs-pod', 'uid': 'pod-uid',
       'namespace': 'media', 'labels': {'app': 'stream'},
       'ownerReferences': [{'kind': 'ReplicaSet', 'name': 'stream-rs',
                            'uid': 'rs-uid', 'controller': True}]},
       'status': {'phase': 'Running', 'containerStatuses': [
           {'name': 'sidecar', 'containerID': 'containerd://sidecar', 'state': {'running': {}}},
           {'name': 'gostream', 'containerID': 'containerd://current',
            'state': {'running': {'startedAt': '2026-01-01T00:00:00Z'}},
            'lastState': {'terminated': {'containerID': 'containerd://previous'}}}]}}
full = {'deployment': deployment, 'pods': pod, 'replicasets': rs,
        'expected-tuning': {'container': 'gostream', 'rollout_at': None,
                            'env': {'GOMEMLIMIT': '1800MiB'}, 'config': deepcopy(values)},
        'loaded-config': {'container_id': 'containerd://current', 'values': values},
        'flux': {'kind': 'Kustomization', 'metadata': {'name': 'stream', 'namespace': 'controllers'},
                 'status': {'lastAppliedRevision': 'main@sha1:0123abcd'}}}
cases = []


def case(name, files, state='unobservable', code=2):
    cases.append((name, deepcopy(files), state, code))


case('current receipt matches', full, 'match', 0)
f = deepcopy(full)
for key in ('pods', 'replicasets'):
    f[key] = {'kind': 'List', 'items': [f[key]]}
case('raw lists match', f, 'match', 0)
f = deepcopy(full); f['loaded-config']['values'] = dict.fromkeys(values, 1)
case('current receipt differs', f, 'mismatch', 3)
for key in values:
    f = deepcopy(full); f['loaded-config']['values'][key] = 1
    case('only ' + key + ' differs', f,
         {name: 'mismatch' if name == key else 'match' for name in values}, 3)
for cid in ('containerd://previous', 'containerd://foreign', 'containerd://sidecar',
            'current', '', None, 42, True, [], {}):
    f = deepcopy(full); f['loaded-config']['container_id'] = cid
    case('invalid/stale/foreign ID ' + repr(cid), f)
for receipt in (None, [], {}, 'bad json', {'container_id': 'containerd://current'},
                {'values': values}, {'container_id': 'containerd://current', 'values': []},
                '{"container_id":"containerd://current","container_id":"containerd://current","values":{}}'):
    f = deepcopy(full); f['loaded-config'] = receipt
    case('malformed receipt ' + repr(receipt), f)
for value in (None, True, '6', [], {}, -1, float('nan'), float('inf')):
    f = deepcopy(full); f['loaded-config']['values']['master_concurrency_limit'] = value
    case('malformed tuning value ' + repr(value), f)
f = deepcopy(full); del f['loaded-config']['values']['read_ahead_budget_mb']
case('partial receipt', f)
for flag in ('loaded-config', 'pods', 'replicasets'):
    f = deepcopy(full); del f[flag]
    case('missing ' + flag, f)
f = deepcopy(full); del f['loaded-config']; f['config'] = values
case('mounted config is not loaded proof', f)
f = deepcopy(full); f['config'] = values; f['loaded-config']['container_id'] = 'containerd://previous'
case('mounted config cannot rescue stale receipt', f)
for key, value in (('namespace', 'foreign'), ('deletionTimestamp', '2026-01-01T00:00:00Z'),
                   ('labels', {'app': 'other'})):
    f = deepcopy(full); f['pods']['metadata'][key] = value
    case('foreign/deleting pod ' + key, f)
for key in ('uid', 'name', 'kind'):
    for source in ('pods', 'replicasets'):
        f = deepcopy(full); f[source]['metadata']['ownerReferences'][0][key] = 'foreign'
        case('broken ownership ' + source + '.' + key, f)
for source in ('pods', 'replicasets'):
    f = deepcopy(full); f[source]['metadata']['ownerReferences'][0]['controller'] = False
    case('non-controller ' + source, f)
    for bad in (None, [], 'invalid', {}, {'kind': 'List', 'items': []}):
        f = deepcopy(full); f[source] = bad
        case('malformed evidence ' + source + ' ' + repr(bad), f)
f = deepcopy(full); f['pods']['status']['containerStatuses'][1]['state'] = {'terminated': {}}
case('not currently running', f)
f = deepcopy(full); f['pods']['status']['containerStatuses'][1]['containerID'] = 'containerd://restarted'
case('restart invalidates receipt', f)
f = deepcopy(full); f['pods']['status']['containerStatuses'].append(deepcopy(f['pods']['status']['containerStatuses'][1]))
case('ambiguous container status', f)
f = deepcopy(full); f['pods']['status']['containerStatuses'].pop()
case('only sidecar status', f)
f = deepcopy(full); f['deployment']['metadata']['uid'] = None
f['replicasets']['metadata']['ownerReferences'][0]['uid'] = None
case('null Deployment UID cannot bind', f)
f = deepcopy(full); f['replicasets']['metadata']['uid'] = None
f['pods']['metadata']['ownerReferences'][0]['uid'] = None
case('null ReplicaSet UID cannot bind', f)
f = deepcopy(full); f['deployment']['spec']['replicas'] = 2
case('single receipt cannot cover replicas', f)
f = deepcopy(full); f['pods'] = {'kind': 'List', 'items': [pod, pod]}
case('ambiguous pod inventory', f)
f = deepcopy(full); del f['loaded-config']; f['deployment']['spec']['template']['spec']['containers'][0]['env'][0]['value'] = '2200MiB'
case('unobserved config preserves env mismatch', f, code=3)

failures = 0
for name, files, state, code in cases:
    with tempfile.TemporaryDirectory() as tmp:
        argv = [str(engine), '--drift', '--namespace', 'media', '--name', 'stream']
        for flag, data in files.items():
            path = Path(tmp) / (flag + '.json')
            path.write_text(data if isinstance(data, str) else json.dumps(data))
            argv += ['--' + flag, str(path)]
        out = subprocess.run(argv, capture_output=True, text=True)
    try:
        verdict, body = out.stdout.split(' ', 1)
        assert out.returncode == code, (out.returncode, code)
        assert verdict == {0: 'DRIFT_MATCH', 2: 'DRIFT_INCOMPLETE', 3: 'DRIFT_MISMATCH'}[code]
        report = json.loads(body)
        assert report['rollout_at'] is None
        fields = [field for field in report['fields'] if field['field'].startswith('config.')]
        assert len(fields) == 2
        assert all(field['state'] == (state[field['field'][7:]] if isinstance(state, dict) else state)
                   for field in fields), fields
        if state == 'unobservable':
            assert all(field['observed'] is None and field.get('reason') for field in fields), fields
        else:
            assert {field['field'][7:]: field['observed'] for field in fields} == files['loaded-config']['values']
        assert not out.stderr, out.stderr
        print('ok   ' + name)
    except (AssertionError, KeyError, ValueError) as error:
        failures += 1
        print(f'FAIL {name}: {error}; rc={out.returncode} {out.stdout.strip()} {out.stderr.strip()}')
print(f'Loaded config: {len(cases)} cases, {failures} failures')
sys.exit(1 if failures else 0)
PY
