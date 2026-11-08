#!/usr/bin/env bash
set -euo pipefail
exec python3 - "${BASH_SOURCE[0]}" <<'PY'
import copy
from datetime import datetime
import json
from pathlib import Path
import subprocess
import sys
import tempfile

engine = Path(sys.argv[1]).resolve().parents[1] / 'gostream-oom-acceptance.sh'
now = '2026-11-08T00:55:28Z'
epoch = datetime.fromisoformat(now.replace('Z', '+00:00')).timestamp()
begin = '2026-11-07T00:55:28Z'
finished = '2026-11-07T21:09:58Z'
old, current = 'b' * 64, 'a' * 64
uid = '11111111-2222-3333-4444-555555555555'


def cgroup(cid):
    return ('/kubepods.slice/kubepods-burstable.slice/kubepods-burstable-pod'
            + uid.replace('-', '_') + '.slice/cri-containerd-' + cid + '.scope')


def vector(rows):
    return {'status': 'success', 'data': {'resultType': 'vector', 'result': [
        {'metric': {'namespace': 'media', 'pod': 'stream-abc-xyz', 'container': 'stream',
                    'id': cgroup(cid), 'name': cid}, 'value': [epoch, str(value)]}
        for cid, value in rows]}}


# Sanitized day26 replay: exact event/start times, restarts, RSS maxima and 3Gi limit;
# deployment, pod, ReplicaSet and container identifiers are neutral test identities.
container = {'name': 'stream', 'image': 'example.com/stream:v1',
             'env': [{'name': 'MKV_PROXY_CONCURRENCY', 'value': '25'}],
             'resources': {'limits': {'memory': '3Gi'}}}
base = {
    'expected-tuning': {'container': 'stream', 'rollout_at': None,
                        'env': {'MKV_PROXY_CONCURRENCY': '6'},
                        'config': {'read_ahead_budget_mb': 64}},
    'deployment': {'kind': 'Deployment', 'metadata': {'namespace': 'media', 'name': 'stream',
                    'uid': 'deploy-1', 'generation': 2},
                   'spec': {'replicas': 1, 'selector': {'matchLabels': {'app': 'stream'}},
                            'template': {'spec': {'containers': [container]}}}, 'status': {}},
    'replicasets': {'kind': 'ReplicaSet', 'metadata': {'namespace': 'media', 'name': 'stream-abc',
                    'uid': 'rs-1', 'ownerReferences': [{'controller': True, 'kind': 'Deployment',
                    'name': 'stream', 'uid': 'deploy-1'}]}},
    'pods': {'kind': 'Pod', 'metadata': {'namespace': 'media', 'name': 'stream-abc-xyz', 'uid': uid,
             'creationTimestamp': '2026-09-17T22:35:21Z', 'labels': {'app': 'stream'},
             'ownerReferences': [{'controller': True, 'kind': 'ReplicaSet', 'name': 'stream-abc', 'uid': 'rs-1'}]},
             'spec': {'containers': [container]}, 'status': {'phase': 'Running', 'containerStatuses': [
                 {'name': 'stream', 'containerID': 'containerd://' + current, 'restartCount': 11, 'ready': True,
                  'state': {'running': {'startedAt': '2026-11-07T21:12:36Z'}},
                  'lastState': {'terminated': {'reason': 'OOMKilled', 'finishedAt': finished,
                      'startedAt': '2026-11-06T11:22:31Z', 'containerID': 'containerd://' + old}}}]}},
    'rss': vector([(current, 2544054272), (old, 2535596032)]),
    'limits': vector([(current, 3221225472)])}


def state(d):
    return d['pods']['status']['containerStatuses'][0]


def previous(d):
    return state(d)['lastState']['terminated']


cases = []


def case(name, mutate=lambda d: None, code=6, contains=(), flags=None):
    data = json.loads(json.dumps(base))
    mutate(data)
    cases.append((name, data, code, contains, flags))


case('sanitized day26 OOM with missing rollout', contains=(finished, old, current, '2535596032',
     '2544054272', 'historical limit unavailable'))
case('ten restarts before latest incident', lambda d: state(d).update(restartCount=10))
case('no OOM but truncated history', lambda d: previous(d).update(reason='Error'), code=2,
     contains=('event_history', 'unknown'))
case('lastState lost', lambda d: state(d).update(lastState={}), code=2)
case('outside observation interval', code=2, contains=('outside_observation_interval',),
     flags=['--diagnostic', '--observed-from', '2026-11-07T22:00:00Z', '--observed-until', now])
case('inclusive observation boundary', flags=['--diagnostic', '--observed-from', finished,
     '--observed-until', finished])
case('current OOM', lambda d: state(d).update(state={'terminated': {
     'reason': 'OOMKilled', 'startedAt': '2026-11-07T21:12:36Z',
     'finishedAt': '2026-11-07T22:00:00Z', 'containerID': 'containerd://' + current}}),
     contains=('state', '2026-11-07T22:00:00Z'))
case('supplied rollout still cannot accept diagnostics',
     lambda d: d['expected-tuning'].update(rollout_at=begin))
case('historical limit mismatch', lambda d: d['limits']['data']['result'].extend(
     vector([(old, 1073741824)])['data']['result']), contains=('limit mismatch',))
case('unproved older container retained', lambda d: d['rss']['data']['result'].extend(
     vector([('c' * 64, 2000000000)])['data']['result']), contains=('c' * 64, 'unproved ID association'))
case('wrong cgroup pod UID', lambda d: d['rss']['data']['result'][1]['metric'].update(
     id=cgroup(old).replace(uid.replace('-', '_'), 'foreign-pod')), contains=('unproved ID association',))
case('all per-ID limits retained', lambda d: d['limits']['data']['result'].extend(
     vector([(old, 3221225472)])['data']['result']))
case('empty metric history does not hide OOM', lambda d: (
     d['rss']['data'].update(result=[]), d['limits']['data'].update(result=[])))
case('unlabelled cgroup remains unavailable', lambda d: d['rss']['data']['result'][1]['metric'].pop('id'),
     contains=('unproved ID association',))
case('mismatched container label remains unavailable', lambda d:
     d['rss']['data']['result'][1]['metric'].update(name='other'), contains=('unproved ID association',))
for flow in ('movie', 'episode'):
    case(f'{flow} uses same shared-service evidence', lambda d, f=flow:
         d['pods']['metadata'].update(annotations={'example.com/playback-type': f}))

for name, mutate, text in (
    ('bad finish', lambda d: previous(d).update(finishedAt='not-a-time'), 'timestamp'),
    ('future finish', lambda d: previous(d).update(finishedAt='2027-01-01T00:00:00Z'), 'lifetime'),
    ('finish before creation', lambda d: previous(d).update(finishedAt='2026-01-01T00:00:00Z'), 'lifetime'),
    ('bad terminated start', lambda d: previous(d).update(startedAt='bad'), 'timestamp'),
    ('start after finish', lambda d: previous(d).update(startedAt=now), 'termination start'),
    ('previous after current start', lambda d: state(d)['state']['running'].update(startedAt=begin), 'current start'),
    ('missing event ID', lambda d: previous(d).pop('containerID'), 'containerID'),
    ('invalid event ID', lambda d: previous(d).update(containerID='bad'), 'container ID'),
    ('same previous and current ID', lambda d: previous(d).update(containerID='containerd://' + current), 'container ID'),
    ('overlapping terminated containers', lambda d: state(d).update(state={'terminated': {
        'reason': 'OOMKilled', 'startedAt': begin, 'finishedAt': now,
        'containerID': 'containerd://' + current}}), 'previous termination'),
    ('zero restarts with lastState', lambda d: state(d).update(restartCount=0), 'zero restarts'),
    ('foreign namespace', lambda d: d['pods']['metadata'].update(namespace='other'), 'foreign'),
    ('foreign selector', lambda d: d['pods']['metadata']['labels'].update(app='other'), 'selector'),
    ('foreign owner name', lambda d: d['pods']['metadata']['ownerReferences'][0].update(name='other'), 'owner'),
    ('foreign owner UID', lambda d: d['replicasets']['metadata']['ownerReferences'][0].update(uid='other'), 'owner'),
    ('foreign ReplicaSet namespace', lambda d: d['replicasets']['metadata'].update(namespace='other'), 'ReplicaSet'),
    ('missing ReplicaSet ownership', lambda d: d['replicasets'].update(kind='List', items=[]), 'owner'),
    ('duplicate RSS ID', lambda d: d['rss']['data']['result'].append(copy.deepcopy(d['rss']['data']['result'][0])), 'duplicate'),
    ('foreign RSS pod', lambda d: d['rss']['data']['result'][0]['metric'].update(pod='other'), 'foreign'),
    ('invalid RSS value', lambda d: d['rss']['data']['result'][0]['value'].__setitem__(1, 'NaN'), 'finite'),
    ('stale RSS', lambda d: d['rss']['data']['result'][0]['value'].__setitem__(0, epoch - 301), 'stale'),
    ('malformed rollout', lambda d: d['expected-tuning'].update(rollout_at='bad'), 'timestamp'),
):
    case(name, mutate, code=2, contains=(text,))
case('missing diagnostic interval', code=2, flags=['--diagnostic'], contains=('observation interval',))
case('reversed interval', code=2, flags=['--diagnostic', '--observed-from', now,
     '--observed-until', begin], contains=('observation interval',))
case('future interval', code=2, flags=['--diagnostic', '--observed-from', begin,
     '--observed-until', '2027-01-01T00:00:00Z'], contains=('observation interval',))
case('acceptance still refuses null rollout', code=2, flags=[], contains=('timestamp',))
case('interval cannot implicitly enable diagnostics', code=2,
     flags=['--observed-from', begin, '--observed-until', now], contains=('require --diagnostic',))

with tempfile.TemporaryDirectory(prefix='gostream-prerollout-') as tmp:
    for name, data, code, contains, flags in cases:
        command = [str(engine), '--now', now]
        command += flags if flags is not None else ['--diagnostic', '--observed-from', begin, '--observed-until', now]
        before = {}
        for key, value in data.items():
            if key == 'replicasets' and '--diagnostic' not in command:
                continue
            path = Path(tmp) / (key + '.json')
            path.write_text(json.dumps(value))
            before[path] = path.read_bytes()
            command += ['--' + key, str(path)]
        result = subprocess.run(command, capture_output=True, text=True, timeout=10)
        assert result.returncode == code, (name, result.returncode, result.stdout, result.stderr)
        assert not result.stderr and 'ACCEPTED' not in result.stdout, (name, result)
        for text in contains:
            assert text in result.stdout, (name, text, result.stdout)
        if result.stdout.startswith('DIAGNOSTIC_'):
            report = json.loads(result.stdout.split(' ', 1)[1])
            assert report['rollout'] in ('unknown', 'unverified')
            assert report['coverage'] == 'unknown' and report['headroom'] == 'unavailable'
            assert report['scope'] == 'shared-service' and report['pods'][0]['restart_count'] in (10, 11)
            assert len(report['rss']) == len(data['rss']['data']['result'])
            assert report['pods'][0]['pod_uid'] == uid
            assert len(report['pods']) == 1
            for retained, raw in zip(report['rss'], data['rss']['data']['result']):
                assert retained['cgroup_id'] == raw['metric'].get('id', '')
                assert retained['rss_bytes'] == raw['value'][1]
                assert retained['headroom'] == 'unavailable'
                assert 'rss_percent' not in retained
            if code == 6:
                assert any(e['reason'] == 'OOMKilled' and e['interval'] == 'observed_in_diagnostic_interval'
                           and e['container_id'].startswith('containerd://') for e in report['events'])
        else:
            assert result.stdout.startswith('INCOMPLETE '), (name, result.stdout)
        assert all(path.read_bytes() == value for path, value in before.items()), name
        print('PASS', name)
        if name == 'sanitized day26 OOM with missing rollout':
            print(result.stdout.strip())
print(f'PASS {len(cases)} offline pre-rollout cases', flush=True)
subprocess.run([str(engine.parent / 'tests/gostream-oom-acceptance.test.sh')], check=True)
PY
