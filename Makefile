# ============================================================
# ClinicBoost — Makefile de desarrollo local + staging
# Uso: make <target>
# ============================================================

.PHONY: help setup supabase-start supabase-stop db-reset api-run api-build api-test \
        web-dev web-build lint dev \
        staging-up staging-down staging-logs staging-ps staging-health \
        staging-migrate staging-build \
        smoke-tests smoke-tests-tc01 smoke-tests-tc02 smoke-tests-tc03 \
        smoke-tests-tc04 smoke-tests-tc05 smoke-tests-tc06 smoke-tests-verbose

help: ## Muestra esta ayuda
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | \
	  awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-24s\033[0m %s\n", $$1, $$2}'

# ─── Setup inicial ────────────────────────────────────────────────────────────
setup: ## Primera configuración del proyecto
	@echo "Copiando archivos de ejemplo de variables de entorno..."
	cp -n apps/web/.env.local.example apps/web/.env.local || true
	@echo "Instalando dependencias frontend..."
	cd apps/web && npm install
	@echo "Restaurando paquetes .NET..."
	cd apps/api && dotnet restore
	@echo ""
	@echo "✅ Setup completado. Edita apps/web/.env.local con tus credenciales."

# ─── Supabase ─────────────────────────────────────────────────────────────────
supabase-start: ## Arrancar Supabase en Docker
	supabase start

supabase-stop: ## Parar Supabase
	supabase stop

db-reset: ## Resetear BD local (migraciones + seed)
	supabase db reset

db-migrate: ## Aplicar nuevas migraciones
	supabase db push

# ─── Backend .NET ─────────────────────────────────────────────────────────────
api-run: ## Arrancar API en modo desarrollo
	cd apps/api && dotnet run --project src/ClinicBoost.Api

api-build: ## Compilar API en Release
	cd apps/api && dotnet build --configuration Release

api-test: ## Ejecutar tests del backend
	cd apps/api && dotnet test --verbosity normal

# ─── Frontend React ───────────────────────────────────────────────────────────
web-dev: ## Arrancar frontend en modo desarrollo
	cd apps/web && npm run dev

web-build: ## Build de producción del frontend
	cd apps/web && npm run build

lint: ## Lint del frontend
	cd apps/web && npm run lint

# ─── Todo a la vez ────────────────────────────────────────────────────────────
dev: ## Instrucciones para arrancar todo (requiere 3 terminales)
	@echo "Abre 3 terminales y ejecuta:"
	@echo "  Terminal 1: make supabase-start"
	@echo "  Terminal 2: make api-run"
	@echo "  Terminal 3: make web-dev"

# ─── Staging (Docker Compose) ─────────────────────────────────────────────────
staging-env-check: ## Verificar que .env.staging existe
	@test -f .env.staging || (echo "❌ .env.staging no encontrado. Copia .env.staging.example y rellena los valores." && exit 1)
	@echo "✅ .env.staging encontrado"

staging-build: staging-env-check ## Construir imágenes Docker de staging
	docker compose -f docker-compose.staging.yml --env-file .env.staging build

staging-up: staging-env-check ## Levantar stack de staging
	@mkdir -p logs/nginx logs/api
	docker compose -f docker-compose.staging.yml --env-file .env.staging up -d --remove-orphans
	@echo "✅ Stack staging levantado. Probando health..."
	@sleep 5
	@$(MAKE) staging-health

staging-down: ## Parar stack de staging
	docker compose -f docker-compose.staging.yml down

staging-logs: ## Ver logs del stack de staging (follow)
	docker compose -f docker-compose.staging.yml logs -f

staging-ps: ## Estado de los contenedores de staging
	docker compose -f docker-compose.staging.yml ps

staging-health: ## Verificar health del stack de staging
	@echo "── health/live ──────────────────────────"
	@curl -sf http://localhost/health/live | python3 -m json.tool || echo "❌ /health/live falló"
	@echo "── health/ready ─────────────────────────"
	@curl -sf http://localhost/health/ready | python3 -m json.tool || echo "❌ /health/ready falló"

staging-migrate: ## Aplicar migraciones a staging Cloud (requiere SUPABASE_ACCESS_TOKEN y STAGING_PROJECT_REF)
	bash infra/scripts/migrate-staging.sh

staging-restart-api: ## Reiniciar solo la API (sin rebuild)
	docker compose -f docker-compose.staging.yml restart api

staging-shell-api: ## Acceder al shell del contenedor API
	docker compose -f docker-compose.staging.yml exec api sh

# ─── Smoke Tests E2E ──────────────────────────────────────────────────────────
smoke-tests: ## Ejecutar suite completa de smoke tests E2E
	bash infra/scripts/run-smoke-tests.sh

smoke-tests-tc01: ## Smoke tests TC-01: llamada perdida → WhatsApp
	TC=TC-01 bash infra/scripts/run-smoke-tests.sh

smoke-tests-tc02: ## Smoke tests TC-02: paciente responde → IA responde
	TC=TC-02 bash infra/scripts/run-smoke-tests.sh

smoke-tests-tc03: ## Smoke tests TC-03: reserva → appointment → revenue
	TC=TC-03 bash infra/scripts/run-smoke-tests.sh

smoke-tests-tc04: ## Smoke tests TC-04: fuera de horario → timezone
	TC=TC-04 bash infra/scripts/run-smoke-tests.sh

smoke-tests-tc05: ## Smoke tests TC-05: conversation human-only
	TC=TC-05 bash infra/scripts/run-smoke-tests.sh

smoke-tests-tc06: ## Smoke tests TC-06: webhook estado Twilio
	TC=TC-06 bash infra/scripts/run-smoke-tests.sh

smoke-tests-verbose: ## Smoke tests con output detallado (debug)
	VERBOSE=1 bash infra/scripts/run-smoke-tests.sh
