# syntax=docker/dockerfile:1

# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, against just the project file, so a code-only change doesn't
# re-download the package graph.
COPY src/ReleaseCalendar.Web/ReleaseCalendar.Web.csproj src/ReleaseCalendar.Web/
RUN dotnet restore src/ReleaseCalendar.Web/ReleaseCalendar.Web.csproj

COPY src/ src/
RUN dotnet publish src/ReleaseCalendar.Web \
      -c Release \
      -o /app \
      --no-restore \
      -p:DebugType=none \
      -p:GenerateDocumentationFile=false

# ---- run ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# The data directory is created owned by the non-root app user *before* anything
# mounts over it. Docker copies this ownership onto a fresh named volume, which is
# what stops SQLite failing with "unable to open database file" on first run.
RUN mkdir -p /data && chown -R 1654:1654 /data

ENV ASPNETCORE_URLS=http://+:8080 \
    Calendar__DatabasePath=/data/calendar.db \
    DOTNET_NOLOGO=1 \
    DOTNET_PrintTelemetryMessage=false

EXPOSE 8080

# 1654 is the `app` user baked into the .NET base images.
USER 1654

ENTRYPOINT ["dotnet", "ReleaseCalendar.Web.dll"]
