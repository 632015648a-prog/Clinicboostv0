# ClinicBoost

> **Revenue recovery layer para clínicas privadas de fisioterapia.**  
> No es un ERP ni una agenda nueva — se superpone al software existente y recupera ingresos perdidos.

## Estado del proyecto

**Piloto asistido: ✅ Listo** — toda la Work Queue (WQ-001 a WQ-007) cerrada.

## Flujos del producto

| Flow | Nombre | Estado | Componentes |
|------|--------|--------|-------------|
| 00 | Inbound WhatsApp + IA conversacional | ✅ Operativo | `WhatsAppInboundWorker`, `ConversationalAgent`, 5 tools, `HardLimitGuard` |
| 01 | Llamadas Perdidas → WhatsApp → Cita | ✅ Operativo | `MissedCallWorker`, `Flow01Orchestrator`, A/B variants, revenue tracking |
| 02 | Gap Detection + Yield Management | 🏗️ Pendiente | — |
| 03 | Recordatorios de cita | ✅ Operativo | `Flow03Orchestrator`, `AppointmentReminderWorker`, polling 15min, RGPD + cooldown |
| 04 | Leads fuera de horario | 🏗️ Pendiente | — |
| 05 | NPS + Referidos post-sesión | 🏗️ Pendiente | — |
| 06 | Reactivación de pacientes inactivos | 🏗️ Pendiente | — |
| 07 | Reprogramación conversacional | 🏗️ Pendiente | — |

## Funcionalidad operativa

| Componente | Estado | Detalle |
|---|---|---|
| **Dashboard MVP** | ✅ | 5 paneles (KPIs, entregabilidad, flows, conversaciones, revenue), polling automático 60s |
| **Inbox operacional** | ✅ | Lista paginada + filtros + detalle + envío manual + historial de estado + polling 30s |
| **Appointments API** | ✅ | Slots, book, cancel, reschedule con control de race conditions |
| **Agente IA** | ✅ | GPT-4o, 5 tools reales, guard waiting_human, LocalNow timezone, MaxDelayMinutes |
| **A/B Testing** | ✅ | Selección de variantes, funnel (sent/delivered/read/reply/booked) |
| **Auth + seguridad** | ✅ | JWT + refresh token rotation + session invalidation + CSP + CORS + Twilio HMAC |
| **Multi-tenant** | ✅ | RLS + ITenantContext + filtro explícito en cada query |

## Estructura del monorepo

```
clinicboost/
├── apps/
│   ├── api/                    ← .NET 10 Minimal API + Vertical Slice
│   │   ├── src/
│   │   │   ├── ClinicBoost.Api/
│   │   │   │   ├── Features/   ← Un directorio por flujo/feature
│   │   │   │   ├── Infrastructure/
│   │   │   │   └── Program.cs
│   │   │   └── ClinicBoost.Domain/
│   │   └── tests/              ← 50+ archivos de test (xUnit + FluentAssertions)
│   └── web/                    ← React 19 + Vite + TypeScript + Tailwind
│       └── src/
│           ├── pages/          ← LoginPage, DashboardPage, InboxPage
│           └── lib/            ← api.ts, hooks, tipos
├── supabase/
│   ├── config.toml
│   ├── migrations/             ← SQL migrations versionadas
│   └── seed.sql                ← Datos de desarrollo (incluye citas para Flow03)
├── docs/
│   ├── adr/                    ← Architecture Decision Records
│   ├── context/                ← Estado vivo del proyecto
│   └── specs/                  ← Especificaciones formales (TASK-001, WQ-002)
├── .github/
│   └── workflows/              ← CI/CD (ci-api, ci-web, cd-staging)
└── Makefile                    ← Comandos de desarrollo + smoke tests
```

## Stack tecnológico

| Capa | Tecnología |
|------|-----------|
| Backend | .NET 10 Minimal API + Vertical Slice |
| Base de datos | Supabase (Postgres + RLS) |
| Auth | Supabase GoTrue + JWT cookies httpOnly |
| Frontend | React 19 + Vite + TypeScript + Tailwind CSS |
| Mensajería | Twilio (WhatsApp Business) |
| IA | OpenAI (GPT-4o / GPT-4o-mini) |

## Arranque rápido

```bash
# 1. Setup inicial
make setup

# 2. Editar variables de entorno
#    → apps/web/.env.local (VITE_SUPABASE_URL, VITE_SUPABASE_ANON_KEY, VITE_API_URL)
#    → apps/api/.../appsettings.Development.Local.json — Supabase:JwtSecret =
#       el «JWT Secret» de `supabase status` (si no coincide → 401 en la API)

# 3. Arrancar servicios (3 terminales)
make supabase-start   # Terminal 1
make api-run          # Terminal 2 (escucha en http://localhost:5011)
make web-dev          # Terminal 3 (escucha en http://localhost:5173)
```

> Ver [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) para instrucciones detalladas.

## Reglas de arquitectura (no negociables)

- ✅ Toda tabla de negocio lleva `tenant_id`
- ✅ RLS activa en todas las tablas
- ✅ La IA nunca confirma citas — el backend ejecuta
- ❌ No MediatR ni AutoMapper
- ❌ No tokens en localStorage
- ❌ No `AddHours()` manual para timezones
- ❌ No bypass RLS desde runtime

> Ver [docs/adr/](docs/adr/) para los Architecture Decision Records completos.

## Documentación

| Documento | Contenido |
|---|---|
| [docs/context/CURRENT_STATUS.md](docs/context/CURRENT_STATUS.md) | Estado real del proyecto (fuente de verdad) |
| [docs/context/FLOWS.md](docs/context/FLOWS.md) | Estado canónico de cada flow |
| [docs/context/WORK_QUEUE.md](docs/context/WORK_QUEUE.md) | Cola de trabajo con estado de cada item |
| [docs/specs/](docs/specs/) | Especificaciones formales aprobadas |
| [AUDIT_REPORT.md](AUDIT_REPORT.md) | Auditoría técnica completa (snapshot + addendum) |
| [docs/SMOKE_TESTS.md](docs/SMOKE_TESTS.md) | Suite de smoke tests E2E (TC-01 a TC-07) |
