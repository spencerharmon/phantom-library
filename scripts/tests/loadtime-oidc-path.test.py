#!/usr/bin/env python3
"""Real HTTP gate + API, real LIVE daily wrapper/engine/emitter/guard."""
import http.server
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
from urllib.parse import parse_qs, urlsplit

SOURCE = Path(__file__).resolve().parents[2]
IDS = dict(Movie='2' * 32, Series='3' * 32, Season='4' * 32, Episode='5' * 32)
requests = []
pushes = []


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        path = urlsplit(self.path)
        query = parse_qs(path.query)
        token = self.headers.get('X-Emby-Token')
        requests.append((path.path, token))
        body = self.rfile.read(int(self.headers.get('Content-Length', 0)))
        if path.path.startswith('/metrics/'):
            pushes.append((path.path, body.decode()))
            self.send_response(200)
            self.end_headers()
            return
        if path.path.startswith('/gate/') or self.server.redirect_api:
            self.send_response(302)
            self.send_header('Location', '/authorize?state=DO-NOT-RETAIN')
            self.end_headers()
            return
        if token != 'fixture-token':
            self.send_response(401)
            self.end_headers()
            return
        route = path.path.removeprefix('/api')
        if route == '/Users':
            value = [{'Id': '1' * 32}]
        elif route == '/Channels':
            value = {'Items': [{'Name': 'Phantom Movies', 'Id': 'movies'},
                               {'Name': 'Phantom Shows', 'Id': 'shows'}]}
        elif route.startswith('/Channels/'):
            kind = 'Movie' if '/movies/' in route else {
                IDS['Series']: 'Season', IDS['Season']: 'Episode',
            }.get(query.get('FolderId', [''])[0], 'Series')
            value = {'Items': [{'Id': IDS[kind], 'Type': kind}], 'TotalRecordCount': 1}
        elif route == '/Plugins/PhantomLibrary/States':
            value = {item: 'Phantom' for item in json.loads(body)['ids']}
        elif route.startswith(('/Items/', '/Videos/')):
            value = {}
        else:
            raise AssertionError(route)
        self.send_response(200)
        self.end_headers()
        self.wfile.write(json.dumps(value).encode())

    do_POST = do_GET
    do_PUT = do_GET


with tempfile.TemporaryDirectory(prefix='loadtime-oidc-') as name:
    tmp = Path(name)
    root = tmp / 'checkout'
    for folder in ('tools', 'scripts'):
        shutil.copytree(SOURCE / folder, root / folder,
                        ignore=shutil.ignore_patterns('bin', 'obj', '__pycache__'))
    (root / 'tools/ci/lib-cleanup.sh').write_text('phantom_ci_cleanup_dotnet() { :; }\n')
    bin_dir = tmp / 'bin'
    bin_dir.mkdir()
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    server.redirect_api = False
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        # Only remap transport addresses. Actual HTTP handling, curl status and
        # redirects are real; no forged curl stdout/exit status.
        (bin_dir / 'curl').write_text('''#!/usr/bin/env python3
import os, subprocess, sys
args = sys.argv[1:]
for i, arg in enumerate(args):
    for prefix, route in [('https://dev.example.com', '/gate'),
                          ('http://phantom-library-' + os.environ['FIXTURE_COLOR'] + '.fixture.svc:8096', '/api')]:
        if arg.startswith(prefix + '/'):
            args[i] = os.environ['FIXTURE_HTTP'] + route + arg[len(prefix):]
raise SystemExit(subprocess.call([os.environ['REAL_CURL'], *args]))
''')
        (bin_dir / 'kubectl').write_text('''#!/usr/bin/env python3
import os, sys
args = sys.argv[1:]
color = os.environ['FIXTURE_COLOR']
if 'phantom-library-' + color in args:
    if '.backend.service' in args[-1]:
        if os.environ.get('FIXTURE_BACKEND_EXIT'):
            sys.exit(int(os.environ['FIXTURE_BACKEND_EXIT']))
        print(os.environ.get('FIXTURE_BACKEND', 'phantom-library-' + color + ':8096'))
    else:
        print('dev.example.com')
        if os.environ.get('FIXTURE_PROD'):
            print('example.com')
''')
        (bin_dir / 'beehive').write_text('#!/bin/sh\nexit 0\n')
        for file in bin_dir.iterdir():
            file.chmod(0o755)
        env = {k: v for k, v in os.environ.items() if not k.startswith(('PHANTOM_', 'GIT_'))}
        env.update(PATH=str(bin_dir) + os.pathsep + os.environ['PATH'],
                   REAL_CURL=shutil.which('curl'), FIXTURE_HTTP=f'http://127.0.0.1:{server.server_port}',
                   PHANTOM_REPO_ROOT=str(root), PHANTOM_CI_DRYRUN='0', PHANTOM_CI_PKILL='0',
                   PHANTOM_INCLUSTER_NAMESPACE='fixture',
                   PHANTOM_INCLUSTER_DEV_HOST='dev.example.com', PHANTOM_INCLUSTER_PROD_HOST='example.com',
                   PHANTOM_INCLUSTER_ADMIN_TOKEN='fixture-token', PHANTOM_LOADTIME_SAMPLES='1',
                   PHANTOM_PUSHGATEWAY_URL=f'http://127.0.0.1:{server.server_port}',
                   MSBUILDDISABLENODEREUSE='1', DOTNET_CLI_USE_MSBUILD_SERVER='0')
        for color in ('green', 'blue'):
            state = tmp / color
            state.mkdir()
            env.update(FIXTURE_COLOR=color, PHANTOM_LOADTIME_THRESHOLDS_PATH=str(state / 'thresholds.json'))
            result = subprocess.run(['bash', str(root / 'tools/ci/loadtime-daily-run.sh')],
                                    env=env, text=True, capture_output=True, timeout=120)
            assert result.returncode == 0, result.stdout + result.stderr
            manifest, = (state / 'loadtime-evidence').glob('run-*.json')
            data = json.loads(manifest.read_text())
            assert data['targets'] == IDS and data['color'] == color, data
            assert data['stages']['discovery']['exit_code'] == 0 and not data['failures'], data
            payload = [p for route, p in pushes if route.endswith('/phantom-loadtime')][-1]
            for kind in ('movie', 'episode'):
                for flow in ('list_load', 'sort_change', 'info_open', 'get_sources', 'materialise', 'play_materialised'):
                    labels = f'{{flow="{flow}",item_type="{kind}",color="{color}"}}'
                    assert f'phantom_loadtime_seconds{labels} ' in payload, payload
                    assert f'phantom_loadtime_errors_total{labels} 0' in payload, payload
            assert all(token == 'fixture-token' for route, token in requests if route.startswith('/api/'))
            assert not any(route.startswith('/gate/') for route, _ in requests), requests
            print(f'OIDC PASS: resolved {color} Service discovers movie + episode, times and pushes all 12 flows')

        for label, overrides in (
                ('wrong-color', dict(FIXTURE_BACKEND='phantom-library-green:8096')),
                ('ambiguous', dict(FIXTURE_BACKEND='phantom-library-blue:8096\nphantom-library-blue:8096')),
                ('named-port', dict(FIXTURE_BACKEND='phantom-library-blue:')),
                ('bad-port', dict(FIXTURE_BACKEND='phantom-library-blue:65536')),
                ('prod-color', dict(FIXTURE_PROD='1'))):
            state = tmp / label
            state.mkdir()
            requests.clear()
            result = subprocess.run(['bash', str(root / 'tools/ci/loadtime-daily-run.sh')],
                env=dict(env, PHANTOM_LOADTIME_THRESHOLDS_PATH=str(state / 'thresholds.json'), **overrides),
                text=True, capture_output=True, timeout=30)
            assert result.returncode != 0 and not requests, (label, result.stdout, result.stderr, requests)
        result = subprocess.run(['bash', str(root / 'tools/rig-scenarios/47-loadtime-flows.sh')],
            env=dict(env, PHANTOM_LOADTIME_API='http://localhost:8096'), text=True, capture_output=True, timeout=10)
        assert result.returncode != 0 and 'refusing' in result.stderr, result
        print('OIDC PASS: wrong/ambiguous backend, invalid ports, prod color and standalone :8096 fail closed')

        for code, reason in ((124, 'kube-lookup-timeout'), (1, 'kube-lookup-error')):
            state = tmp / f'backend-exit-{code}'
            state.mkdir()
            result = subprocess.run(['bash', str(root / 'tools/ci/loadtime-daily-run.sh')],
                env=dict(env, PHANTOM_LOADTIME_THRESHOLDS_PATH=str(state / 'thresholds.json'),
                         FIXTURE_BACKEND_EXIT=str(code), PHANTOM_LOADTIME_KUBE_ATTEMPTS='1'),
                text=True, capture_output=True, timeout=30)
            manifest, = (state / 'loadtime-evidence').glob('run-*.json')
            data = json.loads(manifest.read_text())
            assert result.returncode != 0 and data['discovery_failure'] == dict(
                reason=reason, kubectl_exit_code=code), data
        print('OIDC PASS: backend lookup timeout and error retain distinct reasons')

        # The edge really redirects even a token-bearing request, and the engine
        # must retain a gate diagnostic if its selected API ever redirects too.
        response = subprocess.run([env['REAL_CURL'], '-s', '-o', '/dev/null', '-w', '%{http_code}',
                                   '-H', 'X-Emby-Token: fixture-token', env['FIXTURE_HTTP'] + '/gate/Users'],
                                  text=True, capture_output=True, check=True)
        assert response.stdout == '302'
        server.redirect_api = True
        state = tmp / 'redirect'
        state.mkdir()
        env['PHANTOM_LOADTIME_THRESHOLDS_PATH'] = str(state / 'thresholds.json')
        pushes.clear()
        result = subprocess.run(['bash', str(root / 'tools/ci/loadtime-daily-run.sh')],
                                env=env, text=True, capture_output=True, timeout=30)
        manifest, = (state / 'loadtime-evidence').glob('run-*.json')
        data = json.loads(manifest.read_text())
        assert result.returncode != 0 and data['targets'] == {}, data
        assert data['discovery_failure'] == dict(reason='user-redirect-gate', curl_exit_code=0, http_status='302'), data
        assert 'reason=user-redirect-gate' in result.stderr, result.stderr
        assert 'DO-NOT-RETAIN' not in manifest.read_text() + result.stdout + result.stderr
        assert not any(route.endswith('/phantom-loadtime') for route, _ in pushes)
        assert not any(route.startswith('/authorize') for route, _ in requests)
        print('OIDC PASS: token-bearing HTTP302 fails explicitly, no redirect following, secret retention or flow push')
    finally:
        server.shutdown()
        server.server_close()
        thread.join()
