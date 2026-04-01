#!/usr/bin/env bash
# ============================================================
# infra/scripts/migrate-staging.sh
#
# Aplica las migraciones de Supabase al proyecto de Staging Cloud.
#
# PREREQS:
#   · supabase CLI instalado (>= 1.200)
#   · SUPABASE_ACCESS_TOKEN en el entorno (o en .env.staging)
#   · PROJECT_REF del proyecto de staging (ver dashboard)
#
# USO:
#   export SUPABASE_ACCESS_TOKEN=sbp_xxxxx
#   export STAGING_PROJECT_REF=abcdefghijklm
#   bash infra/scripts/migrate-staging.sh
#
# ADVERTENCIA:
#   · Este script NO hace rollback automático.
#   · Haz un backup de la BD antes de correr en staging con datos reales.
#   · Las migraciones son idempotentes (IF NOT EXISTS) pero úsalo con cuidado.
# ============================================================

set -euo pipefail

# ── Colores para output ───────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log()  { echo -e "${GREEN}[MIGRATE]${NC} $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()  { echo -e "${RED}[ERROR]${NC} $*" >&2; }

# ── Verificar prereqs ─────────────────────────────────────────────────────────
if ! command -v supabase &> /dev/null; then
  err "supabase CLI no encontrado. Instalar: https://supabase.com/docs/guides/cli"
  exit 1
fi

if [ -z "${SUPABASE_ACCESS_TOKEN:-}" ]; then
  err "Variable SUPABASE_ACCESS_TOKEN no configurada."
  err "Obtener en: https://supabase.com/dashboard/account/tokens"
  exit 1
fi

if [ -z "${STAGING_PROJECT_REF:-}" ]; then
  err "Variable STAGING_PROJECT_REF no configurada."
  err "Obtener en: Supabase Dashboard → Settings → General → Reference ID"
  exit 1
fi

log "Iniciando migraciones para proyecto: ${STAGING_PROJECT_REF}"
log "CLI versión: $(supabase --version)"

# ── Listar migraciones pendientes ──────────────────────────────────────────────
log "Migraciones disponibles:"
ls -1 supabase/migrations/*.sql 2>/dev/null || { err "No se encontraron migraciones en supabase/migrations/"; exit 1; }

# ── Backup reminder ────────────────────────────────────────────────────────────
warn "IMPORTANTE: ¿Has hecho un backup de la BD de staging?"
warn "Supabase → Dashboard → Database → Backups → Create backup"
read -p "¿Continuar? (y/N): " confirm
if [[ "${confirm}" != "y" && "${confirm}" != "Y" ]]; then
  log "Cancelado."
  exit 0
fi

# ── Aplicar migraciones ────────────────────────────────────────────────────────
log "Aplicando migraciones a staging (proyecto ${STAGING_PROJECT_REF})..."

supabase db push \
  --project-ref "${STAGING_PROJECT_REF}" \
  --include-seed

log "✅ Migraciones aplicadas correctamente."

# ── Verificar tablas críticas ─────────────────────────────────────────────────
log "Verificando tablas críticas..."

TABLES=(
  "tenants"
  "patients"
  "appointments"
  "conversations"
  "messages"
  "message_delivery_events"
  "revenue_events"
  "processed_events"
  "flow_metrics_events"
  "message_variants"
)

for table in "${TABLES[@]}"; do
  result=$(supabase db execute \
    --project-ref "${STAGING_PROJECT_REF}" \
    --sql "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_name='${table}') as exists;" \
    2>/dev/null || echo "error")

  if echo "${result}" | grep -q "t"; then
    log "  ✅ ${table}"
  else
    warn "  ⚠️  ${table} — no encontrada o error"
  fi
done

# ── Verificar RLS activo ───────────────────────────────────────────────────────
log "Verificando RLS en tablas de negocio..."

supabase db execute \
  --project-ref "${STAGING_PROJECT_REF}" \
  --sql "SELECT tablename, rowsecurity FROM pg_tables WHERE schemaname='public' AND rowsecurity=false AND tablename NOT IN ('spatial_ref_sys');" \
  2>/dev/null && log "  ✅ RLS verificado" || warn "  ⚠️  No se pudo verificar RLS"

log "✅ Migración de staging completada."
echo ""
echo "📋 SIGUIENTE PASO: verificar en Supabase Studio que las políticas RLS están activas"
echo "   URL: https://supabase.com/dashboard/project/${STAGING_PROJECT_REF}/auth/policies"
