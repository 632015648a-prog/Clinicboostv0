# TASK-001 — Envío manual desde Inbox

## 1. Estado
**Approved** — implementación en curso (2026-04-11)

## 2. Contexto
ClinicBoost ya tiene Inbox y detalle de conversación, pero no permite cerrar bien
una conversación escalada a humano desde la propia interfaz.
Eso reduce la credibilidad del piloto: el operador ve la conversación pero no puede responder.

## 3. Objetivo
Permitir que el operador envíe un mensaje de texto libre desde Inbox.
El mensaje debe quedar reflejado en el historial, trazado como intervención manual
y enviado vía Twilio WhatsApp.

## 4. Alcance
- Campo de texto en el panel de detalle.
- Acción de enviar con validación básica (no vacío, max 1600 chars).
- Persistencia del mensaje en tabla `messages` (`direction=outbound`, `generated_by_ai=false`).
- `ai_model` usado como campo de trazabilidad de origen: valor `"operator"`.
- Envío real vía `IOutboundMessageSender` (TwilioOutboundMessageSender).
- Visibilidad inmediata en el historial (invalidación de caché React Query).
- Error visible al operador si falta `WhatsAppNumber` del tenant.

## 5. Fuera de alcance
- Adjuntos / media.
- Plantillas avanzadas.
- Programación de mensajes.
- Sugerencias IA.
- Tiempo real con WebSocket / SSE.
- Analytics específicos de mensajes manuales (P2 post-piloto).
- Tabla de autores/operadores (no existe todavía).

## 6. Estado actual antes de la implementación
Inbox operativa parcial: lista, filtros, detalle de historial, cambio de estado.
Falta la acción de envío de mensaje desde el detalle.

## 7. Propuesta funcional

### Backend
`POST /api/conversations/{id}/messages`
- Auth: JWT requerido. TenantId desde `ITenantContext` (nunca del body).
- Validación:
  - Conversación debe pertenecer al tenant (404 si no).
  - Estado de la conversación: solo `open` y `waiting_human` (422 si `resolved`/`expired`/`opted_out`).
  - `body` no vacío, máximo 1600 caracteres.
  - `Tenant.WhatsAppNumber` debe estar configurado (422 con mensaje legible si no lo está).
- Envío: reutiliza `IOutboundMessageSender.SendAsync`.
- Trazabilidad: `GeneratedByAi=false`, `AiModel="operator"`.
- Respuesta 200: devuelve el `InboxMessageItem` creado.
- Respuesta de error Twilio: 502 con mensaje legible.

### Frontend
- `SendMessagePanel` en la parte inferior del `DetailPanel`, encima de `ActionPanel`.
- Visible solo en estados `open` y `waiting_human`.
- Textarea (2 filas, max 1600 chars) + botón "Enviar".
- Estado de carga: deshabilita botón + spinner.
- Error visible con el mensaje del servidor.
- Al enviar con éxito: limpia el campo e invalida el detalle para refrescar el historial.

## 8. Impacto técnico
- **Backend**: nuevo método `SendManualMessageAsync` en `IConversationInboxService` +
  `ConversationInboxService`. Nuevo endpoint en `ConversationEndpoints`.
  Reutiliza `IOutboundMessageSender` ya existente.
- **Frontend**: `SendManualMessageRequest` / `SendManualMessageResponse` en `inbox.ts`.
  `useSendManualMessage` en `useInbox.ts`. `SendMessagePanel` en `InboxPage.tsx`.
- **Sin migraciones**: no se añaden columnas ni tablas nuevas. `ai_model` ya existe.

## 9. Dependencias
- `IOutboundMessageSender` (ya registrado en DI como Scoped).
- `Tenant.WhatsAppNumber` configurado en la BD del tenant.
- `ITenantContext` operativo (ya lo está).

## 10. Riesgos
- Si `Tenant.WhatsAppNumber` no está configurado → se muestra error claro al operador (no bloqueo silencioso).
- Twilio puede rechazar el envío fuera de la ventana de sesión de 24 h → el error de Twilio se propaga al operador.
- Un mensaje manual en `waiting_human` no reactiva la IA (el guard en `WhatsAppInboundWorker` ya protege esto).

## 11. Criterios de aceptación
1. Se puede escribir un mensaje desde el detalle de una conversación `open` o `waiting_human`.
2. Al enviar, aparece en el historial con `direction=outbound` y sin marca de IA.
3. El tenant queda aislado: no es posible enviar a conversación de otro tenant (404).
4. Si el body está vacío o supera 1600 chars, se muestra error antes de llamar a la API.
5. Si falta `WhatsAppNumber`, el operador ve un mensaje claro ("Número de WhatsApp de la clínica no configurado").
6. Si Twilio falla, el operador ve el error (no falla silenciosamente).
7. En conversación `resolved`, el botón no aparece (el operador debe reabrir primero).
8. La conversación permanece en `waiting_human` tras el envío manual (no reactiva IA).

## 12. Casos de prueba
- Envío correcto en conversación `waiting_human`.
- Envío correcto en conversación `open`.
- Validación body vacío → error frontend, sin llamada API.
- Validación body > 1600 chars → error frontend.
- Tenant incorrecto → 404.
- Conversación `resolved` → botón no visible.
- `WhatsAppNumber` no configurado → 422 con mensaje visible.
- Fallo Twilio → 502 con mensaje visible.

## 13. Decisiones cerradas
| Pregunta | Decisión |
|---|---|
| ¿Guardar autor humano? | `AiModel = "operator"` en campo ya existente. Sin nueva columna. |
| ¿Enviar en conversaciones resolved? | No. El botón no aparece. Hay que reabrir primero. |
| ¿Analytics específicos? | No para piloto. Los mensajes manuales ya aparecen en métricas de mensajes existentes. |
| ¿Error si falta WhatsAppNumber? | Error visible al operador. |

## 14. Decisión
**Aprobado** — 2026-04-11
