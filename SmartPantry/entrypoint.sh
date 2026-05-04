#!/bin/sh
# SmartPantry container entrypoint.
# Runs briefly as root to fix ownership on the mounted /data volume
# (bind mounts inherit the host user and the app user can't open the
# SQLite file otherwise), then drops privileges via gosu.
set -e

mkdir -p /data
chown -R app:app /data

cd /app
exec gosu app dotnet SmartPantry.dll
