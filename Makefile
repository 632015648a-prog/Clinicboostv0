# ============================================================
# ClinicBoost — Makefile de desarrollo local
# Uso: make <target>
# ============================================================

.PHONY: help setup supabase-start supabase-stop db-reset api-run api-build api-test web-dev web-build lint

help: ## Muestra esta ayuda
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | \
	  awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-20s\033[0m %s\n", $$1, $$2}'

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
