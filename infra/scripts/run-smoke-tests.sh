#!/usr/bin/env bash
# ============================================================
# infra/scripts/run-smoke-tests.sh
#
# Runner de la suite de smoke tests E2E de ClinicBoost.
# Ejecuta todos los tests con categoría "SmokeE2E" y genera
# un reporte TRX + resumen en consola.
#
# USO:
#   bash infra/scripts/run-smoke-tests.sh
#
# OPCIONES:
#   TC=TC-01   → solo ejecutar los tests del caso TC-01
#   VERBOSE=1  → output detallado de xUnit
#   BAIL=1     → parar en el primer fallo
#
# PREREQS:
#   · .NET 10 SDK instalado
#   · Desde la raíz del repositorio (ClinicboostV0/)
#
# SALIDA:
#   · Resumen en consola (tests passed/failed/skipped)
#   · apps/api/test-results/smoke-tests-<TIMESTAMP>.trx
#   · apps/api/test-results/smoke-summary-<TIMESTAMP>.txt
# ============================================================

set -euo pipefail

# ── Colores ───────────────────────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'
CYAN='\033[0;36m'; BOLD='\033[1m'; NC='\033[0m'

log()  { echo -e "${GREEN}[SMOKE]${NC} $*"; }
warn() { echo -e "${YELLOW}[WARN]${NC} $*"; }
err()  { echo -e "${RED}[ERROR]${NC} $*" >&2; }
info() { echo -e "${CYAN}[INFO]${NC} $*"; }

# ── Variables ─────────────────────────────────────────────────────────────────
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
API_ROOT="${REPO_ROOT}/apps/api"
TEST_PROJECT="${API_ROOT}/tests/ClinicBoost.Tests/ClinicBoost.Tests.csproj"
RESULTS_DIR="${API_ROOT}/test-results"
TRX_FILE="${RESULTS_DIR}/smoke-tests-${TIMESTAMP}.trx"
SUMMARY_FILE="${RESULTS_DIR}/smoke-summary-${TIMESTAMP}.txt"

# Opciones de ejecución
TC_FILTER="${TC:-}"          # Si TC=TC-01, filtrar solo ese caso
VERBOSE="${VERBOSE:-0}"      # VERBOSE=1 para output detallado
BAIL="${BAIL:-0}"            # BAIL=1 para parar en primer fallo

# ── Verificar prereqs ─────────────────────────────────────────────────────────
if ! command -v dotnet &> /dev/null; then
    err ".NET SDK no encontrado. Instalar desde: https://dotnet.microsoft.com/download"
    exit 1
fi

DOTNET_VERSION=$(dotnet --version)
log "Usando .NET ${DOTNET_VERSION}"

if [ ! -f "${TEST_PROJECT}" ]; then
    err "Proyecto de tests no encontrado: ${TEST_PROJECT}"
    exit 1
fi

# ── Crear directorio de resultados ─────────────────────────────────────────────
mkdir -p "${RESULTS_DIR}"

# ── Banner ─────────────────────────────────────────────────────────────────────
echo ""
echo -e "${BOLD}${CYAN}════════════════════════════════════════════════════════════${NC}"
echo -e "${BOLD}${CYAN}  ClinicBoost — Suite de Smoke Tests E2E${NC}"
echo -e "${BOLD}${CYAN}  Fecha: $(date +'%Y-%m-%d %H:%M:%S')${NC}"
echo -e "${BOLD}${CYAN}════════════════════════════════════════════════════════════${NC}"
echo ""

# ── Construir filtro xUnit ─────────────────────────────────────────────────────
FILTER="Category=SmokeE2E"
if [ -n "${TC_FILTER}" ]; then
    FILTER="${FILTER}&TC=${TC_FILTER}"
    info "Filtrando por: ${TC_FILTER}"
fi

# ── Opciones de verbosidad ─────────────────────────────────────────────────────
VERBOSITY="minimal"
if [ "${VERBOSE}" = "1" ]; then
    VERBOSITY="normal"
fi

# ── Opciones de bail ──────────────────────────────────────────────────────────
BAIL_OPTS=""
if [ "${BAIL}" = "1" ]; then
    BAIL_OPTS="--exit-on-first-error"
    warn "Modo BAIL activado — se detendrá en el primer fallo"
fi

# ── Restaurar dependencias ────────────────────────────────────────────────────
log "Restaurando dependencias..."
dotnet restore "${TEST_PROJECT}" --verbosity quiet

# ── Compilar ──────────────────────────────────────────────────────────────────
log "Compilando suite de tests..."
if ! dotnet build "${TEST_PROJECT}" \
        --no-restore \
        --configuration Release \
        --verbosity quiet; then
    err "Error de compilación. Revisa los errores arriba."
    exit 1
fi
log "Compilación exitosa ✅"

# ── Ejecutar smoke tests ──────────────────────────────────────────────────────
log "Ejecutando smoke tests (filtro: ${FILTER})..."
echo ""

START_TIME=$(date +%s)

dotnet test "${TEST_PROJECT}" \
    --no-build \
    --configuration Release \
    --filter "Trait~${FILTER}" \
    --verbosity "${VERBOSITY}" \
    --logger "trx;LogFileName=${TRX_FILE}" \
    --results-directory "${RESULTS_DIR}" \
    ${BAIL_OPTS} \
    2>&1 | tee "${SUMMARY_FILE}" || TESTS_FAILED=1

END_TIME=$(date +%s)
DURATION=$((END_TIME - START_TIME))

# ── Parsear resultados del TRX ────────────────────────────────────────────────
PASSED=0; FAILED=0; SKIPPED=0; TOTAL=0

if [ -f "${TRX_FILE}" ]; then
    # Extraer contadores del TRX (XML)
    PASSED=$(grep -oP 'passed="\K[0-9]+' "${TRX_FILE}" | head -1 || echo 0)
    FAILED=$(grep -oP 'failed="\K[0-9]+' "${TRX_FILE}" | head -1 || echo 0)
    SKIPPED=$(grep -oP 'skipped="\K[0-9]+' "${TRX_FILE}" | head -1 || echo 0)
    TOTAL=$((PASSED + FAILED + SKIPPED))
fi

# ── Reporte final ─────────────────────────────────────────────────────────────
echo ""
echo -e "${BOLD}${CYAN}════════════════════════════════════════════════════════════${NC}"
echo -e "${BOLD}  REPORTE FINAL — SMOKE TESTS E2E${NC}"
echo -e "${BOLD}  ClinicBoost v$(grep -oP '"version":\s*"\K[^"]+' "${REPO_ROOT}/apps/web/package.json" 2>/dev/null || echo "dev")${NC}"
echo -e "${BOLD}${CYAN}════════════════════════════════════════════════════════════${NC}"
echo ""
echo -e "  ⏱  Duración total:  ${DURATION}s"
echo -e "  🧪 Tests ejecutados: ${TOTAL}"
echo -e "  ${GREEN}✅ Passed:   ${PASSED}${NC}"
echo -e "  ${RED}❌ Failed:   ${FAILED}${NC}"
echo -e "  ${YELLOW}⏭  Skipped:  ${SKIPPED}${NC}"
echo ""

if [ "${FAILED}" -eq 0 ] && [ "${TOTAL}" -gt 0 ]; then
    echo -e "  ${GREEN}${BOLD}🎉 Todos los smoke tests pasaron — entorno listo para staging${NC}"
elif [ "${FAILED}" -gt 0 ]; then
    echo -e "  ${RED}${BOLD}⚠️  Hay fallos — revisar antes de hacer deploy${NC}"
    echo ""
    echo -e "  Tests fallidos:"
    grep -oP 'testName="\K[^"]+' "${TRX_FILE}" 2>/dev/null | \
    while IFS= read -r name; do
        # Verificar si este test está en el bloque outcome="Failed"
        echo -e "    ${RED}• ${name}${NC}"
    done | head -20
fi

echo ""
echo -e "  📄 Reporte TRX: ${TRX_FILE}"
echo -e "  📋 Log consola:  ${SUMMARY_FILE}"
echo -e "${BOLD}${CYAN}════════════════════════════════════════════════════════════${NC}"
echo ""

# ── Salir con código correcto ─────────────────────────────────────────────────
if [ "${FAILED}" -gt 0 ]; then
    exit 1
fi
exit 0
