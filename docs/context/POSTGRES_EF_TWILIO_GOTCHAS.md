# Postgres, EF Core y webhooks Twilio — trampas resueltas

> Actualizado: 2026-05-06. Sirve como checklist al mapear entidades nuevas o al depurar integración local Twilio ↔ API.

Convenciones del proyecto: **`EFCore.NamingConventions` (snake_case)** + columnas **`jsonb`** en varias tablas. Si el CLR usa `string` sin `HasColumnType("jsonb")`, Npgsql puede enviar **`text`** y Postgres responde **`42804`** o **`22P02`**.

---

## 1. `tenants.whatsapp_number`

| | |
|--|--|
| **Síntoma** | `42703`: `column t.whats_app_number does not exist` (hint: `whatsapp_number`) |
| **Causa** | La convención trocea `WhatsAppNumber` como `whats_app_number`; la DDL usa `whatsapp_number`. |
| **Arreglo** | `Tenant` en `AppDbContext`: `.Property(t => t.WhatsAppNumber).HasColumnName("whatsapp_number")`. |

---

## 2. `webhook_events.payload` (y consistencia Twilio → jsonb)

| | |
|--|--|
| **Síntoma** | `22P02`: `invalid input syntax for type json` — detalle menciona algo tipo `AccountSid...` |
| **Causa** | Twilio envía `application/x-www-form-urlencoded`. Se persistía ese string plano en columna **`jsonb`**. |
| **Arreglo** | `TwilioFormPayloadJson.FromForm(IFormCollection)` serializa campos a JSON objeto. Usado en WhatsApp inbound, message-status y voice. La **cadena ordenada para hash de idempotencia** sigue siendo el form concatenado (`rawPayload`); no mezclar con el JSON de trazabilidad. |

---

## 3. `conversations.ai_context`

| | |
|--|--|
| **Síntoma** | `42804`: `column "ai_context" is of type jsonb but expression is of type text` |
| **Causa** | `Conversation` no tenía configuración EF; `string` iba como texto. |
| **Arreglo** | Bloque explícito `model.Entity<Conversation>` con `.Property(x => x.AiContext).HasColumnType("jsonb")` (y columnas alineadas a la tabla). |

---

## 4. Revisión sistemática `jsonb` + `processed_events.metadata` + `audit_logs`

- Toda **`string`** persistida en columna **`jsonb`** debe declarar **`HasColumnType("jsonb")`** en `AppDbContext.ConfigureImmutableEntities` (o en configuración dedicada).
- **`processed_events.metadata`**: en DDL es **`jsonb`**; antes estaba mapeado como `text` en EF → alineado a **`jsonb`**.
- **`AuditLog`** (`old_values`, `new_values`): faltaba mapeo explícito → añadido con **`jsonb`**.
- Metadatos con **`NOT NULL DEFAULT '{}'::jsonb`** en DDL: donde aplica, usar **`.IsRequired()`** en la propiedad mapeada.
- **`ApplyConfigurationsFromAssembly`**: se eliminó la llamada vacía para evitar el **WARN** *No instantiatable types implementing IEntityTypeConfiguration* en cada arranque. Si se añaden clases `IEntityTypeConfiguration`, volver a registrar el ensamblado.

---

## 5. Dashboard: “+1” en citas recuperadas sin haber recuperado nada en Twilio

| | |
|--|--|
| **Síntoma** | El resumen muestra **`RecoveredAppointments` ≥ 1** tras desarrollo local o `supabase db reset`, aunque solo se haya probado WhatsApp sandbox. |
| **Causa principal (datos)** | El **seed** inserta una cita de ejemplo con **`is_recovered = TRUE`** y **`source = WhatsApp`** para la paciente demo Ana García (véase `supabase/seed.sql` y `supabase/seed/dev_seed.sql`, bloque “Citas de ejemplo”). Esa fila tiene **`created_at` reciente**, y el KPI cuenta por **`created_at` en el rango** del dashboard. |
| **Causa secundaria (API)** | `BookAppointmentRequest` usa **`Source = AppointmentSource.WhatsApp` por defecto**; cualquier `POST /api/appointments/book` sin sobreescribir `Source` marca la cita como recuperada (`IsRecovered = true` cuando `Source != Manual`). |
| **Dónde está el KPI** | `DashboardService.GetSummaryAsync`: cuenta `appointments` con **`IsRecovered && CreatedAt`** en `[from, to)`. |
| **Qué hacer** | Para métricas “limpias”: no usar seed en entornos de demo de métricas, o ajustar el seed (`is_recovered`), o cambiar la semántica del KPI (p. ej. filtrar por `starts_at_utc`, excluir pacientes seed, o añadir flag explícito “demo”). Documentar aquí cualquier decisión de producto. |

---

## 6. Flow03 (recordatorios) — notas de pruebas y trampas

### 6.1 `cooldown_minutes = 0` debe ser válido

| | |
|--|--|
| **Síntoma** | Aunque `rule_configs.rule_value = '0'`, Flow03 seguía diciendo `CooldownMin=720` y saltaba el envío. |
| **Causa** | Validación `m > 0` en el parser → el valor `0` caía al default 720. |
| **Arreglo** | Aceptar `m >= 0` para que `0` signifique “sin cooldown”. |

### 6.2 Reply inbound en conversación distinta (flow_00 vs flow_03)

| | |
|--|--|
| **Síntoma** | El paciente responde “OK” al recordatorio Flow03, pero el dashboard lo muestra en otra conversación. |
| **Causa** | Pipeline inbound forzaba `flow_00` al upsert de conversación; además el outbound no actualizaba `conversations.updated_at`, así que “la conversación reciente” podía ser otra. |
| **Arreglo** | Inbound reutiliza la conversación activa más reciente del paciente (cualquier flow); outbound actualiza `conversations.updated_at/message_count` al crear un mensaje outbound. |

### 6.3 Checklist de setup para prueba rápida
- Ajustar `Flow03Options` en `appsettings.Development.Local.json` (no versionado): `PollIntervalMinutes=1` y `DefaultReminderHoursBeforeAppointment=0`.
- Asegurar `patients.rgpd_consent = true`.
- Insertar appointment de prueba a +3 min (`starts_at_utc = now() + interval '3 minutes'`).

## Referencias rápidas en código

- `apps/api/src/ClinicBoost.Api/Infrastructure/Database/AppDbContext.cs` — mapeos explícitos.
- `apps/api/src/ClinicBoost.Api/Infrastructure/Twilio/TwilioFormPayloadJson.cs` — form Twilio → JSON para `jsonb`.
- `apps/api/src/ClinicBoost.Api/Features/Dashboard/DashboardService.cs` — `RecoveredAppointments`.
- `supabase/seed.sql`, `supabase/seed/dev_seed.sql` — cita demo `is_recovered = TRUE`.
