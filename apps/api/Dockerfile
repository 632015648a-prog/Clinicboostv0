# ============================================================
# ClinicBoost.Api — Dockerfile multi-stage
#
# Stage 1: sdk   — restaurar + compilar + publicar
# Stage 2: runtime — imagen mínima de producción
#
# Notas de seguridad:
#   · Se ejecuta como usuario no-root (app:app UID 1001)
#   · No se copia appsettings.Development.json ni secrets locales
#   · Healthcheck integrado en la imagen
#   · Sin shell de depuración en la imagen final
# ============================================================

# ── Stage 1: Build ────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# Copiar solo los archivos de proyecto primero para aprovechar capa de caché
COPY apps/api/src/ClinicBoost.Domain/ClinicBoost.Domain.csproj  apps/api/src/ClinicBoost.Domain/
COPY apps/api/src/ClinicBoost.Api/ClinicBoost.Api.csproj        apps/api/src/ClinicBoost.Api/
COPY apps/api/ClinicBoost.slnx                                   apps/api/

# Restaurar (cacheado si los .csproj no cambian)
RUN dotnet restore apps/api/ClinicBoost.slnx

# Copiar el resto del código fuente
COPY apps/api/ apps/api/

# Compilar y publicar en Release (sin debug symbols en la imagen final)
RUN dotnet publish apps/api/src/ClinicBoost.Api/ClinicBoost.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false \
    -p:PublishReadyToRun=false \
    -p:DebugType=None \
    -p:DebugSymbols=false

# ── Stage 2: Runtime ──────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime

# Crear usuario no-root
RUN addgroup --system --gid 1001 app \
 && adduser  --system --uid 1001 --ingroup app --no-create-home app

WORKDIR /app

# Copiar binarios publicados
COPY --from=build --chown=app:app /app/publish ./

# Configurar entorno de producción
ENV ASPNETCORE_ENVIRONMENT=Staging
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Exponer puerto interno (Nginx reverse-proxy mapea 80/443 → 8080)
EXPOSE 8080

# Cambiar a usuario no-root
USER app

# Healthcheck integrado
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 \
  CMD wget -qO- http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "ClinicBoost.Api.dll"]
