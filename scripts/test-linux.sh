#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
docker build -f scripts/test-linux.Dockerfile -t mxc-sandbox-demo-test:local .
# Disposable test container only: non-root, no capabilities, no host mounts.
# Nested user/mount/network namespaces need unconfined seccomp and an
# unmasked proc filesystem; the test image contains only our trusted checks.
docker run --rm --cap-drop=ALL --security-opt seccomp=unconfined \
    --security-opt systempaths=unconfined --device /dev/net/tun --user 1000:1000 --read-only \
    --tmpfs /tmp:rw,nosuid,size=128m --pids-limit=128 --memory=512m \
    mxc-sandbox-demo-test:local
docker run --rm --cap-drop=ALL --security-opt seccomp=unconfined \
    --security-opt systempaths=unconfined --device /dev/net/tun --user 1000:1000 --read-only \
    --tmpfs /tmp:rw,nosuid,size=128m --pids-limit=128 --memory=512m \
    --entrypoint dotnet mxc-sandbox-demo-test:local \
    tests/MxcSandbox.ConsoleChecks/bin/Release/net10.0/MxcSandbox.ConsoleChecks.dll
