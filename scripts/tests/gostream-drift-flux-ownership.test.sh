#!/usr/bin/env bash
# Exercise the real offline CLI; this shared-service receipt covers movies and episodes.
set -euo pipefail
exec python3 - "${BASH_SOURCE[0]}" <<'PY'
from copy import deepcopy
import json
from pathlib import Path
import subprocess
import sys
import tempfile

here = Path(sys.argv[1]).resolve().parent
engine = here.parent / 'gostream-oom-acceptance.sh'
kname = 'kustomize.toolkit.fluxcd.io/name'
kns = 'kustomize.toolkit.fluxcd.io/namespace'
hname = 'meta.helm.sh/release-name'
hns = 'meta.helm.sh/release-namespace'
deployment = {'kind': 'Deployment', 'metadata': {
    'name': 'stream', 'namespace': 'media',
    'labels': {kname: 'stream-owner', kns: 'controllers'}},
    'spec': {'template': {'spec': {'containers': [{
        'name': 'stream', 'image': 'example.com/stream:v2',
        'env': [{'name': 'GOMEMLIMIT', 'value': '1800MiB'}]}]}}}}
expected = {'container': 'stream', 'rollout_at': None,
            'env': {'GOMEMLIMIT': '1800MiB'}, 'config': {}}
flux = {'kind': 'Kustomization', 'metadata': {'name': 'stream-owner', 'namespace': 'controllers'},
        'status': {'lastAppliedRevision': 'main@sha1:0123abcd'}}
full = {'deployment': deployment, 'flux': flux, 'expected-tuning': expected}
cases = []


def case(name, binding, files=None, code=None, **receipt):
    cases.append((name, binding, deepcopy(full if files is None else files),
                  code if code is not None else {'match': 0, 'mismatch': 3, 'unobservable': 2}[binding],
                  receipt))


case('bound Kustomization', 'match', namespace='controllers')
for key in ('name', 'namespace'):
    f = deepcopy(full); f['flux']['metadata'][key] = 'foreign'
    case('foreign Kustomization ' + key, 'mismatch', f)
for key in (kname, kns):
    f = deepcopy(full); del f['deployment']['metadata']['labels'][key]
    case('missing ownership ' + key, 'unobservable', f)
for value in (None, {}, [], 'bad', 42):
    f = deepcopy(full); f['deployment']['metadata']['labels'] = value
    case('invalid ownership labels ' + repr(value), 'unobservable', f)
f = deepcopy(full); del f['deployment']['metadata']['labels']
case('absent ownership labels', 'unobservable', f)
f = deepcopy(full); del f['flux']['metadata']
case('absent Flux metadata', 'unobservable', f)
for value in (None, {}, [], 'bad', 42):
    f = deepcopy(full); f['flux']['metadata'] = value
    case('invalid Flux metadata ' + repr(value), 'unobservable', f)
for key in ('name', 'namespace'):
    for value in (None, '', [], {}, 42, True):
        f = deepcopy(full); f['flux']['metadata'][key] = value
        case('invalid Flux ' + key + ' ' + repr(value), 'unobservable', f)
    f = deepcopy(full); del f['flux']['metadata'][key]
    case('absent Flux ' + key, 'unobservable', f)
f = deepcopy(full); del f['flux']['status']['lastAppliedRevision']
case('bound but no revision', 'match', f, code=2)
f = deepcopy(full); f['expected-tuning']['config'] = {'read_ahead_mib': 64}
case('bound but config unobserved', 'match', f, code=2)

helm = deepcopy(full)
helm['flux'] = {'kind': 'HelmRelease', 'metadata': {'name': 'chart', 'namespace': 'media'},
                'spec': {}, 'status': {'lastAppliedRevision': '1.2.3'}}
helm['deployment']['metadata'].pop('labels')
helm['deployment']['metadata']['annotations'] = {hname: 'chart', hns: 'media'}
case('Helm defaults', 'match', helm, namespace='media', release_name='chart',
     target_namespace='media', storage_namespace='media')
for key in ('name', 'namespace'):
    f = deepcopy(helm); f['flux']['metadata'][key] = 'foreign'
    case('unrelated HelmRelease ' + key, 'mismatch', f)
for value in (None, {}, [], 'bad'):
    f = deepcopy(helm); f['deployment']['metadata']['annotations'] = value
    case('unbound Helm annotations ' + repr(value), 'unobservable', f)
for key in (hname, hns):
    f = deepcopy(helm); del f['deployment']['metadata']['annotations'][key]
    case('missing Helm annotation ' + key, 'unobservable', f)
    f = deepcopy(helm); f['deployment']['metadata']['annotations'][key] = 'foreign'
    case('wrong Helm annotation ' + key, 'mismatch', f)
f = deepcopy(helm); f['flux']['metadata']['namespace'] = 'controllers'
f['flux']['spec'] = {'releaseName': 'stream-release', 'targetNamespace': 'media',
                     'storageNamespace': 'helm-storage'}
f['deployment']['metadata']['annotations'][hname] = 'stream-release'
case('Helm override cross-namespace separate storage', 'match', f, namespace='controllers',
     release_name='stream-release', target_namespace='media', storage_namespace='helm-storage')
f['deployment']['metadata']['annotations'][hns] = 'helm-storage'
case('storage namespace is not release namespace', 'mismatch', f)
f = deepcopy(helm); f['flux']['metadata']['namespace'] = 'controllers'
f['flux']['spec'] = {'targetNamespace': 'media'}
f['deployment']['metadata']['annotations'][hname] = 'media-chart'
case('Helm generated cross-namespace name', 'match', f, release_name='media-chart',
     target_namespace='media', storage_namespace='controllers')
f['flux']['metadata']['namespace'] = 'media'
case('explicit same namespace still prefixes release name', 'match', f, release_name='media-chart')
f['deployment']['metadata']['annotations'][hname] = 'chart'
case('HelmRelease object name is not generated release name', 'mismatch', f)
for key in ('releaseName', 'targetNamespace', 'storageNamespace'):
    f = deepcopy(helm); f['flux']['spec'][key] = []
    case('malformed Helm ' + key, 'unobservable', f)
for value in (None, [], 'bad'):
    f = deepcopy(helm); f['flux']['spec'] = value
    case('malformed Helm spec ' + repr(value), 'unobservable', f)
f = deepcopy(helm); del f['flux']['spec']
case('missing Helm spec cannot assume defaults', 'unobservable', f)
for key in ('name', 'namespace'):
    for value in (None, '', [], {}, 42, True):
        f = deepcopy(helm); f['flux']['metadata'][key] = value
        case('invalid Helm metadata ' + key + ' ' + repr(value), 'unobservable', f)
f = deepcopy(helm)
f['deployment']['metadata']['labels'] = {'helm.toolkit.fluxcd.io/name': 'chart',
                                       'helm.toolkit.fluxcd.io/namespace': 'media'}
case('Helm origin labels agree', 'match', f)
for key in ('name', 'namespace'):
    foreign = deepcopy(f); foreign['deployment']['metadata']['labels']['helm.toolkit.fluxcd.io/' + key] = 'foreign'
    case('Helm origin label conflicts ' + key, 'mismatch', foreign)
f['deployment']['metadata']['labels'].pop('helm.toolkit.fluxcd.io/name')
case('partial Helm origin labels', 'unobservable', f)
for size, release in ((53, 'a' * 53), (54, 'a' * 40 + '-a3f01b693925')):
    f = deepcopy(helm); f['flux']['metadata']['name'] = 'a' * size
    f['deployment']['metadata']['annotations'][hname] = release
    case('generated Helm name length ' + str(size), 'match', f, release_name=release)
f = deepcopy(helm)
f['flux']['status'] = {'lastAttemptedRevision': '1.2.3', 'history': [{'chartVersion': '1.2.3'}]}
case('Helm attempted revision is not applied proof', 'match', f, code=2)
f = deepcopy(full); f['flux']['status']['lastAppliedRevision'] = '   '
case('blank revision unobserved', 'match', f, code=2)

failures = 0
for name, binding, files, code, receipt in cases:
    with tempfile.TemporaryDirectory() as tmp:
        argv = [str(engine), '--drift', '--namespace', 'media', '--name', 'stream',
                '--expected-image', 'example.com/stream:v2']
        for flag, data in files.items():
            path = Path(tmp) / (flag + '.json')
            path.write_text(json.dumps(data))
            argv += ['--' + flag, str(path)]
        out = subprocess.run(argv, capture_output=True, text=True)
    try:
        verdict, body = out.stdout.split(' ', 1)
        report = json.loads(body)
        assert out.returncode == code, (out.returncode, code)
        assert verdict == {0: 'DRIFT_MATCH', 2: 'DRIFT_INCOMPLETE', 3: 'DRIFT_MISMATCH'}[code]
        assert report['rollout_at'] is None
        assert report['flux']['binding'] == binding
        assert 'namespace' in report['flux']
        observed = binding == 'match' and bool((files['flux']['status'].get('lastAppliedRevision') or '').strip())
        assert report['flux']['state'] == ('observed' if observed else 'unobservable')
        for key, value in receipt.items():
            assert report['flux'][key] == value, key
        assert not out.stderr, out.stderr
        print('ok   ' + name)
    except (AssertionError, KeyError, ValueError) as error:
        failures += 1
        print(f'FAIL {name}: {error}; rc={out.returncode} {out.stdout.strip()} {out.stderr.strip()}')

# The drift suite in turn runs both acceptance and prerollout suites.
result = subprocess.run([str(here / 'gostream-tuning-drift.test.sh')], capture_output=True, text=True)
print(result.stdout, end='')
if result.returncode:
    failures += 1
    print(result.stderr)
print(f'Flux ownership: {len(cases)} cases, {failures} failures (including existing suites)')
sys.exit(1 if failures else 0)
PY
