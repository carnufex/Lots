FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Lots.Mcp.Toolpack/Lots.Mcp.Toolpack.csproj src/Lots.Mcp.Toolpack/
RUN dotnet restore src/Lots.Mcp.Toolpack/Lots.Mcp.Toolpack.csproj
COPY src/Shared/ src/Shared/
COPY src/Lots.Mcp.Toolpack/ src/Lots.Mcp.Toolpack/
RUN dotnet publish src/Lots.Mcp.Toolpack/Lots.Mcp.Toolpack.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# OpenShift-compatible: arbitrary UID with group 0; no privileged port.
RUN chgrp -R 0 /app && chmod -R g=u /app
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
USER 10001:0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Lots.Mcp.Toolpack.dll"]
