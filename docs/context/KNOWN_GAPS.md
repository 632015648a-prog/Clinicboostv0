# Known Gaps — ClinicBoost
> Última sincronización con código: 2026-04-22

## Gaps funcionales
- faltan Flows 02, 04-07
- ~~persistencia de `Note` en PATCH status~~ → WQ-002 completado
- sin expiración automática de conversaciones (`SessionExpiresAt` sin worker)
- sin envío de adjuntos desde Inbox
- sin panel de configuración de `RuleConfig` (solo editable en BD)

## Gaps técnicos
- ~~`agent_turns.message_id` Guid.Empty~~ → WQ-003 completado (nullable Guid?)
- ~~`DEVELOPMENT.md` documenta puerto 5000~~ → corregido a 5011
- ~~`api.ts` fallback `http://localhost:5000`~~ → corregido a 5011
- sin rate-limiting en webhooks Twilio
- colas en memoria (`Channel<T>`) sin persistencia ante restart
- ~~sin `refetchInterval` en Inbox ni Dashboard~~ → WQ-005 completado (Inbox 30s, Dashboard 60s)
- 0 tests para GetInbox, PatchStatus, GetDetail, GetPendingHandoff
- Flow03 nunca validado en staging con citas reales

## Resuelto (quitar de checklists anteriores)
- ~~envío manual pendiente~~ → TASK-001 completado
- ~~placeholders de tools/agente~~ → `propose_cancellation` y `confirm_appointment_response` implementados
- ~~GAP-01: guard waiting_human~~ → implementado en WhatsAppInboundWorker
- ~~GAP-02: LocalNow en AgentContext~~ → implementado
- ~~GAP-03: MaxDelayMinutes~~ → implementado en Flow01Orchestrator
- ~~GAP-04: idempotencia MessageStatus~~ → implementado

## Gaps de narrativa
- Flow03 implementado en código pero nunca comunicado
- riesgo menor de que docs desactualizados generen trabajo duplicado

## Gaps de proceso
- sistema documental adoptado pero requiere disciplina de actualización post-sesión
- `AUDIT_REPORT.md` (snapshot 2026-04-09) tiene 8+ afirmaciones obsoletas (ver addendum)
