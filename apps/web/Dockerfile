# ============================================================
# ClinicBoost Web — Dockerfile multi-stage
#
# Stage 1: node  — instalar deps + build de producción Vite
# Stage 2: nginx — servir estáticos con Nginx
#
# Notas:
#   · Las variables VITE_* se inyectan en build-time (ARG)
#   · Si necesitas runtime injection, usar el script entrypoint
#     env-inject.sh para sustituir window.__ENV en index.html
#   · Nginx sirve en puerto 3000 (mapeado a 80 por docker-compose)
# ============================================================

# ── Stage 1: Build ────────────────────────────────────────────
FROM node:20-alpine AS build
WORKDIR /app

# Copiar manifest primero para capa de caché npm
COPY apps/web/package.json apps/web/package-lock.json ./
RUN npm ci --prefer-offline

# Variables de entorno Vite (inyectadas en build-time por CI)
ARG VITE_SUPABASE_URL
ARG VITE_SUPABASE_ANON_KEY
ARG VITE_API_URL

ENV VITE_SUPABASE_URL=$VITE_SUPABASE_URL
ENV VITE_SUPABASE_ANON_KEY=$VITE_SUPABASE_ANON_KEY
ENV VITE_API_URL=$VITE_API_URL

# Copiar código fuente y compilar
COPY apps/web/ ./
RUN npm run build

# ── Stage 2: Nginx ────────────────────────────────────────────
FROM nginx:1.27-alpine AS runtime

# Eliminar configuración por defecto de nginx
RUN rm /etc/nginx/conf.d/default.conf

# Copiar configuración personalizada (SPA + headers de seguridad)
COPY infra/nginx/nginx.conf      /etc/nginx/nginx.conf
COPY infra/nginx/default.conf    /etc/nginx/conf.d/default.conf

# Copiar build de Vite
COPY --from=build /app/dist /usr/share/nginx/html

# Puerto interno
EXPOSE 3000

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD wget -qO- http://localhost:3000/ || exit 1

CMD ["nginx", "-g", "daemon off;"]
