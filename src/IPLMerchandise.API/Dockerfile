# Build context: repository root.  docker build -f src/IPLMerchandise.API/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/IPLMerchandise.Domain/*.csproj src/IPLMerchandise.Domain/
COPY src/IPLMerchandise.Contracts/*.csproj src/IPLMerchandise.Contracts/
COPY src/IPLMerchandise.Application/*.csproj src/IPLMerchandise.Application/
COPY src/IPLMerchandise.Infrastructure/*.csproj src/IPLMerchandise.Infrastructure/
COPY src/IPLMerchandise.API/*.csproj src/IPLMerchandise.API/
RUN dotnet restore src/IPLMerchandise.API/IPLMerchandise.API.csproj
COPY src/ src/
RUN dotnet publish src/IPLMerchandise.API/IPLMerchandise.API.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# The base image ships a non-root "app" user; never run the API as root.
USER $APP_UID
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=5 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health/live HTTP/1.0\r\n\r\n" >&3 && grep -q "200 OK" <&3' || exit 1
ENTRYPOINT ["dotnet", "IPLMerchandise.API.dll"]
