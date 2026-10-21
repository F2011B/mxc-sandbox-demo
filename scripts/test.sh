#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
dotnet build MxcSandbox.slnx -c Release --disable-build-servers
dotnet run --project tests/MxcSandbox.Checks -c Release --no-build -- "$@"
if [ "${1-}" != "--unit-only" ]; then
    if [ "$(uname -s)" != "MINGW" ]; then
        dotnet run --project tests/MxcSandbox.ConsoleChecks -c Release --no-build
    fi
    dotnet run --project src/MxcSandbox.Desktop -c Release --no-build -- --smoke
fi
