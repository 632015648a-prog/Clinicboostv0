#!/usr/bin/env bash
# ClinicBoost — arranque local “todo en uno” (Supabase + API + Vite)
# Requisitos: Docker en marcha, Supabase CLI, .NET 10, Node 20+.
# Uso:
#   ./scripts/dev-play.sh
#   ./scripts/dev-play.sh --db-reset     # migraciones + seed (borra datos locales)
#   ./scripts/dev-play.sh --no-supabase  # Supabase ya levantado
#
# Ctrl+C detiene API y web (no hace `supabase stop`).

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

DB_RESET=0
NO_SUPABASE=0

for arg in "$@"; do
  case "$arg" in
    --db-reset)   DB_RESET=1 ;;
    --no-supabase) NO_SUPABASE=1 ;;
    -h|--help)
      echo "Uso: $0 [--db-reset] [--no-supabase]"
      exit 0
      ;;
    *)
      echo "Argumento desconocido: $arg (usa --help)"
      exit 1
      ;;
  esac
done

require_cmd() {
  local name="$1"
  shift
  if ! command -v "$name" &>/dev/null; then
    echo "No se encontró «$name» en PATH. $*"
    exit 1
  fi
}

supabase_args=()
if ! command -v supabase &>/dev/null; then
  if command -v npx &>/dev/null; then
    supabase_args=(npx --yes supabase)
  else
    echo "Instala Supabase CLI: npm i -g supabase   (o usa npx en PATH)"
    exit 1
  fi
else
  supabase_args=(supabase)
fi

if [[ "$NO_SUPABASE" -eq 0 ]]; then
  if ! docker info &>/dev/null; then
    echo "Docker no responde. Arranca el daemon (Docker Desktop / servicio) y reintenta."
    exit 1
  fi
  echo "── Supabase (docker) ──"
  "${supabase_args[@]}" start
  if [[ "$DB_RESET" -eq 1 ]]; then
    echo "── Base de datos: reset (migraciones + seed) ──"
    "${supabase_args[@]}" db reset
  else
    echo "── Base de datos: push de migraciones ──"
    "${supabase_args[@]}" db push
  fi
else
  echo "── Supabase omitido (--no-supabase) ──"
fi

if [[ ! -f apps/web/.env.local ]]; then
  if [[ -f apps/web/.env.local.example ]]; then
    echo "Creando apps/web/.env.local desde .env.local.example"
    cp apps/web/.env.local.example apps/web/.env.local
  else
    echo "Falta apps/web/.env.local (y no hay .env.local.example). Créala manualmente."
    exit 1
  fi
fi

require_cmd dotnet "Instala .NET SDK 10: https://dot.net"
require_cmd node "Instala Node.js 20+"

cleanup() {
  if [[ -n "${API_PID:-}" ]] && kill -0 "$API_PID" 2>/dev/null; then
    kill "$API_PID" 2>/dev/null || true
  fi
  if [[ -n "${WEB_PID:-}" ]] && kill -0 "$WEB_PID" 2>/dev/null; then
    kill "$WEB_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

echo "── API (http://localhost:5011) ──"
( cd "$ROOT/apps/api" && dotnet run --project src/ClinicBoost.Api ) &
API_PID=$!

echo "── Web (Vite, http://localhost:5173) ──"
( cd "$ROOT/apps/web" && npm run dev ) &
WEB_PID=$!

echo ""
echo "Listo. Abre en el navegador:"
echo "  • App:   http://localhost:5173"
echo "  • API:   http://localhost:5011/scalar"
echo "  • Studio: http://127.0.0.1:54323  (con Supabase local)"
echo ""
echo "Pulsa Ctrl+C para detener API y web (Supabase sigue en Docker)."

# Espera a ambos; Ctrl+C dispara el trap
wait
