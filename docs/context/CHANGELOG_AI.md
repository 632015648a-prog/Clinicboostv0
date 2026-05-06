# Changelog AI

## 2026-05-06 — Postgres/EF, webhooks Twilio y KPI dashboard

### Problemas resueltos (código ya en `main` / rama de trabajo)
- **`tenants.whatsapp_number`**: convención EF generaba `whats_app_number` frente a DDL `whatsapp_number` → `42703`. Mapeo explícito `HasColumnName("whatsapp_number")`.
- **`webhook_events.payload` como `jsonb`**: Twilio envía form urlencoded → `22P02` si se guardaba crudo → serialización JSON vía `TwilioFormPayloadJson.FromForm`.
- **`conversations.ai_context` como `jsonb`**: `42804` (text vs jsonb) → bloque EF `Conversation` con `HasColumnType("jsonb")`.
- **Revisión `jsonb`**: `processed_events.metadata` alineado a `jsonb`; `AuditLog` (`old_values`, `new_values`); `.IsRequired()` donde DDL `NOT NULL`; eliminado `ApplyConfigurationsFromAssembly` vacío (WARN en arranque).

### Dashboard “+1 citas recuperadas”
- Documentado como **comportamiento esperado con seed actual**, no regression de WhatsApp: `supabase/seed*.sql` inserta cita demo con **`is_recovered = TRUE`**; `DashboardService` cuenta por `created_at` en el rango. Detalle y mitigaciones: **`docs/context/POSTGRES_EF_TWILIO_GOTCHAS.md`**.
- **`KNOWN_GAPS.md`**: entrada en gaps funcionales; **`DEVELOPMENT.md`**: enlace al doc.

---

## 2026-04-22 — Sincronización docs ↔ código

### Problema
Auditoría de cruce reveló que 8+ items implementados en código no estaban reflejados en documentación. Los docs estaban congelados en el estado del 2026-04-09/11, pero el código había avanzado significativamente.

### Items descubiertos como implementados (sin documentar)
- **Flow03 completo**: `Flow03Orchestrator` (536 líneas) + `AppointmentReminderWorker` (200 líneas) + registro en DI + `TC07_AppointmentReminderFlow03Tests` (6 tests). Nunca mencionado en ningún doc de status.
- **GAP-01**: guard `waiting_human` en `WhatsAppInboundWorker.cs:236`
- **GAP-02**: `LocalNow` en `AgentContext` + `SystemPromptBuilder`
- **GAP-03**: `MaxDelayMinutes` en `Flow01Orchestrator.cs:127-157`
- **GAP-04**: idempotencia en `MessageStatusService.cs:94-97`
- **P1-3**: `propose_cancellation` tool real (crea `AppointmentEvent`)
- **P2-4**: `confirm_appointment_response` tool real (crea `AppointmentEvent`)
- **WQ-004 parcial**: `.env.local.example` ya corregido a puerto 5011

### Docs actualizados
- `FLOWS.md` — Flow03 marcado como implementado
- `CURRENT_STATUS.md` — reescrito con estado real completo
- `WORK_QUEUE.md` — reestructurado con sección de completados y resueltos sin WQ
- `KNOWN_GAPS.md` — gaps resueltos tachados, gaps reales añadidos
- `AUDIT_LATEST.md` — veredicto actualizado (3/8 flows, 2 P1 restantes)
- `SMOKE_TESTS.md` — GAPs 01-04 marcados como resueltos
- `AUDIT_REPORT.md` — addendum §15 con tabla de correcciones
- `CHANGELOG_AI.md` — esta entrada

### Fixes de código
- `DEVELOPMENT.md` — puertos corregidos de 5000 a 5011 (5 referencias)
- `api.ts` — fallback corregido de `http://localhost:5000` a `http://localhost:5011`

---

## 2026-04-11 — TASK-001: Envío manual desde Inbox

### Implementado
- **Backend**:
  - `POST /api/conversations/{id}/messages` — nuevo endpoint de envío manual.
  - `SendManualMessageAsync` en `ConversationInboxService`.
  - `ManualSendException` (excepciones tipadas 422/502 con mensajes legibles).
  - `SendManualMessageRequest` / `SendManualMessageResponse` en `ConversationModels`.
  - Reutiliza `IOutboundMessageSender` existente (TwilioOutboundMessageSender).
  - Validaciones: solo estados `open`/`waiting_human`, body no vacío (max 1600),
    `WhatsAppNumber` configurado, patient con teléfono.
  - Trazabilidad: `GeneratedByAi=false`, `AiModel="operator"` (sin nueva columna).

- **Frontend**:
  - `SendManualMessageRequest` / `SendManualMessageResponse` en `inbox.ts`.
  - `useSendManualMessage` hook en `useInbox.ts`.
  - `SendMessagePanel` en `InboxPage.tsx`: textarea + contador de chars + Ctrl+Enter.
  - Solo visible en `open` y `waiting_human` (no aparece en `resolved`).
  - Error del servidor mostrado directamente al operador.

- **Tests** (7 casos):
  - `ConversationInboxServiceSendTests.cs` con TC-SEND-01 a TC-SEND-07.
  - Cubre: envío correcto, tenant incorrecto, estado invalid, sin WhatsAppNumber,
    fallo Twilio, y verificación de trazabilidad `AiModel="operator"`.

- **Docs**: spec TASK-001 actualizada a Approved, WORK_QUEUE, CURRENT_STATUS actualizados.

### Estado de los criterios de aceptación de TASK-001
- ✅ Campo de texto en detalle de conversación
- ✅ Envío funcional vía Twilio
- ✅ Aparece en historial (invalidación React Query)
- ✅ Respeta tenant (404 si no pertenece)
- ✅ Error visible si falla (Twilio, WhatsAppNumber, estado inválido)
- ✅ No aparece en conversaciones `resolved`
- ✅ No reactiva IA en `waiting_human` (el guard en WhatsAppInboundWorker ya lo protege)

---

## 2026-04-11 — Instalación Context Engineering Pack

- Se define el nuevo sistema de trabajo basado en Context Engineering + Spec-Driven Development.
- Se crea el pack inicial de contexto para ClinicBoost (13 archivos en `docs/`).
- Se establece que no se escribirá código nuevo sin spec aprobada.
- Se fija como prioridad cerrar piloto asistido antes de expandir alcance.
