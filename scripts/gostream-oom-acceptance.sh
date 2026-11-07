#!/usr/bin/env bash
# Pure offline analysis; Python's standard library is the only dependency.
set -euo pipefail
exec python3 - "$@" <<'PY'
import argparse
from datetime import datetime
from decimal import Decimal, DecimalException
import json
import re
import sys


def require(condition, message):
    if not condition:
        raise ValueError(message)


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"duplicate JSON key: {key}")
        result[key] = value
    return result


def load(path):
    with open(path) as stream:
        return json.load(stream, object_pairs_hook=unique)


def json_equal(left, right):
    # JSON numbers share a type; Python booleans must not compare as 0/1.
    if type(left) in (int, float) and type(right) in (int, float):
        return left == right
    if type(left) is not type(right):
        return False
    if isinstance(left, dict):
        return left.keys() == right.keys() and all(json_equal(left[k], right[k]) for k in left)
    if isinstance(left, list):
        return len(left) == len(right) and all(json_equal(a, b) for a, b in zip(left, right))
    return left == right


def stamp(value):
    require(isinstance(value, str) and re.fullmatch(
        r'\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)', value),
        "expected timezone-qualified RFC3339 timestamp")
    return Decimal(str(datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp()))


def number(value):
    require(isinstance(value, (str, int, float)) and not isinstance(value, bool), "invalid number")
    result = Decimal(str(value))
    require(result.is_finite() and result >= 0, "expected finite nonnegative number")
    return result


def memory(value):
    match = re.fullmatch(r'(\d+(?:\.\d+)?)([KMGTPE]i|[kMGTPE]|m)?', value)
    require(match is not None, "unsupported Kubernetes memory quantity")
    suffix = match[2] or ''
    scale = Decimal(1)
    if suffix == 'm':
        scale = Decimal('0.001')
    elif suffix:
        scale = Decimal(1024 if suffix.endswith('i') else 1000) ** (
            'KMGTPE'.index(suffix[0].upper()) + 1)
    return Decimal(match[1]) * scale


def named(entries, name):
    matches = [entry for entry in entries if entry['name'] == name]
    require(len(matches) == 1, f"missing/duplicate container {name}")
    return matches[0]


def vector(payload, identities, now):
    require(payload['status'] == 'success' and not payload.get('warnings'), "unsuccessful/partial metric query")
    data = payload['data']
    require(data['resultType'] == 'vector' and isinstance(data['result'], list) and data['result'],
            "missing/empty metric vector")
    result = {}
    for row in data['result']:
        labels = row['metric']
        key = tuple(labels[k] for k in ('namespace', 'pod', 'container'))
        require(key in identities and key not in result, "foreign/duplicate metric identity")
        require(len(row['value']) == 2, "invalid vector sample")
        at, value = map(number, row['value'])
        require(0 <= now - at <= 300, "stale/future metric sample")
        result[key] = (at, value)
    require(set(result) == identities, "metric coverage does not match all pods")
    return result


class Parser(argparse.ArgumentParser):
    def error(self, message):
        raise ValueError(message)


def analyse():
    parser = Parser(description='Offline OOM acceptance. See docs/tasks/gostream-oom-acceptance.md.')
    for flag in ('pods', 'deployment', 'rss', 'limits', 'expected-tuning', 'now'):
        parser.add_argument('--' + flag, required=True)
    parser.add_argument('--runtime', help='JSON array of retained process env/config receipts')
    parser.add_argument('--coverage', help='Retained rollout inventory and RSS sampling coverage receipt')
    args = parser.parse_args()
    now = stamp(args.now)
    coverage = load(args.coverage) if args.coverage else None
    expected = load(args.expected_tuning)
    require(set(expected) == {'container', 'rollout_at', 'env', 'config'}, "invalid expected-tuning fields")
    container = expected['container']
    require(isinstance(container, str) and container, "missing expected container")
    rollout = stamp(expected['rollout_at'])
    require(rollout <= now, "future rollout")
    for key in ('env', 'config'):
        require(isinstance(expected[key], dict), f"expected {key} must be an object")
    require(expected['env'] or expected['config'], "empty tuning expectation")
    require(all(isinstance(k, str) and k and isinstance(v, str) for k, v in expected['env'].items()),
            "expected env must map names to strings")
    deploy = load(args.deployment)
    require(deploy['kind'] == 'Deployment', "expected Deployment")
    namespace = deploy['metadata']['namespace']
    spec, status = deploy['spec'], deploy['status']
    desired = spec.get('replicas', 1)
    require(type(desired) is int and desired > 0, "no desired replicas")
    require(status['observedGeneration'] == deploy['metadata']['generation'], "deployment generation not observed")
    require(all(status.get(k, 0) == desired for k in
                ('replicas', 'updatedReplicas', 'readyReplicas', 'availableReplicas'))
            and status.get('unavailableReplicas', 0) == 0, "deployment rollout incomplete")
    selector = spec['selector']
    require(selector.get('matchLabels') and not selector.get('matchExpressions'), "unsupported/empty deployment selector")
    template = named(spec['template']['spec']['containers'], container)
    payload = load(args.pods)
    pods = [payload] if payload.get('kind') == 'Pod' else payload['items']
    require(isinstance(pods, list) and len(pods) == desired, "pod set must cover exactly all deployment replicas")
    receipts = load(args.runtime) if args.runtime else []
    require(isinstance(receipts, list), "runtime receipts must be an array")
    runtime = {}
    for receipt in receipts:
        key = (receipt['pod_uid'], receipt['container'])
        require(key not in runtime, "duplicate runtime receipt")
        runtime[key] = receipt
    identities, details, absent, ooms, gaps = set(), [], [], [], []
    pod_uids = set()
    if coverage is None:
        gaps.append('coverage receipt required for rollout inventory and RSS sampling continuity')
    else:
        require(coverage['deployment_uid'] == deploy['metadata']['uid']
                and stamp(coverage['rollout_at']) == rollout, 'coverage receipt identifies a different rollout')
        require(0 <= now - stamp(coverage['observed_until']) <= 300,
                'stale/future coverage receipt')
    for pod in pods:
        require(pod['kind'] == 'Pod', "expected Pod")
        meta = pod['metadata']
        require(meta['namespace'] == namespace and not meta.get('deletionTimestamp'), "foreign/deleting pod")
        require(all(meta['labels'].get(k) == v for k, v in selector['matchLabels'].items()), "pod selector mismatch")
        owners = [o for o in meta['ownerReferences'] if o.get('controller') is True]
        require(len(owners) == 1 and owners[0]['kind'] == 'ReplicaSet'
                and owners[0]['name'].startswith(deploy['metadata']['name'] + '-'), "pod owner mismatch")
        if coverage is not None:
            require(coverage['replicaset_owners'][owners[0]['uid']] == deploy['metadata']['uid'],
                    'ReplicaSet belongs to a different deployment')
        require(meta['uid'] not in pod_uids, 'duplicate pod UID')
        pod_uids.add(meta['uid'])
        created = stamp(meta['creationTimestamp'])
        require(created <= now, "future pod creation")
        key = (namespace, meta['name'], container)
        require(key not in identities, "duplicate pod identity")
        identities.add(key)
        live = named(pod['spec']['containers'], container)
        state = named(pod['status']['containerStatuses'], container)
        limit = memory(live['resources']['limits']['memory'])
        require(limit > 0 and limit == memory(template['resources']['limits']['memory']), "incoherent memory limits")
        if created < rollout:
            absent.append(f"{meta['name']}: pod predates tuning rollout")
        for field in ('image', 'command', 'args', 'env', 'envFrom'):
            if live.get(field) != template.get(field):
                absent.append(f"{meta['name']}: template {field} mismatch")
        env = {}
        for entry in live.get('env', []):
            require(entry['name'] not in env, "duplicate environment key")
            env[entry['name']] = entry.get('value')
        for field in ('state', 'lastState'):
            value = state.get(field, {})
            if 'terminated' in value:
                term = value['terminated']
                finished = stamp(term['finishedAt'])
                require(created <= finished <= now, "termination timestamp outside pod lifetime")
                require(isinstance(term['reason'], str) and term['reason'], "missing termination reason")
                if term['reason'] == 'OOMKilled' and finished >= rollout:
                    ooms.append(f"{meta['name']} finished_at={term['finishedAt']}")
        restarts = state['restartCount']
        require(type(restarts) is int and restarts >= 0, "invalid restart count")
        previous = state.get('lastState', {}).get('terminated')
        require(not previous or restarts > 0, "previous termination with zero restarts")
        if restarts > 1 or (restarts and not previous):
            gaps.append('restart history truncated by Kubernetes lastState')
        running = state.get('state', {}).get('running')
        if not running or state.get('ready') is not True or pod['status']['phase'] != 'Running':
            gaps.append('container not running and ready')
        started = stamp(running['startedAt']) if running else None
        if started is not None:
            require(created <= started <= now, "invalid container start time")
            if previous:
                require(stamp(previous['finishedAt']) <= started,
                        "previous termination follows current start")
        receipt = runtime.pop((meta['uid'], container), None)
        if receipt is not None:
            require(receipt['container_id'] == state['containerID'] and started is not None
                    and stamp(receipt['started_at']) == started, "runtime receipt identifies a different process")
            captured = stamp(receipt['captured_at'])
            require(started <= captured <= now and now - captured <= 300, "stale/future runtime receipt")
            require(isinstance(receipt['env'], dict) and isinstance(receipt['config'], dict), "invalid runtime maps")
        for name, value in expected['env'].items():
            observed = receipt['env'].get(name) if receipt is not None else env.get(name)
            if receipt is None and observed is None and (live.get('envFrom') or name not in env or env[name] is None):
                gaps.append(f'{name}: process receipt required for startup-exported/referenced env')
            elif observed != value:
                absent.append(f"{meta['name']}: env {name} mismatch")
        if expected['config'] and receipt is None:
            gaps.append('runtime receipt required for loaded config')
        elif receipt is not None:
            for name, value in expected['config'].items():
                if name not in receipt['config'] or not json_equal(receipt['config'][name], value):
                    absent.append(f"{meta['name']}: config {name} mismatch")
        details.append((key, limit, created))
    require(not runtime, "foreign runtime receipt")
    if coverage is not None:
        inventory = coverage['pod_uids_since_rollout']
        require(isinstance(inventory, list) and len(inventory) == len(set(inventory)),
                'invalid rollout pod inventory')
        if set(inventory) != pod_uids:
            gaps.append('replaced/missing pods leave rollout OOM history incomplete')
        samples = {}
        for sample in coverage['rss']:
            key = tuple(sample[k] for k in ('namespace', 'pod', 'container'))
            require(key in identities and key not in samples, 'foreign/duplicate RSS coverage identity')
            samples[key] = sample
        require(set(samples) == identities, 'missing RSS sampling coverage')
    rss = vector(load(args.rss), identities, now)
    limits = vector(load(args.limits), identities, now)
    breaches = []
    for key, limit, created in details:
        require(rss[key][0] == limits[key][0] and rss[key][0] >= created,
                "incoherent metric evaluation times")
        require(limits[key][1] == limit, "metric limit differs from pod limit")
        if coverage is not None:
            sample = samples[key]
            first, last = stamp(sample['first_sample_at']), stamp(sample['last_sample_at'])
            require(created <= first <= last <= rss[key][0], 'invalid RSS coverage timestamps')
            if (first > max(rollout, created) + 300 or rss[key][0] - last > 300
                    or number(sample['max_gap_seconds']) > 300):
                gaps.append('RSS sampling does not continuously cover the rollout interval')
        percent = rss[key][1] * 100 / limit
        if percent >= 90:
            breaches.append(f'{key[1]} rss_percent={percent:.3f}')
    # Concrete negative evidence survives truncated history; acceptance never does.
    if absent:
        return 3, 'TUNING_ABSENT', '; '.join(absent)
    if ooms:
        return 4, 'OOM_SINCE_ROLLOUT', '; '.join(ooms)
    if breaches:
        return 5, 'HEADROOM_BREACH', '; '.join(breaches)
    if now - rollout > 86400:
        gaps.append('24h RSS vector cannot cover the full interval since rollout')
    require(not gaps, '; '.join(gaps))
    return 0, 'ACCEPTED', f'rollout_at={expected["rollout_at"]} pods={len(pods)}'


try:
    code, verdict, detail = analyse()
except (ValueError, KeyError, TypeError, IndexError, AttributeError, OSError, DecimalException, OverflowError) as error:
    code, verdict, detail = 2, 'INCOMPLETE', str(error)
print(f'{verdict} {detail}')
sys.exit(code)
PY
