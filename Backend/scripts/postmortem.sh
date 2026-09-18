#!/usr/bin/env bash
# Why did a pod die? Container logs live in the journal (compose.yaml sets the journald driver), so
# they outlive the container `docker compose up -d` replaced -- which is the evidence a plain
# `docker logs` has already lost by the time anyone asks. Kernel OOM kills are printed beside them.
#
#   scripts/postmortem.sh gaida-bot          # last 2 hours
#   scripts/postmortem.sh gaida-bot '-30min' # any journalctl --since value

set -euo pipefail

service=${1:-}
since=${2:--2h}

if [[ -z $service ]]; then
    echo "usage: $(basename "$0") <service> [since]" >&2
    exit 64
fi

# `name: gaida` in compose.yaml, one replica each: the container name is the service's, prefixed.
container="gaida-$service-1"

echo "── $container, since $since ──"
journalctl CONTAINER_NAME="$container" --since "$since" --no-pager || true

echo
echo "── kernel OOM kills, since $since ──"
# The kernel names the cgroup, and the cgroup carries the container id, so the id is the only link
# back to a service name -- print both and let the reader match them.
journalctl -k --since "$since" --no-pager --grep 'oom-kill|Killed process|out of memory' || echo "none"

echo
echo "── what docker remembers of the current container ──"
docker inspect "$container" \
    --format 'exit {{.State.ExitCode}}, OOMKilled {{.State.OOMKilled}}, restarts {{.RestartCount}}, started {{.State.StartedAt}}, limit {{.HostConfig.Memory}} bytes' \
    2>/dev/null || echo "no container called $container right now"
