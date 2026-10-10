FROM node:24-alpine@sha256:ebfe2f90462722a7a4de65e91990e97fe0d401c70e0e762c5b53302f905ec1c1 AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
COPY Lots.slnx ./
COPY Directory.Build.props ./
COPY src/Lots.Shell/Lots.Shell.csproj src/Lots.Shell/packages.lock.json src/Lots.Shell/
COPY tests/Lots.Shell.Tests/Lots.Shell.Tests.csproj tests/Lots.Shell.Tests/
RUN dotnet restore src/Lots.Shell/Lots.Shell.csproj --locked-mode
COPY src/Shared/ src/Shared/
COPY src/Lots.Shell/ src/Lots.Shell/
RUN dotnet publish src/Lots.Shell/Lots.Shell.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime
WORKDIR /app
COPY --from=build /app .
COPY profiles/ /app/profiles/
COPY --from=web /web/dist/ /app/wwwroot/
# OpenShift-compatible: runs as an arbitrary UID with group 0; no privileged port.
# /data: conversation audio and keys (a volume); group 0 writable so any assigned UID can write it.
RUN mkdir -p /data && chgrp -R 0 /app /data && chmod -R g=u /app /data
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
USER 10001:0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Lots.Shell.dll"]
