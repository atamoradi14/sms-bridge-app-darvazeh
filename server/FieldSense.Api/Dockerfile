# Prefer building from repo root:
#   docker build -f deploy/docker/Dockerfile.api -t fieldsense-api .
# This file exists for IDE "Dockerfile in project" convenience.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY FieldSense.Api.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /data \
 && chown -R app:app /data /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ConnectionStrings__Default="Data Source=/data/fieldsense.db"
EXPOSE 8080
COPY --from=build --chown=app:app /app/publish ./
USER app
ENTRYPOINT ["dotnet", "FieldSense.Api.dll"]
