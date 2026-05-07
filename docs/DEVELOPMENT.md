# Guía de desarrollo local — ClinicBoost

## Prerequisitos

| Herramienta | Versión mínima | Instalación |
|-------------|----------------|-------------|
| .NET SDK    | 10.0           | https://dot.net |
| Node.js     | 20 LTS         | https://nodejs.org |
| Docker      | 24+            | https://docker.com |
| Supabase CLI| 1.x            | `npm i -g supabase` |
| Git         | 2.40+          | — |

---

## Arranque en local (paso a paso)

### 1. Clonar y preparar entorno

```bash
git clone https://github.com/tu-org/clinicboost.git
cd clinicboost
```

### 2. Supabase local

```bash
# Arrancar Supabase en Docker
supabase start

# Aplicar migraciones
supabase db push

# Cargar seed de desarrollo
supabase db reset   # aplica migraciones + seed automáticamente
```

> URLs locales tras `supabase start`:
> - API: http://localhost:54321
> - Studio: http://localhost:54323
> - DB: postgresql://postgres:postgres@localhost:54322/postgres

Tras `supabase start`, ejecuta **`supabase status`** y copia el **JWT Secret** (firma de los tokens de Auth). La API (.NET) debe usar **el mismo valor** en `Supabase:JwtSecret` (`appsettings.Development.Local.json` o variable `SUPABASE__JWTSECRET`). Si dejas solo el valor de ejemplo de `appsettings.Development.json`, las peticiones autenticadas fallarán con **401** aunque el usuario y `tenant_id` sean correctos.

**Postgres + EF Core + webhooks Twilio:** errores típicicos (`42703`, `42804`, `22P02`), revisión de columnas `jsonb` y el KPI “citas recuperadas” vs datos de seed están documentados en [`docs/context/POSTGRES_EF_TWILIO_GOTCHAS.md`](context/POSTGRES_EF_TWILIO_GOTCHAS.md).

### 3. Backend .NET

```bash
cd apps/api

# Variables de entorno (NO subir a git)
cp src/ClinicBoost.Api/appsettings.Development.Local.json.example \
   src/ClinicBoost.Api/appsettings.Development.Local.json
# Editar appsettings.Development.Local.json: como mínimo Supabase:JwtSecret = JWT Secret de `supabase status`

# Compilar y arrancar
dotnet build
dotnet run --project src/ClinicBoost.Api

# API disponible en: http://localhost:5011
# Scalar UI:         http://localhost:5011/scalar
# Health live:       http://localhost:5011/health/live
# Health ready:      http://localhost:5011/health/ready
```

### 3.1 Prueba rápida: Flow03 (recordatorio cita) en local

- **Objetivo**: disparar Flow03 en minutos y validar que el reply del paciente queda en la misma conversación (`flow_03`).
- **Configuración** (en `appsettings.Development.Local.json`, NO versionado):
  - `Flow03Options.PollIntervalMinutes = 1`
  - `Flow03Options.DefaultReminderHoursBeforeAppointment = 0`
- **Datos**:
  - el paciente debe tener `rgpd_consent = true` (si no, se hará “skip”).
  - crear una cita para dentro de 3 minutos.
- **Cooldown**:
  - durante pruebas, `rule_configs(flow_03, cooldown_minutes)` puede ponerse a `0` (regla activa) para permitir repetir recordatorios.
- **Verificación**:
  - en BD: `messages` outbound con `provider_message_id` (TwilioSid) y `status=sent/delivered`.
  - en BD: inbound reply “OK” debe caer en `conversations.flow_id = 'flow_03'` (no en `flow_00`).

### 4. Frontend React

```bash
cd apps/web

# Variables de entorno
cp .env.local.example .env.local
# Editar .env.local:
#   VITE_SUPABASE_URL=http://localhost:54321
#   VITE_SUPABASE_ANON_KEY=<anon key de supabase start>
#   VITE_API_URL=http://localhost:5011

npm install
npm run dev
# App en: http://localhost:5173
```

---

## Comandos útiles

```bash
# Tests backend
cd apps/api && dotnet test

# Build de producción frontend
cd apps/web && npm run build

# Lint frontend
cd apps/web && npm run lint

# Nueva migración Supabase
supabase migration new <nombre_descriptivo>

# Ver estado de migraciones
supabase migration list
```

---

## Convenciones de código

### Backend (.NET)
- Un archivo por slice (command + handler + endpoint)
- Nombres: `<Acción><Entidad>.cs` (ej: `CreateAppointment.cs`)
- Sin MediatR, sin AutoMapper, sin repositorios genéricos
- Siempre pasar `CancellationToken ct` en métodos async
- `TenantId` siempre desde `HttpContext.Items["TenantId"]`

### Frontend (React)
- Componentes en PascalCase
- Hooks custom en `src/hooks/use*.ts`
- Tipos compartidos en `src/types/`
- Todas las llamadas a la API via `src/lib/api.ts`

### SQL
- snake_case en nombres de tablas y columnas
- Siempre `tenant_id` en tablas de negocio
- Siempre `created_at` y `updated_at` en UTC
- Índices nombrados: `idx_<tabla>_<columna(s)>`
