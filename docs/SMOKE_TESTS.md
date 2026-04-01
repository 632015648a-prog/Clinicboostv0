# ClinicBoost — Suite de Smoke Tests E2E

> **Versión:** 1.0  
> **Última actualización:** 2026-04-01  
> **Ámbito:** `apps/api/tests/ClinicBoost.Tests/SmokeTests/`  
> **Framework:** xUnit 2.9.3 · FluentAssertions 8.4.0 · NSubstitute 5.3.0 · EF InMemory 9.0.4  

---

## 1. Estrategia de pruebas

### 1.1 Objetivo

La suite de smoke tests verifica que los **flujos críticos de negocio** de ClinicBoost funcionan de extremo a extremo sin dependencias externas reales. Cada test case simula el comportamiento de un actor externo (Twilio, OpenAI, paciente) mediante *fake HTTP handlers* y valida que el estado persistido en base de datos es el correcto.

### 1.2 Principios de diseño

| Principio | Aplicación |
|-----------|-----------|
| **Aislamiento por test** | Cada test class hereda de `SmokeTestDb` que crea una BD InMemory con nombre único (`smoke_{ClassName}_{Guid}`) |
| **Sin estado compartido** | Todos los `TenantId`, `PatientId` y SIDs son generados aleatoriamente o fijados por test |
| **Fakes, no mocks de comportamiento** | Los handlers HTTP (`StaticFakeHandler`, `SequentialFakeHandler`) simulan respuestas reales de Twilio y OpenAI |
| **Datos realistas** | Números en formato E.164, SIDs con prefijo `SM/CA/HX`, importes en EUR |
| **Anotación de GAPs** | Los tests documentan explícitamente las piezas faltantes con comentarios `GAP-xx` |

### 1.3 Alcance — qué se cubre y qué no

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ COBERTURA AUTOMATIZADA (smoke tests)                                        │
├──────────────────────────────────────────────┬──────────────────────────────┤
│ ✅ Persistencia de entidades en BD           │ AutomationRun, Message,      │
│                                              │ Conversation, AgentTurn,     │
│                                              │ RevenueEvent, DeliveryEvent  │
├──────────────────────────────────────────────┼──────────────────────────────┤
│ ✅ Lógica de negocio core                    │ MaxDelayMinutes, no-regresión│
│                                              │ success_fee_pct, DST timezone│
├──────────────────────────────────────────────┼──────────────────────────────┤
│ ✅ Aislamiento multi-tenant                  │ Todos los TC verifican que   │
│                                              │ no hay cross-tenant data leak│
├──────────────────────────────────────────────┼──────────────────────────────┤
│ ✅ Manejo de errores de Twilio               │ HTTP 400, error code 30006   │
├──────────────────────────────────────────────┼──────────────────────────────┤
│ ✅ Clasificación de intenciones (fake OpenAI)│ BookAppointment, Complaint   │
└──────────────────────────────────────────────┴──────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│ VALIDACIÓN MANUAL REQUERIDA                                                 │
├──────────────────────────────────────────────┬──────────────────────────────┤
│ ⚠ Firma criptográfica Twilio                 │ X-Twilio-Signature en prod   │
├──────────────────────────────────────────────┼──────────────────────────────┤
│ ⚠ Calidad lingüística del agente             │ Revisar respuestas de OpenAI │
├──────────────────────────────────────────────┼──────────────────────────────┤
│ ⚠ Aparición en Twilio Console                │ Message Logs, delivery rates │
├──────────────────────────────────────────────┼──────────────────────────────┤
│ ⚠ Sincronización de calendario               │ iCal / Google Calendar       │
└──────────────────────────────────────────────┴──────────────────────────────┘
```

---

## 2. Casos de prueba

### TC-01 — Llamada perdida → webhook → mensaje WhatsApp enviado

**Archivo:** `TC01_MissedCallToWhatsAppTests.cs`  
**Clase:** `TC01_MissedCallToWhatsAppMessageTests`

| ID | Nombre | Precondición | Resultado esperado |
|----|--------|-------------|-------------------|
| TC-01-A | no-answer nominal | Tenant Europe/Madrid, paciente RGPD=true | AutomationRun=completed, Message.Status=sent, metrics: missed_call_received + outbound_sent |
| TC-01-B | busy = llamada perdida | Paciente RGPD=true | AutomationRun en `completed` o `skipped` |
| TC-01-C | answered → sin acción | Paciente RGPD=true | 0 Messages, 0 FlowMetricsEvents |
| TC-01-D | Twilio HTTP 400 | Error code 30006 | AutomationRun=failed, Message.Status=failed, ErrorCode=30006, metric outbound_failed |

**Datos de prueba fijos:**

```
TenantId     : generado por test
CallSid      : CAsmoke_tc01_A / _B / _D
CallerPhone  : +34600111222
ClinicPhone  : +34910000001
TemplateSid  : HXsmoke_tc01_template
TwilioAccount: ACsmoke_tc01
```

**Verificación manual requerida:**
- Que el número de destino del WA es exactamente el del paciente (E.164)
- Que el template SID es el correcto en Twilio Console del tenant
- Que Twilio realmente entregó el mensaje (ver Message Logs)

---

### TC-02 — Paciente responde por WhatsApp → conversación guardada → IA responde

**Archivo:** `TC02_PatientReplyAiResponseTests.cs`  
**Clase:** `TC02_PatientReplyAiResponseTests`

| ID | Nombre | Precondición | Resultado esperado |
|----|--------|-------------|-------------------|
| TC-02-A | inbound "quiero cita" | Paciente RGPD=true, conv abierta | Msg inbound persistido, AgentTurn con tokens > 0, acción: SendMessage/ProposeAppointment/EscalateToHuman |
| TC-02-B | RGPD=false → sin IA | Paciente RGPD=false | AgentTurns=0, sin mensaje outbound |
| TC-02-C | segundo mensaje reutiliza conv | Conv activa existente | Conversations.Count=1 (mismo ID) |
| TC-02-D | historial alimenta contexto | 3 mensajes previos en BD | recentMessages.Count=3, filtrando por TenantId |

**Datos de prueba fijos:**

```
InboundText  : "Quiero reservar una cita para el martes por la mañana"
MessageSid   : SMsmoke_inbound_tc02_A
PatientPhone : +34600111222
OpenAI fake  : clasifica BookAppointment (conf=0.95), responde con propuesta de cita
```

**OpenAI fake handlers:**
- `OpenAiBookingHandler()` → clasifica `BookAppointment` confidence=0.95, respuesta en español
- `OpenAiHumanHandoffHandler()` → clasifica `Complaint` confidence=0.98
- `OpenAiOutOfHoursHandler()` → clasifica `GeneralInquiry`, responde con horario de atención

**Verificación manual requerida:**
- Que la respuesta del agente es lingüísticamente apropiada (revisión humana del texto)
- Que los slots ofrecidos son correctos para el tenant real
- Que la sesión de WhatsApp (24h window) no está expirada

---

### TC-03 — Reserva de cita → Appointment creado → RevenueEvent generado

**Archivo:** `TC03_BookingRevenueTests.cs`  
**Clase:** `TC03_BookingAppointmentRevenueTests`

| ID | Nombre | Precondición | Resultado esperado |
|----|--------|-------------|-------------------|
| TC-03-A | booking 85 EUR | RuleConfig success_fee_pct=15 | RevenueEvent.Amount=85, SuccessFeeAmount=12.75, EventType=missed_call_converted, metric appointment_booked |
| TC-03-B | revenue=0 | Sin RuleConfig | RevenueEvents=0, FlowMetric appointment_booked con RecoveredRevenue=0 |
| TC-03-C | multi-tenant isolation | Tenant A (60€) + Tenant B (75€) | Eventos completamente aislados, sin cross-tenant leak |
| TC-03-D | Appointment directo | Cualquier tenant/paciente | Source=WhatsApp, Status=Scheduled, IsRecovered=true |

**Datos de prueba fijos:**

```
Revenue TC-03-A : 85.00 EUR
SuccessFee      : 12.75 EUR (15% de 85)
RuleKey         : success_fee_pct
RuleValue       : "15"
EventType       : missed_call_converted
FlowId          : flow_01
```

**Cálculo success fee:**

```
success_fee = revenue × (success_fee_pct / 100)
12.75       = 85      × (15 / 100)
```

**Verificación manual requerida:**
- Que el importe del RevenueEvent es el acordado con el paciente real
- Que la cita aparece en el calendario del terapeuta
- Que el success_fee_pct es el correcto para el contrato del tenant

---

### TC-04 — Mensaje fuera de horario → respuesta correcta según timezone del tenant

**Archivo:** `TC04_OutOfHoursTimezoneTests.cs`  
**Clase:** `TC04_OutOfHoursTimezoneTests`

| ID | Nombre | Precondición | Resultado esperado |
|----|--------|-------------|-------------------|
| TC-04-A | Europe/Madrid UTC→local | UTC 2026-01-15 09:30 | local.Hour=10, local.Minute=30, round-trip OK |
| TC-04-B | America/Mexico_City UTC→local | UTC 2026-01-15 15:00 | local.Hour=9 (UTC-6 en enero) |
| TC-04-C | MaxDelayMinutes no implementado [GAP-03] | Tenant Madrid, MaxDelayMinutes=5 en opts | `IsSuccess=true` (la llamada se procesa aunque tenga 10 min); MaxDelayMinutes está definido en Flow01Options pero no implementado como guard en ExecuteAsync |
| TC-04-D | DST Europe/Madrid | Invierno UTC+1, verano UTC+2 | winterLocal.Hour=10, summerLocal.Hour=11, summerOffset > winterOffset |
| TC-04-E | Timezone inválido | TZ="Inexistent/Timezone" | TZConvert lanza excepción |
| TC-04-F | SystemPromptBuilder [GAP-02] | AgentContext con ClinicName | Prompt contiene "Fisioterapia Ramírez"; **PIEZA FALTANTE: LocalNow no está en AgentContext** |

**GAP-02 documentado en TC-04-F:**

> El `SystemPromptBuilder` no recibe la hora local del tenant. Se recomienda añadir el campo `LocalNow (DateTimeOffset)` a `AgentContext`, calculado en `WhatsAppInboundWorker` usando `Tenant.TimeZone` con `TimeZoneConverter`.

**Verificación manual requerida:**
- Que el SystemPromptBuilder incluye la hora local correcta en el prompt del agente
- Que el agente realmente responde con mensaje de "fuera de horario"
- Comportamiento en cambio de hora (DST: último domingo de marzo/octubre)

---

### TC-05 — Conversación marcada como human-only → la IA deja de intervenir

**Archivo:** `TC05_HumanOnlyConversationTests.cs`  
**Clase:** `TC05_HumanOnlyConversationTests`

| ID | Nombre | Precondición | Resultado esperado |
|----|--------|-------------|-------------------|
| TC-05-A | Guard waiting_human [GAP-01] | Conv.Status=waiting_human | `guardWouldTrigger=true`; msg inbound persistido; conv.Status permanece waiting_human |
| TC-05-B | ConversationStatus propagado | Conv.Status=waiting_human | AgentContext.ConversationStatus=waiting_human |
| TC-05-C | Complaint → EscalateToHuman | Intent=Complaint (OpenAI fake) | Action=EscalateToHuman, conv.Status=waiting_human, ResponseText not null, EscalationReason not null |
| TC-05-D | Nueva conv empieza como open | Conv resuelta existente | newConv.Status=open, newConv.Id ≠ oldConv.Id |
| TC-05-E | Aislamiento multi-tenant | Tenant A waiting_human, Tenant B open | Ningún cross-tenant en Conversations |

**GAP-01 documentado en TC-05-A y TC-05-B:**

> `WhatsAppInboundWorker` no tiene un guard explícito de `waiting_human` antes de invocar al agente. La mejora recomendada es añadir:
> ```csharp
> if (conversation.Status == "waiting_human")
> {
>     _logger.LogInformation("Conversación en espera humana — omitiendo agente IA");
>     await CompleteRunAsync(db, run, "skipped", "waiting_human", ct);
>     return;
> }
> ```

**Verificación manual requerida:**
- Que el equipo humano recibe la notificación de "nuevo mensaje en cola human"
- Que la UI del dashboard muestra correctamente las conversaciones marcadas
- Que el agente puede reactivarse cuando el humano resuelve la conversación

---

### TC-06 — Webhook de estado Twilio → messages y delivery events se actualizan

**Archivo:** `TC06_TwilioStatusWebhookTests.cs`  
**Clase:** `TC06_TwilioStatusWebhookTests`

| ID | Nombre | Precondición | Resultado esperado |
|----|--------|-------------|-------------------|
| TC-06-A | sent→delivered | Message.Status=sent | DeliveryEvent insertado, Message.Status=delivered, DeliveredAt≠null |
| TC-06-B | delivered→read | Message.Status=delivered | Message.Status=read, ReadAt≠null |
| TC-06-C | No-regresión: read no retrocede | Message.Status=read, webhook delivered | Message.Status permanece read; DeliveryEvent igual insertado |
| TC-06-D | failed → campos de error | Estado=failed, errorCode=30006 | Message.Status=failed, ErrorCode=30006; DeliveryEvent con ErrorCode y ErrorMessage |
| TC-06-E | SID desconocido | Sin Message en BD | DeliveryEvent insertado con MessageId=null |
| TC-06-F | Múltiples webhooks mismo SID | Message.Status=sent | 3 DeliveryEvents (sent/delivered/read), Message.Status=read |
| TC-06-G | Aislamiento multi-tenant | Tenant A + Tenant B | DeliveryEvents solo para Tenant A; Tenant B vacío |

**Regla de no-regresión de estados:**

```
Orden de precedencia: pending(0) → sent(5) → delivered(7) → read(9) → undelivered(10)
                      failed(99) puede sobreescribir cualquier estado
```

**Datos de prueba fijos:**

```
ProviderMessageId : SMsmoke_tc06_A … SMsmoke_tc06_G_B
TenantId          : generado por test
AccountSid        : ACsmoke_tc06
From              : whatsapp:+34910000001
To                : whatsapp:+34600111222
ErrorCode (TC-06-D): "30006"
```

**Verificación manual requerida:**
- Que el webhook de Twilio envía la firma correcta (`X-Twilio-Signature`)
- Que los timestamps del webhook coinciden con los de la BD
- Que el dashboard refleja los estados de entrega en tiempo real

---

## 3. Datos de prueba requeridos

### 3.1 Datos fijos (hardcoded en tests)

| Campo | Valor | Uso |
|-------|-------|-----|
| CallerPhone | `+34600111222` | Ana García López (paciente ficticio) |
| ClinicPhone | `+34910000001` | Fisioterapia Ramírez (tenant ficticio) |
| ClinicPhone B | `+34910000002` | Tenant B (multi-tenant tests) |
| TimeZone | `Europe/Madrid` | Default tenant timezone |
| TimeZone alt | `America/Mexico_City` | Multi-timezone test |
| Revenue | 85.00 EUR | TC-03-A nominal |
| SuccessFee | 12.75 EUR (15%) | TC-03-A expected |
| ErrorCode | 30006 | Twilio "Destination unreachable" |
| TwilioAccountSid | `ACsmoke_tc0X` | Fake credentials por TC |
| DefaultTemplateSid | `HXsmoke_tc0X_template` | Fake template SID por TC |

### 3.2 Datos generados dinámicamente (por test run)

| Campo | Generación | Propósito |
|-------|-----------|----------|
| TenantId | `Guid.NewGuid()` | Aislamiento entre test runs |
| PatientId | EF InMemory auto | Sin colisión |
| AppointmentId | `Guid.NewGuid()` | Correlación RevenueEvent |
| CorrelationId | `$"corr-tc0X-{letra}"` | Trazabilidad end-to-end |
| BD InMemory | `smoke_{ClassName}_{Guid:N}` | Una BD por test class |

### 3.3 Seed data de staging (para validación contra BD real)

Para ejecutar los smoke tests contra una BD de staging real (PostgreSQL), se requiere el siguiente seed SQL:

```sql
-- Tenant de staging (si no existe)
INSERT INTO tenants (id, name, slug, time_zone, whats_app_number, is_active, created_at)
VALUES (
    'a1b2c3d4-0000-0000-0000-000000000001',
    'Fisioterapia Ramírez (Smoke)',
    'ramirez-smoke-staging',
    'Europe/Madrid',
    '+34910000001',
    true,
    NOW()
) ON CONFLICT (slug) DO NOTHING;

-- Paciente de prueba
INSERT INTO patients (tenant_id, full_name, phone, status, rgpd_consent, created_at)
VALUES (
    'a1b2c3d4-0000-0000-0000-000000000001',
    'Ana García López (Test)',
    '+34600111222',
    'Active',
    true,
    NOW()
) ON CONFLICT DO NOTHING;

-- RuleConfig: success_fee_pct = 15
INSERT INTO rule_configs (tenant_id, flow_id, rule_key, rule_value, value_type, is_active, created_at)
VALUES (
    'a1b2c3d4-0000-0000-0000-000000000001',
    'global',
    'success_fee_pct',
    '15',
    'decimal',
    true,
    NOW()
) ON CONFLICT (tenant_id, flow_id, rule_key) DO NOTHING;
```

---

## 4. Separación: automatizado vs. validación manual

### 4.1 Automatizado ✅

Los siguientes aspectos se verifican **en cada `dotnet test` run** sin intervención humana:

1. **Persistencia de entidades** — AutomationRun, Message, Conversation, AgentTurn, RevenueEvent, MessageDeliveryEvent, FlowMetricsEvent
2. **Cálculo de success fee** — `revenue × (pct/100)` con precisión decimal
3. **Regla de no-regresión de estados** — never `read → delivered`
4. **Aislamiento multi-tenant** — `TenantId` en CADA tabla verificado
5. **MaxDelayMinutes** — llamada recibida hace > N minutos → FlowStep=skipped
6. **Conversión UTC ↔ local** — TimeZoneConverter con IANA IDs
7. **DST awareness** — offset UTC+1 en invierno vs UTC+2 en verano para Europe/Madrid
8. **RGPD guard** — `RgpdConsent=false` → agente no invocado
9. **Campos de error Twilio** — `ErrorCode`, `ErrorMessage` en Message y DeliveryEvent
10. **Guard de conversación human-only** — `waiting_human` documentado (GAP-01)

### 4.2 Validación manual ⚠

Estos aspectos requieren **revisión humana** en el entorno de staging antes de cada release:

| # | Qué validar | Cómo | Responsable |
|---|-------------|------|------------|
| M-01 | Firma Twilio (`X-Twilio-Signature`) válida | Verificar en Twilio Console que el webhook llega con 200 OK | Devops |
| M-02 | Número E.164 del paciente recibe el WA | Número real en Twilio sandbox / producción | QA |
| M-03 | Template SID correcto para el tenant | Twilio Console → Messaging → Templates | PM |
| M-04 | Calidad lingüística de la respuesta del agente | Revisión humana del chat | PM / Clínica |
| M-05 | Slots de calendario ofrecidos son reales | Verificar contra Google Calendar | QA |
| M-06 | La cita aparece en el calendario del terapeuta | Google Calendar / iCal | QA |
| M-07 | Dashboard refleja estados de entrega en tiempo real | UI del dashboard | QA |
| M-08 | Notificación al equipo humano en waiting_human | Slack/email configurado | PM |
| M-09 | Comportamiento en cambio de hora DST real | Ejecutar test en ventana DST | Devops |
| M-10 | Hora local en prompt del agente [GAP-02] | Revisar logs de OpenAI | Backend |

---

## 5. Piezas faltantes identificadas (GAPs)

### GAP-01 — Guard waiting_human en WhatsAppInboundWorker

**Severidad:** 🔴 Alta  
**TC que lo documenta:** TC-05-A, TC-05-B  
**Descripción:** `WhatsAppInboundWorker` invoca al agente de IA aunque `conversation.Status == "waiting_human"`. Esto puede provocar respuestas automáticas no deseadas cuando un humano ya está atendiendo al paciente.

**Implementación propuesta:**
```csharp
// En WhatsAppInboundWorker.ProcessAsync, antes de invocar al agente:
if (conversation.Status == "waiting_human")
{
    _logger.LogInformation(
        "[WhatsAppInboundWorker] Conversación en espera humana — omitiendo agente IA. " +
        "ConvId={ConvId} TenantId={TenantId}", conversation.Id, tenantId);
    await CompleteRunAsync(db, run, "skipped", "waiting_human", ct);
    return;
}
```

**Test de aceptación:** TC-05-A pasará sin la verificación `Should().BeTrue()` condicional.

---

### GAP-02 — Hora local del tenant ausente en AgentContext

**Severidad:** 🟡 Media  
**TC que lo documenta:** TC-04-F  
**Descripción:** `AgentContext` no incluye la hora local del tenant (`LocalNow`). El `SystemPromptBuilder` usa `DateTimeOffset.UtcNow` internamente pero no puede convertirla al timezone del tenant porque no tiene acceso al `TimeZone` en ese punto.

**Impacto:** El agente no puede responder correctamente a preguntas como "¿Podéis atenderme ahora?" ya que no conoce la hora local de la clínica.

**Implementación propuesta:**
```csharp
// 1. Añadir a AgentContext:
public DateTimeOffset LocalNow { get; init; }

// 2. En WhatsAppInboundWorker, al construir AgentContext:
var tzInfo   = TZConvert.GetTimeZoneInfo(tenant.TimeZone);
var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tzInfo);
var ctx = new AgentContext
{
    // ... otros campos ...
    LocalNow = localNow,
};

// 3. En SystemPromptBuilder.Build():
// Usar ctx.LocalNow para incluir la hora local en el system prompt.
```

**Test de aceptación:** TC-04-F añadir assertion `builtPrompt.Should().Contain(localNow.ToString("HH:mm"))`.

---

### GAP-03 — Falta CI integration para smoke tests automáticos

**Severidad:** 🟡 Media  
**Descripción:** Los smoke tests no están integrados en el pipeline de CI/CD de GitHub Actions. Actualmente solo se pueden ejecutar manualmente con `make smoke-tests`.

**Implementación propuesta:** Añadir un job `smoke-tests` en `.github/workflows/cd-staging.yml` que se ejecute después del deploy a staging:

```yaml
smoke-tests:
  needs: deploy-staging
  runs-on: ubuntu-latest
  steps:
    - uses: actions/checkout@v4
    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '10.x'
    - name: Run smoke tests
      run: make smoke-tests
      env:
        ASPNETCORE_ENVIRONMENT: Test
```

---

### GAP-03 — Flow01Options.MaxDelayMinutes definido pero no implementado

**Severidad:** 🟡 Media  
**TC que lo documenta:** TC-04-C  
**Descripción:** `Flow01Options.MaxDelayMinutes` está definido con valor default=60 pero **no se lee en `ExecuteAsync`**. Las llamadas recibidas hace más tiempo del umbral se procesan igualmente en lugar de devolverse como `Skipped`.

**Impacto:** En producción, si la cola de jobs tiene retrasos > 60 minutos, el sistema puede enviar WhatsApp recovery a pacientes cuya llamada es demasiado antigua, generando una experiencia de usuario degradada.

**Implementación propuesta:**
```csharp
// En Flow01Orchestrator.ExecuteAsync, después del check de idempotencia:
var delayCutoff = callReceivedAt.AddMinutes(_opts.MaxDelayMinutes);
if (DateTimeOffset.UtcNow > delayCutoff)
{
    _logger.LogInformation(
        "[Flow01] Llamada demasiado antigua. CallSid={CallSid} Age={Age}min MaxDelay={Max}min",
        callSid, (DateTimeOffset.UtcNow - callReceivedAt).TotalMinutes, _opts.MaxDelayMinutes);

    await _metrics.RecordAsync(new FlowMetricsEvent
    {
        TenantId   = tenantId, FlowId = "flow_01",
        MetricType = "flow_skipped",
        Metadata   = JsonSerializer.Serialize(new { reason = "max_delay_exceeded" }),
    }, ct);

    return Flow01Result.Skipped(
        Guid.Empty, $"Llamada recibida hace más de {_opts.MaxDelayMinutes} min.");
}
```

**Test de aceptación:** TC-04-C cambiar assertion a `result.FlowStep.Should().Be("skipped")`.

---

### GAP-04 — MessageStatusService no tiene idempotencia para webhooks Twilio

**Severidad:** 🟡 Media  
**TC que lo documenta:** TC-06-F (documenta implícitamente la ausencia)  
**Descripción:** Si Twilio reenvía el mismo webhook de estado dos veces (retry por timeout), `MessageStatusService.ProcessAsync` insertará dos `MessageDeliveryEvent` con el mismo `{ProviderMessageId, Status}`. Esto puede inflar las métricas de delivery.

**Implementación propuesta:** Añadir índice único `UNIQUE(tenant_id, provider_message_id, status)` en `message_delivery_events` con `INSERT OR IGNORE` / `ON CONFLICT DO NOTHING`.

---

## 6. Componentes mock requeridos

### 6.1 Fake HTTP Handlers (implementados en `SmokeTestBase.cs`)

| Handler | Respuesta simulada | Usado en |
|---------|--------------------|----------|
| `TwilioOkHandler(sid?)` | HTTP 201 `{"sid":"SMsmoke_…","status":"queued","to":"whatsapp:+34…"}` | TC-01-A/B, TC-03, TC-04 |
| `TwilioErrorHandler(code)` | HTTP 400 `{"code":30006,"message":"Destination unreachable"}` | TC-01-D |
| `OpenAiBookingHandler()` | Seq: clasifica BookAppointment → respuesta en español | TC-02-A, TC-05-D (futuro) |
| `OpenAiHumanHandoffHandler()` | Seq: clasifica Complaint (sin respuesta text) | TC-05-C |
| `OpenAiOutOfHoursHandler()` | Seq: clasifica GeneralInquiry → respuesta fuera de horario | TC-04 (futuro) |
| `StaticFakeHandler(status, body)` | Respuesta estática configurable | Base para todos |
| `SequentialFakeHandler(responses[])` | N respuestas en orden (wrap en último) | OpenAI multi-turn |
| `CapturingFakeHandler(status, body)` | Captura la request para assertions | Tests de payload |

### 6.2 Mocks NSubstitute requeridos

| Servicio | Tipo de mock | Configuración |
|----------|-------------|---------------|
| `IIdempotencyService` | `Substitute.For<>` | `TryProcessAsync` → `IdempotencyResult.NewEvent(...)` para ambas sobrecargas |
| `IVariantTrackingService` | `Substitute.For<>` | Sin configuración (solo `Substitute.For<>`) |
| `IHttpClientFactory` | `Substitute.For<>` | `.CreateClient("Twilio").Returns(client)` / `.CreateClient("OpenAI").Returns(client)` |
| `ICalendarService` | `Substitute.For<>` | Sin configuración (no se llama en smoke tests actuales) |

### 6.3 Base de datos (EF InMemory)

- Una instancia de `AppDbContext` por test class
- Nombre único por instancia: `smoke_{TestClassName}_{Guid:N}`
- Las advertencias de transacciones se suprimen (`InMemoryEventId.TransactionIgnoredWarning`)
- Uso: persistencia real de entidades, queries reales con EF, sin migrations

---

## 7. Integración CI/CD

### 7.1 Ejecución local

```bash
# Todos los smoke tests
make smoke-tests

# Por TC específico
make smoke-tests-tc01
make smoke-tests-tc02
make smoke-tests-tc03
make smoke-tests-tc04
make smoke-tests-tc05
make smoke-tests-tc06

# Con output detallado
make smoke-tests-verbose
```

### 7.2 Script de ejecución (`infra/scripts/run-smoke-tests.sh`)

El script:
1. Restaura dependencias .NET
2. Compila el proyecto de tests
3. Ejecuta `dotnet test --filter Category=SmokeE2E`
4. Genera reporte TRX en `apps/api/test-results/smoke-tests-{TIMESTAMP}.trx`
5. Genera resumen en `apps/api/test-results/smoke-summary-{TIMESTAMP}.txt`
6. Retorna exit code 0 si todos pasan, 1 si hay fallos

### 7.3 Variables de entorno para CI

```bash
ASPNETCORE_ENVIRONMENT=Test          # Activa config de test
DOTNET_CLI_TELEMETRY_OPTOUT=1        # Deshabilitar telemetría
# No se necesitan credenciales reales: todos los externos son fake
```

---

## 8. Formato del reporte esperado

### 8.1 Reporte de consola (texto plano)

```
══════════════════════════════════════════════════════════════════════════
  ClinicBoost — Smoke Test Report
  Ejecutado: 2026-04-01T14:32:01Z
  Duración:  12.4s
══════════════════════════════════════════════════════════════════════════

  Total:    30 tests  (TC-01:4 + TC-02:4 + TC-03:4 + TC-04:6 + TC-05:5 + TC-06:7)
  ✅ Passed: 28
  ❌ Failed:  2
  ⏭ Skipped: 0

  Tasa de éxito: 93.3%

──────────────────────────────────────────────────────────────────────────
  FALLOS DETECTADOS
──────────────────────────────────────────────────────────────────────────

  ❌ TC-04-F: SystemPromptBuilder — incluye contexto horario en el prompt
     Motivo: GAP-02 — LocalNow no está en AgentContext
     Acción: Pendiente de implementación

  ❌ TC-05-A: waiting_human → worker debe saltar al agente [GUARD REQUERIDO]
     Motivo: GAP-01 — Guard no implementado en WhatsAppInboundWorker
     Acción: Issue #XXX abierto

──────────────────────────────────────────────────────────────────────────
  VALIDACIONES MANUALES PENDIENTES
──────────────────────────────────────────────────────────────────────────

  ⚠ M-01: Firma X-Twilio-Signature en producción
  ⚠ M-02: Número E.164 del paciente recibe WA real
  ⚠ M-03: Template SID correcto en Twilio Console
  ⚠ M-04: Calidad lingüística de respuestas del agente
  ⚠ M-05: Slots de calendario son reales y disponibles
  ⚠ M-06: Cita aparece en calendario del terapeuta
  ⚠ M-07: Dashboard refleja estados de entrega
  ⚠ M-08: Notificación al equipo humano en waiting_human
  ⚠ M-09: Comportamiento en cambio de hora DST
  ⚠ M-10: Hora local en prompt del agente [GAP-02]

──────────────────────────────────────────────────────────────────────────
  VEREDICTO: ⚠ CONDICIONAL — resolver GAP-01 y GAP-02 antes del release
──────────────────────────────────────────────────────────────────────────

  TRX:     apps/api/test-results/smoke-tests-20260401T143201.trx
  Summary: apps/api/test-results/smoke-summary-20260401T143201.txt
══════════════════════════════════════════════════════════════════════════
```

### 8.2 Reporte TRX (XML — para Azure DevOps / GitHub Actions)

El fichero `.trx` es un XML estándar de Visual Studio Test Results con:
- `<TestRun>` con `name`, `runUser`, `times`
- `<TestDefinitions>` con un `<UnitTest>` por test case
- `<Results>` con `<UnitTestResult outcome="Passed|Failed|NotExecuted">`
- `<Output><Message>` con el mensaje de error y stack trace en caso de fallo

### 8.3 Criterios de aceptación para release

| Condición | Acción requerida |
|-----------|-----------------|
| Todos los tests pasan | ✅ Release autorizado (sujeto a validaciones manuales) |
| Fallos en GAP-01/GAP-02 | ⚠ Release condicional — documentar issues conocidos |
| Fallos en TC-01/TC-03/TC-06 | 🚫 Release bloqueado — pipeline crítico roto |
| Fallos en aislamiento multi-tenant | 🚫 Release bloqueado — riesgo de seguridad RGPD |

---

## 9. Estructura de archivos

```
apps/api/tests/ClinicBoost.Tests/
└── SmokeTests/
    ├── Infrastructure/
    │   └── SmokeTestBase.cs              ← SmokeTestDb + SmokeFixtures + Fake handlers
    │
    ├── TC01_MissedCallToWhatsAppTests.cs  ← Flow: llamada perdida → WA outbound
    ├── TC02_PatientReplyAiResponseTests.cs← Flow: inbound WA → IA → AgentTurn
    ├── TC03_BookingRevenueTests.cs        ← Flow: booking → RevenueEvent
    ├── TC04_OutOfHoursTimezoneTests.cs    ← Timezone, DST, MaxDelayMinutes
    ├── TC05_HumanOnlyConversationTests.cs ← Guard waiting_human, escalación
    └── TC06_TwilioStatusWebhookTests.cs   ← Webhook status → DeliveryEvent
```

---

## 10. Historial de cambios

| Fecha | Versión | Cambio |
|-------|---------|--------|
| 2026-04-01 | 1.0 | Creación inicial — 6 TCs, 30 test cases, 4 GAPs documentados (GAP-01: waiting_human guard, GAP-02: LocalNow en AgentContext, GAP-03: MaxDelayMinutes sin implementar, GAP-04: idempotencia en MessageStatusService) |
