# TASK-WQ002 — Persistir nota interna en cambio de estado de conversación

## 1. Estado
**Draft** — pendiente de aprobación

## 2. Contexto
El operador humano de la Inbox puede cambiar el estado de una conversación (`open`, `waiting_human`, `resolved`) mediante `PATCH /api/conversations/{id}/status`. El DTO `PatchConversationStatusRequest` ya tiene un campo `Note` (string opcional) y el frontend ya muestra un textarea "Nota interna (opcional)" en el `ActionPanel` de `InboxPage.tsx`.

Sin embargo, el campo se recibe en el backend y **se descarta silenciosamente**: `PatchStatusAsync` en `ConversationInboxService.cs:283-336` no usa `request.Note` en ningún punto. No hay columna en la tabla `conversations` ni tabla auxiliar que lo persista.

Esto significa que:
- El operador cree que la nota se guarda, pero no se persiste.
- No hay trazabilidad de quién cambió un estado ni por qué.
- En un piloto real, si un operador escala una conversación a `waiting_human` con motivo "paciente enfadado", ese contexto se pierde.

Según `PILOT_DEFINITION.md`, "añadir nota útil" es parte de la operativa humana mínima requerida para piloto.

## 3. Objetivo
Que la nota escrita por el operador al cambiar el estado de una conversación se persista en BD, sea consultable desde el detalle de la conversación, y quede como registro de auditoría inmutable.

## 4. Alcance
- Persistir la nota del operador en cada cambio de estado.
- Registrar quién hizo el cambio (actor), cuándo, y desde/hacia qué estado.
- Mostrar el historial de cambios de estado (con notas) en el detalle de la conversación.
- Sin cambios en la tabla `conversations` (no añadir columna mutable).

## 5. Fuera de alcance
- Notas independientes del cambio de estado (tipo "sticky note" o comentario libre).
- Edición o borrado de notas ya registradas (inmutabilidad RGPD).
- Notificaciones al equipo cuando se añade una nota.
- Búsqueda por contenido de notas.
- Historial en la lista de conversaciones (solo en el detalle).

## 6. Estado actual antes de la implementación

### Lo que existe

**Backend:**
- `PatchConversationStatusRequest` (`ConversationModels.cs:117-129`) tiene `public string? Note { get; init; }`.
- `PatchStatusAsync` (`ConversationInboxService.cs:283-336`) recibe `request` pero nunca accede a `request.Note`.
- `PatchConversationStatusResponse` (`ConversationModels.cs:131-138`) devuelve `ConversationId`, `NewStatus`, `PreviousStatus`, `UpdatedAt`. No incluye nota.
- `ConversationEndpoints.cs:84-114` pasa el request al service y devuelve la response.
- `AuditLog` entity (`BaseEntity.cs:85-94`) existe con `EntityType`, `EntityId`, `Action`, `OldValues` (JSON), `NewValues` (JSON), `ActorId`, `CreatedAt`.
- Tabla `audit_logs` en BD con RLS, INSERT via `insert_audit_log()` (SECURITY DEFINER), inmutable (no UPDATE/DELETE para `app_user`).
- `AppDbContext` tiene `DbSet<AuditLog> AuditLogs`.

**Frontend:**
- `ActionPanel` en `InboxPage.tsx:234-270` tiene estado local `note` con textarea "Nota interna (opcional)".
- El textarea ya recoge el valor y lo envía en el body del PATCH: `{ status: newStatus, note: note || undefined }`.
- `PatchConversationStatusRequest` en `inbox.ts:75-78` tiene `note?: string`.
- `PatchConversationStatusResponse` en `inbox.ts:80-85` no incluye nota ni historial.

**BD (tabla `conversations`):**
- No tiene columna `last_status_note`, ni `status_note`, ni columna similar.
- Columnas relevantes: `status TEXT`, `resolved_at TIMESTAMPTZ`, `updated_at TIMESTAMPTZ`.

**BD (tabla `audit_logs`):**
- Estructura: `(id, tenant_id, entity_type, entity_id, action, old_values JSONB, new_values JSONB, actor_id, created_at)`.
- RLS activa. `app_user` solo puede SELECT + INSERT (no UPDATE/DELETE).
- Existe función `insert_audit_log()` con SECURITY DEFINER.

### El gap
El campo `Note` viaja desde el frontend hasta `PatchStatusAsync` pero se descarta. No hay persistencia, no hay lectura posterior, no hay historial visible.

## 7. Propuesta funcional

### Estrategia: audit_logs como registro de cambios de estado

No añadir columna mutable a `conversations`. En su lugar, usar la infraestructura existente de `audit_logs` para registrar cada cambio de estado como un registro inmutable con la nota incluida.

### Backend

**En `PatchStatusAsync` (`ConversationInboxService.cs`)**, después de `SaveChangesAsync` del cambio de estado:

1. Insertar un `AuditLog` con:
   - `EntityType = "conversation"`
   - `EntityId = conversationId`
   - `Action = "status_changed"`
   - `OldValues = JSON { "status": previousStatus }`
   - `NewValues = JSON { "status": newStatus, "note": request.Note }` (note es null si no se proporcionó)
   - `ActorId = userId` (extraído de `ITenantContext.UserId`, disponible en el middleware)
   - `TenantId = tenantId`

2. La inserción del AuditLog se hace en el mismo `SaveChangesAsync` o inmediatamente después. No requiere transacción explícita: si falla el audit log, el cambio de estado ya se persistió (la auditoría es best-effort; el estado es la acción primaria).

**Nuevo endpoint o extensión del detalle existente:**

Opción recomendada: extender `ConversationDetailResponse` con una lista de eventos de estado.

- Añadir `StatusHistory` (`IReadOnlyList<StatusChangeItem>`) a `ConversationDetailResponse`.
- `StatusChangeItem`: `{ Timestamp, PreviousStatus, NewStatus, Note, ActorId }`.
- Leer de `audit_logs` con `WHERE entity_type = 'conversation' AND entity_id = {id} AND action = 'status_changed' ORDER BY created_at DESC`.
- Limitar a los últimos 50 eventos (suficiente para cualquier conversación real).

**Ampliar `PatchConversationStatusResponse`:**

- Añadir `Note` (string?) para confirmar que la nota se persistió.

### Frontend

**En `DetailPanel` de `InboxPage.tsx`:**

- Mostrar sección "Historial de estado" debajo del chat y encima del ActionPanel.
- Cada entrada: `[timestamp] estado_anterior → estado_nuevo — "nota" (por operador)`.
- Estilo discreto (texto pequeño, gris, colapsable si hay muchos).

**En `PatchConversationStatusResponse` (`inbox.ts`):**

- Añadir `note?: string` al tipo.

**En `ConversationDetailResponse` (`inbox.ts`):**

- Añadir `statusHistory: StatusChangeItem[]`.
- `StatusChangeItem: { timestamp: string, previousStatus: string, newStatus: string, note?: string, actorId?: string }`.

## 8. Impacto técnico esperado

| Zona | Cambio |
|---|---|
| `ConversationInboxService.cs` | Añadir inserción de `AuditLog` en `PatchStatusAsync`. Añadir query de `audit_logs` en `GetConversationDetailAsync`. |
| `ConversationModels.cs` | Añadir `StatusChangeItem` record. Añadir `StatusHistory` a `ConversationDetailResponse`. Añadir `Note` a `PatchConversationStatusResponse`. |
| `IConversationInboxService.cs` | Sin cambios (firmas no cambian). |
| `ConversationEndpoints.cs` | Sin cambios (ya pasa el request completo). |
| `InboxPage.tsx` | Añadir sección de historial en `DetailPanel`. |
| `inbox.ts` | Añadir `StatusChangeItem`, extender tipos de response. |
| **Migraciones** | Ninguna — `audit_logs` ya existe con la estructura necesaria. |
| **DI / Program.cs** | Sin cambios — `ITenantContext` ya disponible. |

## 9. Dependencias
- `AuditLog` entity y `DbSet<AuditLog>` ya registrados en `AppDbContext`.
- Tabla `audit_logs` ya existe en BD con RLS y permisos correctos.
- `ITenantContext.UserId` ya disponible en el middleware para obtener el actor.
- `PatchConversationStatusRequest.Note` ya existe en el DTO.
- Frontend ya envía el campo `note` en el PATCH.

## 10. Riesgos

| Riesgo | Mitigación |
|---|---|
| `ITenantContext.UserId` es null en ciertos contextos (webhook, service role) | `PatchStatusAsync` se llama solo desde endpoint autenticado. `UserId` debería estar siempre presente. Si es null, registrar `ActorId = null` (no bloquear). |
| Volumen de audit_logs crece | Un cambio de estado genera ~200 bytes en audit_logs. Con 100 conversaciones/día × 3 cambios = 300 registros/día. Insignificante. |
| La nota contiene PII del operador | Las notas son internas y solo visibles para usuarios del mismo tenant (RLS protege). Alinear con política RGPD interna. |
| Fallo de inserción en audit_log | El cambio de estado es la acción primaria. Si el audit_log falla, loguear warning pero no revertir el cambio de estado. |

## 11. Criterios de aceptación

1. Al hacer PATCH con `{ status: "waiting_human", note: "Paciente enfadado" }`, la nota se persiste en `audit_logs` con `entity_type = 'conversation'`, `action = 'status_changed'`, y `new_values` contiene `"note": "Paciente enfadado"`.
2. Al hacer PATCH sin `note` (o `note: null`), se crea igualmente un registro en `audit_logs` con `new_values.note = null`.
3. Al hacer PATCH con `note: ""` (string vacío), se trata como null (no se persiste string vacío).
4. El `PatchConversationStatusResponse` incluye `note` (el valor persistido).
5. `GET /api/conversations/{id}/messages` incluye `statusHistory` con los cambios de estado ordenados por fecha descendente (más reciente primero), limitado a 50.
6. Cada `StatusChangeItem` incluye `timestamp`, `previousStatus`, `newStatus`, `note` (nullable), `actorId` (nullable).
7. El `actorId` corresponde al `UserId` del JWT del operador que hizo el PATCH.
8. El detalle de conversación en `InboxPage.tsx` muestra la sección de historial de estado.
9. Las notas previas son visibles y no editables (inmutabilidad).
10. El tenant aislamiento se respeta: `audit_logs` filtra por `tenant_id` en la query de lectura.

## 12. Casos de prueba

### Backend (automatizados)
- **TC-NOTE-01**: PATCH con nota → audit_log creado con `new_values.note = "texto"`. `PatchConversationStatusResponse.Note = "texto"`.
- **TC-NOTE-02**: PATCH sin nota → audit_log creado con `new_values.note = null`. Response.Note = null.
- **TC-NOTE-03**: PATCH con nota vacía `""` → se normaliza a null. Mismo que TC-NOTE-02.
- **TC-NOTE-04**: Tres cambios de estado sucesivos → `statusHistory` en detalle devuelve 3 items ordenados desc.
- **TC-NOTE-05**: PATCH con tenant A → audit_log tiene `tenant_id = A`. Query desde tenant B → 0 items en `statusHistory`.
- **TC-NOTE-06**: `ActorId` en audit_log corresponde al `UserId` del contexto JWT del request.
- **TC-NOTE-07**: Conversación no encontrada (404) → no se crea audit_log.

### Frontend (manuales)
- Escribir nota → hacer PATCH → la nota aparece en historial de estado del detalle.
- No escribir nota → hacer PATCH → historial muestra el cambio sin nota.
- Recargar la página → el historial persiste.
- Conversación con 5+ cambios de estado → todos visibles, ordenados cronológicamente.

## 13. Preguntas abiertas

| # | Pregunta | Propuesta |
|---|---|---|
| 1 | ¿Limitar longitud de la nota? | Sí. Max 500 caracteres. Suficiente para contexto operativo, previene abuso. Validar en backend (400 si >500). |
| 2 | ¿Mostrar nombre del actor o solo ID? | Para piloto, mostrar solo `actorId` (UUID). Resolver nombre requeriría join con `tenant_users` o Supabase Auth, que añade complejidad fuera de scope. Se puede resolver post-piloto. |
| 3 | ¿Historial colapsable por defecto? | Sí, colapsado si hay más de 3 entradas. Botón "Ver historial completo". |

## 14. Decisiones cerradas

| Pregunta | Decisión |
|---|---|
| ¿Columna en `conversations` o audit_log? | `audit_logs` — la infraestructura existe, es inmutable, tiene RLS, y no contamina la entidad con campos mutables. |
| ¿Migración SQL necesaria? | No — `audit_logs` ya tiene la estructura necesaria (`old_values JSONB`, `new_values JSONB`, `actor_id`). |
| ¿Nuevo endpoint para historial? | No — se extiende `ConversationDetailResponse` existente. |
| ¿La nota bloquea el cambio de estado si falla? | No — el cambio de estado es la acción primaria. Fallo de audit = warning en log. |

## 15. Decisión
Pendiente de aprobación.
