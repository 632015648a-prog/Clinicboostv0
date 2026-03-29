# ADR-001: Arquitectura híbrida .NET 10 + Supabase

**Estado:** Aceptado  
**Fecha:** 2026-03-29  
**Autores:** ClinicBoost Team

## Contexto

ClinicBoost necesita una capa de backend robusta con lógica de negocio compleja (flujos de IA, integraciones con Twilio, gestión de timezone, idempotencia de webhooks) y a la vez aprovechar servicios gestionados de base de datos, autenticación y storage.

## Decisión

Arquitectura **híbrida**:
- **Supabase** como plataforma base: Postgres + Auth (GoTrue) + RLS + Storage
- **.NET 10 Minimal API** como capa de aplicación/negocio con Vertical Slice Architecture
- **React + Vite + TypeScript + Tailwind** en el frontend

## Consecuencias positivas

- Supabase gestiona la infraestructura de BD, Auth y Storage
- .NET tiene el control total de la lógica de negocio sin restricciones
- RLS garantiza aislamiento de tenants a nivel de base de datos (defensa en profundidad)
- Vertical Slice facilita el trabajo en paralelo por features sin conflictos

## Consecuencias negativas / trade-offs

- Dos capas de autenticación que coordinar (Supabase JWT → .NET JwtBearer)
- Overhead de mantener migraciones SQL manualmente

## Alternativas descartadas

- **Solo Supabase** (Edge Functions): Insuficiente para lógica de negocio compleja
- **Solo .NET con Postgres directo**: Pierde los servicios gestionados de Supabase
- **NestJS / FastAPI**: No en el stack de dominio del equipo

## Reglas derivadas (no negociables)

1. RLS activa en todas las tablas de negocio
2. `migration_user` ≠ `app_user`
3. JWT claims incluyen `tenant_id`
4. No guardar tokens en localStorage
