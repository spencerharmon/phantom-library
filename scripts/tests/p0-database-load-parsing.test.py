"""Exercise the actual shell CLI using controlled Kubernetes/Mimir HTTP services.

Only psql is a transport double: it checks the read-only SQL and fetches the
controlled pg_stat response over HTTP. No production endpoint is contacted.
"""
import copy
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import threading
import time
import unittest
import urllib.parse


ROOT = Path(__file__).resolve().parent
IMAGE = 'example.com/workload@sha256:' + 'a' * 64


class ParsingTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scratch = tempfile.TemporaryDirectory(prefix='p0-probe-')
        cls.addClassCleanup(cls.scratch.cleanup)
        cls.directory = Path(cls.scratch.name)
        cls.scenario = 'good'
        cls.requests = []
        cls.sized = False

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                parsed = urllib.parse.urlsplit(self.path)
                cls.requests.append(self.path)
                if self.headers.get('Authorization') != 'Bearer fixture-only':
                    self.send_error(401)
                    return
                if cls.scenario == 'http-error' and '/query' in parsed.path:
                    self.send_error(503)
                    return
                if parsed.path == '/postgres':
                    data = cls.postgres()
                elif parsed.path.endswith('/pods'):
                    role = 'application' if '/namespaces/app/' in parsed.path else 'database'
                    data = {'items': [cls.pod(role)]}
                    if cls.scenario == 'disabled' and role == 'application':
                        data['items'] = []
                elif '/kustomizations/' in parsed.path:
                    data = {'metadata': {'generation': 2}, 'status': {
                        'observedGeneration': 2,
                        'lastAppliedRevision': 'main@sha1:' + 'b' * 40,
                        'conditions': [{'type': 'Ready', 'status': 'True', 'observedGeneration': 2}]}}
                    if cls.scenario == 'flux-drift':
                        data['status']['lastAppliedRevision'] = 'main@sha1:' + 'c' * 40
                else:
                    query = urllib.parse.parse_qs(parsed.query)
                    data = cls.metric(query['query'][0], int(query['time'][0]))
                    if cls.scenario == 'malformed-json':
                        self.send_response(200)
                        self.end_headers()
                        self.wfile.write(b'{not-json')
                        return
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.end_headers()
                self.wfile.write(json.dumps(data).encode())

        cls.server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        cls.thread = threading.Thread(target=cls.server.serve_forever)
        cls.thread.start()
        cls.addClassCleanup(cls.stop_server)
        cls.url = 'http://127.0.0.1:' + str(cls.server.server_port)
        cls.env = dict(os.environ, P0_TEST_AUTH='Bearer fixture-only', P0_PG_URL=cls.url + '/postgres')
        cls.env['PATH'] = str(cls.directory) + os.pathsep + cls.env['PATH']
        psql = cls.directory / 'psql'
        psql.write_text('''#!/usr/bin/env python3
import os, sys, urllib.request
sql = sys.argv[sys.argv.index('-c') + 1]
assert 'BEGIN READ ONLY;' in sql and 'ROLLBACK;' in sql
assert 'pg_stat_user_tables' in sql and 'pg_stat_database' in sql
assert 'default_transaction_read_only=on' in os.environ['PGOPTIONS']
assert '-X' in sys.argv and 'ON_ERROR_STOP=1' in sys.argv
request = urllib.request.Request(os.environ['P0_PG_URL'], headers={'Authorization': os.environ['P0_TEST_AUTH']})
print(urllib.request.urlopen(request).read().decode())
''')
        psql.chmod(0o700)
        cls.serial = 0

    @classmethod
    def stop_server(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()

    @classmethod
    def pod(cls, role):
        resources = {'requests': {'cpu': '100m' if cls.sized else '500m'}, 'limits': {'cpu': '1'}}
        pod = {'metadata': {'name': role + '-pod', 'uid': role + '-uid'},
               'spec': {'containers': [{'name': 'main', 'image': IMAGE, 'resources': resources}]},
               'status': {'phase': 'Running', 'conditions': [{'type': 'Ready', 'status': 'True'}],
                          'containerStatuses': [{'name': 'main', 'ready': True, 'restartCount': 0,
                                                 'imageID': IMAGE,
                                                 'state': {'running': {'startedAt': 1000000000}}}]}}
        if cls.scenario == 'image-drift':
            pod['status']['containerStatuses'][0]['imageID'] = 'sha256:' + 'd' * 64
        if cls.scenario == 'restart':
            pod['status']['containerStatuses'][0]['restartCount'] = 1
        return pod

    @classmethod
    def postgres(cls):
        return {'track_counts': 'off' if cls.scenario == 'stats-disabled' else 'on',
                'observed_at': int(time.time()), 'database': 'phantom', 'postmaster_start': 1000000000,
                'stats_reset': 1000000000 if cls.scenario != 'pg-reset' else 1000000010,
                'tables': [{'schema': 'public', 'table': 'availability_items', 'oid': 123,
                            'seq_scan': 3, 'seq_tup_read': 100 if cls.scenario != 'pg-growth' else 101,
                            'idx_scan': 20, 'n_live_tup': 1000}]}

    @classmethod
    def metric(cls, expression, now):
        name = expression.split('{')[0]
        seconds = int(re.search(r'\[(\d+)s\]$', expression)[1])
        labels = dict(re.findall(r'(\w+)="([^"]+)"', expression))
        start = now - seconds + 1
        times = list(range(start, now, 60)) + [now]
        app = name.startswith('phantom_')
        rows = []
        if name.endswith(('claims_total', 'completions_total')):
            dimensions = [{'media_type': m, 'lane': lane} for m in ('movie', 'episode') for lane in ('priority', 'background')]
        elif name == 'phantom_availability_backlog':
            dimensions = [{'media_type': m, 'state': state} for m in ('movie', 'episode') for state in ('eligible', 'dormant', 'leased', 'scheduled')]
        elif name.startswith('pg_stat'):
            dimensions = [{'schemaname': 'public', 'relname': 'availability_items', 'datname': 'phantom'}]
        else:
            dimensions = [{}]
        for dimension in dimensions:
            metric = dict(labels, __name__=name, pod='application-pod' if app else 'database-pod', **dimension)
            values = []
            for i, timestamp in enumerate(times):
                if name == 'container_cpu_usage_seconds_total':
                    value = (timestamp - start) * (.093 if cls.scenario == 'cpu-high' else .09)
                elif name.endswith(('claims_total', 'completions_total')):
                    value = i + 10000
                    if cls.scenario == 'zero-claims' and name.endswith('claims_total'):
                        value = 10000
                    if cls.scenario == 'zero-completions' and name.endswith('completions_total'):
                        value = 10000
                    if cls.scenario == 'counter-reset' and name.endswith('claims_total') and i == len(times) // 2:
                        value = 1
                elif name == 'phantom_availability_backlog_observed_at':
                    value = timestamp - (900 if cls.scenario == 'observation-stale' else 300)
                elif name == 'phantom_availability_backlog':
                    value = 10 if dimension['state'] == 'eligible' else 0
                    if cls.scenario == 'no-demand':
                        value = 0
                    if cls.scenario == 'backlog-high':
                        value = 1001
                    if cls.scenario == 'backlog-growth':
                        value += i / 100
                else:
                    value = 100
                    if cls.scenario == 'seq-reset' and i == len(times) // 2:
                        value = 0
                values.append([timestamp, str(value)])
            boundary = cls.boundary_fixture
            if boundary and name == boundary['metric'] and dimension['media_type'] == boundary['media']:
                # Deliberately straddle BOTH interval boundaries by 30s rather
                # than accidentally testing exact scrapes at start/end. Derive
                # end from the CLI query time so wall-clock ticks cannot flake it.
                window_start, window_end = cls.window_start, now - 120
                sample_times = (set(range(window_start - 90, now, 60))
                                | {window_end - 30, window_end + 30, now}) - {window_start, window_end}
                if boundary.get('exact'):
                    sample_times |= {window_start, window_end}
                if name.endswith(('claims_total', 'completions_total')):
                    step = {'before-start': window_start - 1, 'after-end': window_end + 1,
                            'inside-start': window_start + 31, 'inside-end': window_end - 30,
                            'at-end': window_end}[boundary['edge']]
                    values = [[t, str(10000 + int(t >= step))] for t in sorted(sample_times)]
                elif dimension['state'] == boundary['state']:
                    values = [[t, str(boundary['high'] if (
                        t < window_start if boundary['edge'] == 'start' else t > window_end
                    ) else 10)] for t in sorted(sample_times)]
            if cls.scenario == 'gap' and app:
                del values[len(values) // 2:len(values) // 2 + 6]
            if cls.scenario == 'stale':
                values = [[t - 600, v] for t, v in values]
            if cls.scenario == 'nan' and app:
                values[-1][1] = 'NaN'
            if cls.scenario == 'partial-replica' and app:
                metric['pod'] = 'other-pod'
            rows.append({'metric': metric, 'values': values})
        if cls.scenario == 'missing-media' and name == 'phantom_availability_backlog':
            rows = [r for r in rows if r['metric']['media_type'] == 'movie']
        if cls.scenario == 'missing-state' and name == 'phantom_availability_backlog':
            rows.pop()
        if cls.scenario == 'late-series' and name.endswith('claims_total'):
            rows[0]['values'] = rows[0]['values'][10:]
        if cls.scenario == 'duplicate-series':
            rows.append(copy.deepcopy(rows[0]))
        if cls.scenario == 'missing-lane' and name.endswith('completions_total'):
            rows.pop()
        if cls.scenario == 'empty' and app:
            rows = []
        response = {'status': 'success', 'data': {'resultType': 'matrix', 'result': rows}}
        if cls.scenario == 'partial-warning':
            response['warnings'] = ['partial response from unavailable store']
        return response

    def setUp(self):
        type(self).scenario = 'good'
        type(self).sized = False
        type(self).boundary_fixture = None
        self.now = int(time.time())
        endpoint = {'url': self.url, 'headers_env': {'Authorization': 'P0_TEST_AUTH'}}
        self.config = {'rollout_at': self.now - 90000, 'mimir': endpoint, 'kubernetes': endpoint,
                       'database_name': 'phantom', 'availability_tables': [{'schema': 'public', 'table': 'availability_items'}],
                       'backlog_max': 1000,
                       'workloads': {role: {'namespace': ns, 'selector': 'app=' + role, 'images': {'main': IMAGE}}
                                     for role, ns in [('application', 'app'), ('database', 'db')]},
                       'metrics': {key: {'namespace': 'app' if key in ('claims', 'completions', 'backlog', 'observed') else 'db',
                                         'job': key} for key in ('cpu', 'claims', 'completions', 'backlog', 'observed', 'seq')}}
        self.config['metrics']['cpu']['container'] = 'main'

    def cli(self, mode, baseline=None, expected='ACCEPTED', fragment=None):
        type(self).serial += 1
        prefix = self.directory / str(self.serial)
        config_path = prefix.with_suffix('.config.json')
        output_path = prefix.with_suffix('.output.json')
        baseline_path = prefix.with_suffix('.baseline.json')
        config_path.write_text(json.dumps(self.config))
        if baseline is None:
            baseline_path = output_path
        else:
            baseline_path.write_text(json.dumps(baseline))
        result = subprocess.run(['bash', str(ROOT / 'p0-database-load-live.test.sh'), '--config', str(config_path),
                                 '--baseline', str(baseline_path), '--output', str(output_path), *mode],
                                env=self.env, capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0 if expected != 'FAILED' else 1, result.stdout + result.stderr)
        evidence = json.loads(output_path.read_text())
        self.assertEqual(evidence['status'], expected, evidence.get('error'))
        if fragment:
            self.assertIn(fragment, evidence['error'])
        self.assertNotIn('Bearer fixture-only', output_path.read_text())
        return evidence, output_path

    def baseline(self):
        data, _ = self.cli(['--capture-baseline'], expected='BASELINE_CAPTURED')
        # Advancing the fixture's persisted capture models the second visit without
        # a sleep or a production clock override in the executable being tested.
        data['captured_at'] = self.now - 87000
        data['postgres']['observed_at'] = data['captured_at']
        return data

    def test_capture_then_full_day_evaluation(self):
        evidence, _ = self.cli(['--evaluate'], self.baseline())
        self.assertAlmostEqual(evidence['cpu_mean_cores'], .09)
        self.assertGreaterEqual(evidence['interval']['seconds'], 86400)
        self.assertEqual(evidence['thresholds']['seq_tup_read_per_hour_max'], 0)
        self.assertEqual(set(evidence['progress']), {'movie', 'episode'})
        self.assertTrue(all('values' in r for q in evidence['http'] if '/api/v1/query?' in q['url']
                            for r in q['response']['data']['result']))

    def test_failure_cases_over_actual_cli(self):
        baseline = self.baseline()
        cases = {'disabled': 'disabled', 'http-error': '503', 'malformed-json': 'Expecting',
                 'image-drift': 'image revision', 'restart': 'restart', 'pg-reset': 'statistics reset',
                 'pg-growth': 'seq_tup_read growth', 'cpu-high': 'CPU mean',
                 'zero-claims': 'zero claims/completions', 'zero-completions': 'zero claims/completions',
                 'counter-reset': 'counter reset', 'seq-reset': 'counter reset', 'gap': 'gap',
                 'stale': 'stale', 'observation-stale': 'observation stale', 'nan': 'nonfinite',
                 'partial-replica': 'replica coverage', 'missing-media': 'media/state',
                 'missing-state': 'media/state', 'empty': 'missing', 'partial-warning': 'partial',
                 'no-demand': 'no eligible demand', 'backlog-high': 'backlog unbounded',
                 'backlog-growth': 'backlog growing'}
        cases.update({'stats-disabled': 'track_counts disabled', 'late-series': 'insufficient history',
                      'duplicate-series': 'duplicate series', 'missing-lane': 'media/state/lane'})
        for scenario, fragment in cases.items():
            with self.subTest(scenario=scenario):
                type(self).scenario = scenario
                self.cli(['--evaluate'], baseline, 'FAILED', fragment)

    def test_outside_only_progress_fails_over_actual_cli(self):
        baseline = self.baseline()
        type(self).window_start = baseline['captured_at']
        for instrument in ('claims', 'completions'):
            for media in ('movie', 'episode'):
                for edge in ('before-start', 'after-end'):
                    with self.subTest(instrument=instrument, media=media, edge=edge):
                        type(self).boundary_fixture = {
                            'metric': 'phantom_availability_' + instrument + '_total',
                            'media': media, 'edge': edge}
                        self.cli(['--evaluate'], baseline, 'FAILED',
                                 media + ': zero claims/completions')

    def test_observed_boundary_progress_over_actual_cli(self):
        baseline = self.baseline()
        type(self).window_start = baseline['captured_at']
        for instrument in ('claims', 'completions'):
            for media in ('movie', 'episode'):
                for edge, exact in (('inside-start', False), ('inside-end', False), ('at-end', True)):
                    with self.subTest(instrument=instrument, media=media, edge=edge):
                        type(self).boundary_fixture = {
                            'metric': 'phantom_availability_' + instrument + '_total',
                            'media': media, 'edge': edge, 'exact': exact}
                        evidence, _ = self.cli(['--evaluate'], baseline)
                        # One positively observed increment in EACH of two lanes;
                        # interpolation must not invent fractional work at edges.
                        self.assertEqual(evidence['progress'][media][instrument], 2)
                        self.assertAlmostEqual(evidence['cpu_mean_cores'], .09)

    def test_backlog_boundary_excess_fails_over_actual_cli(self):
        # Permit growth so the END case specifically exercises backlog_max,
        # not the existing independent backlog_growth_max rejection.
        self.config['backlog_growth_max'] = 3000
        baseline = self.baseline()
        type(self).window_start = baseline['captured_at']
        for media in ('movie', 'episode'):
            for state in ('eligible', 'leased'):
                for edge in ('start', 'end'):
                    # 3000 reproduces the review's interpolated 1505 excess;
                    # 1001 requires conservative bracketing, not interpolation.
                    for high in (3000, 1001):
                        with self.subTest(media=media, state=state, edge=edge, high=high):
                            type(self).boundary_fixture = {
                                'metric': 'phantom_availability_backlog', 'media': media,
                                'state': state, 'edge': edge, 'high': high}
                            self.cli(['--evaluate'], baseline, 'FAILED', media + ': backlog unbounded')

    def test_backlog_exact_boundaries_exclude_outside_excess(self):
        baseline = self.baseline()
        type(self).window_start = baseline['captured_at']
        for edge in ('start', 'end'):
            with self.subTest(edge=edge):
                type(self).boundary_fixture = {
                    'metric': 'phantom_availability_backlog', 'media': 'movie',
                    'state': 'eligible', 'edge': edge, 'high': 3000, 'exact': True}
                self.cli(['--evaluate'], baseline)

    def test_short_history_and_policy_drift(self):
        baseline = self.baseline()
        short = copy.deepcopy(baseline)
        short['captured_at'] = self.now - 3600
        self.cli(['--evaluate'], short, 'FAILED', 'insufficient history')
        self.config['backlog_max'] = 2000
        self.cli(['--evaluate'], baseline, 'FAILED', 'policy/config mismatch')

    def test_tolerance_requires_evidence(self):
        self.config['seq_tup_read_per_hour_max'] = 1
        self.cli(['--capture-baseline'], expected='FAILED', fragment='requires evidence')
        self.config['seq_tolerance_evidence'] = 'Approved maintenance observation: example evidence ID 42'
        baseline = self.baseline()
        type(self).scenario = 'pg-growth'
        self.cli(['--evaluate'], baseline)

    def test_final_receipt_and_live_rerun(self):
        baseline = self.baseline()
        pre, pre_path = self.cli(['--evaluate'], baseline)
        # Prior acceptance is a separate earlier window in this controlled fixture.
        pre['captured_at'] = self.now - 180000
        pre['interval'] = {'start': self.now - 267000, 'end': pre['captured_at'] - 120, 'seconds': 86880}
        pre_path.write_text(json.dumps(pre))
        receipt = {'schema': 1, 'pre_sizing_report': pre_path.name,
                   'pre_sizing_report_sha256': hashlib.sha256(pre_path.read_bytes()).hexdigest(),
                   'applied_at': self.now - 95000,
                   'flux': {'namespace': 'gitops', 'name': 'database-sizing', 'revision': 'main@sha1:' + 'b' * 40},
                   'resources': {role: {'main': {'requests': {'cpu': '100m'}, 'limits': {'cpu': '1'}}}
                                 for role in ('application', 'database')}}
        receipt_path = self.directory / 'receipt.json'
        receipt_path.write_text(json.dumps(receipt))
        type(self).sized = True
        baseline = self.baseline()
        evidence, _ = self.cli(['--final', str(receipt_path)], baseline)
        self.assertEqual(evidence['stage'], 'final')
        self.assertTrue(any('/kustomizations/' in q['url'] for q in evidence['http']))
        for scenario, fragment in [('flux-drift', 'Flux receipt'), ('zero-completions', 'zero claims/completions'),
                                   ('cpu-high', 'CPU mean')]:
            with self.subTest(scenario=scenario):
                type(self).scenario = scenario
                self.cli(['--final', str(receipt_path)], baseline, 'FAILED', fragment)
        type(self).scenario = 'good'
        receipt['resources']['database']['main']['requests']['cpu'] = '200m'
        receipt_path.write_text(json.dumps(receipt))
        self.cli(['--final', str(receipt_path)], baseline, 'FAILED', 'resources differ')
        receipt['pre_sizing_report_sha256'] = '0' * 64
        receipt_path.write_text(json.dumps(receipt))
        self.cli(['--final', str(receipt_path)], baseline, 'FAILED', 'digest mismatch')


if __name__ == '__main__':
    unittest.main(verbosity=2)
