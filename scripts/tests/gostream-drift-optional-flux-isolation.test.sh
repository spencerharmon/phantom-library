#!/usr/bin/env bash
# Offline real-CLI regression: optional Flux failures cannot erase independent drift.
set -euo pipefail
exec python3 - "${BASH_SOURCE[0]}" <<'PY'
from copy import deepcopy
import json
from pathlib import Path
import subprocess
import sys
import tempfile

engine = Path(sys.argv[1]).resolve().parent.parent / 'gostream-oom-acceptance.sh'
full = {
    'deployment': {'kind': 'Deployment', 'metadata': {
        'name': 'stream', 'namespace': 'media', 'uid': 'd-uid', 'generation': 11,
        'labels': {'kustomize.toolkit.fluxcd.io/name': 'stream',
                   'kustomize.toolkit.fluxcd.io/namespace': 'controllers'},
        'annotations': {'meta.helm.sh/release-name': 'stream',
                        'meta.helm.sh/release-namespace': 'controllers'}},
        'spec': {'replicas': 1, 'selector': {'matchLabels': {'app': 'stream'}},
                 'template': {'spec': {'containers': [{'name': 'gostream',
                     'image': 'example.com/stream:v2',
                     'env': [{'name': 'GOMEMLIMIT', 'value': '1800MiB'}]}]}}}},
    'pods': {'kind': 'Pod', 'metadata': {'name': 'p', 'namespace': 'media',
        'labels': {'app': 'stream'}, 'ownerReferences': [{'kind': 'ReplicaSet',
            'name': 'rs', 'uid': 'rs-uid', 'controller': True}]},
        'status': {'phase': 'Running', 'containerStatuses': [{'name': 'gostream',
            'containerID': 'containerd://current', 'state': {'running': {}}}]}},
    'replicasets': {'kind': 'ReplicaSet', 'metadata': {'name': 'rs', 'uid': 'rs-uid',
        'namespace': 'media', 'ownerReferences': [{'kind': 'Deployment',
            'name': 'stream', 'uid': 'd-uid', 'controller': True}]}},
    'expected-tuning': {'container': 'gostream', 'rollout_at': None,
        'env': {'GOMEMLIMIT': '1800MiB'},
        'config': {'master_concurrency_limit': 6, 'read_ahead_budget_mb': 64}},
    'loaded-config': {'container_id': 'containerd://current',
        'values': {'master_concurrency_limit': 6, 'read_ahead_budget_mb': 64}},
    'flux': {'kind': 'Kustomization', 'metadata': {'name': 'stream', 'namespace': 'controllers'},
        'status': {'lastAppliedRevision': 'main@sha1:0123abcd'}},
}
expected_fields = {
    'env.GOMEMLIMIT': ('1800MiB', '1800MiB', 'match'),
    'image': ('example.com/stream:v2', 'example.com/stream:v2', 'match'),
    'config.master_concurrency_limit': (6, 6, 'match'),
    'config.read_ahead_budget_mb': (64, 64, 'match'),
}
cases = []


def case(name, files, code, binding='unobservable', state='unobservable',
         reason='', fields=None, unusable=False, required=False):
    cases.append((name, deepcopy(files), code, binding, state, reason,
                  deepcopy(expected_fields if fields is None else fields), unusable, required))


# None omits a flag; these tuples exercise actual filesystem errors even as root.
missing = ('missing',)
unreadable = ('directory',)
bad_flux = [
    ('malformed JSON', '{', 'Expecting'),
    ('invalid encoding', b'\xff', 'decode'),
    # Depending on Python, decoding hits a recursion limit or yields a non-object.
    ('excessive JSON nesting', '[' * 2000 + '0' + ']' * 2000, ''),
    ('duplicate key', '{"kind":"Kustomization","kind":"HelmRelease"}', 'duplicate JSON key'),
    ('long duplicate key', '{"' + 'x' * 1000 + '":1,"' + 'x' * 1000 + '":2}', 'duplicate JSON key'),
    ('wrong kind', {'kind': 'ConfigMap'}, 'expected Flux Kustomization/HelmRelease'),
    ('missing kind', {}, 'expected Flux Kustomization/HelmRelease'),
    ('non-object array', [], 'expected Flux Kustomization/HelmRelease'),
    ('non-object null', 'null', 'expected Flux Kustomization/HelmRelease'),
    ('non-object number', 42, 'expected Flux Kustomization/HelmRelease'),
    ('non-object string', '"Kustomization"', 'expected Flux Kustomization/HelmRelease'),
    ('missing path', missing, 'No such file'),
    ('unreadable path', unreadable, 'Is a directory'),
]
for kind in ('Kustomization', 'HelmRelease'):
    flux = deepcopy(full['flux'])
    flux.update(kind=kind, spec={})
    for status in (None, [], 'bad', 42, True):
        bad_flux.append((kind + ' status ' + repr(status), dict(flux, status=status), 'invalid Flux status'))
    for revision in ([], {}, 42, 1.5, True):
        bad_flux.append((kind + ' revision ' + repr(revision),
                         dict(flux, status={'lastAppliedRevision': revision}), 'invalid Flux revision'))
    # Raw duplicate nested keys must be rejected, not last-write-wins.
    raw = json.dumps(flux).replace('"lastAppliedRevision":',
                                  '"lastAppliedRevision":"old", "lastAppliedRevision":')
    bad_flux.append((kind + ' duplicate revision', raw, 'duplicate JSON key'))

# Fail after receipt construction too: generated Helm names must be UTF-8 encodable.
flux = deepcopy(full['flux'])
flux.update(kind='HelmRelease', spec={})
flux['metadata']['name'] = '\ud800' * 54
bad_flux.append(('malformed generated Helm name', flux, 'encode'))

for label, flux, reason in bad_flux:
    for source in ('matching', 'env', 'image', 'config'):
        files = deepcopy(full)
        files['flux'] = flux
        fields = deepcopy(expected_fields)
        live = files['deployment']['spec']['template']['spec']['containers'][0]
        if source == 'env':
            live['env'][0]['value'] = '2200MiB'
            fields['env.GOMEMLIMIT'] = ('1800MiB', '2200MiB', 'mismatch')
        elif source == 'image':
            live['image'] = 'example.com/stream:v1'
            fields['image'] = ('example.com/stream:v2', 'example.com/stream:v1', 'mismatch')
        elif source == 'config':
            files['loaded-config']['values']['master_concurrency_limit'] = 7
            fields['config.master_concurrency_limit'] = (6, 7, 'mismatch')
        case(label + '; ' + source, files, 2 if source == 'matching' else 3,
             reason=reason, fields=fields, unusable=True)

# Composition with the existing process-binding and per-field boundaries.
for bound in (True, False):
    for env_drift in (False, True):
        files = deepcopy(full)
        files['flux']['status'] = []
        files['loaded-config']['values'] = {'master_concurrency_limit': 7, 'read_ahead_budget_mb': '64'}
        fields = deepcopy(expected_fields)
        fields['config.master_concurrency_limit'] = (6, 7, 'mismatch') if bound else (6, None, 'unobservable')
        fields['config.read_ahead_budget_mb'] = (64, None, 'unobservable')
        if not bound:
            files['loaded-config']['container_id'] = 'containerd://previous'
        if env_drift:
            files['deployment']['spec']['template']['spec']['containers'][0]['env'][0]['value'] = '2200MiB'
            fields['env.GOMEMLIMIT'] = ('1800MiB', '2200MiB', 'mismatch')
        case(f'bad Flux + bad config sibling; bound={bound}; env drift={env_drift}', files,
             3 if bound or env_drift else 2, reason='invalid Flux status', fields=fields, unusable=True)

for kind in ('Kustomization', 'HelmRelease'):
    files = deepcopy(full)
    files['flux'].update(kind=kind, spec={})
    case(kind + ' valid bound control', files, 0, binding='match', state='observed')
    for rev in (None, '', '   '):
        f = deepcopy(files); f['flux']['status']['lastAppliedRevision'] = rev
        case(kind + ' absent/blank revision ' + repr(rev), f, 2, binding='match', reason='revision')
    f = deepcopy(files); del f['flux']['status']
    case(kind + ' missing status', f, 2, binding='match', reason='revision')
    f = deepcopy(files); del f['flux']['status']['lastAppliedRevision']
    case(kind + ' missing revision', f, 2, binding='match', reason='revision')
    f = deepcopy(files); f['flux']['metadata']['name'] = 'foreign'
    case(kind + ' valid foreign control', f, 3, binding='mismatch', reason='ownership')
    f['flux']['status'] = []
    case(kind + ' malformed foreign is not binding proof', f, 2,
         reason='invalid Flux status', unusable=True)
    f = deepcopy(files); f['flux']['metadata'] = []
    case(kind + ' malformed identity', f, 2, reason='ownership')

for source in ('matching', 'env'):
    files = deepcopy(full); files['flux'] = None
    fields = deepcopy(expected_fields)
    if source == 'env':
        files['deployment']['spec']['template']['spec']['containers'][0]['env'][0]['value'] = '2200MiB'
        fields['env.GOMEMLIMIT'] = ('1800MiB', '2200MiB', 'mismatch')
    case('no Flux flag; ' + source, files, 2 if source == 'matching' else 3,
         reason='required', fields=fields)

# Required-input errors remain global, even if Flux is also malformed.
for flag in ('deployment', 'expected-tuning'):
    for label, payload in [('JSON', '{'), ('non-object', []), ('missing fields', {}),
                           ('missing path', missing), ('unreadable path', unreadable)]:
        files = deepcopy(full); files[flag] = payload; files['flux'] = '{'
        case('required ' + flag + ' ' + label, files, 2, required=True)
files = deepcopy(full); files['deployment']['metadata']['namespace'] = 'foreign'
case('required foreign Deployment', files, 2, required=True)
files = deepcopy(full); files['expected-tuning']['env']['GOMEMLIMIT'] = 1800
case('required malformed env expectation', files, 2, required=True)

failures = 0
for name, files, code, binding, state, reason, fields, unusable, required in cases:
    with tempfile.TemporaryDirectory() as tmp:
        argv = [str(engine), '--drift', '--namespace', 'media', '--name', 'stream',
                '--expected-image', 'example.com/stream:v2']
        for flag, data in files.items():
            if data is None:
                continue
            path = Path(tmp) / (flag + '.json')
            if data == unreadable:
                path.mkdir()
            elif data != missing:
                if isinstance(data, bytes):
                    path.write_bytes(data)
                else:
                    path.write_text(data if isinstance(data, str) else json.dumps(data))
            argv += ['--' + flag, str(path)]
        out = subprocess.run(argv, capture_output=True, text=True, timeout=10)
    try:
        assert not out.stderr, out.stderr
        assert out.returncode == code, (out.returncode, code)
        verdict, body = out.stdout.split(' ', 1)
        if required:
            assert verdict == 'INCOMPLETE' and body.strip(), out.stdout
        else:
            assert verdict == {0: 'DRIFT_MATCH', 2: 'DRIFT_INCOMPLETE', 3: 'DRIFT_MISMATCH'}[code], verdict
            report = json.loads(body)
            assert report['mode'] == 'drift' and report['rollout_at'] is None, report
            assert (report['namespace'], report['name'], report['container'], report['generation']) == (
                'media', 'stream', 'gostream', 11), report
            actual = {f['field']: (f['expected'], f['observed'], f['state']) for f in report['fields']}
            assert len(report['fields']) == len(fields) and actual == fields, actual
            for field in report['fields']:
                if field['state'] == 'unobservable':
                    assert field.get('reason'), field
                else:
                    assert 'reason' not in field, field
            flux = report['flux']
            assert flux['state'] == state and flux['binding'] == binding, flux
            if state == 'unobservable':
                assert reason in flux.get('reason', '') and flux.get('reason'), flux
                assert len(flux['reason']) <= 240, flux
            else:
                assert flux['last_applied_revision'] == full['flux']['status']['lastAppliedRevision'], flux
                assert 'reason' not in flux, flux
            if unusable:
                assert flux['reason'].startswith('unusable Flux evidence: '), flux
                assert flux['namespace'] is None and 'last_applied_revision' not in flux, flux
        print('ok   ' + name)
    except (AssertionError, KeyError, ValueError) as error:
        failures += 1
        print(f'FAIL {name}: {error}; rc={out.returncode} {out.stdout.strip()} {out.stderr.strip()}')
print(f'Optional Flux isolation: {len(cases)} cases, {failures} failures')
sys.exit(1 if failures else 0)
PY
