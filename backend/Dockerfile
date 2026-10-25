# syntax=docker/dockerfile:1

# The API image and the job that migrates the database, built from the same sources. Build from this folder:
#   docker build --target api -t chatnet-api .
#   docker build --target migrate -t chatnet-migrate .
# Each image is for the CPU of the machine that builds it, x86-64 or ARM64. Add --platform linux/arm64 or
# --platform linux/amd64 to build for the other one. Nothing is emulated: every command that runs during a build runs
# on the machine doing the build, and only files are copied into the images.

# ##### Build #####
# The SDK is only here to compile; it does not ship. It runs on the machine doing the build, whichever CPU the image is
# for, because what it publishes is plain .NET assemblies that run on either. Restoring from the project file alone
# comes first, so a change to the code does not download every package again.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src

COPY ChatNET.API/ChatNET.API.csproj ChatNET.API/
RUN dotnet restore ChatNET.API/ChatNET.API.csproj

COPY ChatNET.API/ ChatNET.API/
RUN dotnet publish ChatNET.API/ChatNET.API.csproj --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

# ##### Migration bundle #####
# A single executable that applies every migration to the database named by ConnectionStrings__DefaultConnection.
# The API does not migrate itself when it starts: with several instances starting together they would race to change
# the schema. This runs once, and the instances start after it has finished.
FROM build AS bundle
# Set by the builder to the CPU the image is for: amd64 or arm64.
ARG TARGETARCH
RUN dotnet tool install dotnet-ef --tool-path /tools --version 10.0.8
# Unlike the published assemblies the bundle is machine code with its own runtime, so it has to be for the CPU the image
# is for, which is not always the CPU of the machine building it. Any other CPU stops the build; it is never given
# x86-64 by default.
RUN case "$TARGETARCH" in \
      amd64) echo linux-x64 ;; \
      arm64) echo linux-arm64 ;; \
      *) echo "unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac > /runtime-id
# The tool builds without restoring, so the packages for the bundle's runtime have to be there already.
RUN dotnet restore ChatNET.API/ChatNET.API.csproj --runtime "$(cat /runtime-id)"
# --target-runtime is the CPU the bundle is for. The tool itself keeps running on the machine doing the build, where it
# loads the project to read the migrations; --runtime would make it try to run the other CPU's build there.
RUN /tools/dotnet-ef migrations bundle --project ChatNET.API/ChatNET.API.csproj --configuration Release \
    --self-contained --target-runtime "$(cat /runtime-id)" --output /out/efbundle --force

# The bundle carries its own runtime, so the image under it needs only the operating system libraries.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.13 AS migrate
WORKDIR /app
COPY --from=bundle /out/efbundle .
USER $APP_UID
ENTRYPOINT ["./efbundle"]

# ##### API #####
# The runtime is pinned to 10.0.13 or later: earlier releases carry a SignalR denial of service fix this app needs
# (D-132). The container runs as a user without privileges and listens on 8080, behind the reverse proxy.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13 AS api
WORKDIR /app
COPY --from=build /app/publish .
# The base image already listens on port 8080 (ASPNETCORE_HTTP_PORTS). Setting ASPNETCORE_URLS as well makes the
# framework warn about the two overriding each other on every start.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080

# The image has no curl; bash can open the socket itself. The request asks for /health and succeeds on a 200.
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=5 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["dotnet", "ChatNET.API.dll"]
