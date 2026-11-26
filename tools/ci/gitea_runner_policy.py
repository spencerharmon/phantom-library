"""Narrow Gitea scheduling policy shared by workflow validation and regressions.

Static labels are the shared runners. Dynamic selectors are explicit per-job
exceptions, not an expression language: an operator may use owned disposable
capacity for playback dispatch or configure the swarm image runner. Both retain
k8s when the override is unset; neither changes the mandatory test commands.
"""

SUPPORTED = frozenset({"ubuntu-latest", "k8s"})
PLAYBACK_JOB = ("postgres-playback-parity.yaml", "playback")
PLAYBACK_SELECTOR = "${{ inputs.runner_label || 'k8s' }}"
SWARM_JOB = ("swarm-health-image.yml", "build-test-publish")
SWARM_SELECTOR = "${{ vars.SWARM_HEALTH_RUNNER_LABEL || 'k8s' }}"


def supported_runner(workflow, filename, job_name, selector):
    labels = [selector] if isinstance(selector, str) else selector
    if not isinstance(labels, list) or not labels or not all(isinstance(x, str) for x in labels):
        return False
    if all(label in SUPPORTED for label in labels):
        return True
    # A dynamic override selects ONE runner, never a mixed set of constraints.
    if len(labels) != 1:
        return False
    context = (filename, job_name)
    if context == SWARM_JOB and labels[0] == SWARM_SELECTOR:
        return True
    if context != PLAYBACK_JOB or labels[0] != PLAYBACK_SELECTOR:
        return False
    # PyYAML's YAML 1.1 loader reads unquoted `on` as True. Quoted YAML and
    # callers using a YAML 1.2 loader retain the string key.
    events = workflow.get("on", workflow.get(True))
    if not isinstance(events, dict):
        return False
    dispatch = events.get("workflow_dispatch")
    if not isinstance(dispatch, dict):
        return False
    inputs = dispatch.get("inputs")
    if not isinstance(inputs, dict):
        return False
    runner = inputs.get("runner_label")
    return (isinstance(runner, dict) and runner.get("type") == "string"
            and runner.get("required") is False and runner.get("default") == "k8s")
