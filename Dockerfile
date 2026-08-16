FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
RUN mkdir -p /data \
    && apt-get update \
    && apt-get install -y --no-install-recommends curl libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Ptlk.SSO/Directory.Build.props", "src/Ptlk.SSO/Directory.Packages.props", "src/Ptlk.SSO/"]
COPY ["src/Ptlk.SSO/Ptlk.SSO.Core/Ptlk.SSO.Core.csproj", "src/Ptlk.SSO/Ptlk.SSO.Core/"]
COPY ["src/Ptlk.SSO/Ptlk.SSO.Client/Ptlk.SSO.Client.csproj", "src/Ptlk.SSO/Ptlk.SSO.Client/"]
COPY ["src/Ptlk.Web.Hosting/Ptlk.Web.Hosting.csproj", "src/Ptlk.Web.Hosting/"]
COPY ["src/Ptlk.SCADA.Interop/Ptlk.SCADA.Interop/Ptlk.SCADA.Interop.csproj", "src/Ptlk.SCADA.Interop/Ptlk.SCADA.Interop/"]
COPY ["src/Ptlk.AlarmLogger/Ptlk.AlarmLogger.csproj", "src/Ptlk.AlarmLogger/"]
RUN --mount=type=secret,id=ptlk_ca,required=false \
    if [ -s /run/secrets/ptlk_ca ]; then \
      cat /etc/ssl/certs/ca-certificates.crt /run/secrets/ptlk_ca > /tmp/ptlk-build-ca-bundle.crt \
      && SSL_CERT_FILE=/tmp/ptlk-build-ca-bundle.crt dotnet restore "src/Ptlk.AlarmLogger/Ptlk.AlarmLogger.csproj" \
      && rm -f /tmp/ptlk-build-ca-bundle.crt; \
    else dotnet restore "src/Ptlk.AlarmLogger/Ptlk.AlarmLogger.csproj"; fi
COPY ["src/Ptlk.SCADA.Interop/Ptlk.SCADA.Interop/", "src/Ptlk.SCADA.Interop/Ptlk.SCADA.Interop/"]
COPY ["src/Ptlk.SSO/Ptlk.SSO.Core/", "src/Ptlk.SSO/Ptlk.SSO.Core/"]
COPY ["src/Ptlk.SSO/Ptlk.SSO.Client/", "src/Ptlk.SSO/Ptlk.SSO.Client/"]
COPY ["src/Ptlk.Web.Hosting/", "src/Ptlk.Web.Hosting/"]
COPY ["src/Ptlk.AlarmLogger/", "src/Ptlk.AlarmLogger/"]
WORKDIR "/src/src/Ptlk.AlarmLogger"
RUN dotnet publish "Ptlk.AlarmLogger.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "/app/Ptlk.AlarmLogger.dll"]
