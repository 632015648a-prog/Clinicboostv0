# Changelog AI

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
