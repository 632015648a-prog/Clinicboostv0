# Current Status — ClinicBoost

## Estado general
- Demo local: ✅
- Staging interno: ⚠️
- Piloto asistido: ⚠️ (más cerca, WQ-001 cerrado)
- Producción: ❌

## Implementado y funcional

### Infraestructura base
- .NET 10 Minimal API, Vertical Slice, EF Core
- Supabase/PostgreSQL con RLS
- JWT/Auth (Supabase GoTrue), cookies httpOnly
- Twilio webhook + validación de firma
- Health checks, Serilog, idempotencia SHA-256

### Flujos operativos
- Flow 00: pipeline WhatsApp inbound → GPT-4o → respuesta
- Flow 01: missed call recovery (llamada perdida → WA saliente)

### Inbox operacional (MVP completo para piloto)
- Lista paginada con filtros (estado, flujo, búsqueda)
- Detalle de conversación con historial de mensajes
- Cambio de estado (waiting_human / open / resolved)
- **Envío manual de mensajes por operador** ← nuevo (TASK-001, 2026-04-11)
  - Endpoint: `POST /api/conversations/{id}/messages`
  - Solo en estados `open` y `waiting_human`
  - Error claro si falta `WhatsAppNumber` del tenant
  - Trazabilidad: `GeneratedByAi=false`, `AiModel="operator"`
- Widget pending-handoff para dashboard

### Dashboard MVP
- 5 paneles con datos reales
- Polling pending-handoff (30 s)

### Otros
- Appointments API (slots, book, cancel, reschedule)
- Variantes A/B (tracking de eventos)
- Calendar iCal (cache persistida)

## Pendiente para cerrar piloto asistido

### Alta prioridad (bloquea piloto)
- WQ-002: persistencia del campo `Note` en PATCH status
- WQ-003: revisar `agent_turns.message_id` (posible Guid.Empty en FK)
- WQ-004: validar `.env.local` / `VITE_API_URL` (posible apunte a puerto incorrecto)

### Media prioridad (mejora piloto)
- WQ-005: auto-refresh básico Inbox (polling 30 s)
- WQ-006: más tests de Conversations/Inbox (parcialmente cubierto)

## No implementado (fuera de alcance hasta cerrar piloto)
- Flows 02-07
- Envío de adjuntos desde Inbox
- Panel de configuración del tenant
- Rate-limiting en webhook Twilio

## Objetivo actual
Cerrar WQ-002, WQ-003 y WQ-004 para tener piloto asistido listo.
