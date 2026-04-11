# Project Overview — ClinicBoost

## Qué es ClinicBoost
ClinicBoost es una capa de recuperación de ingresos y operación conversacional para clínicas privadas de fisioterapia.
No pretende sustituir el software clínico principal ni convertirse en una agenda completa.
Su función es ayudar a recuperar oportunidades perdidas, responder más rápido y dar una capa operativa moderna sobre WhatsApp, agenda e IA.

## Problema que resuelve
Muchas clínicas pequeñas pierden ingresos por:
- llamadas perdidas,
- huecos de agenda,
- seguimientos no hechos,
- conversaciones fuera de horario,
- y falta de seguimiento operativo.

## Propuesta de valor
ClinicBoost busca convertir parte de esas pérdidas en ingresos recuperados mediante:
- captación conversacional por WhatsApp,
- respuesta rápida,
- ayuda operativa al equipo,
- dashboard de seguimiento,
- y trazabilidad por tenant.

## Qué no es
ClinicBoost no es:
- una historia clínica completa,
- una agenda médica full-suite,
- una plataforma de telemedicina,
- ni un simple bot de WhatsApp sin capa operativa.

## Stack actual de referencia
- Backend: .NET 10 Minimal APIs
- Arquitectura: Vertical Slice
- Base de datos: PostgreSQL/Supabase con RLS
- Frontend: React + Vite + TypeScript + Tailwind
- Integraciones: Twilio, OpenAI, Supabase Auth/JWT
- Operación: Docker, health checks, logging estructurado, dashboard MVP

## Estado actual resumido
- Demo local: sí
- Staging interno: cerca, pero no cerrado
- Piloto asistido: posible tras cerrar tareas concretas
- Producción autónoma: todavía no

## Realidad actual del producto
A día de hoy, el núcleo más creíble del producto es:
- Flow 00
- Flow 01
- Dashboard MVP
- Inbox operativa parcial

Los Flows 02-07 todavía no deben venderse como entregados.

## Objetivo actual
Cerrar una base fiable para:
1. demo local sólida,
2. staging interno creíble,
3. piloto asistido controlado.
