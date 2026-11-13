#!/usr/bin/env bash
# Pure offline analysis; Python's standard library is the only dependency.
set -euo pipefail
exec python3 - "$@" <<'PY'
import argparse
from datetime import datetime
from decimal import Decimal, DecimalException
import hashlib
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


def drift():
    # Read-only drift receipt: which tuning fields the live Deployment lacks.
    # Never sets rollout_at; unobservable never counts as match.
    parser = Parser(description='Offline tuning drift report (--drift).')
    parser.add_argument('--drift', action='store_true', required=True)
    for flag in ('deployment', 'expected-tuning', 'namespace', 'name'):
        parser.add_argument('--' + flag, required=True)
    parser.add_argument('--expected-image', help='Expected container image reference')
    parser.add_argument('--config', help='Legacy mounted config input; never proves loaded values')
    parser.add_argument('--loaded-config', help='Process receipt JSON: container_id and values')
    parser.add_argument('--pods', help='Retained current Pod or List for loaded-config binding')
    parser.add_argument('--replicasets', help='Retained ReplicaSet or List proving Deployment ownership')
    parser.add_argument('--flux', help='Retained Flux Kustomization/HelmRelease JSON')
    args = parser.parse_args()
    expected = load(args.expected_tuning)
    require(isinstance(expected, dict) and set(expected) == {'container', 'rollout_at', 'env', 'config'},
            'invalid expected-tuning fields')
    container = expected['container']
    require(isinstance(container, str) and container, 'missing expected container')
    require(isinstance(expected['env'], dict) and isinstance(expected['config'], dict), 'invalid expected maps')
    require(expected['env'] or expected['config'] or args.expected_image, 'empty tuning expectation')
    require(all(isinstance(v, str) for v in expected['env'].values()), 'expected env must map names to strings')
    deploy = load(args.deployment)
    require(isinstance(deploy, dict) and deploy.get('kind') == 'Deployment', 'expected Deployment')
    meta = deploy['metadata']
    require(meta['namespace'] == args.namespace and meta['name'] == args.name, 'foreign Deployment namespace/name')
    live = named(deploy['spec']['template']['spec']['containers'], container)
    env = {}
    for entry in live.get('env', []):
        require(entry['name'] not in env, 'duplicate environment key')
        env[entry['name']] = entry
    fields = []

    def add(field, want, state, observed):
        fields.append({'field': field, 'expected': want, 'observed': observed, 'state': state})

    for name, want in sorted(expected['env'].items()):
        entry = env.get(name)
        if entry is None:
            # envFrom may export it at startup; absence is only provable without envFrom.
            if live.get('envFrom'):
                add('env.' + name, want, 'unobservable', None)
            else:
                add('env.' + name, want, 'mismatch', None)
        elif 'value' not in entry:
            add('env.' + name, want, 'unobservable', None)
        else:
            add('env.' + name, want, 'match' if entry['value'] == want else 'mismatch', entry['value'])
    loaded = None
    config_reason = 'loaded-config process receipt required; mounted config is not proof'
    if args.loaded_config:
        try:
            receipt = load(args.loaded_config)
            require(isinstance(receipt, dict), 'malformed loaded-config receipt')
            cid, values = receipt['container_id'], receipt['values']
            require(isinstance(cid, str) and re.fullmatch(r'[a-z0-9-]+://[A-Za-z0-9_.-]+', cid),
                    'malformed receipt container_id')
            require(isinstance(values, dict), 'malformed receipt values')
            for name in expected['config']:
                require(name in values and type(values[name]) in (int, float),
                        'missing/malformed receipt value: ' + name)
                number(values[name])
            require(args.pods and args.replicasets, 'pods and replicasets required for process binding')
            spec = deploy['spec']
            require(type(spec.get('replicas', 1)) is int and spec.get('replicas', 1) == 1,
                    'single process receipt requires exactly one Deployment replica')
            selector = spec['selector']
            require(selector.get('matchLabels') and not selector.get('matchExpressions'),
                    'unsupported/empty Deployment selector')
            payload = load(args.pods)
            pods = [payload] if payload.get('kind') == 'Pod' else payload['items']
            require(isinstance(pods, list) and len(pods) == 1, 'ambiguous/missing current pod')
            pod = pods[0]
            pmeta = pod['metadata']
            require(pod['kind'] == 'Pod' and pmeta['namespace'] == meta['namespace']
                    and not pmeta.get('deletionTimestamp'), 'foreign/deleting pod')
            require(all(pmeta['labels'].get(k) == v for k, v in selector['matchLabels'].items()),
                    'pod selector mismatch')
            owners = [o for o in pmeta['ownerReferences'] if o.get('controller') is True]
            require(len(owners) == 1 and owners[0]['kind'] == 'ReplicaSet', 'pod controller mismatch')
            owner = owners[0]
            require(all(isinstance(uid, str) and uid.strip() for uid in (meta['uid'], owner['uid'])),
                    'missing/malformed ownership UID')
            payload = load(args.replicasets)
            replicasets = [payload] if payload.get('kind') == 'ReplicaSet' else payload['items']
            require(isinstance(replicasets, list), 'malformed ReplicaSet list')
            matches = [rs for rs in replicasets if rs['metadata']['uid'] == owner['uid']]
            require(len(matches) == 1, 'missing/ambiguous owning ReplicaSet')
            rs = matches[0]
            rmeta = rs['metadata']
            require(rs['kind'] == 'ReplicaSet' and rmeta['namespace'] == meta['namespace']
                    and rmeta['name'] == owner['name'], 'foreign ReplicaSet')
            owners = [o for o in rmeta['ownerReferences'] if o.get('controller') is True]
            require(len(owners) == 1 and owners[0]['kind'] == 'Deployment'
                    and owners[0]['uid'] == meta['uid'] and owners[0]['name'] == meta['name'],
                    'ReplicaSet Deployment owner mismatch')
            state = named(pod['status']['containerStatuses'], container)
            require(pod['status']['phase'] == 'Running' and isinstance(state['state'].get('running'), dict),
                    'container is not currently running')
            require(cid == state['containerID'], 'stale/foreign loaded-config container_id')
            loaded = values
        except (ValueError, KeyError, TypeError, IndexError, AttributeError, OSError, DecimalException) as error:
            # Invalid optional evidence must not hide independent env/image drift.
            config_reason = 'unusable loaded-config evidence: ' + str(error)
    for name, want in sorted(expected['config'].items()):
        if loaded is None:
            add('config.' + name, want, 'unobservable', None)
            fields[-1]['reason'] = config_reason
        else:
            add('config.' + name, want, 'match' if json_equal(loaded[name], want) else 'mismatch', loaded[name])
    if args.expected_image:
        image = live.get('image')
        require(isinstance(image, str) and image, 'missing live image')
        add('image', args.expected_image, 'match' if image == args.expected_image else 'mismatch', image)
    report = {'mode': 'drift', 'namespace': meta['namespace'], 'name': meta['name'],
              'generation': meta.get('generation'), 'container': container,
              'rollout_at': None, 'fields': fields}
    report['flux'] = {'state': 'unobservable', 'binding': 'unobservable', 'namespace': None}
    if args.flux:
        flux = load(args.flux)
        require(isinstance(flux, dict) and flux.get('kind') in ('Kustomization', 'HelmRelease'),
                'expected Flux Kustomization/HelmRelease')
        fstatus = flux.get('status', {})
        require(isinstance(fstatus, dict), 'invalid Flux status')
        revision = fstatus.get('lastAppliedRevision')
        require(revision is None or isinstance(revision, str), 'invalid Flux revision')
        fmeta = flux.get('metadata')
        fmeta = fmeta if isinstance(fmeta, dict) else {}
        receipt = report['flux']
        receipt.update(kind=flux['kind'], name=fmeta.get('name'), namespace=fmeta.get('namespace'),
                       last_applied_revision=revision)

        def identity(value):
            return isinstance(value, str) and bool(value.strip())

        pairs = []
        if all(identity(fmeta.get(key)) for key in ('name', 'namespace')):
            if flux['kind'] == 'Kustomization':
                labels = meta.get('labels')
                if isinstance(labels, dict):
                    pairs = [(labels.get('kustomize.toolkit.fluxcd.io/' + key), fmeta[key])
                             for key in ('name', 'namespace')]
            else:
                spec, annotations = flux.get('spec'), meta.get('annotations')
                if (isinstance(spec, dict) and isinstance(annotations, dict)
                        and all(key not in spec or identity(spec[key]) for key in
                                ('releaseName', 'targetNamespace', 'storageNamespace'))):
                    target = spec.get('targetNamespace', fmeta['namespace'])
                    storage = spec.get('storageNamespace', fmeta['namespace'])
                    # Flux prefixes only an explicitly configured targetNamespace.
                    release = spec.get('releaseName')
                    if release is None:
                        release = (spec['targetNamespace'] + '-' if 'targetNamespace' in spec else '') + fmeta['name']
                        if len(release) > 53:
                            release = release[:40] + '-' + hashlib.sha256(release.encode()).hexdigest()[:12]
                    receipt.update(release_name=release, target_namespace=target, storage_namespace=storage)
                    # Helm ownership uses the target (release) namespace, not secret storage.
                    pairs = [(annotations.get('meta.helm.sh/release-name'), release),
                             (annotations.get('meta.helm.sh/release-namespace'), target)]
                    # When retained, Flux's own origin labels must also agree.
                    labels = meta.get('labels', {})
                    if not isinstance(labels, dict):
                        pairs.append((None, None))
                    elif any('helm.toolkit.fluxcd.io/' + key in labels for key in ('name', 'namespace')):
                        pairs.extend((labels.get('helm.toolkit.fluxcd.io/' + key), fmeta[key])
                                     for key in ('name', 'namespace'))
        if any(identity(owner) and owner != supplied for owner, supplied in pairs):
            receipt['binding'] = 'mismatch'
        elif pairs and all(identity(owner) for owner, _ in pairs):
            receipt['binding'] = 'match'
        if receipt['binding'] == 'match' and identity(revision):
            receipt['state'] = 'observed'
    states = {f['state'] for f in fields}
    body = json.dumps(report, sort_keys=True)
    if 'mismatch' in states or report['flux']['binding'] == 'mismatch':
        return 3, 'DRIFT_MISMATCH', body
    if 'unobservable' in states or report['flux']['state'] != 'observed':
        return 2, 'DRIFT_INCOMPLETE', body
    return 0, 'DRIFT_MATCH', body


def analyse():
    if '--drift' in sys.argv[1:]:
        return drift()
    parser = Parser(description='Offline OOM acceptance. See docs/tasks/gostream-oom-acceptance.md.')
    for flag in ('pods', 'deployment', 'rss', 'limits', 'expected-tuning', 'now'):
        parser.add_argument('--' + flag, required=True)
    parser.add_argument('--runtime', help='JSON array of retained process env/config receipts')
    parser.add_argument('--coverage', help='Retained rollout inventory and RSS sampling coverage receipt')
    parser.add_argument('--diagnostic', action='store_true', help='Non-accepting pre-rollout observations')
    parser.add_argument('--observed-from', help='Diagnostic interval start (RFC3339, inclusive)')
    parser.add_argument('--observed-until', help='Diagnostic interval end (RFC3339, inclusive)')
    parser.add_argument('--replicasets', help='Raw ReplicaSet or List proving diagnostic pod ownership')
    args = parser.parse_args()
    now = stamp(args.now)
    require(args.diagnostic or not (args.observed_from or args.observed_until or args.replicasets),
            'observation interval and ReplicaSets require --diagnostic')
    if args.diagnostic:
        require(args.observed_from and args.observed_until, 'diagnostic observation interval required')
        first_observed, last_observed = stamp(args.observed_from), stamp(args.observed_until)
        require(first_observed <= last_observed <= now, 'invalid observation interval')
        require(not args.runtime and not args.coverage,
                'diagnostics do not certify runtime or rollout coverage; omit --runtime/--coverage')
        report = {'rollout': 'unknown', 'coverage': 'unknown', 'headroom': 'unavailable',
                  'event_history': 'unknown', 'scope': 'shared-service',
                  'observed_from': args.observed_from, 'observed_until': args.observed_until,
                  'pods': [], 'events': [], 'rss': [],
                  'rss_note': 'per-cgroup maxima are not summed; multiple IDs are not independent pods'}
    coverage = load(args.coverage) if args.coverage else None
    expected = load(args.expected_tuning)
    require(set(expected) == {'container', 'rollout_at', 'env', 'config'}, "invalid expected-tuning fields")
    container = expected['container']
    require(isinstance(container, str) and container, "missing expected container")
    rollout = None if args.diagnostic and expected['rollout_at'] is None else stamp(expected['rollout_at'])
    require(rollout is None or rollout <= now, "future rollout")
    if args.diagnostic:
        report['rollout'] = 'unknown' if rollout is None else 'unverified'
        report['rollout_at'] = expected['rollout_at']
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
    if not args.diagnostic:
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
    replica_owners, container_ids = {}, {}
    if args.diagnostic:
        require(args.replicasets, 'diagnostic ReplicaSet ownership evidence required')
        rs_payload = load(args.replicasets)
        replicasets = [rs_payload] if rs_payload.get('kind') == 'ReplicaSet' else rs_payload['items']
        for rs in replicasets:
            meta = rs['metadata']
            require(rs['kind'] == 'ReplicaSet' and meta['namespace'] == namespace,
                    'foreign ReplicaSet')
            owners = [o for o in meta['ownerReferences'] if o.get('controller') is True]
            require(len(owners) == 1 and owners[0]['kind'] == 'Deployment'
                    and owners[0]['uid'] == deploy['metadata']['uid']
                    and owners[0]['name'] == deploy['metadata']['name'], 'ReplicaSet owner mismatch')
            require(meta['uid'] not in replica_owners, 'duplicate ReplicaSet UID')
            replica_owners[meta['uid']] = meta['name']
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
        if args.diagnostic:
            require(replica_owners.get(owners[0]['uid']) == owners[0]['name'], 'pod owner mismatch')
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
        if rollout is not None and created < rollout:
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
                if args.diagnostic:
                    cid = term['containerID']
                    require(isinstance(cid, str) and re.fullmatch(r'[a-z0-9-]+://[A-Za-z0-9_.-]+', cid),
                            'invalid terminated container ID')
                    if 'startedAt' in term:
                        require(created <= stamp(term['startedAt']) <= finished, 'invalid termination start')
                    inside = first_observed <= finished <= last_observed
                    report['events'].append({'namespace': namespace, 'pod': meta['name'],
                        'pod_uid': meta['uid'], 'container': container, 'container_id': cid,
                        'source': field, 'reason': term['reason'], 'finished_at': term['finishedAt'],
                        'interval': 'observed_in_diagnostic_interval' if inside else 'outside_observation_interval'})
                if term['reason'] == 'OOMKilled' and (first_observed <= finished <= last_observed
                        if args.diagnostic else finished >= rollout):
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
        if args.diagnostic:
            cid = state['containerID']
            require(isinstance(cid, str) and re.fullmatch(r'[a-z0-9-]+://[A-Za-z0-9_.-]+', cid),
                    'invalid current container ID')
            current_term = state.get('state', {}).get('terminated')
            require(not current_term or current_term['containerID'] == cid, 'current container ID mismatch')
            require(not previous or previous['containerID'] != cid, 'previous container ID equals current container ID')
            if previous and current_term:
                boundary = current_term.get('startedAt', current_term['finishedAt'])
                require(stamp(previous['finishedAt']) <= stamp(boundary),
                        'previous termination follows current termination/start')
            container_ids[key] = (meta['uid'], cid, previous['containerID'] if previous else None, limit, created)
            report['pods'].append({'namespace': namespace, 'pod': meta['name'], 'pod_uid': meta['uid'],
                                  'container': container, 'container_id': cid, 'restart_count': restarts})
            continue
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
    if args.diagnostic:
        # Preserve every series. A cgroup identity proves association, not historical coverage.
        vectors = {}
        for source in ('rss', 'limits'):
            payload = load(getattr(args, source))
            require(payload['status'] == 'success' and not payload.get('warnings'), 'unsuccessful/partial metric query')
            data = payload['data']
            require(data['resultType'] == 'vector' and isinstance(data['result'], list), 'invalid metric vector')
            rows = {}
            for row in data['result']:
                labels = row['metric']
                key = tuple(labels[k] for k in ('namespace', 'pod', 'container'))
                require(key in identities, 'foreign metric identity')
                cgroup = labels.get('id', '')
                require(isinstance(cgroup, str), 'invalid cgroup ID')
                identity = key + (cgroup,)
                require(identity not in rows, 'duplicate metric cgroup identity')
                require(len(row['value']) == 2, 'invalid vector sample')
                at, value = map(number, row['value'])
                require(0 <= now - at <= 300 and at >= container_ids[key][4], 'stale/future metric sample')
                rows[identity] = (at, value, labels)
            vectors[source] = rows
        for identity, (at, value, labels) in vectors['rss'].items():
            key, cgroup = identity[:3], identity[3]
            pod_uid, current, previous, limit, _ = container_ids[key]
            associated = None
            for cid in (current, previous):
                if cid is None:
                    continue
                runtime_name, token = cid.split('://', 1)
                # Match complete pod/container path components, never a suffix substring.
                systemd = (f'/kubepods.slice/kubepods-burstable.slice/kubepods-burstable-pod'
                           f'{pod_uid.replace("-", "_")}.slice/cri-{runtime_name}-{token}.scope')
                cgroupfs = f'/kubepods/burstable/pod{pod_uid}/{token}'
                if cgroup in (systemd, cgroupfs) and labels.get('name', token) == token:
                    associated = cid
            unavailable = 'unproved ID association'
            observed_limit = vectors['limits'].get(identity)
            if associated:
                unavailable = 'historical limit unavailable' if associated != current else 'current limit unavailable'
                if observed_limit:
                    if observed_limit[1] != limit:
                        unavailable = 'limit mismatch'
                    elif observed_limit[0] != at or observed_limit[2].get('name') != labels.get('name'):
                        unavailable = 'limit identity/time mismatch'
                    else:
                        unavailable = 'historical sampling coverage unknown'
            report['rss'].append({'namespace': key[0], 'pod': key[1], 'container': key[2],
                'cgroup_id': cgroup, 'container_id': associated, 'rss_bytes': str(value),
                'limit_bytes': str(observed_limit[1]) if observed_limit else None,
                'headroom': 'unavailable', 'reason': unavailable})
        report['limits'] = [{'namespace': k[0], 'pod': k[1], 'container': k[2],
                             'cgroup_id': k[3], 'limit_bytes': str(v[1])}
                            for k, v in vectors['limits'].items()]
        report['gaps'] = sorted(set(gaps))
        return (6 if ooms else 2), ('DIAGNOSTIC_OOM_OBSERVED' if ooms else 'DIAGNOSTIC_INCOMPLETE'), json.dumps(report, sort_keys=True)
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
except (json.JSONDecodeError, ValueError, KeyError, TypeError, IndexError, AttributeError, OSError, DecimalException, OverflowError) as error:
    code, verdict, detail = 2, 'INCOMPLETE', str(error)
print(f'{verdict} {detail}')
sys.exit(code)
PY
