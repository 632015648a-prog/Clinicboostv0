# ============================================================
# ClinicBoost Web — Dockerfile multi-stage
#
# Stage 1: node  — instalar deps + build de producción Vite
# Stage 2: nginx — servir los estáticos con Nginx (SPA mode)
#
# Notas:
#   · Las variables VITE_* se inyectan en build-time (ARG).
#     Son constantes en el bundle; si cambian, hay que reconstruir la imagen.
#   · Este contenedor sirve SOLO los estáticos de la SPA en :3000.
#     El routing de /api/* → backend lo gestiona el Nginx externo (cb_nginx).
#   · Para runtime injection sin rebuild, usar un entrypoint.sh que
#     sustituya window.__ENV__ en index.html antes de arrancar Nginx.
# ============================================================

# ── Stage 1: Build (Node 20 Alpine) ──────────────────────────────────────────
FROM node:20-alpine AS build
WORKDIR /app

# Copiar manifest primero → capa de caché npm (se invalida solo si cambian deps)
COPY apps/web/package.json apps/web/package-lock.json ./
RUN npm ci --prefer-offline

# Variables de entorno Vite (inyectadas en build-time por CI / docker compose)
ARG VITE_SUPABASE_URL
ARG VITE_SUPABASE_ANON_KEY
ARG VITE_API_URL

ENV VITE_SUPABASE_URL=$VITE_SUPABASE_URL
ENV VITE_SUPABASE_ANON_KEY=$VITE_SUPABASE_ANON_KEY
ENV VITE_API_URL=$VITE_API_URL

# Copiar código fuente y compilar
COPY apps/web/ ./
RUN npm run build

# ── Stage 2: Runtime Nginx (1.27 Alpine) ─────────────────────────────────────
FROM nginx:1.27-alpine AS runtime

# Eliminar configuración por defecto de nginx
RUN rm -f /etc/nginx/conf.d/default.conf

# nginx.conf: configuración de nivel global (worker, gzip, timeouts…)
# spa.conf:   server block para servir la SPA en :3000 con SPA fallback
COPY infra/nginx/nginx.conf   /etc/nginx/nginx.conf
COPY infra/nginx/spa.conf     /etc/nginx/conf.d/spa.conf

# Copiar el build de Vite
COPY --from=build /app/dist   /usr/share/nginx/html

# Puerto interno (cb_nginx externo hace proxy a este puerto)
EXPOSE 3000

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD wget -qO- http://localhost:3000/ || exit 1

CMD ["nginx", "-g", "daemon off;"]
