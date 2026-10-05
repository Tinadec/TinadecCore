# TinadecCore API container image (A4 slice).
# Build from the standalone Core repository root:
#   docker build -f Api/Dockerfile -t tinadec-core-api:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . ./TinadecCore/
RUN dotnet restore TinadecCore/Api/TinadecCore.Api.csproj
RUN dotnet publish TinadecCore/Api/TinadecCore.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# SQLite database and Core-owned file stores (sessions/tasks/events/artifacts/
# vectors) live under one mountable root.
ENV ASPNETCORE_URLS=http://0.0.0.0:48731 \
    TinadecPersistence__Sqlite__DatabasePath=/data/tinadec.db \
    TinadecPersistence__DataRoot=/data
VOLUME /data
EXPOSE 48731
ENTRYPOINT ["dotnet", "TinadecCore.Api.dll"]
