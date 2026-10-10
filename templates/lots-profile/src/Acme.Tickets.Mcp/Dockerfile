# Build from the template's root: docker build -f src/Acme.Tickets.Mcp/Dockerfile -t acme-tickets-mcp .
# Base images are pinned by digest (supply chain): refresh them with `docker buildx imagetools inspect <image>` when you update.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
COPY src/Acme.Tickets.Mcp/Acme.Tickets.Mcp.csproj src/Acme.Tickets.Mcp/
RUN dotnet restore src/Acme.Tickets.Mcp/Acme.Tickets.Mcp.csproj
COPY src/Acme.Tickets.Mcp/ src/Acme.Tickets.Mcp/
RUN dotnet publish src/Acme.Tickets.Mcp/Acme.Tickets.Mcp.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime
WORKDIR /app
COPY --from=build /app .
# OpenShift-compatible: runs as an arbitrary UID with group 0, no privileged port, nothing written outside /tmp.
RUN chgrp -R 0 /app && chmod -R g=u /app
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
USER 10001:0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Acme.Tickets.Mcp.dll"]
