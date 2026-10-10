FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Lots.Mcp.Toolpack/Lots.Mcp.Toolpack.csproj src/Lots.Mcp.Toolpack/packages.lock.json src/Lots.Mcp.Toolpack/
RUN dotnet restore src/Lots.Mcp.Toolpack/Lots.Mcp.Toolpack.csproj --locked-mode
COPY src/Shared/ src/Shared/
COPY src/Lots.Mcp.Toolpack/ src/Lots.Mcp.Toolpack/
RUN dotnet publish src/Lots.Mcp.Toolpack/Lots.Mcp.Toolpack.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime
WORKDIR /app
COPY --from=build /app .
# OpenShift-compatible: arbitrary UID with group 0; no privileged port.
RUN chgrp -R 0 /app && chmod -R g=u /app
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
USER 10001:0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Lots.Mcp.Toolpack.dll"]
