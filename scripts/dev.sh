#!/usr/bin/env bash
# Starts the DataPitcher API and the web client for local development.
# Usage: ./scripts/dev.sh            (Ctrl+C stops both)
#   API_PORT=5080 WEB_PORT=5173 to override ports.
#   Authentication__Development__SigningKey to override the signing key (the sign-in page is prefilled with the default).
set -euo pipefail

# Resolve the repository root from the script location and work from there, whatever the caller's cwd is.
SCRIPT_PATH="${BASH_SOURCE[0]:-$0}"
ROOT="$(cd "$(dirname "$SCRIPT_PATH")/.." && pwd)"
cd "$ROOT"
API_PORT="${API_PORT:-5080}"
WEB_PORT="${WEB_PORT:-5173}"
export Authentication__Development__SigningKey="${Authentication__Development__SigningKey:-local-development-signing-key-0123456789abcdef}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export ASPNETCORE_URLS="http://localhost:${API_PORT}"
export DATAPITCHER_API_URL="http://localhost:${API_PORT}"

log() { printf '\033[1;36m[dev]\033[0m %s\n' "$*"; }

# Stop anything left over from a previous run.
pkill -f "DataPitcher.Api" 2>/dev/null || true
pkill -f "vite" 2>/dev/null || true
sleep 0.5

mkdir -p "$ROOT/src/DataPitcher.Api/data"
if [ ! -d "$ROOT/web/node_modules" ]; then
  log "Installing web dependencies"
  npm --prefix "$ROOT/web" install
fi

log "Building API"
dotnet build "$ROOT/src/DataPitcher.Api" -nologo -v q

log "Starting API on http://localhost:${API_PORT}"
dotnet run --project "$ROOT/src/DataPitcher.Api" --no-build &
API_PID=$!
trap 'log "Stopping"; kill $API_PID 2>/dev/null || true; pkill -f "DataPitcher.Api" 2>/dev/null || true' EXIT INT TERM

for _ in $(seq 1 60); do
  if curl -fs "http://localhost:${API_PORT}/health/live" >/dev/null 2>&1; then break; fi
  if ! kill -0 "$API_PID" 2>/dev/null; then log "API exited early"; exit 1; fi
  sleep 0.5
done

log "Starting web client on http://localhost:${WEB_PORT} (signing key is prefilled on the sign-in page)"
cd "$ROOT/web"
npx vite --port "$WEB_PORT" --strictPort --open
