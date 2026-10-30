# Build context: repository root.  docker build -f src/IPLMerchandise.Web/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/IPLMerchandise.Contracts/*.csproj src/IPLMerchandise.Contracts/
COPY src/IPLMerchandise.Web.Client/*.csproj src/IPLMerchandise.Web.Client/
COPY src/IPLMerchandise.Web/*.csproj src/IPLMerchandise.Web/
RUN dotnet restore src/IPLMerchandise.Web/IPLMerchandise.Web.csproj
COPY src/ src/
RUN dotnet publish src/IPLMerchandise.Web/IPLMerchandise.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=5 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health/live HTTP/1.0\r\n\r\n" >&3 && grep -q "200 OK" <&3' || exit 1
ENTRYPOINT ["dotnet", "IPLMerchandise.Web.dll"]
