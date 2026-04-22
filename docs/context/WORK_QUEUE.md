# Work Queue — prioridades actuales
> Última sincronización con código: 2026-04-22

## Regla general
Ordenar siempre por impacto en piloto, no por apetito técnico.

## Completados

### WQ-001 — Envío manual desde Inbox
- Estado: **✅ Completado** (2026-04-11, commit TASK-001)
- Spec: `docs/specs/TASK-001_INBOX_MANUAL_SEND_SPEC.md`

## Alta prioridad (bloquea piloto)

### WQ-002 — Persistencia de Note en conversación
- Prioridad: Alta
- Bloquea piloto: Sí
- Estado: Pendiente de spec
- Detalle: `PatchConversationStatusRequest.Note` se recibe pero se descarta en `PatchStatusAsync`. No hay columna en BD ni audit trail.

### WQ-003 — Revisar `agent_turns.message_id`
- Prioridad: Alta
- Bloquea piloto: Sí
- Estado: Pendiente de spec
- Detalle: `ConversationalAgent.cs:415` asigna `Guid.Empty` cuando no hay match. `AgentTurn.MessageId` es `Guid` non-nullable. Puede causar FK violation en Postgres.

## Media prioridad (mejora piloto)

### WQ-004 — Validar `.env.local` / `VITE_API_URL`
- Prioridad: Media (bajada de Alta)
- Bloquea piloto: Parcialmente
- Estado: **Parcialmente resuelto**
- Lo resuelto: `.env.local.example` ya apunta a `http://localhost:5011` (correcto).
- Lo pendiente: `docs/DEVELOPMENT.md` sigue documentando puerto 5000 en 5 referencias. `api.ts` tiene fallback `http://localhost:5000` (solo aplica si no hay variable de entorno).

### WQ-005 — Auto-refresh básico Inbox / Dashboard
- Prioridad: Media
- Bloquea piloto: Parcialmente
- Estado: **Parcialmente implementado**
- Lo resuelto: `usePendingHandoff` tiene polling activo (`refetchInterval` + `refetchIntervalInBackground`). `useInbox` tiene `staleTime: 30s`.
- Lo pendiente: `useInbox` no tiene `refetchInterval` (no hace polling en background). `useDashboard` usa `staleTime: 2min` sin polling.

### WQ-006 — Tests mínimos de Conversations/Inbox
- Prioridad: Media
- Bloquea piloto: No, pero reduce riesgo
- Estado: **Parcialmente completado**
- Lo resuelto: 7 tests de SendManualMessage (`ConversationInboxServiceSendTests.cs`).
- Lo pendiente: 0 tests para `GetInboxAsync`, `PatchStatusAsync`, `GetConversationDetailAsync`, `GetPendingHandoffAsync`.

### WQ-007 — Validar Flow 03 en staging
- Prioridad: Media
- Bloquea piloto: No
- Estado: Pendiente
- Detalle: Flow03 tiene orchestrator, worker, DI y 6 tests, pero nunca se ha probado con citas reales en staging.

## Resuelto en código (sin WQ formal previo)

Estos items del AUDIT_REPORT / SMOKE_TESTS se implementaron sin pasar por Work Queue:

| Item | Referencia original | Estado |
|---|---|---|
| GAP-01: guard `waiting_human` en WhatsAppInboundWorker | SMOKE_TESTS §5 | ✅ Implementado |
| GAP-02: `LocalNow` en AgentContext + SystemPromptBuilder | SMOKE_TESTS §5 | ✅ Implementado |
| GAP-03: `MaxDelayMinutes` en Flow01Orchestrator | SMOKE_TESTS §5 | ✅ Implementado |
| GAP-04: idempotencia en MessageStatusService | SMOKE_TESTS §5 | ✅ Implementado |
| P1-3: `propose_cancellation` tool real | AUDIT_REPORT §3.2 | ✅ Implementado — crea `AppointmentEvent` |
| P2-4: `confirm_appointment_response` tool real | AUDIT_REPORT §3.2 | ✅ Implementado — crea `AppointmentEvent` |
| Flow03 completo | AUDIT_REPORT §6 | ✅ Implementado — orchestrator + worker + DI + TC-07 |

## Regla de sprint
No meter nuevas tareas si no desplazan claramente a una prioridad superior.
