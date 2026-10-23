# syntax=docker/dockerfile:1

# The API image and the job that migrates the database, built from the same sources. Build from this folder:
#   docker build --target api -t chatnet-api .
#   docker build --target migrate -t chatnet-migrate .

# ##### Build #####
# The SDK is only here to compile; it does not ship. Restoring from the project file alone comes first, so a change to
# the code does not download every package again.
FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
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
RUN dotnet tool install dotnet-ef --tool-path /tools --version 10.0.8
# The tool builds without restoring, so the packages for the bundle's runtime have to be there already.
RUN dotnet restore ChatNET.API/ChatNET.API.csproj --runtime linux-x64
RUN /tools/dotnet-ef migrations bundle --project ChatNET.API/ChatNET.API.csproj --configuration Release \
    --self-contained --runtime linux-x64 --output /out/efbundle --force

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
