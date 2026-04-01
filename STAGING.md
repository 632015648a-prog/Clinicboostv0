# ClinicBoost — Guía de Staging

> **Objetivo**: levantar un entorno de staging demostrable con dominio, HTTPS, variables
> de entorno seguras y una configuración reproducible.
>
> **Veredicto previo a demo con cliente**: ver sección [Veredicto Final](#-veredicto-final).

---

## Índice

1. [Arquitectura del entorno](#1-arquitectura-del-entorno)
2. [Archivos de infraestructura creados](#2-archivos-de-infraestructura-creados)
3. [Variables de entorno requeridas](#3-variables-de-entorno-requeridas)
4. [Instrucciones: arrancar staging desde cero](#4-instrucciones-arrancar-staging-desde-cero)
5. [Health checks: /health/live y /health/ready](#5-health-checks-healthlive-y-healthready)
6. [Checklist Twilio Voice y WhatsApp Business](#6-checklist-twilio-voice-y-whatsapp-business)
7. [Checklist base de datos y migraciones](#7-checklist-base-de-datos-y-migraciones)
8. [Seguridad: cookies, CORS y CSP](#8-seguridad-cookies-cors-y-csp)
9. [CI/CD: pipeline de staging automático](#9-cicd-pipeline-de-staging-automático)
10. [Riesgos pendientes antes de piloto real](#10-riesgos-pendientes-antes-de-piloto-real)
11. [Veredicto final](#-veredicto-final)

---

## 1. Arquitectura del entorno

```
[Internet / Cloudflare CDN + TLS]
         │  :443 (Cloudflare termina TLS, reenvía HTTP a :80)
         ▼
   cb_nginx  :80  (nginx:1.27-alpine)
     ├─ /api/*         → cb_api:8080  (.NET 10 Minimal API)
     ├─ /auth/*        → cb_api:8080
     ├─ /webhooks/*    → cb_api:8080
     ├─ /health/*      → cb_api:8080
     └─ /*             → cb_web:3000  (Nginx sirviendo SPA React)

   cb_api   :8080  (mcr.microsoft.com/dotnet/aspnet:10.0-noble)
     └─ BD: Supabase Cloud (PostgreSQL + RLS)

   cb_web   :3000  (nginx:1.27-alpine, solo archivos estáticos)
     └─ React + Vite (VITE_* baked en build-time)
```

**Sin base de datos en Docker**: la BD vive en Supabase Cloud.
**TLS**: delegado a Cloudflare (recomendado) o Nginx + Let's Encrypt.

---

## 2. Archivos de infraestructura creados

| Archivo | Descripción |
|---------|-------------|
| `docker-compose.staging.yml` | Orquestación: cb_nginx + cb_api + cb_web |
| `.env.staging.example` | Plantilla de variables de entorno |
| `apps/api/Dockerfile` | Multi-stage .NET 10: SDK build → runtime mínimo, usuario no-root |
| `apps/web/Dockerfile` | Multi-stage Node 20 → Nginx (spa.conf, :3000) |
| `infra/nginx/nginx.conf` | Config global Nginx (gzip, timeouts, rate-limit zones) |
| `infra/nginx/default.conf` | Proxy externo: enruta /api/* → cb_api, /* → cb_web |
| `infra/nginx/spa.conf` | Nginx interno del cb_web: SPA fallback, headers seguridad |
| `infra/scripts/migrate-staging.sh` | Script para aplicar migraciones Supabase a staging Cloud |
| `apps/api/src/ClinicBoost.Api/appsettings.Staging.json` | Config Staging (.NET): valores override para ASPNETCORE_ENVIRONMENT=Staging |
| `.github/workflows/cd-staging.yml` | CI/CD: build → Docker verify → deploy SSH → smoke test |
| `Makefile` | Targets `staging-up`, `staging-down`, `staging-health`, `staging-migrate` |

---

## 3. Variables de entorno requeridas

Todas las variables se configuran en `.env.staging` (NO commitear al repo).

### 3.1 Variables obligatorias

| Variable | Fuente | Descripción |
|----------|--------|-------------|
| `DB_CONNECTION_STRING` | Supabase Dashboard → Settings → Database → Connection string → .NET | Npgsql con `app_user` (no `postgres`) |
| `SUPABASE_URL` | Supabase Dashboard → Settings → API | URL del proyecto: `https://<ref>.supabase.co` |
| `SUPABASE_ANON_KEY` | Supabase Dashboard → Settings → API | Clave pública anon (safe para frontend) |
| `SUPABASE_JWT_SECRET` | Supabase Dashboard → Settings → API → JWT Settings | **Secreto** — mín. 32 chars |
| `VITE_SUPABASE_URL` | Igual que `SUPABASE_URL` | Baked en el build de Vite |
| `VITE_SUPABASE_ANON_KEY` | Igual que `SUPABASE_ANON_KEY` | Baked en el build de Vite |
| `VITE_API_URL` | `https://staging.clinicboost.es` | URL pública del staging |
| `CORS_ORIGIN_0` | `https://staging.clinicboost.es` | Origen CORS permitido |
| `TWILIO_ACCOUNT_SID` | Twilio Console → Account Info | `ACxxxxxxxxxxxxxxxx` |
| `TWILIO_AUTH_TOKEN` | Twilio Console → Account Info | Token de autenticación Twilio |
| `TWILIO_WEBHOOK_BASE_URL` | `https://staging.clinicboost.es` | Base URL para webhooks de Twilio |

### 3.2 Variables opcionales

| Variable | Default | Descripción |
|----------|---------|-------------|
| `CORS_ORIGIN_1` | `""` | Segundo origen CORS (e.g., preview deploy) |
| `OPENAI_API_KEY` | `""` | Solo si el agente conversacional está activo |
| `ANTHROPIC_API_KEY` | `""` | Solo si el agente usa Claude |
| `CSP_REPORT_ONLY` | `"true"` | `true` en staging, `false` en producción |
| `CSP_REPORT_URI` | `"/auth/csp-report"` | Endpoint para reportes CSP |

### 3.3 Variables de GitHub Actions (Secrets)

Configurar en GitHub → Settings → Secrets and variables → Actions:

```
STAGING_DB_CONNECTION_STRING
STAGING_SUPABASE_URL
STAGING_SUPABASE_ANON_KEY
STAGING_SUPABASE_JWT_SECRET
STAGING_TWILIO_ACCOUNT_SID
STAGING_TWILIO_AUTH_TOKEN
STAGING_TWILIO_WEBHOOK_BASE_URL
STAGING_OPENAI_API_KEY          (opcional)
STAGING_ANTHROPIC_API_KEY       (opcional)
STAGING_HOST                    (IP del servidor, e.g. 185.x.x.x)
STAGING_SSH_USER                (usuario SSH, e.g. deploy)
STAGING_SSH_KEY                 (clave privada SSH en PEM)
STAGING_DOMAIN                  (https://staging.clinicboost.es)
```

---

## 4. Instrucciones: arrancar staging desde cero

### 4.1 Prerrequisitos del servidor

```bash
# Servidor Ubuntu 22.04 / 24.04 con:
# · Docker Engine >= 24.0
# · Docker Compose >= 2.20 (plugin, no standalone)
# · Git
# · Puerto 80 abierto (Cloudflare hace el TLS)

# Instalar Docker (si no está instalado):
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker $USER
newgrp docker

# Verificar:
docker --version          # Docker version 24.x.x
docker compose version    # Docker Compose version v2.x.x
```

### 4.2 Clonar el repositorio en el servidor

```bash
# Crear directorio del proyecto
sudo mkdir -p /opt/clinicboost
sudo chown $USER:$USER /opt/clinicboost
cd /opt/clinicboost

# Clonar (o hacer pull si ya existe)
git clone https://github.com/632015648a-prog/Clinicboostv0.git .
# O si ya está clonado:
git pull origin main
```

### 4.3 Configurar variables de entorno

```bash
# Copiar la plantilla
cp .env.staging.example .env.staging

# Editar con los valores reales
nano .env.staging

# Proteger el archivo (solo lectura para el propietario)
chmod 600 .env.staging
```

**Verificar que está en .gitignore**:
```bash
grep ".env.staging" .gitignore
# debe mostrar: .env*
```

### 4.4 Aplicar migraciones a Supabase Cloud

> ⚠️ **Hacer backup antes**: Supabase Dashboard → Database → Backups → Create backup

```bash
# Requerimientos:
export SUPABASE_ACCESS_TOKEN=sbp_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
export STAGING_PROJECT_REF=abcdefghijklm   # 20 chars, en Dashboard → Settings → General

# Ejecutar script de migración:
bash infra/scripts/migrate-staging.sh
```

**O usando supabase CLI directamente**:
```bash
supabase db push --project-ref $STAGING_PROJECT_REF
```

### 4.5 Crear directorios de logs

```bash
mkdir -p logs/nginx logs/api
```

### 4.6 Construir y levantar el stack

```bash
# Usando Make (recomendado):
make staging-build   # Construir imágenes
make staging-up      # Levantar + health check automático

# O directamente con Docker Compose:
docker compose -f docker-compose.staging.yml --env-file .env.staging build
docker compose -f docker-compose.staging.yml --env-file .env.staging up -d --remove-orphans
```

### 4.7 Verificar el stack

```bash
# Estado de los contenedores
make staging-ps
# Debe mostrar: cb_api (healthy), cb_web (healthy), cb_nginx (healthy)

# Health checks
make staging-health
# /health/live  → {"status":"live"}
# /health/ready → {"status":"Healthy", ...}

# Verificar desde el exterior (sustituir por tu dominio):
curl -s https://staging.clinicboost.es/health/live | jq
curl -s https://staging.clinicboost.es/health/ready | jq

# Verificar que la SPA carga:
curl -s https://staging.clinicboost.es/ | grep -i "clinicboost"

# Verificar que los endpoints protegidos requieren autenticación:
curl -o /dev/null -s -w "%{http_code}" https://staging.clinicboost.es/api/dashboard/summary
# Debe devolver: 401
```

### 4.8 Configurar DNS (Cloudflare)

1. En Cloudflare → DNS → Add record:
   ```
   Type: A
   Name: staging
   Value: <IP del servidor>
   Proxy: ✅ Proxied (naranja — TLS automático)
   TTL: Auto
   ```

2. En Cloudflare → SSL/TLS → Overview: seleccionar **Full (strict)** si el servidor tiene cert, o **Flexible** si solo escucha en :80.

3. Verificar que `https://staging.clinicboost.es` responde con TLS.

### 4.9 Comandos de mantenimiento

```bash
make staging-ps              # Estado de contenedores
make staging-health          # Verificar health endpoints
make staging-logs            # Ver logs en tiempo real (Ctrl+C para salir)
make staging-restart-api     # Reiniciar solo la API (sin rebuild)
make staging-shell-api       # Acceder al shell del contenedor API
make staging-down            # Parar el stack
make staging-build           # Reconstruir imágenes (necesario tras cambios de código)
```

---

## 5. Health checks: /health/live y /health/ready

### Endpoints disponibles

| Endpoint | Descripción | Auth requerida |
|----------|-------------|----------------|
| `GET /health/live` | Liveness: ¿el proceso está vivo? | No |
| `GET /health/ready` | Readiness: ¿BD conectada? ¿servicio listo? | No |
| `GET /health/deps` | Dependencias con detalle (BD, Twilio, etc.) | No |

### Respuesta esperada

```bash
# /health/live — siempre 200 si el proceso responde
curl https://staging.clinicboost.es/health/live
# {"status":"live","ts":"2026-04-01T10:00:00Z"}

# /health/ready — 200 si BD conectada, 503 si no
curl https://staging.clinicboost.es/health/ready
# {"status":"Healthy","totalDuration":"00:00:00.0234567","entries":{"npgsql":{"status":"Healthy","duration":"00:00:00.0221234"}}}
```

### Healthcheck en Docker Compose

El `docker-compose.staging.yml` configura healthchecks para los tres servicios:
- `cb_api`: `wget http://localhost:8080/health/live` cada 30s
- `cb_web`: `wget http://localhost:3000/` cada 30s
- `cb_nginx`: `wget http://localhost:80/health/live` cada 30s (hace proxy a `cb_api`)

El orden de arranque está garantizado: `cb_api` healthy → `cb_web` y `cb_nginx` arrancan.

---

## 6. Checklist Twilio Voice y WhatsApp Business

### 6.1 Twilio Voice (llamadas perdidas)

- [ ] **Cuenta Twilio verificada** con número de teléfono activo
- [ ] **Número con Voice capability** habilitada en Twilio Console
- [ ] **TwiML App** configurada con webhook:
  ```
  Voice URL: https://staging.clinicboost.es/webhooks/voice/incoming
  HTTP Method: POST
  Status Callback URL: https://staging.clinicboost.es/webhooks/voice/status
  ```
- [ ] **`TWILIO_ACCOUNT_SID`** y **`TWILIO_AUTH_TOKEN`** configurados en `.env.staging`
- [ ] **`TWILIO_WEBHOOK_BASE_URL`** apunta a `https://staging.clinicboost.es`
- [ ] Verificar que Twilio puede alcanzar el endpoint:
  ```bash
  # El endpoint debe responder con TwiML válido:
  curl -X POST https://staging.clinicboost.es/webhooks/voice/incoming \
    -d "CallSid=CAtest&From=+34600000000&To=+34900000000"
  ```
- [ ] **Validación de firma Twilio** activa en el webhook handler (evitar spoofing)
  > El código actual en `WhatsAppInboundHandler` verifica la firma; asegurarse de que también lo hace el handler de Voice.

### 6.2 WhatsApp Business (mensajes)

- [ ] **WhatsApp Business Account** aprobada en Meta Business Manager
- [ ] **Número de teléfono WhatsApp** verificado y activo
- [ ] **Twilio Sandbox** o número de producción configurado
- [ ] **Webhook de mensajes entrantes** configurado en Twilio Console:
  ```
  Messaging → A message comes in:
  URL: https://staging.clinicboost.es/webhooks/whatsapp/inbound
  HTTP Method: POST

  Messaging → A message status changes:
  URL: https://staging.clinicboost.es/webhooks/whatsapp/status
  HTTP Method: POST
  ```
- [ ] **Templates de WhatsApp** (HSM) aprobados en Meta para:
  - Mensaje de recuperación de cita perdida (Flow 01)
  - Confirmación de cita
- [ ] Verificar entrega de mensaje de prueba:
  ```bash
  # Enviar un mensaje de prueba desde la consola de Twilio
  # Verificar que llega al webhook y se registra en la BD
  SELECT * FROM messages ORDER BY created_at DESC LIMIT 5;
  ```
- [ ] **Opt-out / Opt-in** manejado correctamente (palabras clave STOP, START)
- [ ] **Rate limits** de Twilio configurados en Nginx (30r/m para API, 120r/m para webhooks)

### 6.3 Verificación end-to-end en staging

```bash
# Simular llamada perdida (trigger Flow 01):
curl -X POST https://staging.clinicboost.es/webhooks/voice/incoming \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "CallSid=CA$(openssl rand -hex 16)&CallStatus=no-answer&From=%2B34600000000&To=%2B34900000000"

# Verificar en la BD que se creó una FlowMetricsEvent con metric_type='outbound_sent':
# (requiere acceso a Supabase Studio o psql)
```

---

## 7. Checklist base de datos y migraciones

### 7.1 Estado de migraciones

Las siguientes migraciones deben estar aplicadas en staging:

| Migración | Descripción |
|-----------|-------------|
| `20260329000001` | Esquema inicial: tenants, patients, appointments |
| `20260329000002` | Conversations y messages |
| `20260329000003` | Message delivery events |
| `20260329000004` | Revenue events |
| `20260329000005` | Flow metrics events |
| `20260329000006` | RLS policies para todas las tablas |
| `20260329000007` | Índices de rendimiento |
| `20260329000008` | Message variants y conversion events |
| `20260329000009` | Agent turns y webhook events |
| `20260331000080` | Correcciones RLS (drop function `current_tenant_id()` → reemplazada) |
| `20260331000090` | Índices adicionales para Dashboard queries |

### 7.2 Verificar migraciones aplicadas

```sql
-- En Supabase Studio → SQL Editor:
SELECT version, inserted_at
FROM supabase_migrations.schema_migrations
ORDER BY inserted_at DESC;
```

### 7.3 Verificar RLS activo en todas las tablas

```sql
-- Tablas de negocio SIN RLS (debe estar vacío):
SELECT tablename
FROM pg_tables
WHERE schemaname = 'public'
  AND rowsecurity = false
  AND tablename NOT IN (
    'spatial_ref_sys',
    'schema_migrations'
  );
-- Resultado esperado: 0 filas
```

### 7.4 Verificar usuario `app_user`

```sql
-- El usuario app_user debe existir con permisos mínimos:
SELECT usename, usesuper, usecreatedb, usecreaterole
FROM pg_user
WHERE usename = 'app_user';
-- usesuper, usecreatedb, usecreaterole deben ser false

-- Verificar que tiene los permisos necesarios:
SELECT grantee, table_name, privilege_type
FROM information_schema.role_table_grants
WHERE grantee = 'app_user'
  AND table_schema = 'public'
LIMIT 20;
```

### 7.5 Insertar tenant de staging

```sql
-- Si no existe un tenant de staging, insertarlo:
INSERT INTO tenants (id, name, slug, status, created_at, updated_at)
VALUES (
  gen_random_uuid(),
  'ClinicBoost Staging',
  'staging',
  'active',
  NOW(),
  NOW()
)
ON CONFLICT (slug) DO NOTHING;

-- Anotar el tenant_id generado para las pruebas:
SELECT id, name, slug FROM tenants WHERE slug = 'staging';
```

### 7.6 Backup antes de cada deploy

```bash
# En Supabase Dashboard → Database → Backups → Create backup
# O via CLI:
supabase db dump --project-ref $STAGING_PROJECT_REF > backup_$(date +%Y%m%d_%H%M%S).sql
```

---

## 8. Seguridad: cookies, CORS y CSP

### 8.1 Cookies de sesión

Las cookies de autenticación son gestionadas por **Supabase Auth** (en el frontend) y por el API .NET para la cookie de sesión propia.

**Configuración requerida**:
- `Secure=true`: solo en HTTPS ✅ (Cloudflare garantiza HTTPS)
- `SameSite=Strict`: configurado en `Program.cs` para el entorno Staging
- `HttpOnly=true`: la cookie de refresh token no debe ser accesible desde JS

**Verificar en el navegador**:
```
DevTools → Application → Cookies → staging.clinicboost.es
Verificar: Secure ✅, HttpOnly ✅, SameSite: Strict ✅
```

### 8.2 CORS

**Configuración en `appsettings.Staging.json`** (override por ENV):
```json
"Cors": {
  "AllowedOrigins": ["https://staging.clinicboost.es"]
}
```

**Verificar**:
```bash
# Petición con Origin correcto → debe incluir Access-Control-Allow-Origin:
curl -H "Origin: https://staging.clinicboost.es" \
     -I https://staging.clinicboost.es/api/dashboard/summary
# Respuesta esperada: 401 (no autorizado) pero con cabecera CORS

# Petición con Origin no permitido → NO debe incluir la cabecera CORS:
curl -H "Origin: https://malicious.com" \
     -I https://staging.clinicboost.es/api/dashboard/summary
# Respuesta: sin Access-Control-Allow-Origin (CORS bloqueado)
```

### 8.3 CSP (Content Security Policy)

**En staging**: `CSP_REPORT_ONLY=true` (observar sin bloquear).

La política CSP mínima configurada en `CspMiddleware` para staging:
```
default-src 'self';
script-src 'self' 'nonce-{generado}';
style-src 'self' 'nonce-{generado}';
img-src 'self' data: blob:;
font-src 'self';
connect-src 'self' https://*.supabase.co wss://*.supabase.co;
frame-ancestors 'none';
base-uri 'self';
upgrade-insecure-requests;
report-uri /auth/csp-report
```

**Verificar reportes CSP**:
```bash
# Ver si llegan reportes de violaciones CSP:
docker compose -f docker-compose.staging.yml logs api | grep "csp-report"
# Si hay violaciones, revisar y ajustar la política antes de pasar a report-only=false
```

### 8.4 Cabeceras de seguridad en Nginx

El `default.conf` y `spa.conf` añaden estas cabeceras en todas las respuestas:
- `X-Frame-Options: DENY`
- `X-Content-Type-Options: nosniff`
- `Referrer-Policy: strict-origin-when-cross-origin`
- `Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=()`
- `Cross-Origin-Opener-Policy: same-origin`

**Verificar con securityheaders.com**:
```
https://securityheaders.com/?q=https://staging.clinicboost.es
# Objetivo mínimo: grado B (grado A con HSTS en producción)
```

### 8.5 ForwardedHeaders en .NET

`Program.cs` configura `ForwardedHeaders` para aceptar el IP real del cliente a través del proxy Nginx:
```csharp
app.UseForwardedHeaders(new ForwardedHeadersOptions {
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // Solo acepta proxies de la red interna Docker (172.28.0.0/16)
});
```

---

## 9. CI/CD: pipeline de staging automático

### 9.1 Flujo de CD (`.github/workflows/cd-staging.yml`)

```
Push a main
    │
    ▼
1. build-and-test
   · dotnet restore + build + test
   · npm ci + lint + tsc + build (con VITE_* de staging)
    │
    ▼
2. build-images
   · Docker buildx build API (sin push, solo verificación)
   · Docker buildx build Web (sin push, solo verificación)
    │
    ▼
3. deploy (via SSH)
   · git pull origin main en /opt/clinicboost
   · Escribir .env.staging desde secrets de GitHub
   · docker compose build --no-cache
   · docker compose up -d --remove-orphans
   · Esperar hasta 60s que /health/live responda
   · Verificar /health/ready
    │
    ▼
4. smoke-test (desde GitHub Actions runner)
   · GET /health/live → espera {"status":"live"}
   · GET /health/ready → espera {"status":"Healthy"}
   · GET / → espera "ClinicBoost" en el HTML
   · GET /api/dashboard/summary → espera 401
```

### 9.2 Configurar CD desde cero

1. **Generar clave SSH para el servidor**:
   ```bash
   # En local (no en el servidor):
   ssh-keygen -t ed25519 -C "github-actions-staging" -f ~/.ssh/clinicboost_staging
   # Clave pública → añadir al servidor:
   ssh-copy-id -i ~/.ssh/clinicboost_staging.pub deploy@<IP_SERVIDOR>
   # Clave privada → añadir como secret STAGING_SSH_KEY en GitHub
   cat ~/.ssh/clinicboost_staging
   ```

2. **Crear usuario `deploy` en el servidor**:
   ```bash
   sudo adduser deploy --disabled-password
   sudo usermod -aG docker deploy
   sudo mkdir -p /opt/clinicboost
   sudo chown deploy:deploy /opt/clinicboost
   ```

3. **Añadir todos los secrets de GitHub Actions** (sección 3.3)

4. **Verificar el pipeline**:
   ```bash
   git push origin main
   # Ir a GitHub → Actions → CD — Deploy Staging
   ```

---

## 10. Riesgos pendientes antes de piloto real

### 🔴 Críticos (bloquean el piloto)

| # | Riesgo | Acción requerida |
|---|--------|-----------------|
| R1 | **Sin HTTPS propio si no se usa Cloudflare** | Configurar Cloudflare (recomendado) o Let's Encrypt + Certbot en el servidor |
| R2 | **`app_user` puede no existir en el proyecto Supabase Cloud** | Crear el usuario con permisos mínimos antes del primer deploy |
| R3 | **Validación de firma de webhook Twilio no verificada en Voice handler** | Confirmar que `ValidateTwilioSignature` está activo en el endpoint de voz |
| R4 | **Sin tests de integración para DashboardService** | Antes del piloto, ejecutar al menos un smoke test con datos reales del tenant de staging |
| R5 | **Variables VITE_* baked en imagen Docker** | Si cambia la URL de Supabase, hay que reconstruir la imagen; no hay runtime injection |

### 🟡 Importantes (no bloquean pero son riesgo en demo)

| # | Riesgo | Acción requerida |
|---|--------|-----------------|
| R6 | **CSP_REPORT_ONLY=true en staging** | Antes de producción, cambiar a `false` y verificar que no hay violaciones |
| R7 | **Sin HSTS** | Añadir `Strict-Transport-Security` en Nginx para producción |
| R8 | **Sin rotación de tokens de Twilio** | Anotar en backlog: rotar auth_token periódicamente |
| R9 | **Imágenes Docker construidas en el servidor** | Para mayor seguridad, usar un registry (GHCR/Docker Hub) y pull en el servidor |
| R10 | **Sin monitorización ni alertas** | Implementar Uptime Kuma, Grafana Cloud o similar antes del piloto |
| R11 | **Sin backup automático de BD** | Activar backups automáticos en Supabase Dashboard (PITR en plan Pro) |
| R12 | **Logs de API en disco del contenedor** | Configurar log rotation o enviar a un servicio externo (Loki, Datadog) |
| R13 | **Agente conversacional no probado en staging** | Si se usará en la demo, probar flujo completo con el tenant de staging |

### 🟢 Mejoras futuras (post-piloto)

| Mejora | Descripción |
|--------|-------------|
| Docker registry | Push imágenes a GHCR → pull en servidor (evita rebuild en cada deploy) |
| Secrets manager | Migrar de `.env.staging` a HashiCorp Vault o Infisical |
| Multi-tenant isolation test | Test automatizado de RLS cross-tenant |
| Terraform / Pulumi | IaC para provisionar el servidor de staging de forma reproducible |
| Canary deploys | Deploy gradual con Nginx upstream ponderado |
| Dashboard en tiempo real | WebSocket o SSE para actualización sin polling |
| CSV/Excel export | Endpoint `/api/dashboard/export` para el dashboard |

---

## ✅ Veredicto final

### ¿Puede el entorno usarse en una demo con cliente real?

| Criterio | Estado | Notas |
|----------|--------|-------|
| Dominio + HTTPS | ✅ Con Cloudflare | Configurar DNS (sección 4.8) |
| Docker reproducible | ✅ | `make staging-up` desde cero |
| Variables seguras | ✅ | `.env.staging` + GitHub Secrets, fuera del repo |
| Health checks | ✅ | `/health/live` y `/health/ready` implementados |
| Dashboard funcional | ✅ | 5 endpoints + React UI completo |
| Autenticación | ✅ | Supabase Auth + JWT |
| RLS multi-tenant | ✅ | Verificado con 8/8 tests pasados |
| CORS y CSP mínima | ✅ | CSP report-only en staging |
| Cookies seguras | ✅ | Secure + HttpOnly + SameSite=Strict |
| Twilio webhooks | ⚠️ | Requiere configurar URLs en consola Twilio |
| Validación firma Twilio | ⚠️ | Verificar que está activa en Voice handler |
| Migraciones staging | ⚠️ | Aplicar con `make staging-migrate` antes del deploy |
| Datos reales | ⚠️ | Necesita tenant + datos seed en BD de staging |
| CI/CD automático | ✅ | Push a `main` → deploy automático |

### 🟡 Veredicto: **CASI LISTO — 3 acciones antes de la demo**

El entorno es técnicamente sólido y reproducible. Para la demo real:

1. **[R1]** Configurar DNS de `staging.clinicboost.es` apuntando al servidor + Cloudflare proxy activo
2. **[R2]** Crear `app_user` en Supabase Cloud + aplicar migraciones (`make staging-migrate`)
3. **Insertar tenant de staging + datos demo** mínimos (ver sección 7.5)

Una vez hechas estas 3 acciones, el entorno es válido para una demo con cliente real.

---

## Referencia rápida: comandos más usados

```bash
# Primera vez en un servidor limpio:
git clone https://github.com/632015648a-prog/Clinicboostv0.git /opt/clinicboost && cd /opt/clinicboost
cp .env.staging.example .env.staging && nano .env.staging
make staging-migrate      # Migraciones Supabase Cloud
make staging-up           # Levantar stack

# Operaciones diarias:
make staging-ps           # Estado
make staging-health       # Health checks
make staging-logs         # Logs en tiempo real
make staging-restart-api  # Reiniciar API sin rebuild

# Después de cambios de código:
git pull origin main && make staging-build && make staging-up

# Parar todo:
make staging-down
```
