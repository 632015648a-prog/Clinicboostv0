# Audit Latest — resumen operativo
> Última sincronización con código: 2026-04-22

## Regla de mantenimiento
Este documento no se actualiza por cada cambio pequeño.
Se actualiza cuando cambian las conclusiones globales del proyecto:
- prioridades P0/P1/P2,
- riesgos relevantes,
- veredicto general,
- preparación para demo, staging, piloto o producción.

## Veredicto corto
ClinicBoost tiene 3 flows operativos (00, 01, 03), inbox con envío manual, dashboard MVP, y la mayoría de los GAPs técnicos críticos del audit original resueltos. Está más cerca del piloto asistido de lo que reflejaban los docs anteriores.

## Fortalezas
- arquitectura sólida (Vertical Slice, RLS, multi-tenant),
- 3 flows implementados y con tests (00, 01, 03),
- inbox operacional con envío manual (TASK-001),
- agente conversacional con tools reales (no placeholders),
- GAPs técnicos 01-04 del smoke test original todos resueltos,
- pipeline idempotente end-to-end,
- 47 archivos de test, incluyendo 7 TCs de smoke E2E.

## Debilidades reales
- WQ-002 (`Note` descartada) y WQ-003 (`Guid.Empty` en FK) siguen abiertos,
- `DEVELOPMENT.md` documenta puertos incorrectos,
- sin rate-limiting en webhooks,
- colas en memoria sin persistencia,
- sin auto-refresh activo en Inbox/Dashboard (solo staleTime),
- Flow03 nunca validado en staging con citas reales.

## Prioridades reales
### P1 inmediatas (bloquean piloto)
- WQ-002: persistir `Note` en PATCH status
- WQ-003: resolver `Guid.Empty` en `agent_turns.message_id`

### P2 posteriores (mejoran piloto)
- WQ-004: corregir puertos en `DEVELOPMENT.md` y fallback `api.ts`
- WQ-005: añadir `refetchInterval` a Inbox y Dashboard
- WQ-006: tests de GetInbox, PatchStatus, GetDetail
- WQ-007: validar Flow03 en staging con citas reales
- rate-limiting en webhooks Twilio
- migrar colas de `Channel<T>` a persistentes

### Resuelto desde el audit original (2026-04-09)
- ✅ P1-1: envío manual desde Inbox (TASK-001)
- ✅ P1-3: `propose_cancellation` tool implementada
- ✅ P2-4: `confirm_appointment_response` tool implementada
- ✅ GAP-01: guard `waiting_human`
- ✅ GAP-02: `LocalNow` en AgentContext
- ✅ GAP-03: `MaxDelayMinutes` implementado
- ✅ GAP-04: idempotencia MessageStatusService
- ✅ Flow03 completo (orchestrator + worker + DI + 6 tests)

## Conclusión práctica
El proyecto está más avanzado de lo que la documentación reflejaba. Solo quedan 2 items P1 para cerrar piloto (WQ-002 y WQ-003). No hace falta rediseñar — hace falta cerrar esos dos y validar Flow03 en staging.
