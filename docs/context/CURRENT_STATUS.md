# Current Status — ClinicBoost
> Última sincronización con código: 2026-04-22

## Estado general
- Demo local: ✅
- Staging interno: ⚠️
- Piloto asistido: ⚠️ (más cerca — WQ-001 cerrado, GAPs 01-04 cerrados, Flow03 implementado)
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
- Flow 03: recordatorio automático de cita próxima vía WhatsApp
  - `Flow03Orchestrator`: doble idempotencia, RGPD, cooldown, ventana configurable por tenant
  - `AppointmentReminderWorker`: polling con intervalo configurable, AutomationRun por ciclo
  - Registrado en DI (`AddFlow03Feature`), arranca con la API como `HostedService`
  - TC-07: 6 tests (happy path, idempotencia, RGPD, ventana, cita pasada, config per-tenant)
  - Pendiente: validación en staging con citas reales

### Inbox operacional (MVP completo para piloto)
- Lista paginada con filtros (estado, flujo, búsqueda)
- Detalle de conversación con historial de mensajes
- Cambio de estado (waiting_human / open / resolved)
- **Envío manual de mensajes por operador** (TASK-001, 2026-04-11)
  - Endpoint: `POST /api/conversations/{id}/messages`
  - Solo en estados `open` y `waiting_human`
  - Error claro si falta `WhatsAppNumber` del tenant
  - Trazabilidad: `GeneratedByAi=false`, `AiModel="operator"`
- Widget pending-handoff para dashboard (polling activo 30 s)

### Dashboard MVP
- 5 paneles con datos reales
- Polling pending-handoff (30 s con `refetchInterval` + `refetchIntervalInBackground`)

### Agente conversacional — mejoras implementadas
- Guard `waiting_human` en `WhatsAppInboundWorker` (GAP-01 cerrado)
- `LocalNow` del tenant en `AgentContext` + `SystemPromptBuilder` (GAP-02 cerrado)
- `MaxDelayMinutes` implementado en `Flow01Orchestrator` (GAP-03 cerrado)
- Idempotencia en `MessageStatusService` (GAP-04 cerrado)
- `propose_cancellation` tool: crea `AppointmentEvent(cancellation_requested)` en BD
- `confirm_appointment_response` tool: crea `AppointmentEvent(patient_confirmed/patient_cancelled)` en BD

### Otros
- Appointments API (slots, book, cancel, reschedule)
- Variantes A/B (tracking de eventos)
- Calendar iCal (cache persistida)

## Pendiente para cerrar piloto asistido

### Alta prioridad (bloquea piloto)
- WQ-002: persistencia del campo `Note` en PATCH status
- WQ-003: revisar `agent_turns.message_id` (Guid.Empty en FK non-nullable)

### Media prioridad (mejora piloto)
- WQ-004: `DEVELOPMENT.md` sigue documentando puerto 5000 (real: 5011). `.env.local.example` ya corregido.
- WQ-005: auto-refresh Inbox (staleTime 30 s pero sin `refetchInterval`; Dashboard sin polling)
- WQ-006: más tests de Conversations/Inbox (solo SendManualMessage cubierto, faltan Get/Patch/Detail)

## No implementado (fuera de alcance hasta cerrar piloto)
- Flows 02, 04-07
- Envío de adjuntos desde Inbox
- Panel de configuración del tenant
- Rate-limiting en webhook Twilio
- Expiración automática de conversaciones (`SessionExpiresAt` sin worker)

## Objetivo actual
Cerrar WQ-002 y WQ-003 para tener piloto asistido listo.
