#!/usr/bin/env bash
# Offline regression: drift receipts must never round JSON numbers into a match.
# Raw JSON tokens are written verbatim and fed to the real --drift CLI.
set -euo pipefail
exec python3 - "${BASH_SOURCE[0]}" <<'PY'
import json
from pathlib import Path
import subprocess
import sys
import tempfile

engine = Path(sys.argv[1]).resolve().parent.parent / 'gostream-oom-acceptance.sh'
deployment = {'kind': 'Deployment', 'metadata': {'name': 'stream', 'namespace': 'media', 'uid': 'd-uid'},
              'spec': {'replicas': 1, 'selector': {'matchLabels': {'app': 'stream'}},
                       'template': {'spec': {'containers': [{'name': 'gostream',
                           'env': [{'name': 'GOMEMLIMIT', 'value': '1800MiB'}]}]}}}}
rs = {'kind': 'ReplicaSet', 'metadata': {'name': 'rs', 'uid': 'rs-uid', 'namespace': 'media',
      'ownerReferences': [{'kind': 'Deployment', 'name': 'stream', 'uid': 'd-uid', 'controller': True}]}}
pod = {'kind': 'Pod', 'metadata': {'name': 'p', 'uid': 'p-uid', 'namespace': 'media', 'labels': {'app': 'stream'},
       'ownerReferences': [{'kind': 'ReplicaSet', 'name': 'rs', 'uid': 'rs-uid', 'controller': True}]},
       'status': {'phase': 'Running', 'containerStatuses': [{'name': 'gostream',
           'containerID': 'containerd://cur', 'state': {'running': {}}}]}}


def run(expected_tok, observed_tok, env='1800MiB', cid='containerd://cur'):
    files = {
        'deployment': json.dumps(deployment).replace('1800MiB', env),
        'pods': json.dumps(pod), 'replicasets': json.dumps(rs),
        'expected-tuning': '{"container":"gostream","rollout_at":null,"env":{"GOMEMLIMIT":"1800MiB"},'
                           '"config":{"master_concurrency_limit":%s,"read_ahead_budget_mb":64}}' % expected_tok,
        'loaded-config': '{"container_id":"%s","values":{"master_concurrency_limit":%s,'
                         '"read_ahead_budget_mb":64}}' % (cid, observed_tok),
    }
    with tempfile.TemporaryDirectory() as tmp:
        argv = [str(engine), '--drift', '--namespace', 'media', '--name', 'stream']
        for flag, raw in files.items():
            path = Path(tmp) / (flag + '.json')
            path.write_text(raw)
            argv += ['--' + flag, str(path)]
        out = subprocess.run(argv, capture_output=True, text=True)
    verdict, body = out.stdout.split(' ', 1)
    report = json.loads(body)
    states = {f['field']: f['state'] for f in report['fields']}
    return out.returncode, verdict, states, out.stderr


K = 'config.master_concurrency_limit'
cases = [
    # (name, expected token, observed token, kwargs, rc, key state)
    ('6 == 6', '6', '6', {}, 2, 'match'),  # no Flux evidence => incomplete overall
    ('6 == 6.0', '6', '6.0', {}, 2, 'match'),  # no Flux evidence => incomplete overall
    ('6 == 6e0', '6', '6e0', {}, 2, 'match'),  # no Flux evidence => incomplete overall
    ('6.0 == 6E0', '6.0', '6E0', {}, 2, 'match'),  # no Flux evidence => incomplete overall
    ('observed 6.0000000000000001 not a match', '6', '6.0000000000000001', {}, 2, 'unobservable'),
    ('expected 6.0000000000000001 not a match', '6.0000000000000001', '6', {}, 2, 'unobservable'),
    ('both 6.0000000000000001 not a match', '6.0000000000000001', '6.0000000000000001', {}, 2, 'unobservable'),
    ('observed -1e-400 not accepted as -0.0', '0', '-1e-400', {}, 2, 'unobservable'),
    ('expected -1e-400 not a match', '-1e-400', '0', {}, 2, 'unobservable'),
    ('observed 1e400 overflow rejected', '6', '1e400', {}, 2, 'unobservable'),
    ('genuine mismatch still mismatch', '6', '7.5', {}, 3, 'mismatch'),
    ('env mismatch wins over lossy observed', '6', '6.0000000000000001', {'env': '2200MiB'}, 3, 'unobservable'),
    ('env mismatch wins over lossy expected', '6.0000000000000001', '6', {'env': '2200MiB'}, 3, 'unobservable'),
    ('process binding still enforced', '6', '6', {'cid': 'containerd://old'}, 2, 'unobservable'),
]
failures = 0
for name, exp, obs, kw, rc, state in cases:
    try:
        code, verdict, states, err = run(exp, obs, **kw)
        assert code == rc, (code, rc, verdict)
        assert states[K] == state, states
        assert not err, err
        print('ok   ' + name)
    except (AssertionError, ValueError, KeyError) as error:
        failures += 1
        print(f'FAIL {name}: {error}')
print(f'Numeric fidelity: {len(cases)} cases, {failures} failures')
sys.exit(1 if failures else 0)
PY
