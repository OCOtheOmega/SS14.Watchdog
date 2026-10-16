# Pinned SDK also supplies the compiler required by the Git update provider.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY SS14.Watchdog/SS14.Watchdog.csproj SS14.Watchdog/packages.lock.json SS14.Watchdog/
RUN dotnet restore SS14.Watchdog/SS14.Watchdog.csproj --locked-mode
COPY SS14.Watchdog/ SS14.Watchdog/
RUN dotnet publish SS14.Watchdog/SS14.Watchdog.csproj --configuration Release --runtime linux-x64 --self-contained false --no-restore --output /out

FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
RUN apt-get update && apt-get install --yes --no-install-recommends curl git python3 tzdata zstd \
    && rm -rf /var/lib/apt/lists/* \
    && install -d -o app -g app /data /data/instances /data/instances/main
COPY --from=build /out /app
ENV ASPNETCORE_URLS=http://0.0.0.0:5000 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_CLI_HOME=/data/.dotnet \
    NUGET_PACKAGES=/data/.nuget/packages
WORKDIR /data
USER app
EXPOSE 5000/tcp 1212/tcp 1212/udp
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent http://127.0.0.1:5000/watchdog/padlock || exit 1
ENTRYPOINT ["dotnet", "/app/SS14.Watchdog.dll"]
