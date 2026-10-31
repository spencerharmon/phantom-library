#!/usr/bin/env python3
"""PATH fixtures and black-box assertions for in-cluster-acceptance-run.sh.

Only stdlib is needed. Each case owns its state, argv log and fake executables;
the production script is executed verbatim, never copied or rewritten.
"""
import copy
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from urllib.parse import urlsplit


NAME = "phantom-library-" + ("blue" if os.environ.get("WORKLOAD_FIXTURE_CASE") == "inactive-blue" else "green")
UID = "fixture-statefulset-uid"
POD = NAME + "-0"
COMPONENT = "app.kubernetes.io/component"
INSTANCE = "app.kubernetes.io/instance"
CONTROLLER_CASES = (
    "missing-controller", "controller-query-error", "controller-kind",
    "controller-api-version", "controller-name", "controller-uid",
    "controller-deleting", "controller-container", "controller-selector",
)
POD_CASES = (
    "missing-pods", "pod-query-error", "ambiguous-pods", "missing-owner",
    "empty-owners", "noncontroller-owner", "unspecified-controller",
    "string-controller", "multiple-controllers", "wrong-owner-kind",
    "wrong-owner-name", "wrong-owner-uid", "terminating-pod",
    "not-ready-pod", "no-ready-condition", "nonrunning-pod", "pod-container",
    "container-not-ready", "missing-container-status",
)
SUCCESS_CASES = (
    "statefulset", "different-release-label", "splash-first-shared-instance",
    "wrong-owner-siblings", "noncontroller-sibling-reference", "inactive-blue",
)
LATE_CASES = ("late-playback-failure", "late-tv-failure", "late-exec-failure")
CASES = SUCCESS_CASES + ("rollout-failure",) + CONTROLLER_CASES + POD_CASES + (
    "prod-equality", "ingress-active",
) + LATE_CASES


def objects(case):
    owner = {"apiVersion": "apps/v1", "kind": "StatefulSet", "name": NAME,
             "uid": UID, "controller": True}
    labels = {COMPONENT: "jellyfin", INSTANCE: NAME}
    if case == "different-release-label":
        labels[INSTANCE] = "independent-release"
    controller = {
        "apiVersion": "apps/v1", "kind": "StatefulSet",
        "metadata": {"name": NAME, "namespace": "test-media", "uid": UID},
        "spec": {"selector": {"matchLabels": {COMPONENT: "jellyfin"}},
                 "template": {"metadata": {"labels": labels},
                              "spec": {"containers": [{"name": "jellyfin"}]}}},
    }
    pod = {
        "apiVersion": "v1", "kind": "Pod",
        "metadata": {"name": POD, "namespace": "test-media", "uid": "fixture-pod-uid",
                     "labels": labels, "ownerReferences": [owner]},
        "spec": {"containers": [{"name": "jellyfin"}]},
        "status": {"phase": "Running", "conditions": [{"type": "Ready", "status": "True"}],
                   "containerStatuses": [{"name": "jellyfin", "ready": True}]},
    }
    if case == "controller-kind":
        controller["kind"] = "Deployment"
    if case == "controller-api-version":
        controller["apiVersion"] = "extensions/v1beta1"
    if case == "controller-name":
        controller["metadata"]["name"] = "other-controller"
    if case == "controller-uid":
        controller["metadata"].pop("uid")
    if case == "controller-deleting":
        controller["metadata"]["deletionTimestamp"] = "2026-01-01T00:00:00Z"
    if case == "controller-container":
        controller["spec"]["template"]["spec"]["containers"] = [{"name": "splash"}]
    if case == "controller-selector":
        controller["spec"]["selector"]["matchLabels"][COMPONENT] = "splash"
    if case == "missing-owner":
        pod["metadata"].pop("ownerReferences")
    if case == "empty-owners":
        pod["metadata"]["ownerReferences"] = []
    if case == "noncontroller-owner":
        owner["controller"] = False
    if case == "unspecified-controller":
        owner.pop("controller")
    if case == "string-controller":
        owner["controller"] = "true"
    if case == "multiple-controllers":
        pod["metadata"]["ownerReferences"].append(dict(owner, uid="fixture-other-uid"))
    if case == "noncontroller-sibling-reference":
        pod["metadata"]["ownerReferences"].insert(0, dict(owner, controller=False,
                                                        uid="fixture-observer-uid"))
    for suffix, value in (("kind", "ReplicaSet"), ("name", "other-controller"),
                          ("uid", "fixture-stale-uid")):
        if case == "wrong-owner-" + suffix:
            owner[suffix] = value
    if case == "terminating-pod":
        pod["metadata"]["deletionTimestamp"] = "2026-01-01T00:00:00Z"
    if case == "not-ready-pod":
        pod["status"]["conditions"][0]["status"] = "False"
    if case == "no-ready-condition":
        pod["status"]["conditions"] = []
    if case == "nonrunning-pod":
        pod["status"]["phase"] = "Pending"
    if case == "pod-container":
        pod["spec"]["containers"] = [{"name": "splash"}]
    if case == "container-not-ready":
        pod["status"]["containerStatuses"][0]["ready"] = False
    if case == "missing-container-status":
        pod["status"].pop("containerStatuses")
    pods = [pod]
    if case == "missing-pods":
        pods = []
    if case == "ambiguous-pods":
        sibling = copy.deepcopy(pod)
        sibling["metadata"].update(name=NAME + "-1", uid="fixture-second-pod-uid")
        pods.append(sibling)
    if case == "splash-first-shared-instance":
        splash = copy.deepcopy(pod)
        splash["metadata"].update(name=NAME + "-splash-abc123", uid="fixture-splash-uid")
        splash["metadata"]["labels"] = dict(labels, **{COMPONENT: "splash"})
        splash["metadata"]["ownerReferences"] = [dict(owner, kind="ReplicaSet",
                                                        name=NAME + "-splash", uid="fixture-splash-owner")]
        splash["spec"]["containers"] = [{"name": "splash"}]
        pods.insert(0, splash)
    if case == "wrong-owner-siblings":
        for field, value in (("kind", "ReplicaSet"), ("name", "other-controller"),
                             ("uid", "fixture-stale-uid")):
            sibling = copy.deepcopy(pod)
            sibling["metadata"]["name"] = "wrong-" + field
            sibling["metadata"]["ownerReferences"][0][field] = value
            pods.insert(0, sibling)
    return controller, pods


def fixture(tool):
    directory = Path(os.environ["WORKLOAD_FIXTURE_DIR"])
    case = os.environ["WORKLOAD_FIXTURE_CASE"]
    args = sys.argv[1:]

    def event(kind, **values):
        with (directory / "events.jsonl").open("a") as stream:
            stream.write(json.dumps(dict(event=kind, **values)) + "\n")

    def reject(message):
        event("unexpected", message=message)
        print("UNEXPECTED FIXTURE CALL: " + message, file=sys.stderr)
        raise SystemExit(97)

    event("call", tool=tool, args=args)
    state_path = directory / "state.json"
    state = json.loads(state_path.read_text())

    def save():
        state_path.write_text(json.dumps(state))

    def ready():
        if not state.get("rolled_out") or not state.get("pods_queried"):
            reject("network/exec before StatefulSet rollout and pod discovery")

    def http(argv, internal):
        method, data, headers, url, code = "GET", None, {}, None, False
        index = 0
        while index < len(argv):
            arg = argv[index]
            if arg == "-s":
                index += 1
                continue
            if arg in ("-X", "-H", "-d", "-o", "-w", "-m") and index + 1 < len(argv):
                value = argv[index + 1]
                if arg == "-X":
                    method = value
                elif arg == "-H":
                    key, value = value.split(":", 1)
                    headers[key] = value.strip()
                elif arg == "-d":
                    data = json.loads(value)
                elif arg == "-w":
                    if value != "%{http_code}":
                        reject("unknown curl output format")
                    code = True
                elif (arg == "-o" and value != "/dev/null") or (arg == "-m" and value != "20"):
                    reject("unknown curl option value")
                index += 2
                continue
            if arg.startswith(("http://", "https://")) and url is None:
                url = arg
                index += 1
                continue
            reject("curl argument " + arg)
        parsed = urlsplit(url or "")
        expected = ("http", "localhost:8096") if internal else ("https", "dev.example.com")
        if (parsed.scheme, parsed.netloc) != expected:
            reject("curl escaped fixture host: " + str(url))
        path, token = parsed.path, headers.get("X-Emby-Token")
        response, status = {}, 200
        if not internal:
            if path != "/System/Info/Public" or method != "GET":
                reject("external curl " + str(url))
        elif path == "/Users/AuthenticateByName" and method == "POST":
            match = re.fullmatch(r"phantom-icar-([ab])-\d+", data["Username"])
            if not match or "fixture-user-" + match[1] not in state["users"]:
                reject("authentication for unknown user")
            response = {"AccessToken": "fixture-token-" + match[1]}
        else:
            if token not in (state.get("key_token"), "fixture-token-a", "fixture-token-b") or not token:
                reject("missing/unknown API token")
            if path == "/Users" and method == "GET":
                response = []
            elif path == "/Users/New" and method == "POST":
                match = re.fullmatch(r"phantom-icar-([ab])-\d+", data["Name"])
                if not match:
                    reject("unscoped user creation")
                uid = "fixture-user-" + match[1]
                state["users"].append(uid)
                event("user-created", uid=uid)
                response = {"Id": uid}
            elif re.fullmatch(r"/Users/fixture-user-[ab]/Password", path) and method == "POST":
                if data["Id"] not in state["users"]:
                    reject("password set on missing user")
            elif re.fullmatch(r"/Users/fixture-user-[ab]", path) and method == "DELETE":
                uid = path.rsplit("/", 1)[1]
                if uid not in state["users"]:
                    reject("duplicate or foreign user cleanup")
                state["users"].remove(uid)
                state["hidden"] = False
                event("user-deleted", uid=uid)
            elif path == "/Channels" and method == "GET":
                response = {"Items": [{"Name": "Phantom Movies", "Id": "movies"},
                                      {"Name": "Phantom Shows", "Id": "shows"}]}
            elif path == "/Channels/movies/Items" and method == "GET" and parsed.query in (
                    "Limit=1&Fields=ProviderIds", "Limit=200&Fields=ProviderIds"):
                visible = not (token == "fixture-token-a" and state["hidden"])
                response = {"Items": [{"Id": "movie-item", "ProviderIds": {"Tmdb": "99000001"}}]
                            if visible else []}
                event("movie-browse", token=token, visible=visible)
            elif path == "/Plugins/PhantomLibrary/User/Hidden/movie/99000001" and method in ("POST", "DELETE"):
                if token != "fixture-token-a":
                    reject("hide/unhide must use user A token")
                state["hidden"] = method == "POST"
                event("hide" if state["hidden"] else "unhide")
                status = 204
            elif path == "/Items/movie-item/PlaybackInfo" and method == "POST":
                event("playback")
                if case == "late-exec-failure":
                    print("fixture: playback transport exited 42", file=sys.stderr)
                    raise SystemExit(42)
                status = 503 if case == "late-playback-failure" else 200
            elif path == "/Channels/shows/Items" and method == "GET" and parsed.query == "Limit=1":
                response = {"Items": [{"Id": "series-item"}]}
            elif path == "/Channels/shows/Items" and method == "GET" and parsed.query == "Limit=200&FolderId=series-item":
                event("tv-drill")
                response = {"TotalRecordCount": 0 if case == "late-tv-failure" else 2}
            else:
                reject("unhandled HTTP request " + method + " " + str(url))
        save()
        print(status if code else json.dumps(response))

    if tool == "kubectl":
        prefix = ["--context", "fixture-context", "-n", "test-media"]
        if args[:4] != prefix:
            reject("kubectl context/namespace " + repr(args))
        args = args[4:]
        controller, pods = objects(case)
        if args[:2] == ["get", "ingress"] and args[2] in ("phantom-library-blue", "phantom-library-green") and args[3:] == [
                "-o", 'jsonpath={range .spec.rules[*]}{.host}{"\\n"}{end}']:
            print("dev.example.com" if args[2] == NAME else "prod.example.com")
            if case == "ingress-active" and args[2] == NAME:
                print("prod.example.com")
        elif args == ["get", "statefulset", NAME, "-o", "json"]:
            event("controller")
            if case in ("missing-controller", "controller-query-error"):
                print("Error from server (NotFound): statefulsets.apps not found" if case == "missing-controller"
                      else "Error from server (Forbidden): fixture controller query denied", file=sys.stderr)
                raise SystemExit(1 if case == "missing-controller" else 23)
            print(json.dumps(controller))
        elif args == ["rollout", "status", "deploy/" + NAME, "--timeout=60s"]:
            event("deployment-notfound")
            print('Error from server (NotFound): deployments.apps "' + NAME + '" not found', file=sys.stderr)
            raise SystemExit(1)
        elif args == ["rollout", "status", "statefulset/" + NAME, "--timeout=60s"]:
            event("rollout")
            if case == "rollout-failure":
                print("error: timed out waiting for StatefulSet rollout", file=sys.stderr)
                raise SystemExit(19)
            state["rolled_out"] = True
            save()
            print("statefulset rolling update complete")
        elif args == ["get", "pods", "-l", INSTANCE + "=" + NAME,
                      "-o", "jsonpath={.items[0].metadata.name}"]:
            # The baseline must reach its real Deployment NotFound regression,
            # not fail because its old pod-selection command is unsupported.
            event("legacy-pods")
            print(next((p["metadata"]["name"] for p in pods
                        if p["metadata"]["labels"][INSTANCE] == NAME), ""), end="")
        elif args == ["get", "pods", "-o", "name"]:
            event("legacy-pods")
            print("\n".join("pod/" + p["metadata"]["name"] for p in pods))
        elif args in (["get", "pods", "-l", COMPONENT + "=jellyfin", "-o", "json"],
                      ["get", "pods", "-o", "json", "-l", COMPONENT + "=jellyfin"]):
            event("pods")
            if case == "pod-query-error":
                print("Error from server (Forbidden): fixture pod query denied", file=sys.stderr)
                raise SystemExit(24)
            state["pods_queried"] = True
            save()
            print(json.dumps({"apiVersion": "v1", "kind": "List", "items": [
                p for p in pods if p["metadata"]["labels"][COMPONENT] == "jellyfin"]}))
        elif args[:1] == ["exec"]:
            ready()
            if args[1:5] != [POD, "-c", "jellyfin", "--"]:
                reject("exec selected wrong pod/container " + repr(args))
            event("exec", pod=args[1])
            command = args[5:]
            if command == ["sh", "-c", 'echo "$GOSTREAM_MOUNT_PATH"']:
                print("/fixture/mount")
            elif command == ["mountpoint", "/fixture/mount"]:
                print("/fixture/mount is a mountpoint")
            elif command[:1] == ["curl"]:
                http(command[1:], True)
            elif len(command) == 3 and command[:2] == ["sh", "-c"]:
                sql = command[2]
                if sql == ('PGPASSWORD="$PHANTOM_POSTGRES_PASSWORD" psql -h "$PHANTOM_POSTGRES_HOST" '
                           '-U "$PHANTOM_POSTGRES_USER" -d "$PHANTOM_POSTGRES_DB" -tAc '
                           '"select to_regclass(\'public.user_hidden_items\'), to_regclass(\'public.user_prefs\')"'):
                    print("user_hidden_items|user_prefs")
                else:
                    prefix = 'PGPASSWORD=$POSTGRES_PASSWORD psql -h $POSTGRES_HOST -U $POSTGRES_USER -d $POSTGRES_DB -c "'
                    if not sql.startswith(prefix) or not sql.endswith('"'):
                        reject("unknown exec shell command " + sql)
                    statement = sql[len(prefix):-1].replace('\\"', '"')
                    mint = re.fullmatch(r'INSERT INTO "ApiKeys" \("DateCreated","DateLastActivity","Name","AccessToken"\) VALUES \(now\(\), now\(\), \'(phantom-icar-\d+)\', \'([0-9a-f]{32})\'\)', statement)
                    if mint:
                        if state.get("key_token"):
                            reject("duplicate key mint")
                        state.update(key_name=mint[1], key_token=mint[2])
                        event("key-created")
                    elif statement == 'DELETE FROM "ApiKeys" WHERE "Name"=\'' + state.get("key_name", "") + "'":
                        if not state.get("key_token"):
                            reject("cleanup without minted key")
                        state["key_token"] = None
                        event("key-deleted")
                    else:
                        reject("unexpected SQL " + statement)
                    save()
            else:
                reject("unknown exec " + repr(command))
        else:
            reject("kubectl " + repr(args))
    elif tool == "curl":
        ready()
        event("network")
        http(args, False)
    elif tool == "openssl":
        ready()
        event("network")
        if args == ["s_client", "-connect", "dev.example.com:443", "-servername", "dev.example.com"]:
            sys.stdin.read()
            print("fixture-certificate")
        elif args == ["x509", "-noout", "-ext", "subjectAltName"]:
            sys.stdin.read()
            print("X509v3 Subject Alternative Name:\n    DNS:dev.example.com")
        else:
            reject("openssl " + repr(args))


def run():
    selected = sys.argv[1:] or CASES
    unknown = set(selected) - set(CASES)
    if unknown:
        raise SystemExit("Unknown cases: " + ", ".join(sorted(unknown)))
    root = Path(__file__).resolve().parents[2]
    failures = 0
    with tempfile.TemporaryDirectory(prefix="acceptance-workload-") as temp:
        for case in selected:
            directory = Path(temp) / case
            directory.mkdir()
            (directory / "state.json").write_text(json.dumps({"users": [], "hidden": False}))
            for tool in ("kubectl", "curl", "openssl"):
                (directory / tool).symlink_to(Path(__file__).resolve())
            env = {k: v for k, v in os.environ.items()
                   if not k.startswith(("PHANTOM_INCLUSTER_", "PHANTOM_CI_"))}
            env.update(PATH=str(directory) + os.pathsep + os.environ["PATH"],
                       PHANTOM_REPO_ROOT=str(root), PHANTOM_CI_DRYRUN="0",
                       PHANTOM_INCLUSTER_DEV_HOST="prod.example.com" if case == "prod-equality" else "dev.example.com",
                       PHANTOM_INCLUSTER_PROD_HOST="prod.example.com",
                       PHANTOM_INCLUSTER_NAMESPACE="test-media",
                       PHANTOM_INCLUSTER_KUBE_CONTEXT="fixture-context",
                       WORKLOAD_FIXTURE_DIR=str(directory), WORKLOAD_FIXTURE_CASE=case)
            completed = subprocess.run(["bash", str(root / "tools/ci/in-cluster-acceptance-run.sh")],
                                       env=env, text=True, stdout=subprocess.PIPE,
                                       stderr=subprocess.STDOUT, timeout=30)
            logfile = directory / "events.jsonl"
            events = [json.loads(line) for line in logfile.read_text().splitlines()] if logfile.exists() else []
            names = [e["event"] for e in events]
            state = json.loads((directory / "state.json").read_text())
            output = completed.stdout
            try:
                assert "unexpected" not in names, "fixture received unknown/unsafe call"
                assert "DRYRUN" not in output, "must execute non-dry-run path"
                if case in SUCCESS_CASES:
                    assert completed.returncode == 0, f"expected success, exit={completed.returncode}"
                    for marker in ("RESULT: rollout:", "RESULT: tls:", "RESULT: fuse:",
                                   "RESULT: postgres:", "RESULT: admin-api:", "RESULT: movie+per-user:",
                                   "RESULT: scenario-35-parity:", "RESULT: tv-drill:", "rig PASSED"):
                        assert marker in output, "missing downstream assertion: " + marker
                else:
                    assert completed.returncode != 0, "unsafe fixture was accepted"
                    assert "rig PASSED" not in output, "failure reported success"
                if case == "prod-equality":
                    assert not events, "prod equality must refuse before any tools"
                    assert "equals PHANTOM_INCLUSTER_PROD_HOST" in output
                elif case == "ingress-active":
                    assert "ALSO carries the apex/prod host" in output
                    assert all(e["event"] == "call" and e["args"][4:6] == ["get", "ingress"] for e in events)
                else:
                    assert names.count("controller") == 1, "expected one StatefulSet controller query"
                    assert "deployment-notfound" not in names, "must not query nonexistent Deployment"
                    assert "legacy-pods" not in names, "must not use first instance-label pod selection"
                    if case in CONTROLLER_CASES:
                        assert "rollout" not in names and "pods" not in names, "invalid controller must fail before rollout/pods"
                    else:
                        assert names.count("rollout") == 1, "expected one StatefulSet rollout"
                        assert names.index("controller") < names.index("rollout"), "validate controller before rollout"
                        if case == "rollout-failure":
                            assert "pods" not in names, "failed rollout must precede pod discovery"
                        else:
                            assert names.count("pods") == 1, "expected one component-labeled pod query"
                            assert names.index("rollout") < names.index("pods"), "rollout must precede pod discovery"
                if case not in SUCCESS_CASES + LATE_CASES:
                    assert not any(n in names for n in ("exec", "network", "key-created", "user-created")), "unsafe preflight reached exec/network/mutation"
                    assert not state["users"] and not state.get("key_token")
                else:
                    for index, event in enumerate(events):
                        if event["event"] in ("exec", "network", "key-created", "user-created"):
                            assert names.index("pods") < index, "I/O or mutation before ready pod selection"
                        if event["event"] == "exec":
                            expected_pod = "phantom-library-blue-0" if case == "inactive-blue" else POD
                            assert event["pod"] == expected_pod, "exec must target the StatefulSet-owned Jellyfin pod"
                    assert names.count("key-created") == names.count("key-deleted") == 1, "minted key must be cleaned once"
                    assert names.count("user-created") == names.count("user-deleted") == 2, "both users must be cleaned once"
                    assert names.index("key-created") < names.index("user-created") < names.index("hide")
                    assert names.index("hide") < names.index("unhide") < names.index("playback")
                    assert names.index("playback") < names.index("user-deleted") < names.index("key-deleted")
                    assert not state["users"] and not state.get("key_token") and not state["hidden"], "cleanup leaked rig state"
                    browses = [(e["token"], e["visible"]) for e in events if e["event"] == "movie-browse"
                               and e["token"] in ("fixture-token-a", "fixture-token-b")]
                    assert browses == [("fixture-token-a", True), ("fixture-token-b", True),
                                       ("fixture-token-a", False), ("fixture-token-b", True),
                                       ("fixture-token-a", True)], "missing movie isolation/restoration assertions"
                    if case == "late-exec-failure":
                        assert completed.returncode == 42, "EXIT cleanup must preserve transport exit 42"
                        assert "fixture: playback transport exited 42" in output
                    elif case == "late-playback-failure":
                        assert completed.returncode == 1 and "returned 503, expected 200" in output
                    elif case == "late-tv-failure":
                        assert completed.returncode == 1 and "expanded to 0 season/episode children" in output
                        assert "RESULT: scenario-35-parity:" in output
                    else:
                        assert names.index("playback") < names.index("tv-drill") < names.index("user-deleted")
                print("PASS: " + case)
            except AssertionError as error:
                failures += 1
                print(f"FAIL: {case}: {error}")
                if failures == 1:
                    print("--- real entrypoint output ---\n" + output.rstrip())
                    print("--- fixture argv log ---")
                    for event in events:
                        print(json.dumps(event))
    print(f"{len(selected) - failures} passed; {failures} failed")
    return 1 if failures else 0


if __name__ == "__main__":
    executable = Path(sys.argv[0]).name
    if executable in ("kubectl", "curl", "openssl"):
        fixture(executable)
    else:
        sys.exit(run())
