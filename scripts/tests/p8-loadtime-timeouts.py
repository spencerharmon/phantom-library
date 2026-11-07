"""Exercise the live engine with real curl, not a mock of curl's timeout flags."""
import json
import os
from pathlib import Path
import signal
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit


engine = Path(sys.argv[1]).resolve()
ids = dict(user='1' * 32, movie='2' * 32, series='3' * 32,
           season='4' * 32, episode='5' * 32)
stop = threading.Event()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        self.rfile.read(int(self.headers.get('Content-Length', 0)))
        self.do_GET()

    def do_GET(self):
        url = urlsplit(self.path)
        q = parse_qs(url.query)
        page = q.get('Limit') == ['200']
        badge = url.path == '/Plugins/PhantomLibrary/States'
        mode = self.server.mode
        if ((mode in ('page', 'sample-page', 'partial') and page)
                or (mode in ('badge', 'sample-badge') and badge)
                or (mode == 'post' and self.command == 'POST' and not badge)):
            if mode == 'partial':
                self.send_response(200)
                self.send_header('Content-Length', '10000')
                self.end_headers()
                self.wfile.write(b'{')
                self.wfile.flush()
            stop.wait(30)
            return
        if url.path == '/Users':
            body = [{'Id': ids['user']}]
        elif url.path == '/Channels':
            body = {'Items': [{'Name': 'Phantom Movies', 'Id': 'movies'},
                              {'Name': 'Phantom Shows', 'Id': 'shows'}]}
        elif q.get('Limit') == ['1']:
            kind = ('movie' if '/movies/' in url.path else
                    {ids['series']: 'season', ids['season']: 'episode'}.get(
                        q.get('FolderId', [''])[0], 'series'))
            body = {'Items': [{'Id': ids[kind], 'Type': kind.title()}]}
        elif page:
            # Individually fast requests, but a catalogue that never ends.
            body = {'Items': [{'Id': 'item'}],
                    'TotalRecordCount': 1000000000 if mode == 'endless' else 1}
        else:
            body = {}
        data = json.dumps(body).encode()
        self.send_response(200)
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        try:
            self.wfile.write(data)
        except BrokenPipeError:
            pass


with ThreadingHTTPServer(('127.0.0.1', 0), Handler) as server:
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        for mode in ('page', 'sample-page', 'partial', 'badge', 'sample-badge', 'endless', 'post'):
            server.mode = mode
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                scratch = root / 'scratch'
                scratch.mkdir()
                evidence = subprocess.check_output([
                    'python3', str(engine.parent.parent / 'ci/loadtime-evidence.py'),
                    'init', str(root / 'evidence'), str(engine.parents[2]),
                    '1234567890', '32'], text=True).strip()
                env = dict(os.environ, PHANTOM_CI_DRYRUN='0',
                           PHANTOM_LOADTIME_API=f'http://127.0.0.1:{server.server_port}',
                           PHANTOM_LOADTIME_SAMPLES='1', PHANTOM_LOADTIME_COLOR='rigtest',
                           PHANTOM_LOADTIME_REQUEST_TIMEOUT_SECONDS='10' if mode.startswith('sample-') else '0.2',
                           PHANTOM_LOADTIME_LIST_TIMEOUT_SECONDS='0.6',
                           PHANTOM_LOADTIME_EVIDENCE_FILE=evidence,
                           PHANTOM_LOADTIME_OUT=str(root / 'metrics'), TMPDIR=str(scratch))
                start = time.monotonic()
                proc = subprocess.Popen(['bash', str(engine)], env=env, text=True,
                                        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                        start_new_session=True)
                try:
                    stdout, stderr = proc.communicate(timeout=12)
                except subprocess.TimeoutExpired:
                    os.killpg(proc.pid, signal.SIGKILL)
                    proc.communicate()
                    raise AssertionError(f'{mode}: engine hung instead of attributing timeout')
                assert proc.returncode == 0, stderr
                assert time.monotonic() - start < 12
                assert not list(scratch.iterdir()), 'scratch leaked'
                assert (root / 'metrics').read_text() == stdout
                failures = json.loads(Path(evidence).read_text())['failures']
                expected_stage = ('request' if mode == 'post' else
                                  'badge_states' if mode in ('badge', 'sample-badge') else 'page_fetch')
                assert len(failures) == (2 if mode == 'post' else 4), failures
                for failure in failures:
                    assert failure['stage'] == expected_stage, failure
                    assert failure['curl_exit_code'] == 28, failure
                    assert failure['http_status'] == '000', failure
                for kind in ('movie', 'episode'):
                    for flow in ('list_load', 'sort_change', 'info_open', 'get_sources',
                                 'materialise', 'play_materialised'):
                        failed = flow == 'materialise' if mode == 'post' else flow in ('list_load', 'sort_change')
                        labels = f'flow="{flow}",item_type="{kind}",color="rigtest"'
                        assert f'phantom_loadtime_errors_total{{{labels}}} {int(failed)}' in stdout
                        assert f'phantom_loadtime_runs_total{{{labels}}} 1' in stdout
                        if failed and mode != 'post':
                            assert (f'phantom_loadtime_list_errors_total{{flow="{flow}",item_type="{kind}",'
                                    f'stage="{expected_stage}",http_code="000"}} 1') in stdout
                print(f'TIMEOUT {mode}: bounded, attributed, all twelve flows emitted, cleanup: PASS')
    finally:
        stop.set()
        server.shutdown()
        thread.join()

# Run the real daily wrapper with only its engine/cleanup dependencies stubbed.
# Even a wedged engine must produce a durable stage outcome before Job expiry.
with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    (root / 'tools/ci').mkdir(parents=True)
    (root / 'tools/rig-scenarios').mkdir()
    daily = root / 'tools/ci/loadtime-daily-run.sh'
    shutil.copyfile(engine.parent.parent / 'ci/loadtime-daily-run.sh', daily)
    (daily.parent / 'lib-cleanup.sh').write_text('phantom_ci_cleanup_dotnet() { :; }\n')
    (root / 'tools/rig-scenarios/47-loadtime-flows.sh').write_text('sleep 30\n')
    result = subprocess.run(['bash', str(daily)], env=dict(
        os.environ, PHANTOM_CI_DRYRUN='1', PHANTOM_LOADTIME_ACTIVE_DEADLINE_SECONDS='1800',
        PHANTOM_LOADTIME_MEASURE_TIMEOUT_SECONDS='0.2', PHANTOM_INCLUSTER_DEV_HOST='dev.example.com',
        PHANTOM_INCLUSTER_PROD_HOST='example.com'), capture_output=True, text=True, timeout=8)
    assert result.returncode != 0, result.stdout
    assert 'phantom_loadtime_daily_stage_exit_code{stage="measure"} 124' in result.stderr
    assert '[2] pushing' not in result.stdout, 'incomplete batch pushed'
    print('TIMEOUT daily: wedged engine bounded, measure exit=124 published, no partial batch push: PASS')
    for deadline, budget in [('300', ''), ('1800', '1501'), ('5400', '5101'), ('1800', '0')]:
        result = subprocess.run(['bash', str(daily)], env=dict(
            os.environ, PHANTOM_CI_DRYRUN='1', PHANTOM_LOADTIME_ACTIVE_DEADLINE_SECONDS=deadline,
            PHANTOM_LOADTIME_MEASURE_TIMEOUT_SECONDS=budget), capture_output=True, text=True, timeout=3)
        assert result.returncode == 2 and 'FAIL: measure timeout' in result.stderr, result
    print('TIMEOUT daily: invalid/oversized budgets fail closed: PASS')

for name in ('PHANTOM_LOADTIME_REQUEST_TIMEOUT_SECONDS', 'PHANTOM_LOADTIME_LIST_TIMEOUT_SECONDS'):
    for value in ('0', '-1', 'nan', 'inf', 'bad'):
        result = subprocess.run(['bash', str(engine)], env=dict(
            os.environ, PHANTOM_CI_DRYRUN='1', **{name: value}),
            capture_output=True, text=True, timeout=3)
        assert result.returncode == 2 and 'timeout seconds' in result.stderr, result
print('TIMEOUT engine: non-positive/non-finite/invalid budgets rejected: PASS')

for name in ('PHANTOM_LOADTIME_ENGINE_BUDGET_SECONDS',):
    for value in ('0', '-1', 'nan', 'bad'):
        result = subprocess.run(['bash', str(engine)], env=dict(
            os.environ, PHANTOM_CI_DRYRUN='1', **{name: value}),
            capture_output=True, text=True, timeout=3)
        assert result.returncode == 2 and 'timeout seconds' in result.stderr, result
print('TIMEOUT engine: invalid engine budget rejected: PASS')


# loadtime-daily-list-load-timeout: (a) a timed-out list sample fails the rest
# of that flow's samples fast instead of re-burning LIST_TIMEOUT per sample;
# (b) an exhausted engine budget still exits 0 and flushes all twelve flows.
def run_engine(server, extra, limit):
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        env = dict(os.environ, PHANTOM_CI_DRYRUN='0',
                   PHANTOM_LOADTIME_API=f'http://127.0.0.1:{server.server_port}',
                   PHANTOM_LOADTIME_COLOR='rigtest', TMPDIR=directory,
                   PHANTOM_LOADTIME_REQUEST_TIMEOUT_SECONDS='0.2',
                   PHANTOM_LOADTIME_LIST_TIMEOUT_SECONDS='0.6', **extra)
        start = time.monotonic()
        proc = subprocess.run(['bash', str(engine)], env=env, capture_output=True,
                              text=True, timeout=limit)
        return proc, time.monotonic() - start


stop.clear()
with ThreadingHTTPServer(('127.0.0.1', 0), Handler) as server:
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        server.mode = 'page'
        proc, _ = run_engine(server, {'PHANTOM_LOADTIME_SAMPLES': '3'}, 30)
        assert proc.returncode == 0, proc.stderr
        for kind in ('movie', 'episode'):
            for flow in ('list_load', 'sort_change'):
                labels = f'flow="{flow}",item_type="{kind}",color="rigtest"'
                assert f'phantom_loadtime_runs_total{{{labels}}} 3' in proc.stdout, proc.stdout
                assert f'phantom_loadtime_errors_total{{{labels}}} 3' in proc.stdout, proc.stdout
                # only ONE sample was actually attempted (and attributed)
                assert (f'phantom_loadtime_list_errors_total{{flow="{flow}",item_type="{kind}",'
                        f'stage="page_fetch",http_code="000"}} 1') in proc.stdout, proc.stdout
        assert proc.stderr.count('skipped (prior sample timed out') == 8, proc.stderr
        print('TIMEOUT fail-fast: timed-out list sample skips remaining samples: PASS')

        proc, elapsed = run_engine(server, {'PHANTOM_LOADTIME_SAMPLES': '3',
                                            'PHANTOM_LOADTIME_ENGINE_BUDGET_SECONDS': '0.4'}, 30)
        assert proc.returncode == 0, proc.stderr
        for kind in ('movie', 'episode'):
            for flow in ('list_load', 'sort_change', 'info_open', 'get_sources',
                         'materialise', 'play_materialised'):
                labels = f'flow="{flow}",item_type="{kind}",color="rigtest"'
                assert f'phantom_loadtime_runs_total{{{labels}}} 3' in proc.stdout, proc.stdout
                assert f'phantom_loadtime_errors_total{{{labels}}} 3' in proc.stdout, proc.stdout
        assert 'engine budget spent' in proc.stderr
        print('TIMEOUT engine budget: spent budget still flushes all twelve flows, exit 0: PASS')
    finally:
        stop.set()
        server.shutdown()
        thread.join()
