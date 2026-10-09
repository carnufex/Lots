FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Lots.slnx ./
COPY src/Lots.Shell/Lots.Shell.csproj src/Lots.Shell/
COPY tests/Lots.Shell.Tests/Lots.Shell.Tests.csproj tests/Lots.Shell.Tests/
RUN dotnet restore src/Lots.Shell/Lots.Shell.csproj
COPY src/Lots.Shell/ src/Lots.Shell/
RUN dotnet publish src/Lots.Shell/Lots.Shell.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# OpenShift-compatible: runs as an arbitrary UID with group 0; no privileged port.
RUN chgrp -R 0 /app && chmod -R g=u /app
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
USER 10001:0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Lots.Shell.dll"]
