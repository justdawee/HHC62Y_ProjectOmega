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

# Copy npm assets so the in-build NpmInstall target uses the same lockfile
COPY package.json package-lock.json ./
COPY tailwind.config.js ./
COPY styles ./styles

# Bring in the prebuilt Tailwind CSS so MSBuild does not re-run it
COPY --from=css-build /src/wwwroot ./wwwroot

# Source
COPY *.fs Main.html appsettings.json wsconfig.json esbuild.config.mjs vite.config.js ./

# Publish
RUN dotnet publish SmartPantry.fsproj -c Release -o /app/publish /p:UseAppHost=false

# ============================================================
# Stage 3 — Runtime
# ============================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Persistent SQLite directory (mounted as volume in compose)
RUN mkdir -p /data && chown -R 1000:1000 /data

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DB_PATH=/data/smartpantry.db

EXPOSE 8080

COPY --from=dotnet-build /app/publish ./

# Drop to non-root for runtime
USER 1000:1000

ENTRYPOINT ["dotnet", "SmartPantry.dll"]
