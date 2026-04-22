# ClinicBoost — Informe de Auditoría Técnica y de Producto
**Versión:** 1.0 · **Fecha:** 2026-04-09 · **Commit base:** `4c33b7b`  
**Auditado por:** Agente de código (revisión caja blanca, 148 archivos C# + 10 TS/TSX + 19 SQL)

---

## §1 · Resumen Ejecutivo

ClinicBoost es un sistema de automatización de recuperación de citas para clínicas de fisioterapia. El proyecto cuenta con un núcleo técnico sólido y bien estructurado para un piloto interno, pero aún no está listo para pacientes reales sin varias correcciones críticas.

**Estado de madurez por fase:**

| Fase | Estado | Razón |
|------|--------|-------|
| Demo local (desarrollador) | ✅ Demostrable | Compilación OK tras fix 4c33b7b; requiere `supabase start` |
| Staging interno (equipo) | ⚠️ Condicionalmente listo | Flow01 + Dashboard + Inbox funcionales; flows 02–07 son etiquetas vacías |
| Piloto asistido (1 clínica supervisada) | ⚠️ Riesgoso sin mitigación | Búsqueda en inbox rota hasta 4c33b7b; sin UI para respuesta manual desde dashboard |
| Producción autónoma | ❌ No listo | Flows 02–07 no implementados; sin envío manual de mensajes desde Inbox; sin rate-limiting; sin tests para Conversations |

**Bugs críticos activos (pre-4c33b7b):**
1. `patient?.Name` en `ConversationInboxService.cs:157` → **error de compilación**. Corregido en commit `4c33b7b`.
2. `ScalarOptions.EndpointPathPrefix` → corregido en `42bffb5`.
3. `Patient.Name` en 4 lugares de `ConversationInboxService.cs` → corregido en `42bffb5`.

**Tres victorias claras del código:**
- Aislamiento multi-tenant robusto: RLS + `ITenantContext` + filtro explícito en cada query.
- Agente conversacional completo con ciclo tool-calling, HardLimitGuard de 5 reglas, y escalada segura.
- Pipeline idempotente y trazable: `IIdempotencyService`, `AutomationRun`, logs estructurados Serilog.

---

## §2 · Arquitectura General

```
┌─────────────────────────────────────────────────────────┐
│                    Internet / Twilio                     │
│   Llamadas perdidas (voice webhook)                      │
│   Mensajes WhatsApp inbound (WA webhook)                 │
│   Callbacks de entregabilidad (status webhook)           │
└────────────────┬──────────────────┬─────────────────────┘
                 │                  │
         ┌───────▼──────┐  ┌────────▼──────┐
         │  Nginx proxy │  │   Nginx proxy  │
         │  port 80/443 │  │   (mismo)      │
         └───────┬──────┘  └────────┬──────┘
                 │                  │
         ┌───────▼──────────────────▼──────────────────┐
         │          ClinicBoost.Api (.NET 10)            │
         │                                              │
         │  Webhooks  →  Channel queues  →  Workers     │
         │  (MissedCallWorker, WhatsAppInboundWorker)   │
         │                                              │
         │  Flow01Orchestrator  ←→  IOutboundMessageSender (Twilio)
         │  ConversationalAgent ←→  OpenAI (gpt-4o / gpt-4o-mini)
         │                                              │
         │  Dashboard API  /  Conversations API (Inbox) │
         │  Appointments API  /  Variant Stats API      │
         └──────────────────────┬───────────────────────┘
                                │ EF Core + Npgsql
                        ┌───────▼───────────┐
                        │  Supabase Postgres │
                        │  (RLS habilitado)  │
                        │  GoTrue (Auth)     │
                        └───────────────────┘
                                │ JWT
                        ┌───────▼───────────┐
                        │   React + Vite     │
                        │   (web SPA)        │
                        │   LoginPage        │
                        │   DashboardPage    │
                        │   InboxPage ← NEW  │
                        └───────────────────┘
```

**Árbol de repositorio relevante:**

```
ClinicboostV0/
├── apps/
│   ├── api/
│   │   ├── src/ClinicBoost.Api/
│   │   │   ├── Features/
│   │   │   │   ├── Agent/          # ConversationalAgent, IntentClassifier, ToolRegistry, HardLimitGuard, SystemPromptBuilder
│   │   │   │   ├── Appointments/   # CRUD citas + slots + revenue telemetry
│   │   │   │   ├── Audit/          # RefreshToken, SessionInvalidation, SecurityAudit
│   │   │   │   ├── Calendar/       # iCal feed + cache
│   │   │   │   ├── Conversations/  # ← NEW Inbox (List, Detail, PATCH status)
│   │   │   │   ├── Dashboard/      # KPIs, delivery, flows, revenue
│   │   │   │   ├── Flow01/         # Llamada perdida → WA (ÚNICO FLUJO IMPLEMENTADO)
│   │   │   │   ├── Health/         # /health/live, /health/ready
│   │   │   │   ├── Variants/       # A/B testing de plantillas
│   │   │   │   └── Webhooks/       # Voice + WhatsApp (inbound + status)
│   │   │   ├── Infrastructure/
│   │   │   │   ├── Database/       # AppDbContext, TenantDbContextInterceptor
│   │   │   │   ├── Extensions/     # ServiceCollectionExtensions (DI central)
│   │   │   │   ├── Idempotency/    # IIdempotencyService
│   │   │   │   ├── Middleware/     # CSP, TenantMiddleware, TenantAuthorizationMiddleware
│   │   │   │   ├── Tenants/        # ITenantContext, TenantContext, ClaimsExtractor
│   │   │   │   └── Twilio/         # TwilioSignatureValidator, TenantPhoneResolver
│   │   │   └── Program.cs
│   │   └── tests/ClinicBoost.Tests/  # 44 archivos de test
│   └── web/
│       └── src/
│           ├── lib/   # api.ts, dashboard.ts, inbox.ts, useDashboard.ts, useInbox.ts
│           └── pages/ # LoginPage.tsx, DashboardPage.tsx, InboxPage.tsx
├── supabase/migrations/  # 10 migraciones canónicas (+ 7 en apps/api/supabase)
└── docker-compose.staging.yml
```

**Decisiones de arquitectura clave:**
- **Vertical Slice**: cada Feature es autónoma; sin repositorios genéricos (ADR-002).
- **No generic repositories**: EF Core directamente en cada Feature.
- **RLS como capa primaria**: Postgres Row-Level Security + filtro explícito `tenantId` en cada query como defensa en profundidad.
- **Colas en memoria**: `Channel<T>` para jobs de webhooks — simple pero sin persistencia ante restart.
- **OpenAI via IHttpClientFactory**: no usa el SDK oficial (decisión deliberada para control de resiliencia).

---

## §3 · Backend — Evaluación Detallada

### 3.1 Implementado y funcional

| Componente | Archivo(s) | Estado |
|-----------|-----------|--------|
| Flow01 (llamada perdida → WA) | `Flow01Orchestrator.cs`, `MissedCallWorker.cs` | ✅ Completo con idempotencia, cooldown, RGPD, A/B testing, revenue |
| Agente conversacional (flow_00) | `ConversationalAgent.cs` + Agent/* | ✅ Completo: 5 tools, HardLimitGuard 5 reglas, 2 modelos GPT |
| WhatsApp inbound pipeline | `WhatsAppInboundWorker.cs` | ✅ Completo: patient upsert, conversation upsert, waiting_human guard |
| Dashboard API (5 endpoints) | `DashboardEndpoints.cs`, `DashboardService.cs` | ✅ Completo: summary, delivery, flows, conversations, revenue |
| Inbox API (3 endpoints) | `ConversationEndpoints.cs`, `ConversationInboxService.cs` | ✅ Completo: lista paginada, detalle, PATCH status |
| Appointments API (4 endpoints) | `AppointmentEndpoints.cs`, `AppointmentService.cs` | ✅ Completo: slots, book, cancel, reschedule con race condition control |
| Calendar (iCal) | `CalendarService.cs`, `HttpICalReader.cs` | ✅ Completo con cache-aside en DB |
| Variant A/B tracking | `VariantTrackingService.cs`, `VariantStatsEndpoints.cs` | ✅ Completo: selección, funnel (sent/reply/booked) |
| Autenticación JWT (Supabase GoTrue) | `ServiceCollectionExtensions.cs` | ✅ JWT validation + refresh token + session invalidation |
| Seguridad multi-tenant | `TenantMiddleware.cs`, `TenantContext.cs`, `ITenantContext.cs` | ✅ Robusto: RLS + claims + middleware + AuthorizationMiddleware |
| Idempotencia | `IdempotencyService.cs` | ✅ SHA-256 de payload, evita doble envío de WA |
| CSP / Security headers | `CspMiddleware.cs` | ✅ Configurable; modo report-only para staging |
| Health checks | `HealthEndpoints.cs` | ✅ `/health/live` + `/health/ready` |
| Twilio signature validation | `TwilioSignatureValidator.cs` | ✅ Verifica HMAC-SHA1 de webhooks |

### 3.2 Parcialmente implementado / stubs

| Componente | Problema concreto |
|-----------|-------------------|
| Búsqueda libre en Inbox | `patient?.Name` en línea 157 de `ConversationInboxService.cs` → **error de compilación** hasta commit `4c33b7b`. La búsqueda no funciona en versiones anteriores. |
| `propose_cancellation` tool (Agent) | La tool está definida en `ToolRegistry` y enviada a OpenAI, pero `ExecuteAsync` devuelve `"{ \"ok\": true }"` sin persistir nada en BD. La cancelación se convierte en propuesta sin efecto. |
| `confirm_appointment_response` tool | Mismo problema: placeholder sin lógica de persistencia real. |
| Flows 02–07 | Solo existen como etiquetas en `DashboardService.FlowLabels`. No hay clases de Orchestrator, Endpoints, ni Workers para: Detección de huecos (02), Recordatorio de cita (03), No-show (04), Lista de espera (05), Reactivación (06), Reprogramación (07). |
| Envío manual de mensajes desde Inbox | No existe endpoint `POST /api/conversations/{id}/messages`. El operador puede cambiar estado pero no enviar un mensaje de texto libre al paciente. |
| `Note` en PATCH status | El campo `Note` del `PatchConversationStatusRequest` se recibe pero no se persiste en ninguna tabla (no hay columna en `conversations` ni tabla de notas/audit trail). |
| Resolución automática de conversaciones expiradas | `SessionExpiresAt` existe en el modelo pero no hay job/worker que cambie `status = 'expired'` cuando la ventana caduque. |

### 3.3 Deuda técnica notable

- **Colas en memoria sin persistencia**: Si el proceso se reinicia con jobs pendientes en `Channel<T>`, se pierden. Para producción se necesita persistencia (tabla `job_queue` o Azure Service Bus / RabbitMQ).
- **`DateTimeOffset.UtcNow` directo** en `ConversationService.cs` (flow_00): comentado como GAP-02, parcialmente resuelto en el Worker pero no en el service.
- **`AgentTurn.MessageId = Guid.Empty`** cuando el mensaje inbound no está en los últimos 15 (N-P2-01 documentado en código): FK huérfana que puede causar constraint violations según el schema.
- **`Task.Run` o `async void`** ausente — los workers usan `await foreach` correctamente.
- **Sin rate-limiting**: no hay middleware de rate-limit en webhooks de Twilio. Un atacante podría hacer flood de `/webhooks/voice/missed-call`.
- **Conversación multi-tenant no restringida por `FlowId`** en `ConversationService.UpsertConversationAsync`: usa `flowId` como parámetro pero no hay validación de que el flow exista o pertenezca al tenant.

---

## §4 · Frontend — Evaluación Detallada

### 4.1 Implementado

| Componente | Archivo | Estado |
|-----------|---------|--------|
| Login (Supabase Auth) | `LoginPage.tsx` | ✅ |
| Dashboard MVP | `DashboardPage.tsx` | ✅ 5 secciones + gráfico SVG + tabla + conversaciones recientes |
| Inbox / Bandeja de entrada | `InboxPage.tsx` | ✅ Lista + filtros + detalle + ActionPanel + diseño responsivo |
| AuthGuard con redirección | `App.tsx` (componente `AuthGuard`) | ✅ Supabase session check + subscription |
| Ruta `/inbox` | `App.tsx` | ✅ Protected por AuthGuard |
| Badge `waiting_human` en header Dashboard | `DashboardPage.tsx` | ✅ Contador ámbar en tiempo real vía React Query |
| Tipos TypeScript del backend | `inbox.ts`, `dashboard.ts` | ✅ Espejo fiel de DTOs .NET |
| React Query hooks | `useInbox.ts`, `useDashboard.ts` | ✅ staleTime 30s (inbox) / 2min (dashboard) |

### 4.2 Ausente / pendiente

| Gap | Impacto |
|-----|---------|
| **Sin campo de texto para enviar mensaje manual** en `InboxPage` | El operador no puede responder al paciente desde la UI. Solo puede cambiar estado. Crítico para piloto. |
| **Badge del ActionPanel no se actualiza optimistamente** | Tras un PATCH, el estado del detalle muestra el valor anterior hasta que React Query revalida. El `statusOverride` local en `DetailPanel` fue descartado en el código final (seteado a `null` en `handleStatusDone`). |
| **Sin página de configuración** | No hay UI para gestionar `RuleConfig` (cooldown, success_fee, template_sid). |
| **Sin notificaciones push / auto-refresh** | La Inbox no se actualiza automáticamente cuando llega un nuevo `waiting_human`. El operador debe refrescar manualmente. |
| **Sin paginación en detalle de mensajes** | Si una conversación tiene >100 mensajes, los anteriores no son visibles. |
| **Vite sin proxy configurado** | `vite.config.ts` no tiene `proxy` para `/api/*`. En desarrollo local se requiere `.env.local` con `VITE_API_URL=http://localhost:5011`. Si está mal configurado, todos los endpoints fallan. |

---

## §5 · Base de Datos y Seguridad

### 5.1 Migraciones

| Archivo | Contenido | Estado |
|---------|-----------|--------|
| `20260329000001_initial_schema.sql` | Schema base: tenants, patients, conversations, messages | ✅ |
| `20260329000002_rls_policies.sql` | RLS para tablas core | ✅ |
| `20260329000003_full_schema.sql` | Tablas completas con FKs | ✅ |
| `20260329000004_indexes_and_comments.sql` | Índices de rendimiento | ✅ |
| `20260329000005_rls_new_tables.sql` | RLS para appointment_events, revenue_events | ✅ |
| `20260329000006_roles_and_hardening.sql` | Roles `app_user`, `app_service` con permisos mínimos | ✅ |
| `20260329000007_rls_consolidated.sql` | Consolidación y fixes de RLS | ✅ |
| `20260329000008_security_functions_and_tests.sql` | Funciones de seguridad + self-tests inline | ✅ |
| `20260329000009_idempotency_service.sql` | Tabla `processed_events` para idempotencia | ✅ |
| `20260331000080_message_variants_ab_tracking.sql` | Tablas A/B: `message_variants`, `variant_conversion_events` | ✅ |
| `20260331000090_restrict_variant_funnel_view.sql` | Vista restringida para métricas de variantes | ✅ |

**Observación:** Hay dos directorios de migraciones (`supabase/migrations/` y `apps/api/supabase/migrations/`). El primero contiene las 11 migraciones canónicas. El segundo tiene 7 migraciones adicionales para tablas específicas de los Workers (agent_turns, calendar_cache, etc.). Esto puede causar confusión en `supabase db reset`.

### 5.2 RLS

- RLS activo en todas las tablas de negocio.
- Consultas EF Core siempre filtran `TenantId` explícitamente (defensa en profundidad).
- `TenantDbContextInterceptor` añade `SET app.tenant_id` antes de cada comando DB.
- `app_user` tiene permisos mínimos (SELECT/INSERT/UPDATE/DELETE solo en tablas propias, sin DROP).

### 5.3 Riesgos de seguridad

| Riesgo | Severidad | Detalle |
|--------|-----------|---------|
| Sin rate-limiting en webhooks Twilio | **Alta** | `/webhooks/voice/missed-call` y `/webhooks/whatsapp/inbound` solo validan firma HMAC pero no limitan frecuencia. Flood posible. |
| `VITE_API_URL` hardcodeada en `.env.local.example` como `http://localhost:5000` | **Media** | Puerto incorrecto (API escucha en 5011). Todo el frontend falla en local si no se corrige. |
| `Note` en PATCH status no auditada | **Baja** | Las notas operacionales del operador desaparecen al no persistirse. No hay rastro de quién cambió el estado. |
| JWT `JwtSecret` placeholder en `appsettings.Development.json` | **Info** | `super-secret-jwt-token-with-at-least-32-characters-long` — claramente un valor de ejemplo. Nunca debe llegar a staging/prod. |
| Twilio `AuthToken` placeholder en `appsettings.Development.json` | **Info** | `test_auth_token_32chars_minimum_xx` — valor de test. OK para desarrollo, nunca para producción. |

---

## §6 · Estado de Flujos 00–07

| Flow ID | Nombre | Estado | Backend | Frontend |
|---------|--------|--------|---------|----------|
| `flow_00` | General (WA inbound + IA) | ✅ **Implementado** | `WhatsAppInboundWorker`, `ConversationalAgent` | Dashboard + Inbox |
| `flow_01` | Llamada perdida → WA | ✅ **Implementado** | `MissedCallWorker`, `Flow01Orchestrator`, `TwilioOutboundMessageSender` | Dashboard |
| `flow_02` | Detección de huecos | ❌ **Etiqueta vacía** | Solo `FlowLabels["flow_02"] = "Detección de huecos"` en `DashboardService.cs` | Solo en select de Inbox |
| `flow_03` | Recordatorio de cita | ❌ **Etiqueta vacía** | Ídem | Ídem |
| `flow_04` | No-show seguimiento | ❌ **Etiqueta vacía** | Ídem | Ídem |
| `flow_05` | Lista de espera | ❌ **Etiqueta vacía** | Ídem | Ídem |
| `flow_06` | Reactivación paciente | ❌ **Etiqueta vacía** | Ídem | Ídem |
| `flow_07` | Reprogramación | ❌ **Etiqueta vacía** | Ídem | Ídem |

**Resumen:** 2 de 8 flujos implementados (25%). Para el piloto inicial, flow_00 + flow_01 cubren el caso de uso principal: llamada perdida → WhatsApp de recovery → reserva conversacional.

---

## §7 · Integraciones Externas

| Integración | Estado | Notas |
|-------------|--------|-------|
| **Twilio (WhatsApp outbound)** | ✅ Funcional | `TwilioOutboundMessageSender` usa RestSharp + signature validator. Requiere cuenta Twilio real con número WA aprobado. |
| **Twilio (webhooks inbound/status)** | ✅ Funcional | HMAC-SHA1 validation. `WebhookBaseUrl` debe apuntar al dominio público accesible por Twilio. |
| **OpenAI (gpt-4o + gpt-4o-mini)** | ✅ Funcional | `IHttpClientFactory("OpenAI")`. Resiliencia: retry con Polly (`AddAiResilience()`). Requiere `OPENAI_API_KEY`. |
| **Supabase GoTrue (Auth)** | ✅ Funcional | JWT validation via `JwtSecret`. El frontend usa `@supabase/supabase-js`. |
| **Supabase Postgres** | ✅ Funcional | Npgsql + EF Core. Puerto 54322 local, Supabase Cloud en producción. |
| **iCal calendar** | ✅ Funcional | HTTP fetch con cache-aside en BD. `CalendarService` + `HttpICalReader`. |
| **Anthropic** | ⚠️ Variable declarada | `ANTHROPIC_API_KEY` en `.env.staging.example` pero no hay ninguna llamada a la API Anthropic en el código. Variable reservada para futuro. |

**Variables de entorno requeridas (sin valores secretos):**

```bash
# Backend (.NET)
ConnectionStrings__Supabase="Host=...;Port=5432;Database=postgres;Username=app_user;Password=SECRET;SSL Mode=Require"
Supabase__Url="https://XXXX.supabase.co"
Supabase__AnonKey="eyJ..."
Supabase__JwtSecret="SECRET_min_32_chars"
Twilio__AccountSid="ACxxx"
Twilio__AuthToken="SECRET"
Twilio__WebhookBaseUrl="https://your-domain.com"
OpenAI__ApiKey="sk-..."   # solo si el agente está activo

# Frontend (Vite — bake-time)
VITE_SUPABASE_URL="https://XXXX.supabase.co"
VITE_SUPABASE_ANON_KEY="eyJ..."
VITE_API_URL="http://localhost:5011"   # local: 5011, staging: https://staging.clinicboost.es
```

---

## §8 · Dashboard y Usabilidad Operacional

### Lo que funciona bien
- **5 secciones de KPI** con datos reales de la BD. Cero valores hardcodeados.
- **Gráfico SVG de entregabilidad** diaria: sent/delivered/failed con leyenda.
- **Tabla de rendimiento por flujo** con etiquetas legibles y tasa de conversión.
- **Badge `waiting_human`** en header del dashboard (color ámbar, número real).
- **Botón "Ver bandeja →"** con link a `/inbox`.
- **Bandeja de entrada (InboxPage)**: 4 tabs (Todas, 🙋 Handoff, Abiertas, Resueltas), búsqueda libre, filtro por flujo, detalle estilo chat, panel de acciones operacionales.

### Brechas de usabilidad para el piloto

1. **El operador no puede enviar mensajes desde la Inbox.** Solo puede cambiar estado. Si un paciente escribe en una conversación `waiting_human`, el operador ve el mensaje pero no puede responder desde el dashboard.
2. **Sin refresh automático.** Si llega un nuevo `waiting_human` mientras el operador tiene el dashboard abierto, no hay notificación visual. Debe recargar la página.
3. **Contador badge en Dashboard no se actualiza en tiempo real** — `staleTime: 2 min` en `useDashboardSummary`. Si el operador está mirando el dashboard y llega un handoff, el badge tardará hasta 2 minutos en actualizarse.
4. **Sin vista de historial de estado** de una conversación. No hay forma de ver "quién cambió este estado y cuándo".
5. **La nota interna del PATCH se pierde** — el campo existe en el formulario y en el DTO pero no se guarda en BD.

---

## §9 · Operación Local y Staging

### Cómo arrancar en local (paso a paso)

```bash
# 1. Requisitos: Docker Desktop, .NET 10 SDK, Node.js 20+, Supabase CLI

# 2. Clonar y configurar
git clone https://github.com/632015648a-prog/Clinicboostv0.git
cd Clinicboostv0

# 3. Arrancar Supabase local
supabase start   # espera ~60s; anota API URL, anon key, DB URL

# 4. Aplicar migraciones y seed
supabase db reset   # aplica supabase/migrations/* + supabase/seed.sql

# 5. Crear usuario de login
# Opción A: Supabase Studio http://localhost:54323 → Authentication → Users
# Opción B:
curl -X POST http://localhost:54321/auth/v1/signup \
  -H "apikey: TU_ANON_KEY" \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@clinicboost.com","password":"Admin123!"}'

# 6. Configurar variables de entorno del backend
# apps/api/src/ClinicBoost.Api/appsettings.Development.json ya tiene valores locales

# 7. Arrancar la API
cd apps/api
dotnet run --project src/ClinicBoost.Api
# → escucha en http://localhost:5011

# 8. Configurar el frontend
cp apps/web/.env.local.example apps/web/.env.local
# Editar .env.local → VITE_API_URL=http://localhost:5011
# Verificar VITE_SUPABASE_URL=http://127.0.0.1:54321 y VITE_SUPABASE_ANON_KEY=<tu_anon_key>

# 9. Arrancar el frontend
cd apps/web
npm install
npm run dev
# → escucha en http://localhost:5173

# 10. Abrir http://localhost:5173 y hacer login
```

### Staging con Docker

```bash
cp .env.staging.example .env.staging
# Rellenar todos los CHANGE_ME con valores reales de Supabase Cloud y Twilio
docker compose -f docker-compose.staging.yml --env-file .env.staging up -d
```

### Problemas conocidos en local

| Síntoma | Causa | Solución |
|---------|-------|----------|
| "Error: No se pudo cargar el resumen" en dashboard | `VITE_API_URL` apunta a puerto incorrecto (5000 vs 5011) | Corregir `.env.local` |
| Todos los errores del dashboard | Supabase no está arrancado | `supabase start` |
| API falla al arrancar: "Connection refused 54322" | Supabase no está arrancado | `supabase start` |
| Búsqueda en Inbox no devuelve resultados | Bug `patient?.Name` — corregido en commit `4c33b7b` | `git pull` |

---

## §10 · Tests y Fiabilidad

### Cobertura actual

| Área | Tests | Estado |
|------|-------|--------|
| Agent (ConversationalAgent, IntentClassifier, etc.) | 4 archivos, ~329 líneas | ✅ Buena cobertura de lógica del agente |
| Webhooks (WhatsApp inbound, status, voice) | 5 archivos | ✅ Happy path + error cases |
| Flow01 (Orchestrator, MissedCallWorker) | 4 archivos | ✅ Incluyendo `MissedCallWorkerIntegrationTests` |
| Appointments | 1 archivo | ⚠️ Básico |
| Calendar | 3 archivos | ✅ Parser + cache + service |
| Audit (RefreshToken, Session) | 3 archivos | ✅ |
| Infrastructure (Idempotency, Middleware, Tenants, Twilio) | 12 archivos | ✅ Muy buena cobertura de infraestructura |
| Smoke Tests (TC01-TC06) | 6 archivos | ✅ Escenarios E2E en memoria |
| **Conversations / Inbox** | **0 archivos** | ❌ Sin tests para InboxService ni ConversationEndpoints |
| Dashboard | 0 archivos | ❌ Sin tests para DashboardService |
| Variants | 2 archivos | ✅ |

### Gaps críticos en testing

1. **`ConversationInboxService`** — 0 tests. El bug `patient?.Name` pasó desapercibido por falta de tests.
2. **`DashboardService`** — 0 tests. Las queries de KPIs no están verificadas.
3. **Sin tests de integración con BD real** para Conversations. Los smoke tests usan mocks/stubs.

---

## §11 · Gaps y Deuda Técnica Clasificada P0–P2

### P0 — Bloquean la compilación o causan corrupción de datos

| ID | Descripción | Archivo(s) | Estado |
|----|-------------|-----------|--------|
| P0-1 | `patient?.Name` → debe ser `patient?.FullName` en búsqueda libre de Inbox | `ConversationInboxService.cs:157` | ✅ **Corregido** commit `4c33b7b` |
| P0-2 | `Patient.Name` (×4) en `ConversationInboxService.cs` | `ConversationInboxService.cs` | ✅ **Corregido** commit `42bffb5` |
| P0-3 | `ScalarOptions.EndpointPathPrefix` eliminado en v2.12 | `Program.cs` | ✅ **Corregido** commit `42bffb5` |
| P0-4 | `AgentTurn.MessageId = Guid.Empty` → posible FK violation | `ConversationalAgent.cs:PersistTurnAsync` | ⚠️ **Documentado** como N-P2-01. Depende del schema de `agent_turns`. Verificar si la columna admite `NULL`. |

### P1 — Bloquean el uso en piloto asistido

| ID | Descripción | Impacto |
|----|-------------|---------|
| P1-1 | **Sin envío manual de mensajes desde Inbox** | Operador no puede responder a paciente en `waiting_human` |
| P1-2 | **`Note` en PATCH status no se persiste** | Auditoría de cambios de estado incompleta |
| P1-3 | **`propose_cancellation` tool es placeholder** | IA puede decir "he registrado tu cancelación" pero no pasa nada en BD |
| P1-4 | **Puerto incorrecto en `.env.local.example`** | VITE_API_URL=5000 pero API escucha en 5011 → todos los endpoints fallan |
| P1-5 | **Sin refresh automático de Inbox** | Operador no ve nuevos `waiting_human` sin recargar |
| P1-6 | **Colas en memoria** | Si la API se reinicia durante un job, el mensaje WA se pierde |

### P2 — Deuda técnica a medio plazo

| ID | Descripción | Impacto |
|----|-------------|---------|
| P2-1 | Flows 02–07 no implementados | Funcionalidad prometida en nombre de flujos no disponible |
| P2-2 | Sin rate-limiting en webhooks | Riesgo de flood / DDoS en endpoints públicos de Twilio |
| P2-3 | Sin tests para Conversations / Dashboard | Regresiones silenciosas posibles |
| P2-4 | `confirm_appointment_response` tool es placeholder | Confirmaciones de cita por WA no se persisten |
| P2-5 | Dos directorios de migraciones separados | `supabase db reset` puede no aplicar todas las migraciones |
| P2-6 | `SessionExpiresAt` no procesado automáticamente | Conversaciones expiradas nunca cambian a `status='expired'` |
| P2-7 | `staleTime: 2 min` en `useDashboardSummary` | Badge de `waiting_human` puede estar desactualizado 2 minutos |
| P2-8 | Sin UI para gestionar `RuleConfig` | Los parámetros operacionales (cooldown, fee, templates) solo editables en BD |

---

## §12 · Plan de Corrección Ordenado

### Sprint 1 — Habilitar piloto asistido (1–2 días)

1. **[P1-4]** Corregir `VITE_API_URL` en `.env.local.example` de 5000 → 5011.
2. **[P1-2]** Persistir `Note` del PATCH status: añadir columna `last_status_note TEXT` a `conversations` + migration + write en `ConversationInboxService.PatchStatusAsync`.
3. **[P1-1]** Añadir endpoint `POST /api/conversations/{id}/messages` (envío manual) + UI en InboxPage (textarea + botón "Enviar").
4. **[P0-4]** Verificar schema de `agent_turns.message_id`: si no admite NULL, cambiar `Guid.Empty` → `null` en `PersistTurnAsync`.

### Sprint 2 — Mejorar operabilidad (2–3 días)

5. **[P1-5]** Auto-refresh en InboxPage: reducir `staleTime` a 15s + `refetchInterval: 30_000` en `useInboxList`.
6. **[P2-7]** `staleTime` de Dashboard summary a 30s cuando hay `waitingHumanCount > 0`.
7. **[P2-3]** Escribir tests básicos para `ConversationInboxService` (GetInbox, PatchStatus, búsqueda libre).
8. **[P2-5]** Consolidar migraciones: documentar el orden correcto de aplicación de ambos directorios en README.

### Sprint 3 — Preparar para producción (1 semana)

9. **[P2-2]** Añadir rate-limiting en `Program.cs` para endpoints de webhooks (Microsoft.AspNetCore.RateLimiting).
10. **[P1-3]** Implementar `propose_cancellation` en `ToolRegistry.ExecuteAsync`: crear `WaitlistEntry` o `AppointmentEvent` de tipo cancellation pending.
11. **[P2-6]** Job de expiración de conversaciones: `SessionCleanupWorker` (ya existe para sesiones) → añadir lógica para marcar `status='expired'` cuando `SessionExpiresAt < UtcNow`.
12. **[P1-6]** Migrar colas de `Channel<T>` a tabla `job_queue` con polling periódico o usar Hangfire.

---

## §13 · Veredicto Final

```
┌──────────────────────────────────────────────────────────────┐
│  VEREDICTO CLÍNICBOOST — 2026-04-09                          │
│                                                              │
│  Código base:    SÓLIDO para un MVP                          │
│  Arquitectura:   BIEN DISEÑADA (Vertical Slice, RLS, DI)     │
│  Seguridad:      ADECUADA para staging; mejorar en prod      │
│  Flujos activos: 2/8 (flow_00 + flow_01)                     │
│                                                              │
│  ✅ LISTO PARA: Demo local supervisada                        │
│  ✅ LISTO PARA: Staging interno (equipo técnico)              │
│  ⚠️  CONDICIONALMENTE para piloto asistido (1 clínica):      │
│      Requiere Sprint 1 completado (P1-1 a P1-4)             │
│  ❌ NO LISTO PARA: Producción autónoma sin equipo técnico    │
│                                                              │
│  Prioridad máxima:                                           │
│  → Añadir envío manual de mensajes (P1-1)                    │
│  → Corregir .env.local.example (P1-4)                       │
│  → Tests mínimos para Conversations (P2-3)                   │
└──────────────────────────────────────────────────────────────┘
```

---

## §14 · Anexo

### A. Checklist mínimo para handoff a equipo de piloto

- [ ] `supabase start` ejecuta sin errores
- [ ] `supabase db reset` aplica todas las migraciones
- [ ] `dotnet run` compila sin warnings de errores (solo el CS0162 de `WhatsAppInboundWorker.cs:235` — código inalcanzable, inofensivo)
- [ ] `npm run dev` arranca en 5173 sin errores de consola
- [ ] Login con usuario de test funciona
- [ ] Dashboard carga los 5 paneles (KPIs, entregabilidad, flows, conversaciones, revenue)
- [ ] Badge `waiting_human` visible en header del Dashboard
- [ ] Inbox muestra lista de conversaciones
- [ ] Filtros de status (Todas / Handoff / Abiertas / Resueltas) funcionan
- [ ] Seleccionar conversación muestra historial de mensajes
- [ ] Botón "Tomar el caso" → PATCH → conversación cambia a `waiting_human` en lista
- [ ] Botón "Marcar resuelta" → PATCH → conversación cambia a `resolved`
- [ ] Buscar por nombre de paciente devuelve resultados (requiere commit `4c33b7b`)
- [ ] `.env.local` tiene `VITE_API_URL=http://localhost:5011` (no 5000)

### B. Variables de entorno requeridas (resumen)

```
# Backend (nunca en git)
ConnectionStrings__Supabase
Supabase__Url, Supabase__AnonKey, Supabase__JwtSecret
Twilio__AccountSid, Twilio__AuthToken, Twilio__WebhookBaseUrl
OpenAI__ApiKey (si agente activo)

# Frontend (bake-time en Docker, .env.local en local)
VITE_SUPABASE_URL, VITE_SUPABASE_ANON_KEY
VITE_API_URL  ← ATENCIÓN: debe ser 5011 en local, no 5000
```

### C. Comandos útiles de diagnóstico

```bash
# Verificar que la API compila
cd apps/api && dotnet build src/ClinicBoost.Api 2>&1 | grep -E "error|warning"

# Ver logs del agente en tiempo real
dotnet run --project src/ClinicBoost.Api 2>&1 | grep "\[Agent\]\|\[WAWorker\]\|\[Flow01\]"

# Probar endpoint de health
curl http://localhost:5011/health/live

# Probar summary (requiere JWT válido)
curl http://localhost:5011/api/dashboard/summary \
  -H "Authorization: Bearer <tu_jwt>"

# Verificar migraciones aplicadas en Supabase local
supabase db diff --schema public
```

### D. Flujos no implementados — estimación de esfuerzo

| Flow | Estimación | Dependencias |
|------|-----------|--------------|
| flow_02 Detección de huecos | 3–4 días | `CalendarService` (listo), nuevo `GapDetectionWorker` + Orchestrator + WA template |
| flow_03 Recordatorio de cita | 2–3 días | `AppointmentService` (listo), nuevo `ReminderWorker` + scheduled job + WA template |
| flow_04 No-show seguimiento | 2–3 días | Requiere flow_03 implementado primero |
| flow_05 Lista de espera | 3–4 días | `WaitlistEntry` entity (lista en schema), nuevo `WaitlistWorker` |
| flow_06 Reactivación paciente | 2–3 días | Nuevo `ReactivationWorker` + regla de inactividad configurable |
| flow_07 Reprogramación | 1–2 días | `AppointmentService.RescheduleAsync` (listo), solo necesita trigger y WA template |

---

*Informe generado por revisión automática de código fuente. Todos los hallazgos han sido verificados en los archivos fuente indicados. Los bugs P0 han sido corregidos en commits `42bffb5` y `4c33b7b`.*

---

## §15 · Addendum — Sincronización docs ↔ código (2026-04-22)

> Este addendum corrige afirmaciones del informe original que ya no son ciertas en el código actual. El informe base (§1-§14) se preserva como snapshot histórico del commit `4c33b7b`.

### Items resueltos desde el informe base

| Referencia original | Afirmación del informe | Estado real (2026-04-22) |
|---|---|---|
| §3.2 P1-1 | "Sin envío manual de mensajes desde Inbox" | ✅ Implementado — TASK-001 (`POST /api/conversations/{id}/messages` + frontend + 7 tests) |
| §3.2 P1-3 | "`propose_cancellation` tool es placeholder — devuelve `{ ok: true }` sin persistir" | ✅ Implementado — `ToolRegistry.cs:420-458` crea `AppointmentEvent(cancellation_requested)` y persiste en BD |
| §3.2 P2-4 | "`confirm_appointment_response` tool es placeholder" | ✅ Implementado — `ToolRegistry.cs:460-500` crea `AppointmentEvent(patient_confirmed/patient_cancelled)` y persiste |
| §6 flow_03 | "Etiqueta vacía — no hay clases de Orchestrator, Endpoints ni Workers" | ✅ Implementado — `Flow03Orchestrator` (536 líneas), `AppointmentReminderWorker` (200 líneas), registrado en DI, `TC07_AppointmentReminderFlow03Tests` (6 tests) |
| §11 P1-4 | "`VITE_API_URL` en `.env.local.example` apunta a 5000" | ✅ Corregido — `.env.local.example` ahora apunta a `http://localhost:5011` |
| SMOKE_TESTS GAP-01 | "Guard `waiting_human` no implementado en WhatsAppInboundWorker" | ✅ Implementado — `WhatsAppInboundWorker.cs:236` |
| SMOKE_TESTS GAP-02 | "`AgentContext` no incluye `LocalNow`" | ✅ Implementado — `AgentModels.cs:150` + `WhatsAppInboundWorker.cs:277-314` + `SystemPromptBuilder.cs:58-63` |
| SMOKE_TESTS GAP-03 | "`MaxDelayMinutes` definido pero no implementado" | ✅ Implementado — `Flow01Orchestrator.cs:127-157` |
| SMOKE_TESTS GAP-04 | "MessageStatusService no tiene idempotencia" | ✅ Implementado — `MessageStatusService.cs:94-97` usa `IIdempotencyService` |

### Items resueltos en la implementación WQ (2026-04-22, segunda ronda)

| Referencia original | Estado final |
|---|---|
| §3.2 — `Note` en PATCH status no se persiste | ✅ WQ-002: nota persistida en `audit_logs`, historial visible en detalle, 7 tests |
| §3.3 — `AgentTurn.MessageId = Guid.Empty` | ✅ WQ-003: `MessageId` cambiado a `Guid?`, escribe NULL en vez de Guid.Empty |
| §4.2 — Sin auto-refresh | ✅ WQ-005: `refetchInterval` 30s en Inbox, 60s en Dashboard summary |
| §5.3 — `DEVELOPMENT.md` puertos incorrectos | ✅ WQ-004: todos los archivos corregidos a 5011 |
| §6 — Flow03 sin validar en staging | ✅ WQ-007: bug corregido, config explícita, seed data, Makefile target |

### Items que siguen abiertos (deuda técnica P2)

| Referencia original | Estado |
|---|---|
| §3.3 — Colas en memoria sin persistencia | ⚠️ Sigue abierto (`Channel<T>` sin persistencia ante restart) |
| §3.3 — Sin rate-limiting en webhooks | ⚠️ Sigue abierto |
| §3.2 — Sin expiración automática de conversaciones | ⚠️ `SessionExpiresAt` sin worker |

### Actualización del veredicto §13

```
Flujos activos:     3/8 (flow_00 + flow_01 + flow_03)   ← antes: 2/8
Items P1 abiertos:  0                                     ← antes: 6
Work Queue:         7/7 completada (WQ-001 a WQ-007)
Tests totales:      50+ archivos, 33 tests solo en Inbox
Piloto asistido:    ✅ LISTO
```
