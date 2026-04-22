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
- Estado: **✅ Completado** (2026-04-22)
- Spec: `docs/specs/TASK-WQ002_PERSIST_STATUS_NOTE_SPEC.md`
- Nota del operador se persiste en `audit_logs` como registro inmutable. Historial de cambios de estado visible en detalle de conversación. 7 tests (TC-NOTE-01 a TC-NOTE-07).

### WQ-003 — Revisar `agent_turns.message_id`
- Prioridad: Alta
- Estado: **✅ Completado** (2026-04-22)
- `AgentTurn.MessageId` cambiado de `Guid` a `Guid?`. `ConversationalAgent.PersistTurnAsync` usa `matchedMsg?.Id` (null) en vez de `Guid.Empty`. La columna SQL ya era nullable.

## Media prioridad (mejora piloto)

### WQ-004 — Validar `.env.local` / `VITE_API_URL`
- Prioridad: Media
- Bloquea piloto: No
- Estado: **✅ Completado**
- `.env.local.example`: `VITE_API_URL=http://localhost:5011` (correcto)
- `docs/DEVELOPMENT.md`: 5 referencias corregidas a 5011
- `apps/web/src/lib/api.ts`: fallback corregido a `http://localhost:5011`

### WQ-005 — Auto-refresh básico Inbox / Dashboard
- Prioridad: Media
- Estado: **✅ Completado** (2026-04-22)
- `useInboxList`: `refetchInterval: 30s` (lista se actualiza sola)
- `useConversationDetail`: `refetchInterval: 30s` (nuevos mensajes aparecen sin recargar)
- `useDashboardSummary`: `refetchInterval: 60s` (badge waiting_human actualizado)
- `usePendingHandoff`: ya tenía polling 30s (sin cambios)
- Otros hooks de Dashboard (delivery, flows, revenue): sin polling (analytics, no operacional)

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
