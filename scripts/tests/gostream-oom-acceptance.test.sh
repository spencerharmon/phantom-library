#!/usr/bin/env bash
set -euo pipefail
exec python3 - "${BASH_SOURCE[0]}" <<'PY'
import copy
from datetime import datetime
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

engine = Path(sys.argv[1]).resolve().parents[1] / 'gostream-oom-acceptance.sh'
assert os.access(engine, os.X_OK), f'missing executable: {engine}'
subprocess.run(['bash', '-n', str(engine)], check=True)
now = '2026-11-06T12:00:00Z'
epoch = datetime.fromisoformat(now.replace('Z', '+00:00')).timestamp()
rollout = '2026-11-06T10:00:00Z'
container = {'name': 'stream', 'image': 'example.com/stream:v2',
             'env': [{'name': 'MKV_PROXY_CONCURRENCY', 'value': '6'}],
             'resources': {'limits': {'memory': '1Gi'}}}
pod = {'kind': 'Pod', 'metadata': {'name': 'stream-abc-xyz', 'namespace': 'media',
       'uid': 'pod-1', 'creationTimestamp': rollout, 'labels': {'app': 'stream'},
       'ownerReferences': [{'kind': 'ReplicaSet', 'name': 'stream-abc', 'uid': 'rs-1', 'controller': True}]},
       'spec': {'containers': [container]}, 'status': {'phase': 'Running', 'containerStatuses': [
           {'name': 'stream', 'ready': True, 'restartCount': 0, 'containerID': 'containerd://one',
            'state': {'running': {'startedAt': rollout}}, 'lastState': {}}]}}
deployment = {'kind': 'Deployment', 'metadata': {'name': 'stream', 'namespace': 'media', 'generation': 2, 'uid': 'deploy-1'},
              'spec': {'replicas': 1, 'selector': {'matchLabels': {'app': 'stream'}},
                       'template': {'spec': {'containers': [container]}}},
              'status': {'observedGeneration': 2, 'replicas': 1, 'updatedReplicas': 1,
                         'readyReplicas': 1, 'availableReplicas': 1}}


def vector(value):
    return {'status': 'success', 'data': {'resultType': 'vector', 'result': [
        {'metric': {'namespace': 'media', 'pod': 'stream-abc-xyz', 'container': 'stream'},
         'value': [epoch, str(value)]}]}}


base = {'pods': pod, 'deployment': deployment, 'rss': vector(500000000), 'limits': vector(1073741824),
        'expected-tuning': {'container': 'stream', 'rollout_at': rollout,
                            'env': {'MKV_PROXY_CONCURRENCY': '6'}, 'config': {}},
        'coverage': {'deployment_uid': 'deploy-1', 'rollout_at': rollout, 'observed_until': now,
                     'replicaset_owners': {'rs-1': 'deploy-1'}, 'pod_uids_since_rollout': ['pod-1'],
                     'rss': [{'namespace': 'media', 'pod': 'stream-abc-xyz', 'container': 'stream',
                              'first_sample_at': rollout, 'last_sample_at': now, 'max_gap_seconds': 60}]}}
cases = []


def case(name, code, word, mutate=lambda d: None, contains=()):
    # Break shared references as a JSON capture would.
    data = json.loads(json.dumps(base))
    mutate(data)
    cases.append((name, data, code, word, contains))


def state(d):
    return d['pods']['status']['containerStatuses'][0]


def oom(d, current=False):
    state(d)['restartCount'] = 10
    state(d)['lastState' if not current else 'state'] = {'terminated': {
        'reason': 'OOMKilled', 'finishedAt': '2026-11-06T11:22:31Z'}}
    state(d)['state'] = state(d)['state'] if current else {'running': {'startedAt': '2026-11-06T11:23:00Z'}}
    d['rss']['data']['result'][0]['value'][1] = str(Decimal('0.942') * 1073741824)


from decimal import Decimal
case('accepted literal environment', 0, 'ACCEPTED')
case('admission-injected service account mount', 0, 'ACCEPTED',
     lambda d: d['pods']['spec']['containers'][0].update(volumeMounts=[{
         'name': 'kube-api-access-abc', 'mountPath': '/var/run/secrets/kubernetes.io/serviceaccount'}]))
case('missing history coverage', 2, 'INCOMPLETE', lambda d: d.pop('coverage'))
case('replaced pod missing from evidence', 2, 'INCOMPLETE',
     lambda d: d['coverage']['pod_uids_since_rollout'].append('deleted-pod'))
case('fresh evaluation with stale source samples', 2, 'INCOMPLETE',
     lambda d: d['coverage']['rss'][0].update(last_sample_at=rollout))
case('gapped RSS sampling', 2, 'INCOMPLETE', lambda d: d['coverage']['rss'][0].update(max_gap_seconds=601))
case('foreign ReplicaSet despite matching name prefix', 2, 'INCOMPLETE',
     lambda d: d['coverage']['replicaset_owners'].update({'rs-1': 'other-deployment'}))
case('day25 OOM takes precedence over 94.2 percent RSS', 4, 'OOM_SINCE_ROLLOUT', oom,
     ('finished_at=2026-11-06T11:22:31Z',))
case('currently OOMKilled', 4, 'OOM_SINCE_ROLLOUT', lambda d: oom(d, True))
case('exact 90 percent breaches', 5, 'HEADROOM_BREACH',
     lambda d: d['rss']['data']['result'][0]['value'].__setitem__(1, str(Decimal('.9') * 1073741824)),
     ('rss_percent=90.000',))
case('just below 90 percent accepted', 0, 'ACCEPTED',
     lambda d: d['rss']['data']['result'][0]['value'].__setitem__(1, str(Decimal('.89999') * 1073741824)))
case('old pod env', 3, 'TUNING_ABSENT',
     lambda d: d['pods']['spec']['containers'][0]['env'][0].update(value='25'))
case('desired env also wrong', 3, 'TUNING_ABSENT', lambda d: (
     d['pods']['spec']['containers'][0]['env'][0].update(value='25'),
     d['deployment']['spec']['template']['spec']['containers'][0]['env'][0].update(value='25')))
case('old pod predates rollout', 3, 'TUNING_ABSENT',
     lambda d: d['pods']['metadata'].update(creationTimestamp='2026-11-06T09:00:00Z'))
case('unresolved startup env', 2, 'INCOMPLETE', lambda d: (
     d['pods']['spec']['containers'][0].update(env=[]),
     d['deployment']['spec']['template']['spec']['containers'][0].update(env=[])))
case('missing mounted config evidence', 2, 'INCOMPLETE',
     lambda d: d['expected-tuning']['config'].update(read_ahead_budget_mb=64))


def runtime(d):
    d['expected-tuning']['config'] = {'read_ahead_budget_mb': 64}
    d['runtime'] = [{'pod_uid': 'pod-1', 'container': 'stream', 'container_id': 'containerd://one',
                     'started_at': rollout, 'captured_at': now,
                     'env': {'MKV_PROXY_CONCURRENCY': '6'}, 'config': {'read_ahead_budget_mb': 64}}]


case('process and config receipt accepted', 0, 'ACCEPTED', runtime)
case('process missing expected env', 3, 'TUNING_ABSENT', lambda d: (
     runtime(d), d['runtime'][0].update(env={})))
case('loaded config wrong', 3, 'TUNING_ABSENT', lambda d: (
     runtime(d), d['runtime'][0]['config'].update(read_ahead_budget_mb=256)))
case('old process receipt', 2, 'INCOMPLETE', lambda d: (
     runtime(d), d['runtime'][0].update(container_id='containerd://old')))
case('stale process receipt', 2, 'INCOMPLETE', lambda d: (
     runtime(d), d['runtime'][0].update(captured_at=rollout)))
case('truncated non-OOM restart history', 2, 'INCOMPLETE', lambda d: state(d).update(restartCount=10))
case('unready container', 2, 'INCOMPLETE', lambda d: state(d).update(ready=False))
case('unobserved deployment generation', 2, 'INCOMPLETE',
     lambda d: d['deployment']['status'].update(observedGeneration=1))
case('partial rollout', 2, 'INCOMPLETE', lambda d: d['deployment']['status'].update(updatedReplicas=0))
case('missing replica', 2, 'INCOMPLETE', lambda d: d['deployment']['spec'].update(replicas=2))
case('empty expectations', 2, 'INCOMPLETE', lambda d: d['expected-tuning'].update(env={}))
case('future rollout', 2, 'INCOMPLETE', lambda d: d['expected-tuning'].update(rollout_at='2026-11-07T10:00:00Z'))
case('future OOM timestamp', 2, 'INCOMPLETE', lambda d: (
     oom(d), state(d)['lastState']['terminated'].update(finishedAt='2026-11-07T11:22:31Z')))
case('foreign pod', 2, 'INCOMPLETE', lambda d: d['pods']['metadata'].update(namespace='other'))
for source in ('rss', 'limits'):
    case(f'{source} empty', 2, 'INCOMPLETE', lambda d, s=source: d[s]['data'].update(result=[]))
    case(f'{source} missing', 2, 'INCOMPLETE', lambda d, s=source: d.pop(s))
    case(f'{source} malformed', 2, 'INCOMPLETE', lambda d, s=source: d.update({s: '{bad'}))
    case(f'{source} duplicate JSON keys', 2, 'INCOMPLETE',
         lambda d, s=source: d.update({s: '{"status":"error","status":"success"}'}))
    case(f'{source} failed query', 2, 'INCOMPLETE', lambda d, s=source: d[s].update(status='error'))
    case(f'{source} stale', 2, 'INCOMPLETE',
         lambda d, s=source: d[s]['data']['result'][0]['value'].__setitem__(0, epoch - 301))
    case(f'{source} foreign container', 2, 'INCOMPLETE',
         lambda d, s=source: d[s]['data']['result'][0]['metric'].update(container='sidecar'))
    case(f'{source} duplicate series', 2, 'INCOMPLETE',
         lambda d, s=source: d[s]['data']['result'].append(copy.deepcopy(d[s]['data']['result'][0])))
    for bad in ('NaN', 'Inf', '-1', True, None, '1e1000000'):
        case(f'{source} invalid value {bad}', 2, 'INCOMPLETE',
             lambda d, s=source, v=bad: d[s]['data']['result'][0]['value'].__setitem__(1, v))
case('zero limit', 2, 'INCOMPLETE', lambda d: d['limits']['data']['result'][0]['value'].__setitem__(1, '0'))
case('metrics disagree on evaluation time', 2, 'INCOMPLETE',
     lambda d: d['rss']['data']['result'][0]['value'].__setitem__(0, epoch - 1))
case('missing OOM timestamp', 2, 'INCOMPLETE', lambda d: (oom(d), state(d)['lastState']['terminated'].pop('finishedAt')))
case('OOM exactly at rollout boundary', 4, 'OOM_SINCE_ROLLOUT', lambda d: (
     oom(d), state(d)['lastState']['terminated'].update(finishedAt=rollout)))


def old_rollout(d):
    old = '2026-11-05T10:00:00Z'
    d['expected-tuning']['rollout_at'] = old
    d['coverage']['rollout_at'] = old
    d['coverage']['rss'][0]['first_sample_at'] = old
    d['pods']['metadata']['creationTimestamp'] = old
    state(d)['state']['running']['startedAt'] = old


case('24h maximum cannot prove older rollout', 2, 'INCOMPLETE', old_rollout)


def two_pods(d):
    second = copy.deepcopy(d['pods'])
    second['metadata'].update(name='stream-abc-second', uid='pod-2')
    d['pods'] = {'kind': 'List', 'items': [d['pods'], second]}
    d['deployment']['spec']['replicas'] = 2
    for key in ('replicas', 'updatedReplicas', 'readyReplicas', 'availableReplicas'):
        d['deployment']['status'][key] = 2
    d['coverage']['pod_uids_since_rollout'].append('pod-2')
    sample = copy.deepcopy(d['coverage']['rss'][0])
    sample['pod'] = 'stream-abc-second'
    d['coverage']['rss'].append(sample)
    for key in ('rss', 'limits'):
        row = copy.deepcopy(d[key]['data']['result'][0])
        row['metric']['pod'] = 'stream-abc-second'
        d[key]['data']['result'].append(row)


case('all replicas accepted', 0, 'ACCEPTED', two_pods)
case('missing second replica metric', 2, 'INCOMPLETE', lambda d: (
     two_pods(d), d['rss']['data']['result'].pop()))

with tempfile.TemporaryDirectory(prefix='gostream-oom-test-') as tmp:
    for name, data, code, word, contains in cases:
        command = [str(engine), '--now', now]
        before = {}
        for key in ('pods', 'deployment', 'rss', 'limits', 'expected-tuning', 'runtime', 'coverage'):
            if key in ('runtime', 'coverage') and key not in data:
                continue
            path = Path(tmp) / (key + '.json')
            if path.exists():
                path.unlink()
            if key in data:
                path.write_text(data[key] if isinstance(data[key], str) else json.dumps(data[key]))
                before[path] = path.read_bytes()
            command += ['--' + key, str(path)]
        result = subprocess.run(command, text=True, capture_output=True, timeout=10)
        assert result.returncode == code, (name, result.returncode, result.stdout, result.stderr)
        assert result.stdout.startswith(word + ' ') and len(result.stdout.splitlines()) == 1, (name, result.stdout)
        assert not result.stderr, (name, result.stderr)
        for text in contains:
            assert text in result.stdout, (name, result.stdout)
        assert all(path.read_bytes() == value for path, value in before.items()), name
        print('PASS', name)
    result = subprocess.run([str(engine)], text=True, capture_output=True, timeout=10)
    assert result.returncode == 2 and result.stdout.startswith('INCOMPLETE '), result
print(f'PASS {len(cases) + 1} offline acceptance cases')
PY
