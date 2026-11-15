"""Read-only, two-visit database load acceptance probe (Python standard library)."""
import argparse
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.parse
import urllib.request


SQL = """BEGIN READ ONLY;
SET LOCAL statement_timeout = '15s';
SELECT json_build_object(
 'track_counts', current_setting('track_counts'),
 'observed_at', extract(epoch FROM clock_timestamp()),
 'database', current_database(),
 'postmaster_start', extract(epoch FROM pg_postmaster_start_time()),
 'stats_reset', (SELECT extract(epoch FROM stats_reset) FROM pg_stat_database
                 WHERE datname = current_database()),
 'tables', (SELECT json_agg(json_build_object('schema', schemaname,
   'table', relname, 'oid', relid, 'seq_scan', seq_scan,
   'seq_tup_read', seq_tup_read, 'idx_scan', idx_scan,
   'n_live_tup', n_live_tup)) FROM pg_stat_user_tables));
ROLLBACK;
"""
MEDIA = ('movie', 'episode')
STATES = ('eligible', 'dormant', 'leased', 'scheduled')
LANES = ('priority', 'background')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def number(value):
    require(not isinstance(value, bool), 'boolean is not a number')
    result = float(value)
    require(math.isfinite(result) and result >= 0, 'nonfinite or negative number')
    return result


def epoch(value):
    if isinstance(value, str) and 'T' in value:
        return datetime.datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp()
    return number(value)


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()


def load(path):
    return decode(Path(path).read_text())


def decode(raw):
    def invalid(value):
        raise ValueError('nonfinite JSON constant: ' + value)
    return json.loads(raw, parse_constant=invalid)


def selector(name, labels):
    require(re.fullmatch(r'[a-zA-Z_:][a-zA-Z0-9_:]*', name), 'invalid metric name')
    require(labels and all(re.fullmatch(r'[a-zA-Z_][a-zA-Z0-9_]*', k)
                           and isinstance(v, str) and v for k, v in labels.items()),
            'metric selectors require nonempty exact label matches')
    return name + '{' + ','.join(k + '=' + json.dumps(v) for k, v in sorted(labels.items())) + '}'


class Probe:
    def __init__(self, config, evidence):
        self.c = config
        self.e = evidence
        self.now = int(time.time())
        self.gap = number(config.get('max_gap_seconds', 120))
        require(1 <= self.gap <= 300, 'max_gap_seconds must be 1..300')
        self.rollout = epoch(config['rollout_at'])
        require(self.rollout <= self.now, 'rollout is in the future')
        for role, spec in config['workloads'].items():
            require(role in ('application', 'database') and spec['namespace'] and spec['selector'],
                    'workload namespace and selector required')
        require(len(config['workloads']['database']['images']) == 1,
                'CPU scope requires exactly one PostgreSQL container per database pod')
        for key, labels in config['metrics'].items():
            role = 'application' if key in ('claims', 'completions', 'backlog', 'observed') else 'database'
            require(labels.get('namespace') == config['workloads'][role]['namespace'],
                    key + ': metric namespace must match workload')
        require(config['metrics']['cpu'].get('container') == next(iter(config['workloads']['database']['images'])),
                'CPU selector must name the PostgreSQL container')
        self.thresholds = {
            'window_seconds_min': 86400,
            'cpu_baseline_cores': number(config.get('cpu_baseline_cores', .46)),
            'seq_tup_read_per_hour_max': number(config.get('seq_tup_read_per_hour_max', 0)),
            'backlog_max': number(config['backlog_max']),
            'backlog_growth_max': number(config.get('backlog_growth_max', 0)),
            'max_gap_seconds': self.gap,
            'max_backlog_age_seconds': 660,
        }
        baseline = self.thresholds['cpu_baseline_cores']
        require(baseline > 0, 'CPU baseline must be positive')
        self.thresholds['cpu_mean_cores_max'] = min(.092, baseline * .2)
        if self.thresholds['seq_tup_read_per_hour_max']:
            require(isinstance(config.get('seq_tolerance_evidence'), str)
                    and config['seq_tolerance_evidence'].strip(), 'seq tolerance requires evidence')
        evidence['thresholds'] = self.thresholds
        evidence['config'] = config
        self.policy = digest({k: v for k, v in config.items() if k != 'rollout_at'})
        self.e['policy_sha256'] = self.policy

    def http(self, endpoint, url):
        headers = {'Accept': 'application/json'}
        for key, env in endpoint.get('headers_env', {}).items():
            require(os.environ.get(env), 'missing credential environment: ' + env)
            headers[key] = os.environ[env]
        parsed = urllib.parse.urlsplit(url)
        require(parsed.scheme in ('http', 'https') and not parsed.username and not parsed.password,
                'HTTP endpoint must not contain credentials')
        request = urllib.request.Request(url, headers=headers)
        # Do not forward authorization across redirects.
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, *args, **kwargs):
                return None
        with urllib.request.build_opener(NoRedirect).open(request, timeout=30) as response:
            raw = response.read(64 * 1024 * 1024 + 1)
        require(len(raw) <= 64 * 1024 * 1024, 'HTTP response exceeds 64 MiB')
        data = decode(raw)
        self.e.setdefault('http', []).append({'url': url, 'received_at': time.time(), 'response': data})
        return data

    def kube(self, namespace, resource, name=None, labels=None):
        endpoint = self.c.get('kubernetes')
        if endpoint:
            group = '/apis/kustomize.toolkit.fluxcd.io/v1' if resource == 'kustomizations' else '/api/v1'
            url = endpoint['url'].rstrip('/') + group + '/namespaces/' + urllib.parse.quote(namespace, safe='')
            url += '/' + resource
            if name:
                url += '/' + urllib.parse.quote(name, safe='')
            if labels:
                url += '?' + urllib.parse.urlencode({'labelSelector': labels})
            return self.http(endpoint, url)
        command = ['kubectl', '--request-timeout=30s', '-n', namespace, 'get', resource]
        if name:
            command.append(name)
        if labels:
            command += ['-l', labels]
        result = subprocess.run(command + ['-o', 'json'], capture_output=True, text=True, timeout=40)
        require(result.returncode == 0, 'kubectl read failed')
        data = decode(result.stdout)
        self.e.setdefault('kubernetes', []).append({'command': command, 'response': data})
        return data

    def deployment(self, before):
        binding = {}
        for role in ('application', 'database'):
            spec = self.c['workloads'][role]
            pods = self.kube(spec['namespace'], 'pods', labels=spec['selector'])['items']
            require(pods, role + ': no pods (disabled workload)')
            found = []
            for pod in pods:
                meta, status = pod['metadata'], pod['status']
                require(not meta.get('deletionTimestamp') and status['phase'] == 'Running', role + ': pod not running')
                require(any(c['type'] == 'Ready' and c['status'] == 'True'
                            for c in status.get('conditions', [])), role + ': pod not ready')
                containers = []
                for name, image in spec['images'].items():
                    require('@sha256:' in image, 'expected images must use immutable digests')
                    container = next(c for c in pod['spec']['containers'] if c['name'] == name)
                    state = next(c for c in status['containerStatuses'] if c['name'] == name)
                    require(container['image'] == image and image.split('@')[1] in state['imageID'],
                            role + ': deployed image revision mismatch')
                    require(state['ready'] is True and state['restartCount'] == 0,
                            role + ': restart or unready container')
                    require(epoch(state['state']['running']['startedAt']) <= before,
                            role + ': container started inside observation interval')
                    containers.append({'name': name, 'image': image, 'imageID': state['imageID'],
                                       'resources': container.get('resources', {})})
                require(containers, 'no expected containers')
                found.append({'uid': meta['uid'], 'name': meta['name'], 'containers': containers})
            binding[role] = sorted(found, key=lambda p: p['uid'])
        return binding

    def postgres(self):
        env = dict(os.environ)
        env['PGOPTIONS'] = '-c default_transaction_read_only=on -c statement_timeout=15000'
        result = subprocess.run(['psql', '-X', '-q', '-A', '-t', '-v', 'ON_ERROR_STOP=1',
                                 '-c', SQL], env=env, capture_output=True, text=True, timeout=30)
        self.e['postgres_raw'] = {'sql': SQL, 'stdout': result.stdout, 'returncode': result.returncode}
        require(result.returncode == 0, 'PostgreSQL statistics query failed')
        data = decode(result.stdout)
        require(data['track_counts'] == 'on', 'PostgreSQL track_counts disabled')
        require(abs(epoch(data['observed_at']) - time.time()) <= self.gap, 'stale PostgreSQL observation')
        require(data['database'] == self.c['database_name'], 'wrong PostgreSQL database')
        require(data['stats_reset'] is not None, 'missing pg_stat_database.stats_reset')
        selected = {}
        for target in self.c['availability_tables']:
            rows = [r for r in data['tables'] if r['schema'] == target['schema'] and r['table'] == target['table']]
            require(len(rows) == 1, 'missing/duplicate availability table statistics')
            row = rows[0]
            for key in ('oid', 'seq_scan', 'seq_tup_read', 'n_live_tup'):
                number(row[key])
            selected[target['schema'] + '.' + target['table']] = row
        require(selected, 'no availability tables configured')
        data['selected'] = selected
        return data

    def history(self, name, labels, start, end):
        # Instant-query range selectors return ORIGINAL scrape timestamps, unlike
        # query_range's evaluation timestamps and lookback-filled samples.
        expression = selector(name, labels) + '[' + str(int(self.now - start + self.gap + 1)) + 's]'
        endpoint = self.c['mimir']
        url = endpoint['url'].rstrip('/') + '/api/v1/query?' + urllib.parse.urlencode({
            'query': expression, 'time': self.now, 'partial_response': 'false'})
        data = self.http(endpoint, url)
        require(data.get('status') == 'success' and not data.get('warnings') and not data.get('infos'),
                name + ': error/partial/warning response')
        require(data['data']['resultType'] == 'matrix', name + ': expected raw sample matrix')
        rows = data['data']['result']
        require(rows and len(rows) <= 256, name + ': missing or excessive series')
        series = {}
        for row in rows:
            metric = row['metric']
            require(all(metric.get(k) == v for k, v in labels.items()), name + ': selector mismatch')
            identity = tuple(sorted((k, v) for k, v in metric.items() if k != '__name__'))
            require(identity not in series, name + ': duplicate series')
            points = [(number(t), number(v)) for t, v in row['values']]
            require(2 <= len(points) <= 700000, name + ': insufficient/excessive samples')
            require(points[0][0] <= start and points[-1][0] >= end
                    and 0 <= self.now - points[-1][0] <= self.gap,
                    name + ': stale or insufficient history')
            require(all(0 < b[0] - a[0] <= self.gap for a, b in zip(points, points[1:])),
                    name + ': gap or unordered samples')
            series[identity] = (metric, points)
        return series

    def metrics(self, start, end, binding):
        spec = self.c['metrics']
        names = {'cpu': 'container_cpu_usage_seconds_total',
                 'claims': 'phantom_availability_claims_total',
                 'completions': 'phantom_availability_completions_total',
                 'backlog': 'phantom_availability_backlog',
                 'observed': 'phantom_availability_backlog_observed_at',
                 'seq': 'pg_stat_user_tables_seq_tup_read'}
        result = {}
        for key, name in names.items():
            result[key] = self.history(name, spec[key], start, end)
        app_pods = {p['name'] for p in binding['application']}
        db_pods = {p['name'] for p in binding['database']}
        for key in names:
            rows = result[key].values()
            expected = app_pods if key in ('claims', 'completions', 'backlog', 'observed') else db_pods
            require({m.get('pod') for m, _ in rows} == expected, key + ': replica coverage mismatch')
            if key in ('claims', 'completions', 'backlog'):
                dimensions = ('media_type', 'state' if key == 'backlog' else 'lane')
                combinations = {(m, v) for m in MEDIA for v in (STATES if key == 'backlog' else LANES)}
                for pod in expected:
                    actual = [tuple(m.get(d) for d in dimensions) for m, _ in result[key].values() if m['pod'] == pod]
                    require(set(actual) == combinations and len(actual) == len(combinations),
                            key + ': missing/duplicate media/state/lane series')
                if key == 'backlog':
                    require(all('lane' not in m for m, _ in result[key].values()), 'backlog must not have lane')
            elif key == 'seq':
                combinations = {(t['schema'], t['table']) for t in self.c['availability_tables']}
                for pod in expected:
                    actual = [(m.get('schemaname'), m.get('relname')) for m, _ in result[key].values() if m['pod'] == pod]
                    require(set(actual) == combinations and len(actual) == len(combinations), 'seq: table coverage mismatch')
                require(all(m.get('datname') == self.c['database_name'] for m, _ in result[key].values()), 'seq: wrong database')
            else:
                require(len(result[key]) == len(expected), key + ': duplicate replica series')
                if key == 'observed':
                    require(all(not {'media_type', 'state', 'lane'} & m.keys()
                                for m, _ in result[key].values()), 'observed_at must have no instrument labels')
        for _, points in result['observed'].values():
            require(all(0 <= t - v <= self.thresholds['max_backlog_age_seconds'] for t, v in points), 'backlog observation stale/disabled')
        return result


def at(points, when):
    for a, b in zip(points, points[1:]):
        if a[0] <= when <= b[0]:
            return a[1] + (b[1] - a[1]) * (when - a[0]) / (b[0] - a[0])
    raise ValueError('boundary not covered')


def delta(points, start, end):
    require(all(b[1] >= a[1] for a, b in zip(points, points[1:])), 'per-series counter reset')
    return at(points, end) - at(points, start)


def cpu_quantity(value):
    return number(value[:-1]) / 1000 if isinstance(value, str) and value.endswith('m') else number(value)


def run(args, evidence):
    probe = Probe(load(args.config), evidence)
    c = probe.c
    end = probe.now - probe.gap
    baseline = None if args.capture_baseline else load(args.baseline)
    start = probe.now if baseline is None else number(baseline['captured_at'])
    require(start >= probe.rollout, 'baseline predates rollout')
    if baseline is not None:
        require(baseline['schema'] == 1 and baseline['status'] == 'BASELINE_CAPTURED', 'invalid baseline')
        require(baseline['policy_sha256'] == probe.policy, 'baseline policy/config mismatch')
        require(86400 <= end - start <= 7 * 86400, 'insufficient history: require 24h..7d after baseline plus freshness margin')
    binding = probe.deployment(start)
    pg = probe.postgres()
    evidence.update(schema=1, captured_at=probe.now, binding=binding, postgres=pg,
                    stage='final' if args.final else 'pre-sizing')
    if baseline is None:
        probe.metrics(probe.now - probe.gap, probe.now - probe.gap, binding)
        evidence['status'] = 'BASELINE_CAPTURED'
        return
    require(binding == baseline['binding'], 'deployed revisions/resources/replicas changed since baseline')
    old = baseline['postgres']
    require(pg['postmaster_start'] == old['postmaster_start'] and pg['stats_reset'] == old['stats_reset'],
            'PostgreSQL restart/statistics reset')
    require(pg['selected'].keys() == old['selected'].keys(), 'PostgreSQL table coverage changed')
    for key, row in pg['selected'].items():
        previous = old['selected'][key]
        require(row['oid'] == previous['oid'], 'availability table replaced')
        elapsed = epoch(pg['observed_at']) - epoch(old['observed_at'])
        require(elapsed >= 86400 and abs(epoch(old['observed_at']) - start) <= probe.gap,
                'invalid PostgreSQL baseline interval')
        growth = number(row['seq_tup_read']) - number(previous['seq_tup_read'])
        require(growth >= 0 and row['seq_scan'] >= previous['seq_scan'], 'PostgreSQL table counter reset')
        require(growth / elapsed * 3600 <= probe.thresholds['seq_tup_read_per_hour_max'], 'PostgreSQL seq_tup_read growth')
    metrics = probe.metrics(start, end, binding)
    totals = {}
    for key in ('cpu', 'claims', 'completions', 'seq'):
        totals[key] = [(m, delta(p, start, end)) for m, p in metrics[key].values()]
    cpu = sum(v for _, v in totals['cpu']) / (end - start)
    require(cpu <= probe.thresholds['cpu_mean_cores_max'] + 1e-12, 'CPU mean exceeds threshold')
    for _, growth in totals['seq']:
        require(growth * 3600 / (end - start) <= probe.thresholds['seq_tup_read_per_hour_max'], 'historical seq_tup_read growth')
    for m, points in metrics['seq'].values():
        row = pg['selected'][m['schemaname'] + '.' + m['relname']]
        require(points[-1][1] <= number(row['seq_tup_read']) and points[0][1] <= number(old['selected'][m['schemaname'] + '.' + m['relname']]['seq_tup_read']),
                'PostgreSQL exporter/live statistics inconsistent')
    progress = {}
    for media in MEDIA:
        eligible = [p for m, p in metrics['backlog'].values() if m['media_type'] == media and m['state'] == 'eligible']
        # Backlog is a shared-database snapshot: use maximum, not sum of replicas.
        require(any(v > 0 for p in eligible for t, v in p if start <= t <= end), media + ': no eligible demand evidence')
        active = [p for m, p in metrics['backlog'].values() if m['media_type'] == media and m['state'] in ('eligible', 'leased')]
        for points in active:
            require(max(v for t, v in points if start <= t <= end) <= probe.thresholds['backlog_max'], media + ': backlog unbounded')
            require(at(points, end) - at(points, start) <= probe.thresholds['backlog_growth_max'], media + ': backlog growing')
        progress[media] = {key: sum(v for m, v in totals[key] if m['media_type'] == media) for key in ('claims', 'completions')}
        require(all(v > 0 for v in progress[media].values()), media + ': zero claims/completions under eligible demand (disabled/stalled)')
    if args.final:
        receipt = load(args.final)
        evidence['receipt'] = receipt
        pre_path = Path(args.final).resolve().parent / receipt['pre_sizing_report']
        require(hashlib.sha256(pre_path.read_bytes()).hexdigest() == receipt['pre_sizing_report_sha256'], 'receipt report digest mismatch')
        pre = load(pre_path)
        require(receipt['schema'] == 1 and pre['schema'] == 1 and pre['status'] == 'ACCEPTED'
                and pre['stage'] == 'pre-sizing' and pre['policy_sha256'] == probe.policy, 'receipt pre-sizing acceptance mismatch')
        require(pre['interval']['seconds'] >= 86400
                and pre['interval']['end'] <= pre['captured_at']
                and pre['cpu_mean_cores'] <= probe.thresholds['cpu_mean_cores_max'] + 1e-12
                and pre['thresholds'] == probe.thresholds
                and all(pre['progress'][media][key] > 0 for media in MEDIA for key in ('claims', 'completions')),
                'receipt pre-sizing measurements invalid')
        applied = epoch(receipt['applied_at'])
        require(pre['captured_at'] < applied <= probe.rollout <= start, 'receipt sizing chronology invalid')
        flux_spec = receipt['flux']
        flux = probe.kube(flux_spec['namespace'], 'kustomizations', name=flux_spec['name'])
        require(flux['status']['observedGeneration'] == flux['metadata']['generation']
                and flux['status']['lastAppliedRevision'] == flux_spec['revision']
                and any(x['type'] == 'Ready' and x['status'] == 'True' and x.get('observedGeneration') == flux['metadata']['generation'] for x in flux['status']['conditions']),
                'Flux receipt revision not reconciled/ready')
        require(receipt['resources'].keys() == binding.keys(), 'receipt missing workload resources')
        for role, pods in binding.items():
            for pod in pods:
                actual = {v['name']: v['resources'] for v in pod['containers']}
                require(actual == receipt['resources'][role], 'receipt resources differ from live pods')
        old_cpu = sum(cpu_quantity(v['resources']['requests']['cpu']) for p in pre['binding']['database'] for v in p['containers'])
        new_cpu = sum(cpu_quantity(v['resources']['requests']['cpu']) for p in binding['database'] for v in p['containers'])
        require(new_cpu < old_cpu, 'receipt does not reduce database CPU requests')
    # Re-read to catch a rollout racing collection; never accept only desired state.
    require(probe.deployment(start) == binding, 'deployment changed during probe')
    evidence.update(status='ACCEPTED', interval={'start': start, 'end': end, 'seconds': end - start},
                    cpu_mean_cores=cpu, progress=progress)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True)
    parser.add_argument('--baseline', required=True)
    parser.add_argument('--output', required=True, help='new evidence JSON file (never overwritten)')
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--capture-baseline', action='store_true')
    mode.add_argument('--evaluate', action='store_true')
    mode.add_argument('--final', metavar='RECEIPT_JSON')
    args = parser.parse_args()
    evidence = {'status': 'FAILED', 'started_at': time.time()}
    # Exclusive creation prevents clobbering previous acceptance or baseline evidence.
    with open(args.output, 'x') as output:
        try:
            require(not args.capture_baseline or Path(args.baseline).resolve() == Path(args.output).resolve(),
                    'capture requires --baseline and --output to name the same new file')
            run(args, evidence)
        except (ValueError, KeyError, TypeError, OSError, StopIteration, subprocess.SubprocessError) as error:
            evidence['error'] = str(error) or type(error).__name__
        evidence['finished_at'] = time.time()
        json.dump(evidence, output, indent=2, allow_nan=False)
        output.write('\n')
    print(evidence['status'] + ': ' + evidence.get('error', args.output))
    return 0 if evidence['status'] in ('ACCEPTED', 'BASELINE_CAPTURED') else 1


if __name__ == '__main__':
    sys.exit(main())
