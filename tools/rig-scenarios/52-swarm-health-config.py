#!/usr/bin/env python3
"""Isolated P0.5 config rig. Helm-rendered startup + real Jellyfin/Phantom HTTP worker.

No live cluster, production seed, host service or shared rig is used. Python stdlib
plus PyYAML, Helm, .NET 9 and ffmpeg are required. The shell entrypoint builds both
plugin and host; no mock PluginConfiguration or substitute measurement consumer.
"""
import argparse
import contextlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import shutil
import socket
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET

import yaml

PLUGIN = 'Jellyfin.Plugin.PhantomLibrary'
PLUGIN_ID = '9e7a1f4c-2b5d-4e8f-9a3b-7c1d2e5f6a8b'
HASHES = ['a' * 40, 'b' * 40]
SECRET = 'operator & <secret> "quoted" \'token\''


def render(root, work, values):
    file = work / 'values.json'
    file.write_text(json.dumps(values))
    result = subprocess.run(['helm', 'template', 'rig', str(root / 'deploy/helm/phantom-library'),
                             '-f', str(file)], capture_output=True, text=True, check=True)
    return [d for d in yaml.safe_load_all(result.stdout) if d]


def startup(root, work, cfg, values, extra_env=None, expected=0):
    docs = render(root, work, values)
    pod = next(d for d in docs if d['kind'] == 'StatefulSet')['spec']['template']
    init = next((c for c in pod['spec'].get('initContainers', []) if c['name'] == 'wire-plugin-config'), None)
    if init is None:
        assert not values.get('swarmHealth', {}).get('enabled'), 'missing managed init path'
        return
    pvc_mount = next(v for v in init['volumeMounts'] if v['mountPath'] == '/var/lib/jellyfin')
    workload = next(c for c in pod['spec']['containers'] if c['name'] == 'jellyfin')
    assert any(v['mountPath'] == '/var/lib/jellyfin' and v['name'] == pvc_mount['name']
               for v in workload['volumeMounts']), 'init must edit the actual plugin PVC'
    mount = next(v for v in init['volumeMounts'] if v['mountPath'] == '/plugin-config')
    volume = next(v for v in pod['spec']['volumes'] if v['name'] == mount['name'])
    cm = next(d for d in docs if d['kind'] == 'ConfigMap' and d['metadata']['name'] == volume['configMap']['name'])
    script = work / 'rendered-merge-plugin-config.py'
    script.write_text(cm['data']['merge-plugin-config.py'])
    env = {k: v for k, v in os.environ.items() if not k.startswith('PHANTOM_')}
    env.update({e['name']: e['value'] for e in init['env']})
    env.update(extra_env or {})
    command = [str(script) if s == '/plugin-config/merge-plugin-config.py' else s for s in init['command']]
    result = subprocess.run(command + init.get('args', []) + ['--config', str(cfg)],
                            env=env, text=True, capture_output=True)
    assert (result.returncode == 0) == (expected == 0), (result.stdout, result.stderr)
    assert SECRET not in result.stdout + result.stderr
    return pod


def fields(cfg):
    return {c.tag: c.text or '' for c in ET.parse(cfg).getroot()}


def values(url=None, enabled=True, prowlarr=False):
    v = {'prowlarrConfig': {'apiKeySecretName': 'rig-secret' if prowlarr else ''}}
    if url is not None:
        v['swarmHealth'] = {'enabled': enabled, 'serviceUrl': url}
    return v


def chart_tests(root, work):
    cfg = work / 'synthetic-pvc/plugins/configurations' / (PLUGIN + '.xml')
    startup(root, work, cfg, values())
    assert not cfg.exists(), 'unmanaged fresh PVC must remain untouched'
    cfg.parent.mkdir(parents=True, exist_ok=True)
    cfg.write_text('<PluginConfiguration><!--keep--><SwarmHealthServiceUrl>http://old.example.com</SwarmHealthServiceUrl>'
                   '<GostreamApiToken>operator &amp; &lt;secret&gt; "quoted" \'token\'</GostreamApiToken>'
                   '<MinSeeders>17</MinSeeders><Unknown><Nested secret="keep">opaque</Nested></Unknown></PluginConfiguration>')
    cfg.chmod(0o640)
    before = cfg.read_bytes()
    for v in [values(), values('not a URL; ignored while unmanaged', enabled=False)]:
        startup(root, work, cfg, v)
        assert cfg.read_bytes() == before
    endpoint = "http://swarm-health.example.com:8095/a&b/'quoted'/json%22"
    pod = startup(root, work, cfg, values(endpoint))
    assert fields(cfg)['SwarmHealthServiceUrl'] == endpoint
    assert fields(cfg)['GostreamApiToken'] == SECRET
    assert fields(cfg)['MinSeeders'] == '17'
    assert ET.parse(cfg).find('Unknown/Nested').attrib == {'secret': 'keep'}
    assert '<!--keep-->' in cfg.read_text()
    assert cfg.stat().st_mode & 0o777 == 0o640
    after = cfg.read_bytes()
    startup(root, work, cfg, values(endpoint))
    assert cfg.read_bytes() == after, 'merge must be idempotent'
    assert startup(root, work, cfg, values(endpoint + '/new')) != pod, 'URL change must roll the Pod'
    for invalid in ['ftp://example.com', 'http://', 'http://example.com:bad', 'http://example.com:65536',
                    'http://user:secret@example.com', ' http://example.com', 'http://example.com/\n',
                    'http://example.com?x=1&y=2', 'http://example.com/#frag', 'http://example.com/"quote']:
        before = cfg.read_bytes()
        startup(root, work, cfg, values(invalid), expected=1)
        assert cfg.read_bytes() == before, 'invalid input must not modify PVC'
    startup(root, work, cfg, values(''))
    assert fields(cfg)['SwarmHealthServiceUrl'] == ''
    assert fields(cfg)['GostreamApiToken'] == SECRET
    # Replacement Prowlarr path uses the same safe serializer, including fresh install.
    key = work / 'prowlarr-key'
    key.write_text(SECRET + '\n')
    fresh = work / 'fresh.xml'
    startup(root, work, fresh, values(endpoint, prowlarr=True), {'PHANTOM_PROWLARR_API_KEY_FILE': str(key)})
    assert fields(fresh)['ProwlarrApiKey'] == SECRET
    assert fields(fresh)['SwarmHealthServiceUrl'] == endpoint
    # A Prowlarr-only restart must not erase an operator's service URL.
    startup(root, work, fresh, values(prowlarr=True), {'PHANTOM_PROWLARR_API_KEY_FILE': str(key)})
    assert fields(fresh)['SwarmHealthServiceUrl'] == endpoint
    for invalid_values in [{'enabled': 'false', 'serviceUrl': ''}, {'enabled': True, 'serviceUrl': 123},
                           {'enabled': True, 'serviceUrl': None}]:
        try:
            render(root, work, {'swarmHealth': invalid_values})
        except subprocess.CalledProcessError:
            pass
        else:
            raise AssertionError('Helm accepted invalid setting types: ' + repr(invalid_values))
    for xml in ['<broken>', '<Other/>', '<!DOCTYPE PluginConfiguration><PluginConfiguration/>',
                '<PluginConfiguration><SwarmHealthServiceUrl/><SwarmHealthServiceUrl/></PluginConfiguration>']:
        fresh.write_text(xml)
        startup(root, work, fresh, values(endpoint), expected=1)
        assert fresh.read_text() == xml
    print('PASS: rendered startup fresh/retained, absent/disabled/clear, XML/JSON escaping, secrets, invalid input, atomic/idempotent merge', flush=True)


def request(base, path, body=None, token=None):
    headers = {'Content-Type': 'application/json',
               'Authorization': 'MediaBrowser Client="P05Rig", Device="Isolated", DeviceId="p05-config", Version="1"'}
    if token:
        headers['X-Emby-Token'] = token
    req = urllib.request.Request(base + path, data=json.dumps(body).encode() if body is not None else None, headers=headers)
    with urllib.request.urlopen(req, timeout=5) as reply:
        data = reply.read()
        return json.loads(data) if data else None


def eventually(predicate, process, log, seconds=90):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        if process.poll() is not None:
            raise AssertionError('Jellyfin exited: ' + log.read_text()[-18000:])
        try:
            result = predicate()
            if result:
                return result
        except (urllib.error.URLError, sqlite3.OperationalError, json.JSONDecodeError):
            pass
        time.sleep(0.25)
    raise AssertionError('timed out: ' + log.read_text()[-18000:])


@contextlib.contextmanager
def host(build, rig):
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    config = rig / 'config'
    config.mkdir(exist_ok=True)
    (config / 'network.xml').write_text(f'<NetworkConfiguration><InternalHttpPort>{port}</InternalHttpPort>'
                                       '<EnableIPV6>false</EnableIPV6><EnableIPV4>true</EnableIPV4>'
                                       '<EnableSSDP>false</EnableSSDP><EnableUPnP>false</EnableUPnP>'
                                       '<LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses></NetworkConfiguration>')
    log = rig / 'host.log'
    command = ['dotnet', str(build / 'jellyfin/Jellyfin.Server/bin/Release/net9.0/jellyfin.dll'),
               '--datadir', str(rig / 'data'), '--configdir', str(config), '--cachedir', str(rig / 'cache'),
               '--logdir', str(rig / 'logs'), '--nowebclient', '--ffmpeg', shutil.which('ffmpeg') or 'ffmpeg']
    env = {k: v for k, v in os.environ.items() if not k.startswith(('PHANTOM_', 'JELLYFIN_', 'POSTGRES_'))}
    with log.open('w') as output:
        process = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT, env=env)
        try:
            base = f'http://127.0.0.1:{port}'
            def healthy():
                with urllib.request.urlopen(base + '/health', timeout=5) as response:
                    return response.read() == b'Healthy'
            eventually(healthy, process, log)
            info = request(base, '/System/Info/Public')
            if not info['StartupWizardCompleted']:
                request(base, '/Startup/Configuration', {'UICulture': 'en-US', 'MetadataCountryCode': 'US', 'PreferredMetadataLanguage': 'en'})
                request(base, '/Startup/User')
                request(base, '/Startup/User', {'Name': 'rig', 'Password': 'isolated-config-rig-password'})
                request(base, '/Startup/Complete', {})
            auth = request(base, '/Users/AuthenticateByName', {'Username': 'rig', 'Pw': 'isolated-config-rig-password'})
            yield base, auth['AccessToken'], process, log
        except Exception:
            print(log.read_text()[-18000:], file=sys.stderr)
            raise
        finally:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


def rig_tests(root, work, build):
    received = []

    class Service(BaseHTTPRequestHandler):
        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
            received.append((self.path, body))
            data = json.dumps({'results': [{'info_hash': h, 'dht_peers': 11 + HASHES.index(h.lower()), 'measured_at': int(time.time())}
                                           for h in body['info_hashes']], 'dht_nodes': 200}).encode()
            self.send_response(200)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer(('127.0.0.1', 0), Service)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    endpoint = f'http://127.0.0.1:{server.server_port}/a&b'
    try:
        for fresh in [True, False]:
            rig = work / ('fresh-rig' if fresh else 'retained-rig')
            cfg = rig / 'data/plugins/configurations' / (PLUGIN + '.xml')
            cfg.parent.mkdir(parents=True)
            if not fresh:
                cfg.write_text('<PluginConfiguration><SwarmHealthServiceUrl>http://127.0.0.1:1/stale</SwarmHealthServiceUrl>'
                               '<GostreamApiToken>retained-secret</GostreamApiToken><MinSeeders>17</MinSeeders></PluginConfiguration>')
            # Startup does the first/fresh initialization. No test-written service URL in XML/API.
            startup(root, work, cfg, values(endpoint))
            plugin_dir = rig / 'data/plugins' / PLUGIN
            plugin_dir.mkdir()
            output = build / 'src' / PLUGIN / 'bin/Release/net9.0'
            # Include transitive plugin dependencies, not duplicate Jellyfin host assemblies.
            host_output = build / 'jellyfin/Jellyfin.Server/bin/Release/net9.0'
            for dll in output.glob('*.dll'):
                if not (host_output / dll.name).exists():
                    shutil.copy2(dll, plugin_dir / dll.name)
            version = ET.parse(root / 'src' / PLUGIN / (PLUGIN + '.csproj')).findtext('.//AssemblyVersion')
            (plugin_dir / 'meta.json').write_text(json.dumps({'guid': PLUGIN_ID, 'name': 'Phantom Library',
                                                            'version': version, 'targetAbi': '10.11.9.0', 'status': 'Active'}))
            for restart in range(2):
                startup(root, work, cfg, values(endpoint))
                received.clear()
                with host(build, rig) as (base, token, process, log):
                    loaded = request(base, f'/Plugins/{PLUGIN_ID}/Configuration', token=token)
                    assert loaded['SwarmHealthServiceUrl'] == endpoint, loaded
                    if not fresh:
                        assert loaded['GostreamApiToken'] == 'retained-secret'
                        assert loaded['MinSeeders'] == 17
                    # Change only rig scheduling settings through the real config API. The URL
                    # remains what the chart loaded. Next boot also re-proves serializer reload.
                    loaded.update({'SwarmHealthIntervalSeconds': 10, 'AvailabilityProbeEnabled': False,
                                   'MagnetCacheSweepEnabled': False, 'MagnetCacheDrainEnabled': False,
                                   'EagerResolveEnabled': False, 'ChannelWarmupEnabled': False})
                    request(base, f'/Plugins/{PLUGIN_ID}/Configuration', loaded, token)
                    db = cfg.parent / 'PhantomLibrary/phantom.db'
                    eventually(lambda: db.exists(), process, log)
                    with sqlite3.connect(db) as connection:
                        connection.execute('DELETE FROM swarm_health')
                        for i, (kind, season, episode) in enumerate([('movie', -1, -1), ('episode', 1, 2)]):
                            connection.execute('''INSERT OR REPLACE INTO availability_items
                                (tmdb_id,type,season,episode,status,next_check_at,priority,candidate_info_hash)
                                VALUES (?,?,?,?,?,?,?,?)''', (99000001 + i, kind, season, episode, 'available', int(time.time()) + 86400, 0, HASHES[i]))
                    def measured():
                        with sqlite3.connect(db) as connection:
                            rows = connection.execute('SELECT type,dht_peers FROM swarm_health ORDER BY type').fetchall()
                        return rows == [('episode', 12), ('movie', 11)]
                    eventually(measured, process, log, seconds=100)
                    assert any(path == '/a&b/measure' and set(h.lower() for h in body['info_hashes']) == set(HASHES)
                               for path, body in received), received
                print(f'PASS: actual Jellyfin plugin {"fresh" if fresh else "retained"} PVC boot={restart + 1}, loaded URL, movie+episode HTTP measurements persisted', flush=True)
            # Explicit clear reaches the actual consumer, not only XML. Its running worker
            # must make no request even with two due candidates waiting.
            startup(root, work, cfg, values(''))
            received.clear()
            with sqlite3.connect(cfg.parent / 'PhantomLibrary/phantom.db') as connection:
                connection.execute('DELETE FROM swarm_health')
            with host(build, rig) as (base, token, process, log):
                loaded = request(base, f'/Plugins/{PLUGIN_ID}/Configuration', token=token)
                assert loaded['SwarmHealthServiceUrl'] == ''
                if not fresh:
                    assert loaded['GostreamApiToken'] == 'retained-secret'
                    assert loaded['MinSeeders'] == 17
                # A complete worker interval is necessary negative evidence (not convergence polling).
                end = time.monotonic() + 12
                while time.monotonic() < end:
                    assert process.poll() is None
                    assert not received, received
                    time.sleep(0.25)
            print(f'PASS: actual plugin explicit disable, no HTTP calls ({"fresh" if fresh else "retained"} PVC)', flush=True)
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--work', type=Path, required=True)
    parser.add_argument('--chart-only', action='store_true')
    # Tree holding the built plugin + Jellyfin server; defaults to --root (in-place build).
    parser.add_argument('--build-root', type=Path)
    args = parser.parse_args()
    if args.chart_only:
        chart_tests(args.root, args.work)
    else:
        rig_tests(args.root, args.work, args.build_root or args.root)
        print('ALL PASS: p05-swarm-health-config', flush=True)
