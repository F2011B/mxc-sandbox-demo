FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update && apt-get install -y --no-install-recommends bubblewrap slirp4netns iptables iproute2 curl ca-certificates util-linux && rm -rf /var/lib/apt/lists/*
WORKDIR /demo
COPY . .
RUN dotnet restore tests/MxcSandbox.Checks --locked-mode && dotnet restore tests/MxcSandbox.ConsoleChecks --locked-mode && dotnet build tests/MxcSandbox.Checks --no-restore -c Release && dotnet build tests/MxcSandbox.ConsoleChecks --no-restore -c Release
ENTRYPOINT ["dotnet", "tests/MxcSandbox.Checks/bin/Release/net10.0/MxcSandbox.Checks.dll"]
