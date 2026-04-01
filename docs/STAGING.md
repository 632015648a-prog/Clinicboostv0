# ClinicBoost — Guía de Staging

> **Objetivo**: levantar un entorno de staging demostrable con dominio, HTTPS, variables
> de entorno seguras y una configuración reproducible, listo para demo con cliente real.

---

## Tabla de contenidos

1. [Arquitectura del stack](#arquitectura)
2. [Prerrequisitos del servidor](#prerrequisitos)
3. [Arrancar staging desde cero](#arrancar-desde-cero)
4. [Variables de entorno](#variables-de-entorno)
5. [Health checks](#health-checks)
6. [Checklist Twilio Voice & WhatsApp Business](#checklist-twilio)
7. [Checklist base de datos y migraciones](#checklist-bd)
8. [Seguridad: cookies, CORS y CSP](#seguridad)
9. [CI/CD — GitHub Actions](#cicd)
10. [Riesgos antes de piloto real](#riesgos)
11. [Veredicto de demo](#veredicto)

---

## Arquitectura

```
[Internet]
    │
    ▼  HTTPS (Cloudflare proxy o certificado propio)
[Nginx :80]  ──────────────────────────────────
    │                                          │
    ├─ /api/*  /auth/*                    ─►  [clinicboost-api :8080]
    ├─ /webhooks/*  /health/*             ─►  (contenedor Docker, .NET 10)
    └─ /*  (SPA React)                         ↕
       └─ archivos estáticos del volumen       │
          web_dist (Vite build)           [Supabase Cloud]
                                               │ PostgreSQL + Auth + RLS
```

- **Nginx** actúa de reverse-proxy y sirve la SPA estática.
- **API (.NET 10)** solo está expuesta en la red interna Docker (puerto 8080).
- **Base de datos**: Supabase Cloud (no se dockeriza la BD).
- **TLS**: recomendado terminar en Cloudflare (modo "Full strict") o en Nginx con Let's Encrypt.

---

## Prerrequisitos

### Servidor (VPS / Cloud VM)

| Requisito | Mínimo staging | Notas |
|-----------|---------------|-------|
| SO | Ubuntu 22.04 LTS | o Debian 12 |
| RAM | 2 GB | 1 GB libre para Docker |
| CPU | 1 vCPU | |
| Disco | 20 GB | |
| Docker | 24+ | `docker --version` |
| Docker Compose | 2.20+ (plugin) | `docker compose version` |
| Git | cualquiera | para `git pull` en deploy |
| Dominio | `staging.clinicboost.es` | DNS apuntando al servidor |

### Script de setup del servidor (primera vez)

```bash
# Ejecutar como root en el servidor
bash infra/scripts/setup-server.sh
```

El script:
1. Instala Docker + Docker Compose (si no están).
2. Crea el usuario `deploy` con permisos Docker.
3. Clona el repositorio en `/opt/clinicboost`.
4. Crea los directorios de logs.

### Cuentas externas necesarias

- **Supabase**: proyecto de staging creado en [supabase.com](https://supabase.com)
- **Twilio**: cuenta con número de teléfono y WhatsApp Business habilitado
- **Cloudflare** (recomendado): dominio con proxy activado para HTTPS automático

---

## Arrancar staging desde cero

### Paso 1 — Clonar el repositorio en el servidor

```bash
ssh deploy@tu-servidor-ip
cd /opt/clinicboost
git clone https://github.com/<org>/ClinicboostV0.git .
# o si ya está clonado:
git pull origin main
```

### Paso 2 — Configurar variables de entorno

```bash
cp .env.staging.example .env.staging
nano .env.staging   # Rellenar TODOS los valores CHANGE_ME
chmod 600 .env.staging   # Solo lectura para el propietario
```

Ver la sección [Variables de entorno](#variables-de-entorno) para descripción de cada variable.

### Paso 3 — Aplicar migraciones de base de datos

```bash
# Instalar supabase CLI si no está (solo la primera vez)
curl -sSL https://supabase.com/install.sh | sh

# Exportar credenciales
export SUPABASE_ACCESS_TOKEN=sbp_XXXX   # Supabase → Account → Access Tokens
export STAGING_PROJECT_REF=XXXX         # Supabase → Settings → General → Reference ID

# Aplicar migraciones
bash infra/scripts/migrate-staging.sh
```

> ⚠️ **Importante**: hacer backup de la BD antes de aplicar migraciones en staging con datos reales.
> Supabase Dashboard → Database → Backups → Create backup

### Paso 4 — Crear directorios de logs

```bash
mkdir -p /opt/clinicboost/logs/nginx /opt/clinicboost/logs/api
```

### Paso 5 — Construir y levantar el stack

```bash
cd /opt/clinicboost
make staging-build   # Construye imágenes Docker (tarda ~5 min la primera vez)
make staging-up      # Levanta los contenedores + verifica health
```

O manualmente:

```bash
docker compose -f docker-compose.staging.yml --env-file .env.staging build
docker compose -f docker-compose.staging.yml --env-file .env.staging up -d --remove-orphans
```

### Paso 6 — Verificar health

```bash
make staging-health
# Espera ver:
#   {"status":"live",...}     → /health/live OK
#   {"status":"Healthy",...}  → /health/ready OK

# También desde fuera del servidor:
curl https://staging.clinicboost.es/health/live
curl https://staging.clinicboost.es/health/ready
```

### Paso 7 — Verificar que el auth protege los endpoints

```bash
# Debe devolver 401 (no 200 ni 500)
curl -o /dev/null -s -w "%{http_code}" https://staging.clinicboost.es/api/dashboard/summary
# → 401
```

### Comandos útiles de operación

```bash
make staging-ps           # Estado de los contenedores
make staging-logs         # Logs en tiempo real (Ctrl+C para salir)
make staging-restart-api  # Reiniciar solo la API sin rebuild
make staging-down         # Parar todo el stack
make staging-shell-api    # Acceder al shell del contenedor API
```

---

## Variables de entorno

Todas las variables van en `.env.staging` (no commitear).

| Variable | Descripción | Dónde obtenerla |
|----------|-------------|-----------------|
| `DB_CONNECTION_STRING` | Cadena Npgsql con SSL=Require. Usar usuario `app_user`, no `postgres` | Supabase → Settings → Database |
| `SUPABASE_URL` | `https://XXXX.supabase.co` | Supabase → Settings → API |
| `SUPABASE_ANON_KEY` | Clave pública anon (JWT) | Supabase → Settings → API |
| `SUPABASE_JWT_SECRET` | Secret para validar JWTs (mín. 32 chars) | Supabase → Settings → API → JWT Settings |
| `VITE_SUPABASE_URL` | Igual que `SUPABASE_URL` (para el frontend) | Mismo |
| `VITE_SUPABASE_ANON_KEY` | Igual que `SUPABASE_ANON_KEY` | Mismo |
| `VITE_API_URL` | URL pública de staging (`https://staging.clinicboost.es`) | Tu dominio |
| `CORS_ORIGIN_0` | Dominio principal del frontend en staging | Tu dominio |
| `TWILIO_ACCOUNT_SID` | `ACxxxxxxxx` | Twilio Console → Account Info |
| `TWILIO_AUTH_TOKEN` | Token de autenticación Twilio | Twilio Console → Account Info |
| `TWILIO_WEBHOOK_BASE_URL` | URL pública de staging para webhooks Twilio | Tu dominio |
| `OPENAI_API_KEY` | Solo si el agente OpenAI está activo | platform.openai.com |
| `ANTHROPIC_API_KEY` | Solo si el agente Claude está activo | console.anthropic.com |
| `CSP_REPORT_ONLY` | `true` en staging para observar sin bloquear | — |

### Variables en GitHub Actions (Secrets)

Configurar en: `GitHub → Settings → Secrets and variables → Actions`

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
STAGING_HOST                    (IP del servidor)
STAGING_SSH_USER                (usuario deploy)
STAGING_SSH_KEY                 (clave privada SSH, formato PEM)
STAGING_DOMAIN                  (https://staging.clinicboost.es)
```

---

## Health checks

### Endpoints disponibles

| Endpoint | Auth | Descripción |
|----------|------|-------------|
| `GET /health/live` | Público | Liveness: la app arrancó y responde |
| `GET /health/ready` | Público | Readiness: BD conectada y lista |
| `GET /health/deps` | JWT requerido | Estado detallado de dependencias externas |

### Respuestas esperadas

```bash
# /health/live — siempre 200 si la app está viva
curl https://staging.clinicboost.es/health/live
# → {"status":"live","ts":"2026-04-01T12:00:00Z"}

# /health/ready — 200 si la BD está conectada, 503 si no
curl https://staging.clinicboost.es/health/ready
# → {"status":"Healthy","totalDuration":"00:00:00.050",
#    "entries":{"postgres":{"status":"Healthy","duration":"00:00:00.045"}}}
```

### Configurar alertas de uptime

- [Better Uptime](https://betteruptime.com) o [UptimeRobot](https://uptimerobot.com): monitorizar `https://staging.clinicboost.es/health/ready` cada 1 min.
- Alerta por email/SMS si el status no es `Healthy`.

---

## Checklist Twilio

### Voice (llamadas perdidas)

- [ ] **Número de teléfono comprado** en Twilio con capacidad Voice.
- [ ] **Webhook `statusCallback`** configurado para llamadas entrantes:
  - URL: `https://staging.clinicboost.es/webhooks/twilio/voice/status`
  - Método: `POST`
  - Eventos: `initiated`, `ringing`, `answered`, `completed`, `busy`, `no-answer`, `failed`
- [ ] **URL de respuesta de voz** (TwiML):
  - URL: `https://staging.clinicboost.es/webhooks/twilio/voice`
  - Método: `POST`
- [ ] **Validación de firma Twilio** activa en el webhook handler (verificar `X-Twilio-Signature`).
- [ ] **Test**: llamar al número → llamada aparece en logs de staging → se dispara el flujo de recovery.

### WhatsApp Business (mensajes)

- [ ] **Sender WhatsApp** aprobado en Twilio Console → Messaging → WhatsApp Senders.
  - Para staging: puede usarse el sandbox de Twilio (`+14155238886`).
  - Para producción: número aprobado por Meta.
- [ ] **Webhook de mensajes entrantes** configurado:
  - URL: `https://staging.clinicboost.es/webhooks/twilio/whatsapp/inbound`
  - Método: `POST`
- [ ] **Webhook de estado de mensajes** configurado:
  - URL: `https://staging.clinicboost.es/webhooks/twilio/whatsapp/status`
  - Método: `POST`
- [ ] **Templates aprobados por Meta** (para mensajes outbound fuera de la ventana de 24h):
  - Template de recovery de cita (texto + botón CTA).
  - Template de confirmación de cita.
  - Estado: `approved` en Twilio Console → Messaging → Content Template Builder.
- [ ] **Variables de entorno** `TWILIO_ACCOUNT_SID`, `TWILIO_AUTH_TOKEN` y `TWILIO_WEBHOOK_BASE_URL` correctas en `.env.staging`.
- [ ] **Test sandbox**: unirse al sandbox de Twilio con el móvil de prueba (`join <palabra>`) y enviar un mensaje → aparece en conversaciones del dashboard.

### Validación de firma Twilio en webhooks

```csharp
// El middleware debe validar X-Twilio-Signature en TODOS los webhooks
// Ver: apps/api/src/ClinicBoost.Api/Features/Webhooks/
// La URL base debe coincidir exactamente con TWILIO_WEBHOOK_BASE_URL + la ruta
```

> ⚠️ Si `TWILIO_WEBHOOK_BASE_URL` no está configurado, la validación de firma falla y todos los webhooks serán rechazados con 403.

---

## Checklist base de datos y migraciones

### Configuración Supabase de staging

- [ ] **Proyecto de staging creado** en Supabase Cloud (separado del de producción).
- [ ] **Usuario `app_user` creado** con permisos mínimos (solo las tablas de negocio, sin acceso a `auth.*`):
  ```sql
  CREATE ROLE app_user WITH LOGIN PASSWORD 'CHANGE_ME';
  GRANT USAGE ON SCHEMA public TO app_user;
  GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO app_user;
  GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_user;
  ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_user;
  ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO app_user;
  ```
- [ ] **RLS activo** en todas las tablas de negocio:
  ```sql
  -- Verificar tablas sin RLS:
  SELECT tablename FROM pg_tables
  WHERE schemaname = 'public' AND rowsecurity = false;
  -- → Debe devolver 0 filas (excepto tablas de solo lectura como spatial_ref_sys)
  ```
- [ ] **Función `current_tenant_id()` disponible** (requerida por las políticas RLS):
  ```sql
  -- Verificar que existe:
  SELECT proname FROM pg_proc WHERE proname = 'current_tenant_id';
  ```
- [ ] **Migraciones aplicadas** (sin errores):
  ```bash
  export SUPABASE_ACCESS_TOKEN=sbp_XXXX
  export STAGING_PROJECT_REF=XXXX
  bash infra/scripts/migrate-staging.sh
  ```
- [ ] **Tablas críticas verificadas** (el script de migración las comprueba automáticamente):
  - `tenants`, `patients`, `appointments`, `conversations`, `messages`
  - `message_delivery_events`, `revenue_events`, `processed_events`
  - `flow_metrics_events`, `message_variants`
- [ ] **Tenant de demo creado** (para las demos con cliente):
  ```sql
  INSERT INTO tenants (name, slug, plan, status)
  VALUES ('Demo Clínica', 'demo-clinica', 'starter', 'active')
  RETURNING id;
  -- Guardar el UUID para crear el usuario admin del tenant
  ```
- [ ] **Pool de conexiones**: en Supabase, activar PgBouncer en modo `transaction` para staging (Settings → Database → Connection pooling).

### Rollback de migraciones

Las migraciones son aditivas (no destructivas) y usan `IF NOT EXISTS`. En caso de error:
1. Identificar la migración fallida en `supabase/migrations/`.
2. Aplicar manualmente el rollback (si está definido en comentarios de la migración).
3. Eliminar el registro de la migración de la tabla `supabase_migrations.schema_migrations`.

---

## Seguridad

### Cookies de sesión

Las sesiones las gestiona **Supabase Auth** (JWT en `localStorage` por defecto en el SDK de Supabase JS). Para staging:

- [ ] **HTTPS obligatorio**: sin HTTPS, las cookies Secure no funcionan.
- [ ] En producción real, migrar a **cookie httpOnly + SameSite=Strict** usando el [SSR helper de Supabase](https://supabase.com/docs/guides/auth/server-side/creating-a-client).
- [ ] El **JWT Secret** debe tener al menos 32 caracteres y rotarse si se compromete.

### CORS

Configurado en `appsettings.Staging.json` y sobrescrito por variables de entorno:

```json
{
  "Cors": {
    "AllowedOrigins": ["https://staging.clinicboost.es"]
  }
}
```

- [ ] `CORS_ORIGIN_0` apunta al dominio exacto de staging (con `https://`).
- [ ] No se usa wildcard `*` — se requiere `withCredentials: true`.
- [ ] Preflight máximo 10 minutos (`Access-Control-Max-Age: 600`).

### CSP — Content Security Policy

En staging, la CSP está en **modo report-only** (`CSP_REPORT_ONLY=true`):
- Los navegadores reportan violaciones pero **no bloquean** nada.
- Los reportes llegan a `/auth/csp-report` (revisar logs de la API).
- Cuando no haya más violaciones legítimas, cambiar a `CSP_REPORT_ONLY=false`.

**CSP actual (nginx/default.conf)**:
```
default-src 'self';
script-src 'self' 'unsafe-inline';
style-src 'self' 'unsafe-inline';
img-src 'self' data: blob:;
font-src 'self';
connect-src 'self' https://*.supabase.co wss://*.supabase.co;
frame-ancestors 'none';
base-uri 'self';
upgrade-insecure-requests
```

> ⚠️ `'unsafe-inline'` en `script-src` es necesario mientras Vite no genere nonces. Para producción: migrar a nonces o hashes.

### Cabeceras de seguridad (verificar con securityheaders.com)

| Cabecera | Valor |
|----------|-------|
| `X-Frame-Options` | `DENY` |
| `X-Content-Type-Options` | `nosniff` |
| `Referrer-Policy` | `strict-origin-when-cross-origin` |
| `Permissions-Policy` | camera=(), microphone=(), geolocation=(), payment=() |
| `Cross-Origin-Opener-Policy` | `same-origin` |
| `HSTS` | `max-age=31536000; includeSubDomains; preload` (solo HTTPS) |

Verificar en: `https://securityheaders.com/?q=https://staging.clinicboost.es`

---

## CI/CD — GitHub Actions

### Workflows activos

| Workflow | Trigger | Descripción |
|----------|---------|-------------|
| `ci-api.yml` | Push/PR a `main`/`develop` en `apps/api/**` | Build + test API .NET |
| `ci-web.yml` | Push/PR a `main`/`develop` en `apps/web/**` | Lint + typecheck + build frontend |
| `cd-staging.yml` | Push a `main` | Build + test + deploy a staging vía SSH |

### Flujo del CD de staging

```
push main
  │
  ▼
[1] build-and-test  ── .NET build + tests
  │                 ── Frontend lint + typecheck + build
  │
  ▼
[2] build-images    ── Build Docker images (sin push, solo verificación)
  │
  ▼
[3] deploy          ── SSH al servidor
  │                    git pull + docker compose build + up -d
  │
  ▼
[4] smoke-test      ── /health/live, /health/ready, frontend, auth 401
```

### Configurar el entorno de staging en GitHub

1. Ir a `Settings → Environments → New environment → staging`.
2. Añadir todos los secrets de la tabla de [Variables en GitHub Actions](#variables-en-github-actions-secrets).
3. (Opcional) Configurar **required reviewers** para aprobar deploys manuales.

### Trigger manual de deploy

```bash
# Desde la UI de GitHub:
# Actions → CD — Deploy Staging → Run workflow → main

# O vía CLI:
gh workflow run cd-staging.yml --ref main
```

---

## Riesgos antes de piloto real

### 🔴 Bloqueantes (deben resolverse antes de cualquier demo)

| # | Riesgo | Mitigación |
|---|--------|-----------|
| R1 | **Conflicto con `current_tenant_id()`**: la función puede no existir en el proyecto de staging si las migraciones tienen el error identificado | Verificar que la función existe en Supabase Studio → SQL Editor. Si no, ejecutar la migración manualmente. |
| R2 | **RLS sin `current_tenant_id()`**: sin esta función, las políticas RLS fallan y las queries devuelven 0 rows o 500 | Corregir la migración y re-aplicar antes de cualquier prueba con datos reales. |
| R3 | **JWT Secret vacío o incorrecto**: si `SUPABASE_JWT_SECRET` no coincide con el del proyecto Supabase, todos los JWTs serán rechazados con 401 | Copiar el secret exacto desde Supabase Dashboard → Settings → API → JWT Settings. |
| R4 | **Twilio Webhook URL incorrecta**: si `TWILIO_WEBHOOK_BASE_URL` no apunta al dominio público, los webhooks no llegan | Configurar HTTPS en el servidor antes de registrar los webhooks en Twilio. |

### 🟡 Importantes (no bloqueantes para demo interna, sí para piloto con cliente)

| # | Riesgo | Mitigación |
|---|--------|-----------|
| R5 | **WhatsApp templates no aprobados**: los mensajes outbound fuera de 24h requieren template aprobado por Meta (proceso de 1-7 días) | Iniciar aprobación con antelación. Para demo, usar el sandbox de Twilio. |
| R6 | **Sin backup automatizado de BD staging**: si se corrompen los datos de demo, no hay punto de restauración | Activar backups automáticos en Supabase (plan gratuito incluye 7 días). |
| R7 | **CSP report-only**: la CSP no bloquea aún, posible XSS si hay una vulnerabilidad | Revisar los reportes CSP antes de pasar a producción. |
| R8 | **Sin rate limiting en API .NET**: Nginx tiene rate limiting básico pero la API no tiene throttling propio | Añadir `Microsoft.AspNetCore.RateLimiting` antes del piloto. |
| R9 | **Logs sin alertas**: los errores en la API solo son visibles revisando logs manualmente | Configurar alertas en [Sentry](https://sentry.io) o [Seq](https://datalust.co/seq) para errores de nivel Error/Fatal. |
| R10 | **Datos de demo en la BD de staging**: si se usan datos de pacientes reales en staging, requiere acuerdo de tratamiento RGPD | Usar solo datos ficticios (seed de demo) o datos anonimizados en staging. |

### 🟢 Mejoras futuras (no críticas para staging)

- [ ] Migrar autenticación a cookies httpOnly (en lugar de localStorage).
- [ ] Añadir rate limiting en la API .NET (`IpRateLimiter`).
- [ ] Integrar Sentry para error tracking en frontend y backend.
- [ ] Implementar rotation automática del JWT Secret.
- [ ] Añadir tests de integración E2E con Playwright.
- [ ] Pasar CSP de report-only a enforced con nonces.

---

## Veredicto de demo

### ¿Puede usarse en una demo con cliente real?

**SÍ**, con las siguientes condiciones:

| Condición | Estado |
|-----------|--------|
| Infraestructura Docker reproducible | ✅ Implementado |
| Health checks `/live` y `/ready` | ✅ Implementado |
| Variables de entorno documentadas | ✅ Implementado |
| CI/CD automático (push → staging) | ✅ Implementado |
| Cabeceras de seguridad básicas | ✅ Implementado |
| CORS restringido al dominio de staging | ✅ Implementado |
| HTTPS (via Cloudflare o Let's Encrypt) | ⚠️ Requiere configurar en el servidor |
| Migraciones aplicadas correctamente | ⚠️ Requiere resolver R1/R2 |
| Twilio webhooks validados | ⚠️ Requiere configurar en Twilio Console |
| Datos de demo (no reales) | ⚠️ Crear seed de demo antes de la demo |

**Para una demo interna de 2-4 horas**: el stack está listo una vez resueltos R1-R4.

**Para un piloto con cliente real**: resolver adicionalmente R5-R10 antes de exponer datos de pacientes reales.

---

*Última actualización: 2026-04-01*
