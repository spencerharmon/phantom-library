#!/usr/bin/env bash
# Offline real-CLI regression: invalid siblings cannot erase bound config evidence.
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
full = {
    'deployment': {'kind': 'Deployment', 'metadata': {
        'name': 'stream', 'namespace': 'media', 'uid': 'd-uid',
        'labels': {'kustomize.toolkit.fluxcd.io/name': 'stream',
                   'kustomize.toolkit.fluxcd.io/namespace': 'controllers'}},
        'spec': {'replicas': 1, 'selector': {'matchLabels': {'app': 'stream'}},
                 'template': {'spec': {'containers': [{'name': 'gostream',
                     'image': 'example.com/stream:v2',
                     'env': [{'name': 'GOMEMLIMIT', 'value': '1800MiB'}]}]}}}},
    'pods': {'kind': 'Pod', 'metadata': {'name': 'p', 'namespace': 'media',
        'labels': {'app': 'stream'}, 'ownerReferences': [{'kind': 'ReplicaSet',
            'name': 'rs', 'uid': 'rs-uid', 'controller': True}]},
        'status': {'phase': 'Running', 'containerStatuses': [{'name': 'gostream',
            'containerID': 'containerd://current', 'state': {'running': {}},
            'lastState': {'terminated': {'containerID': 'containerd://previous'}}}]}},
    'replicasets': {'kind': 'ReplicaSet', 'metadata': {'name': 'rs', 'uid': 'rs-uid',
        'namespace': 'media', 'ownerReferences': [{'kind': 'Deployment',
            'name': 'stream', 'uid': 'd-uid', 'controller': True}]}},
    'expected-tuning': {'container': 'gostream', 'rollout_at': None,
        'env': {'GOMEMLIMIT': '1800MiB'}, 'config': values},
    'flux': {'kind': 'Kustomization', 'metadata': {'name': 'stream', 'namespace': 'controllers'},
        'status': {'lastAppliedRevision': 'main@sha1:0123abcd'}},
}
cases = []


def case(name, tokens, states, code, reasons=None, files=None, receipt=None):
    files = deepcopy(full if files is None else files)
    # Keep raw tokens intact, especially numbers that Python floats would round.
    files['loaded-config'] = receipt if receipt is not None else (
        '{"container_id":"containerd://current","values":{' +
        ','.join(json.dumps(key) + ':' + token for key, token in tokens.items()) + '}}')
    observed = {key: None if state == 'unobservable' else json.loads(tokens[key])
                for key, state in states.items()}
    cases.append((name, files, states, observed, code, reasons or {}))


valid = {key: str(value) for key, value in values.items()}
case('all valid match', valid, dict.fromkeys(values, 'match'), 0)
invalid = [
    ('missing', None, 'missing/malformed receipt value'),
    ('string', '"6"', 'missing/malformed receipt value'),
    ('null', 'null', 'missing/malformed receipt value'),
    ('bool', 'true', 'missing/malformed receipt value'),
    ('array', '[]', 'missing/malformed receipt value'),
    ('object', '{}', 'missing/malformed receipt value'),
    ('negative', '-1', 'expected finite nonnegative number'),
    ('nan', 'NaN', 'expected finite nonnegative number'),
    ('infinity', 'Infinity', 'expected finite nonnegative number'),
    ('negative infinity', '-Infinity', 'expected finite nonnegative number'),
    ('excess precision', '6.0000000000000001', 'receipt value precision unsupported'),
    ('underflow', '1e-400', 'receipt value precision unsupported'),
    ('negative underflow', '-1e-400', 'receipt value precision unsupported'),
    ('overflow', '1e400', 'receipt value precision unsupported'),
]
for key in values:
    sibling = next(name for name in values if name != key)
    for label, token, reason in invalid:
        for state, delta, code in [('mismatch', 1, 3), ('match', 0, 2)]:
            tokens = {key: str(values[key] + delta)}
            if token is not None:
                tokens[sibling] = token
            case(f'{key} {state}; sibling {label}', tokens,
                 {key: state, sibling: 'unobservable'}, code,
                 {sibling: reason + ': ' + sibling})
    tokens = {key: str(values[key] + 1)}
    for cid in ('containerd://previous', 'containerd://foreign'):
        receipt = json.dumps({'container_id': cid, 'values': {key: values[key] + 1}})
        case(key + ' rejected ' + cid, tokens, dict.fromkeys(values, 'unobservable'), 2,
             dict.fromkeys(values, 'stale/foreign loaded-config container_id'), receipt=receipt)
    for source in ('pods', 'replicasets'):
        files = deepcopy(full)
        files[source]['metadata']['ownerReferences'][0]['uid'] = 'foreign'
        case(key + ' broken ownership ' + source, tokens,
             dict.fromkeys(values, 'unobservable'), 2, files=files)
    files = deepcopy(full)
    files['pods'] = {'kind': 'List', 'items': [files['pods'], files['pods']]}
    case(key + ' ambiguous pods', tokens, dict.fromkeys(values, 'unobservable'), 2, files=files)

for label, receipt in [
    ('malformed JSON', '{'), ('non-object', '[]'),
    ('invalid values', '{"container_id":"containerd://current","values":[]}'),
    ('duplicate envelope', '{"container_id":"containerd://current",'
        '"container_id":"containerd://current","values":{"master_concurrency_limit":7}}'),
    ('duplicate value', '{"container_id":"containerd://current","values":'
        '{"master_concurrency_limit":7,"master_concurrency_limit":7,"read_ahead_budget_mb":64}}'),
]:
    case(label, valid, dict.fromkeys(values, 'unobservable'), 2, receipt=receipt)

for source in ('env', 'image', 'flux'):
    files = deepcopy(full)
    live = files['deployment']['spec']['template']['spec']['containers'][0]
    if source == 'env':
        live['env'][0]['value'] = '2200MiB'
    elif source == 'image':
        live['image'] = 'example.com/stream:v1'
    else:
        files['flux']['metadata']['name'] = 'foreign'
    for bound in (True, False):
        case(source + ' mismatch precedence; bound=' + str(bound), {},
             dict.fromkeys(values, 'unobservable'), 3, files=files,
             reasons={key: 'missing/malformed receipt value: ' + key for key in values} if bound else None,
             receipt='{"container_id":"containerd://%s","values":{}}' %
                     ('current' if bound else 'foreign'))

failures = 0
for name, files, states, observed, code, reasons in cases:
    with tempfile.TemporaryDirectory() as tmp:
        argv = [str(engine), '--drift', '--namespace', 'media', '--name', 'stream',
                '--expected-image', 'example.com/stream:v2']
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
        fields = {f['field'][7:]: f for f in report['fields'] if f['field'].startswith('config.')}
        assert {key: f['state'] for key, f in fields.items()} == states, fields
        assert {key: f['observed'] for key, f in fields.items()} == observed, fields
        for key, field in fields.items():
            if states[key] == 'unobservable':
                assert field.get('reason'), field
                assert reasons.get(key, 'unusable loaded-config evidence:') in field['reason'], field
            else:
                assert 'reason' not in field, field
        assert not out.stderr, out.stderr
        print('ok   ' + name)
    except (AssertionError, KeyError, ValueError) as error:
        failures += 1
        print(f'FAIL {name}: {error}; rc={out.returncode} {out.stdout.strip()} {out.stderr.strip()}')
print(f'Field isolation: {len(cases)} cases, {failures} failures')
sys.exit(1 if failures else 0)
PY
