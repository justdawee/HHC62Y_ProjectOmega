# syntax=docker/dockerfile:1.7

# ============================================================
# Stage 1 — Tailwind CSS build (Node)
# ============================================================
FROM node:20-alpine AS css-build
WORKDIR /src

COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY tailwind.config.js ./
COPY styles ./styles
COPY Main.html Client.fs Site.fs ./
RUN npx tailwindcss -i ./styles/input.css -o ./wwwroot/css/output.css --minify

# ============================================================
# Stage 2 — .NET publish (SDK + Node, because esbuild bundle runs in publish)
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src

# Install Node so the esbuild + npm install MSBuild targets can run during publish
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

# Restore first for layer caching
COPY SmartPantry.fsproj ./
RUN dotnet restore SmartPantry.fsproj

# Copy npm assets and install up-front so the MSBuild ESBuildBundle target
# (which runs `node esbuild.config.mjs` after WebSharperCompile) finds esbuild
# already on disk. Doing it here instead of inside the publish step avoids
# the Inputs/Outputs incremental check on the NpmInstall target tripping.
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY tailwind.config.js ./
COPY styles ./styles

# Bring in the prebuilt Tailwind CSS so MSBuild does not re-run it
COPY --from=css-build /src/wwwroot ./wwwroot

# Hand-authored static assets that live in source (favicon, etc.) — Stage 1
# only generates css/output.css, so without this the image has no favicon and
# every page load 404s on /favicon.svg.
COPY wwwroot/favicon.svg ./wwwroot/

# Source
COPY *.fs Main.html appsettings.json wsconfig.json esbuild.config.mjs vite.config.js ./

# Publish
RUN dotnet publish SmartPantry.fsproj -c Release -o /app/publish /p:UseAppHost=false

# WebSharper auto-injects <link href="/Scripts/WebSharper/all.css"> alongside
# all.js even though the Release bundler only emits all.js. Without this stub
# the browser gets a 404 on every page load. The stub is intentionally empty
# because all real styles ship through Tailwind's output.css.
RUN printf '/* SmartPantry uses Tailwind output.css instead. This empty stub silences the\n   404 that WebSharper emits when a Release bundle injects a default stylesheet. */\n' \
    > /app/publish/wwwroot/Scripts/WebSharper/all.css

# Fail fast if either bundle artifact is missing — prevents shipping a broken image.
RUN test -s /app/publish/wwwroot/Scripts/WebSharper/all.js \
    && test -f /app/publish/wwwroot/Scripts/WebSharper/all.css \
    || (echo "FATAL: WebSharper bundle missing from publish output." \
        && ls -laR /app/publish/wwwroot/Scripts 2>&1 || true; exit 1)

# ============================================================
# Stage 3 — Runtime
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# gosu lets the entrypoint drop root privileges cleanly after fixing
# ownership of the bind-mounted /data volume.
# We reuse the 'app' user the dotnet/aspnet base image already ships
# with (creating it on the fly if a future base image drops it). Hard-
# coding a UID like 1000 conflicts with the base image's own user.
RUN apt-get update \
    && apt-get install -y --no-install-recommends gosu \
    && rm -rf /var/lib/apt/lists/* \
    && (id -u app >/dev/null 2>&1 || useradd --system --create-home app) \
    && mkdir -p /data \
    && chown -R app:app /data /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DB_PATH=/data/smartpantry.db

EXPOSE 8080

COPY --from=dotnet-build /app/publish ./
COPY --chmod=0755 entrypoint.sh /entrypoint.sh

# Container starts as root so the entrypoint can chown the mounted
# volume; the entrypoint then `gosu appuser` switches to the app user
# before launching dotnet.
ENTRYPOINT ["/entrypoint.sh"]
