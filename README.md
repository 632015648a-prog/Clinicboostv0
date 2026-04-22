# ClinicBoost

> **Revenue recovery layer para clínicas privadas de fisioterapia.**  
> No es un ERP ni una agenda nueva — se superpone al software existente y recupera ingresos perdidos.

## Flujos del producto

| Flow | Nombre | Estado |
|------|--------|--------|
| 00 | RGPD Onboarding | ✅ Operativo |
| 01 | Llamadas Perdidas → WhatsApp → Cita | ✅ Operativo |
| 02 | Gap Detection + Yield Management | 🏗️ En construcción |
| 03 | Recordatorios de cita | ✅ Operativo |
| 04 | Leads fuera de horario | 🏗️ En construcción |
| 05 | NPS + Referidos post-sesión | 🏗️ En construcción |
| 06 | Reactivación de pacientes inactivos | 🏗️ En construcción |
| 07 | Reprogramación conversacional | 🏗️ En construcción |

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
│   │   └── tests/
│   └── web/                    ← React + Vite + TypeScript + Tailwind
│       └── src/
│           ├── pages/
│           ├── components/
│           ├── hooks/
│           └── lib/
├── supabase/
│   ├── config.toml
│   ├── migrations/             ← SQL migrations versionadas
│   └── seed/                   ← Datos de desarrollo
├── docs/
│   └── adr/                    ← Architecture Decision Records
├── .github/
│   └── workflows/              ← CI/CD automático
└── Makefile                    ← Comandos de desarrollo
```

## Stack tecnológico

| Capa | Tecnología |
|------|-----------|
| Backend | .NET 10 Minimal API + Vertical Slice |
| Base de datos | Supabase (Postgres + RLS) |
| Auth | Supabase GoTrue + JWT cookies httpOnly |
| Frontend | React 18 + Vite + TypeScript + Tailwind CSS |
| Mensajería | Twilio (WhatsApp Business) |
| IA | Claude / OpenAI |

## Arranque rápido

```bash
# 1. Setup inicial
make setup

# 2. Editar variables de entorno
#    → apps/web/.env.local (VITE_SUPABASE_URL, VITE_SUPABASE_ANON_KEY, VITE_API_URL)
#    → apps/api/src/ClinicBoost.Api/appsettings.Development.Local.json

# 3. Arrancar servicios (3 terminales)
make supabase-start   # Terminal 1
make api-run          # Terminal 2
make web-dev          # Terminal 3
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
