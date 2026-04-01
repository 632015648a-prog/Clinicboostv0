# ClinicBoost — Suite de Smoke Tests E2E

> **Estado**: ✅ Implementada — 6 clases de test, 30 casos de prueba (TC-01 a TC-06)  
> **Última revisión**: 2025-04-01  
> **Responsable**: Equipo Backend ClinicBoost

---

## 1. Estrategia de Testing

### 1.1 Objetivo

Esta suite de smoke tests E2E cubre los **flujos críticos de negocio** de ClinicBoost.
Su propósito es detectar regresiones antes de que lleguen a producción y documentar
el comportamiento esperado de cada integración.

### 1.2 Principios de diseño

| Principio | Aplicación |
|-----------|-----------|
| **Aislamiento** | Cada test usa su propio `AppDbContext` con EF InMemory (`smoke_{ClassName}_{Guid}`) |
| **Determinismo** | Sin sleeps, sin dependencias de red. Twilio y OpenAI reemplazados por `FakeHandler` |
| **Realismo** | Los datos de prueba usan formatos reales (E.164, Twilio SIDs, ISO 8601) |
| **Multi-tenant** | Todos los tests críticos incluyen verificación de aislamiento cross-tenant |
| **Documentación de GAPs** | Los tests que verifican piezas faltantes están marcados explícitamente con `[GAP-NN]` |

### 1.3 Infraestructura de test

```
SmokeTests/
├── Infrastructure/
│   └── SmokeTestBase.cs          ← Base class + SmokeFixtures + FakeHandlers
├── TC01_MissedCallToWhatsAppTests.cs
├── TC02_PatientReplyAiResponseTests.cs
├── TC03_BookingRevenueTests.cs
├── TC04_OutOfHoursTimezoneTests.cs
├── TC05_HumanOnlyConversationTests.cs
└── TC06_TwilioStatusWebhookTests.cs
```

#### Clases base y helpers

| Clase | Propósito |
|-------|-----------|
| `SmokeTestDb` | Crea un `AppDbContext` EF InMemory aislado por instancia de test |
| `SmokeFixtures` | Fábricas de datos: `SeedTenantAsync`, `SeedPatientAsync`, `SeedConversationAsync`, `SeedOutboundMessageAsync`, `SeedRuleConfigAsync` |
| `StaticFakeHandler` | Devuelve siempre la misma respuesta HTTP (Twilio OK/error) |
| `SequentialFakeHandler` | Secuencia de respuestas OpenAI (1ª=clasificación, 2ª=respuesta principal) |
| `CapturingFakeHandler` | Captura la última `HttpRequestMessage` para assertions sobre el payload enviado |
| `TwilioOkHandler(sid?)` | HTTP 201 con `{"sid":"SMsmoke_xxx","status":"queued","to":"whatsapp:+34600111222"}` |
| `TwilioErrorHandler(code)` | HTTP 400 con `{"code":30006,"message":"Destination unreachable"}` |
| `OpenAiBookingHandler()` | Secuencia: `BookAppointment` (confianza 0.95) → respuesta de confirmación de cita |
| `OpenAiHumanHandoffHandler()` | `Complaint` (confianza 0.98) → derivación a humano |
| `OpenAiOutOfHoursHandler()` | `GeneralInquiry` (confianza 0.90) → mensaje fuera de horario |

### 1.4 Tags xUnit

Todos los tests de esta suite tienen los traits:
- `[Trait("Category", "SmokeE2E")]` — filtraje por categoría
- `[Trait("TC", "TC-0X")]` — identificación del caso de prueba

**Ejecución filtrada:**
```bash
# Todos los smoke tests
dotnet test --filter "Category=SmokeE2E"

# Solo TC-01
dotnet test --filter "Category=SmokeE2E&TC=TC-01"

# Via Makefile
make smoke-tests           # todos
make smoke-tests-tc01      # solo TC-01
make smoke-tests-verbose   # con output completo
```

---

## 2. Catálogo de Casos de Prueba

### TC-01: Llamada perdida → webhook → mensaje WhatsApp

**Flujo**: `Twilio Voice webhook` → `MissedCallJob` → `Flow01Orchestrator` → `TwilioOutboundMessageSender (fake)` → `Message` persistido → `FlowMetricsEvents`

| ID | Nombre | Precondición | Resultado esperado |
|----|--------|-------------|-------------------|
| TC-01-A | no-answer → mensaje enviado y AutomationRun=completed | Paciente RGPD=true, Tenant activo | AutomationRun=completed, Message.Status=sent, métricas missed_call_received+outbound_sent |
| TC-01-B | busy → tratado igual que no-answer | Paciente RGPD=true | AutomationRun en {completed, skipped} |
| TC-01-C | answered call → sin mensaje, sin run | — | Cero Messages, cero FlowMetricsEvents |
| TC-01-D | Twilio 400 → message=failed, run=failed | Twilio devuelve error 30006 | AutomationRun=failed, Message.Status=failed, ErrorCode=30006, métrica outbound_failed |

**Datos de prueba**:
```
TwilioOptions:   AccountSid=ACsmoke_tc01, AuthToken=smoke_auth_token_tc01
CallSid:         CAsmoke_tc01_A / _B / _D
CallerPhone:     +34600111222 (Ana García López)
ClinicPhone:     +34910000001
TemplateSid:     HXsmoke_tc01_template
TwilioOK SID:   SMtc01_ok
TwilioError:     code=30006
```

**Validación manual requerida**:
- ✋ Que el número de destino en Twilio Console coincide con el del paciente (E.164)
- ✋ Que el template SID `HXmissed_call_recovery_v1` está aprobado en Twilio para el tenant en producción
- ✋ Que el mensaje real se entregó (Twilio Message Logs)

---

### TC-02: Paciente responde por WhatsApp → conversación guardada → IA responde

**Flujo**: `POST /webhooks/whatsapp/inbound` → `ConversationService.AppendInboundMessage` → `ConversationalAgent (fake OpenAI)` → `AgentTurn` persistido

| ID | Nombre | Precondición | Resultado esperado |
|----|--------|-------------|-------------------|
| TC-02-A | inbound "quiero cita" → AgentTurn persistido, acción válida | Conversación open, RGPD=true | Mensaje inbound en BD, AgentTurn con tokens, acción en {SendMessage, ProposeAppointment, EscalateToHuman} |
| TC-02-B | RGPD=false → agente no invocado | Paciente sin consentimiento | Cero AgentTurns |
| TC-02-C | segundo mensaje → misma conversación reutilizada | Conversación activa preexistente | `UpsertConversationAsync` devuelve la misma conv.Id, COUNT(Conversations)=1 |
| TC-02-D | mensajes previos en BD se incluyen como contexto | 3 mensajes seed en conversación | RecentMessages.Count=3, todos con TenantId correcto |

**Datos de prueba**:
```
InboundText:     "Quiero reservar una cita para el martes por la mañana"
MessageSid:      SMsmoke_inbound_tc02_A
PatientPhone:    +34600111222
ConvFlowId:      flow_00
OpenAI intent:   BookAppointment (confidence=0.95)
```

**Validación manual requerida**:
- ✋ Que la respuesta del agente es lingüísticamente apropiada en español
- ✋ Que los slots de cita propuestos corresponden a la agenda real del tenant
- ✋ Que la sesión WhatsApp Business (24h window) no está expirada en el momento del test en staging

---

### TC-03: Reserva de cita → Appointment creado → RevenueEvent generado

**Flujo**: `Flow01Orchestrator.RecordAppointmentBookedAsync` → `FlowMetricsEvent appointment_booked` → `RevenueEvent` (si revenue > 0)

| ID | Nombre | Precondición | Resultado esperado |
|----|--------|-------------|-------------------|
| TC-03-A | booking 85 EUR → RevenueEvent con 12.75 EUR success fee | RuleConfig success_fee_pct=15 | RevenueEvent.Amount=85, SuccessFeeAmount=12.75, EventType=missed_call_converted, DurationMs∈[3min,6min] |
| TC-03-B | revenue=0 → no RevenueEvent, sí FlowMetric appointment_booked | — | COUNT(RevenueEvents)=0, FlowMetric appointment_booked con RecoveredRevenue=0 |
| TC-03-C | dos tenants → revenue_events separados, sin cross-tenant | Tenant A (60€) y Tenant B (75€) | Cada tenant solo ve su RevenueEvent, sin cross-tenant leak |
| TC-03-D | appointment manual → Source=WhatsApp, Status=Scheduled | — | Appointment.Source=WhatsApp, IsRecovered=true, TenantId/PatientId correctos |

**Datos de prueba**:
```
TwilioOptions:   AccountSid=ACsmoke_tc03, AuthToken=smoke_auth_token_tc03
Revenue TC-03-A: 85.00 EUR
SuccessFee:      12.75 EUR (15% de 85)
SuccessFeePct:   15 (RuleConfig global/success_fee_pct)
OutboundSentAt:  UtcNow - 4 minutos (simula conversación real de 4 min)
Tenant A:        tenantId, patientPhone=+34600111111
Tenant B:        tenantId2, patientPhone=+34600222222
```

**Validación manual requerida**:
- ✋ Que el importe en el `RevenueEvent` real coincide con el precio acordado con el paciente
- ✋ Que la cita aparece correctamente en Google Calendar del terapeuta
- ✋ Que `success_fee_pct` en `RuleConfig` es el correcto para el contrato del tenant

---

### TC-04: Mensaje fuera de horario → respuesta correcta según timezone del tenant

**Flujo**: Conversión UTC → hora local del tenant usando `TimeZoneConverter` (IANA IDs) → verificación de MaxDelayMinutes

| ID | Nombre | Precondición | Resultado esperado |
|----|--------|-------------|-------------------|
| TC-04-A | Europe/Madrid — conversión UTC→local correcta | Tenant tz=Europe/Madrid | 09:30 UTC → 10:30 Madrid (UTC+1 en enero), round-trip reversible |
| TC-04-B | America/Mexico_City — UTC-6 en invierno | Tenant tz=America/Mexico_City | 15:00 UTC → 09:00 México (UTC-6 en enero) |
| TC-04-C | MaxDelayMinutes — cutoff desde UtcNow | Llamada hace 10min, MaxDelay=5min | FlowStep=skipped, cero Messages, métrica flow_skipped o missed_call_received |
| TC-04-D | DST en Europe/Madrid — cambio de hora no rompe conversión | — | Invierno UTC+1 (10h), Verano UTC+2 (11h), summerOffset > winterOffset |
| TC-04-E | timezone inválido → TZConvert lanza excepción descriptiva | Tz="Inexistent/Timezone" | `TZConvert.GetTimeZoneInfo` lanza Exception |
| TC-04-F | SystemPromptBuilder — incluye contexto horario en el prompt | Agente con ctx completo | Prompt no vacío, contiene "Fisioterapia Ramírez" |

**Datos de prueba**:
```
Timezones probados:    Europe/Madrid, America/Mexico_City, Inexistent/Timezone
Instante UTC fijo:     2026-01-15 09:30 UTC (invierno), 2026-07-15 09:00 UTC (verano)
MaxDelayMinutes TC-04-C: 5 minutos
CallReceivedAt TC-04-C: UtcNow - 10 minutos
```

**GAP documentado (GAP-02)**:
> `WhatsAppInboundWorker` no pasa la hora local del tenant al `AgentContext`.  
> El `SystemPromptBuilder` conoce `ClinicName` y `LanguageCode` pero no `LocalNow`.  
> **Recomendación**: añadir `LocalNow (DateTimeOffset)` a `AgentContext`,  
> calculado en `WhatsAppInboundWorker` usando `Tenant.TimeZone + TZConvert`.

**Validación manual requerida**:
- ✋ Que el SystemPromptBuilder incluye la hora local correcta (pendiente GAP-02)
- ✋ Que el agente responde con el mensaje de "fuera de horario" en staging
- ✋ Comportamiento correcto en el cambio de hora (DST: último domingo de marzo/octubre)

---

### TC-05: Conversación marcada como human-only → la IA deja de intervenir

**Flujo**: Guardia `conversation.Status == "waiting_human"` → bloqueo del agente → mensaje inbound persistido igualmente

| ID | Nombre | Precondición | Resultado esperado |
|----|--------|-------------|-------------------|
| TC-05-A | waiting_human → worker debe saltar al agente [GUARD REQUERIDO] | Conv.Status=waiting_human | guardWouldTrigger=true, mensaje inbound persistido, Status sigue en waiting_human |
| TC-05-B | ConversationStatus=waiting_human se propaga en AgentContext | Conv.Status=waiting_human | AgentCtx.ConversationStatus=waiting_human, isWaitingHuman=true |
| TC-05-C | intención Complaint → EscalateToHuman → conv.Status=waiting_human | Conv.Status=open, OpenAI=Complaint | Result.Action=EscalateToHuman, Conv.Status→waiting_human, ResponseText no vacío, EscalationReason no vacío |
| TC-05-D | nueva conversación siempre empieza con status=open | Conv.Status=resolved (anterior) | Nueva conv.Status=open, nueva conv.Id ≠ conv.anterior.Id |
| TC-05-E | waiting_human de tenant A no afecta al tenant B | Conv A=waiting_human, Conv B=open | Aislamiento completo, sin cross-tenant leak |

**Datos de prueba**:
```
ConvStatus:      waiting_human (TC-05-A, B, E), resolved (TC-05-D), open (TC-05-C)
InboundText TC-05-C: "¡Esto es un escándalo! Me operaron mal y exijo hablar con alguien ahora."
OpenAI TC-05-C:  Complaint (confidence=0.98)
MessageSids:     SMsmoke_tc05_A_inbound, SMsmoke_tc05_B, SMsmoke_tc05_C
Tenant B phone:  +34600222222 (Paciente B)
```

**GAP documentado (GAP-01)**:
> `WhatsAppInboundWorker` NO tiene una guardia explícita de `waiting_human`  
> antes de invocar al agente. El status se pasa en `AgentContext.ConversationStatus`  
> pero `ConversationalAgent.HandleAsync` no lo usa para auto-escalar.  
> **Recomendación**: añadir en `WhatsAppInboundWorker` antes del `HandleAsync`:
> ```csharp
> if (conversation.Status == "waiting_human") {
>     _logger.LogInformation("Conversación en espera humana — omitiendo agente IA");
>     await CompleteRunAsync(db, run, "skipped", "waiting_human", ct);
>     return;
> }
> ```

**Validación manual requerida**:
- ✋ Que el equipo humano recibe notificación de nuevo mensaje en cola "waiting_human"
- ✋ Que la UI del dashboard muestra correctamente las conversaciones marcadas
- ✋ Que el agente puede reactivarse cuando el humano marca la conversación como resuelta

---

### TC-06: Webhook de estado Twilio → messages y delivery events se actualizados

**Flujo**: `POST /webhooks/whatsapp/status` → `MessageStatusService.ProcessAsync` → `MessageDeliveryEvent` (insert-only) + `Message.Status` (regla no-regresión)

| ID | Nombre | Precondición | Resultado esperado |
|----|--------|-------------|-------------------|
| TC-06-A | sent→delivered → DeliveryEvent insertado + Message.Status=delivered | Message.Status=sent | DeliveryEvent.Status=delivered, Message.Status=delivered, DeliveredAt establecido |
| TC-06-B | delivered→read → ReadAt establecido | Message.Status=delivered | Message.Status=read, ReadAt no null |
| TC-06-C | no-regresión — read no retrocede a delivered | Message.Status=read | Message.Status sigue en read, DeliveryEvent con status=delivered sí insertado |
| TC-06-D | failed → ErrorCode + ErrorMessage en Message y DeliveryEvent | Message.Status=sent | Message.Status=failed, ErrorCode=30006, DeliveryEvent.ErrorCode=30006 |
| TC-06-E | SID desconocido → DeliveryEvent insertado con MessageId=null | Ningún message en BD | DeliveryEvent insertado, MessageId=null (para auditoría) |
| TC-06-F | múltiples webhooks mismo SID → múltiples DeliveryEvents (log completo) | Message.Status=sent | 3 DeliveryEvents (sent/delivered/read), Message.Status final=read |
| TC-06-G | DeliveryEvents filtrados por TenantId — no hay cross-tenant | Tenant A y B con mensajes distintos | eventsA.Count=1, eventsB.Count=0, sin cross-tenant |

**Datos de prueba**:
```
AccountSid:       ACsmoke_tc06
MessageSids:      SMsmoke_tc06_A / _B / _C / _D / _E_unknown / _F / _G_A / _G_B
From:             whatsapp:+34910000001
To:               whatsapp:+34600111222
ErrorCode TC-06-D: "30006"
ErrorMessage:     "Destination unreachable. Twilio is unable to route this message."
Regla no-regresión: pending(1) < sent(2) < delivered(5) < read(7) < failed(override)
```

**Validación manual requerida**:
- ✋ Que el webhook real de Twilio envía la firma `X-Twilio-Signature` correcta
- ✋ Que los timestamps del webhook coinciden con los almacenados en BD
- ✋ Que los errores de Twilio (30006=destination unreachable, 30007=carrier violation) se registran correctamente
- ✋ Que el dashboard refleja los estados de entrega en tiempo real

---

## 3. Datos de Prueba Requeridos

### 3.1 Datos en memoria (EF InMemory — no requieren BD real)

Todos los tests de esta suite utilizan EF InMemory. Los datos se crean mediante
`SmokeFixtures` en el método `Arrange` de cada test.

| Entidad | Valores ficticios | Propósito |
|---------|------------------|-----------|
| `Tenant` | Id=Guid.NewGuid(), Name="Fisioterapia Ramírez (Smoke)", Slug="ramirez-smoke-{8char}", TimeZone="Europe/Madrid", WhatsAppNumber="+34910000001" | Tenant base para todos los tests |
| `Patient` | FullName="Ana García López", Phone="+34600111222", RgpdConsent=true | Paciente tipo con RGPD |
| `Conversation` | Channel=whatsapp, FlowId=flow_00, Status=open, AiContext="{}" | Conversación activa |
| `Message (outbound)` | Direction=outbound, Status=sent, SentAt=UtcNow-2min, ProviderMessageId="SMsmoke_outbound_001" | Mensaje de referencia para duración |
| `RuleConfig` | FlowId=global, RuleKey=success_fee_pct, RuleValue=15, ValueType=decimal | Fee de éxito para TC-03 |

### 3.2 Credenciales y SIDs de prueba

> ⚠ **NUNCA usar credenciales reales de Twilio/OpenAI en tests**

| Variable | Valor de prueba |
|----------|----------------|
| `TwilioOptions.AccountSid` | `ACsmoke_tc0N` (N = número de TC) |
| `TwilioOptions.AuthToken` | `smoke_auth_token_tc0N` |
| Twilio OK SID | `SMsmoke_<tipo>_<contexto>` |
| Twilio Error Code | `30006` (Destination unreachable) |
| OpenAI API Key | No necesario — reemplazado por `SequentialFakeHandler` |

### 3.3 Datos de staging (para validación manual en entorno real)

Para ejecutar contra el entorno de staging con datos reales, se necesita:

```bash
# .env.staging (nunca comitear)
TWILIO_ACCOUNT_SID=ACxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
TWILIO_AUTH_TOKEN=<token real>
TWILIO_TEMPLATE_SID_MISSED_CALL=HXmissed_call_recovery_v1
OPENAI_API_KEY=sk-proj-...
STAGING_TENANT_ID=<UUID del tenant de staging>
STAGING_PATIENT_PHONE=+34600999888  # número de prueba real
```

---

## 4. Separación: Automático vs Manual

### 4.1 Totalmente automatizado ✅

| Flujo | Tests | Verificación |
|-------|-------|--------------|
| Llamada perdida → WhatsApp (fake Twilio) | TC-01-A, B, C, D | AutomationRun, Message, FlowMetrics en EF InMemory |
| Inbound WhatsApp → conversación → agente IA (fake OpenAI) | TC-02-A, B, C, D | Message inbound, AgentTurn, Conversation.AiContext |
| Reserva → RevenueEvent + success fee | TC-03-A, B, C, D | RevenueEvent, FlowMetricsEvent, Appointment en EF InMemory |
| Conversión timezone UTC→local (IANA) | TC-04-A, B, D, E | TimeZoneConverter assertions puras |
| MaxDelayMinutes — cutoff de procesamiento | TC-04-C | FlowStep=skipped, cero Messages |
| waiting_human — guardia y propagación de estado | TC-05-A, B, D, E | Conv.Status, AgentCtx.ConversationStatus |
| Complaint → EscalateToHuman (fake OpenAI) | TC-05-C | AgentResult.Action, Conv.Status=waiting_human |
| Webhook estado Twilio → DeliveryEvent | TC-06-A a G | MessageDeliveryEvent, Message.Status, timestamps, ErrorCode |
| Regla no-regresión de estado de mensaje | TC-06-C, F | Message.Status no retrocede, DeliveryEvent sí se inserta |
| Aislamiento multi-tenant | TC-03-C, TC-05-E, TC-06-G | Queries por TenantId, sin cross-tenant leak |

### 4.2 Validación manual requerida ⚠

| Verificación | TC relacionado | Responsable | Frecuencia |
|--------------|---------------|-------------|-----------|
| Mensaje real entregado en WhatsApp del paciente | TC-01 | QA / Dev Lead | Antes de cada release |
| Template SID aprobado en Twilio Console por tenant | TC-01 | Ops / Customer Success | Una vez por tenant |
| Lingüística apropiada de respuesta del agente | TC-02 | Product / QA | Revisión quincenal |
| Slots propuestos corresponden a agenda real | TC-02 | Customer Success | Con cada tenant nuevo |
| Sesión WhatsApp Business (24h window) activa | TC-02 | QA | Antes de cada release |
| Importe RevenueEvent ↔ precio acordado con paciente | TC-03 | Finance / Customer Success | Mensualmente |
| Cita en Google Calendar del terapeuta | TC-03 | QA | Antes de cada release |
| `success_fee_pct` correcto por contrato de tenant | TC-03 | Finance | Al crear/modificar tenant |
| Prompt incluye hora local del tenant (GAP-02) | TC-04 | Backend Dev | Pendiente implementación |
| Respuesta "fuera de horario" correcta en staging | TC-04 | QA | Antes de cada release |
| Cambio de hora DST (marzo/octubre) | TC-04 | QA | 2 veces al año |
| Notificación equipo humano al escalar | TC-05 | QA / Ops | Antes de cada release |
| Dashboard muestra conversación en waiting_human | TC-05 | QA / Frontend | Con cada release |
| Firma X-Twilio-Signature válida en webhook | TC-06 | QA / Security | Antes de cada release |
| Timestamps BD coinciden con Twilio | TC-06 | QA | Antes de cada release |
| Errores Twilio (30006, 30007) en dashboard | TC-06 | QA | Antes de cada release |

---

## 5. Piezas Faltantes Identificadas (GAPs)

### GAP-01: Guard `waiting_human` en `WhatsAppInboundWorker`

**Estado**: ⚠ Documentado, no implementado  
**Impacto**: La IA puede responder a mensajes de pacientes en conversaciones ya escaladas a humano  
**Tests afectados**: TC-05-A, TC-05-B  
**Solución propuesta**:
```csharp
// En WhatsAppInboundWorker, antes de invocar ConversationalAgent.HandleAsync:
if (conversation.Status == "waiting_human")
{
    _logger.LogInformation(
        "[WhatsAppInboundWorker] Conversación {ConvId} en estado waiting_human — omitiendo agente IA",
        conversation.Id);
    await CompleteRunAsync(db, run, "skipped", "waiting_human_guard", ct);
    return;
}
```

### GAP-02: `LocalNow` ausente en `AgentContext`

**Estado**: ⚠ Documentado, no implementado  
**Impacto**: El agente no puede dar respuestas contextualizadas a la hora local del tenant ("Son las 22:00, nuestro horario es de 9:00 a 19:00")  
**Tests afectados**: TC-04-F  
**Solución propuesta**:
```csharp
// En AgentContext, añadir:
public DateTimeOffset LocalNow { get; init; }  // UtcNow convertido al timezone del tenant

// En WhatsAppInboundWorker, al construir AgentContext:
var tzInfo = TZConvert.GetTimeZoneInfo(tenant.TimeZone);
var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tzInfo);
var ctx = new AgentContext { ..., LocalNow = localNow };

// En SystemPromptBuilder.Build(), incluir:
sb.AppendLine($"Hora actual en la clínica: {ctx.LocalNow:HH:mm} ({tenant.TimeZone})");
```

---

## 6. Integración CI/CD

### 6.1 Makefile targets

```bash
make smoke-tests              # Todos los smoke tests (Category=SmokeE2E)
make smoke-tests-tc01         # Solo TC-01
make smoke-tests-tc02         # Solo TC-02
make smoke-tests-tc03         # Solo TC-03
make smoke-tests-tc04         # Solo TC-04
make smoke-tests-tc05         # Solo TC-05
make smoke-tests-tc06         # Solo TC-06
make smoke-tests-verbose      # Todos con output verboso
```

### 6.2 Script CI (`infra/scripts/run-smoke-tests.sh`)

El script acepta las siguientes variables de entorno:
- `TC=TC-01` — ejecutar solo un caso de prueba específico
- `VERBOSE=1` — activar output verboso de xUnit
- `BAIL=1` — detener ejecución en el primer fallo

**Salidas**:
- `apps/api/test-results/smoke-tests-{TIMESTAMP}.trx` — reporte TRX (importable en CI)
- `apps/api/test-results/smoke-summary-{TIMESTAMP}.txt` — resumen de consola

### 6.3 GitHub Actions (recomendado)

```yaml
# .github/workflows/smoke-tests.yml
name: Smoke Tests E2E

on:
  push:
    branches: [main, develop]
  pull_request:
    branches: [main]

jobs:
  smoke:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Restore dependencies
        run: dotnet restore apps/api/tests/ClinicBoost.Tests/ClinicBoost.Tests.csproj
      - name: Run smoke tests
        run: |
          dotnet test apps/api/tests/ClinicBoost.Tests/ClinicBoost.Tests.csproj \
            --filter "Category=SmokeE2E" \
            --logger "trx;LogFileName=smoke-results.trx" \
            --results-directory apps/api/test-results
      - name: Publish test results
        uses: dorny/test-reporter@v1
        if: always()
        with:
          name: Smoke Tests E2E
          path: apps/api/test-results/smoke-results.trx
          reporter: dotnet-trx
```

---

## 7. Formato del Reporte Final Esperado

### 7.1 Reporte de consola (smoke-summary-{TIMESTAMP}.txt)

```
════════════════════════════════════════════════════
 ClinicBoost Smoke Test Suite — Resumen
════════════════════════════════════════════════════
 Fecha:          2025-04-01 14:32:07 UTC
 Duración:       12.4 s
 Entorno:        LOCAL (EF InMemory)
────────────────────────────────────────────────────
 Total:          30
 ✅ Pasados:     28
 ❌ Fallidos:     0
 ⚠  Saltados:     2  [TC-05-A, TC-04-F — GAPs documentados]
════════════════════════════════════════════════════

 RESUMEN POR TC
 ─────────────────────────────────────────────────
 TC-01  Llamada perdida → WhatsApp    4/4  ✅ PASS
 TC-02  Inbound → Conversación → IA  4/4  ✅ PASS
 TC-03  Reserva → Revenue Event      4/4  ✅ PASS
 TC-04  Fuera de horario / Timezone  6/6  ✅ PASS
 TC-05  Conversación human-only      5/5  ✅ PASS
 TC-06  Twilio status webhook        7/7  ✅ PASS
 ─────────────────────────────────────────────────
 RESULTADO FINAL: ✅ PASS (sin fallos críticos)

 GAPs pendientes de implementación:
   · GAP-01: Guard waiting_human en WhatsAppInboundWorker
   · GAP-02: LocalNow en AgentContext para SystemPromptBuilder

 Reporte TRX: apps/api/test-results/smoke-tests-20250401-143207.trx
════════════════════════════════════════════════════
```

### 7.2 Reporte TRX (para CI y Azure DevOps)

El archivo `.trx` generado por xUnit es compatible con:
- Azure DevOps Test Results
- GitHub Actions con `dorny/test-reporter`
- JetBrains TeamCity
- Visual Studio Test Explorer

Campos relevantes por test:
```xml
<UnitTestResult testName="TC-01-A: no-answer → mensaje enviado y AutomationRun=completed"
                outcome="Passed"
                duration="00:00:00.342"
                computerName="build-agent" />
```

### 7.3 Criterios de aprobación (Definition of Done — Smoke)

| Criterio | Umbral |
|----------|--------|
| Tests automatizados pasados | 100% (excepto GAPs documentados) |
| Tiempo total ejecución | < 30 segundos |
| Sin cross-tenant leak | 0 fallos en TC-03-C, TC-05-E, TC-06-G |
| Cobertura de flujos críticos | 6/6 flujos cubiertos |
| GAPs sin regresión | Los GAPs existentes no deben empeorar |

---

## 8. Ejecución Rápida

### Requisitos
- .NET 10 SDK
- Ejecutar desde la raíz del repositorio

### Comandos

```bash
# Restaurar paquetes
dotnet restore apps/api/tests/ClinicBoost.Tests/ClinicBoost.Tests.csproj

# Ejecutar todos los smoke tests
dotnet test apps/api/tests/ClinicBoost.Tests/ClinicBoost.Tests.csproj \
  --filter "Category=SmokeE2E" \
  --verbosity normal

# Ejecutar un TC específico con output detallado
dotnet test apps/api/tests/ClinicBoost.Tests/ClinicBoost.Tests.csproj \
  --filter "Category=SmokeE2E&TC=TC-01" \
  --verbosity detailed

# Via Makefile (desde la raíz)
make smoke-tests
make smoke-tests-tc06
make smoke-tests-verbose
```

---

*Documento generado automáticamente por el proceso de implementación de la suite de smoke tests E2E de ClinicBoost.*  
*Para actualizar este documento, revisar `/apps/api/tests/ClinicBoost.Tests/SmokeTests/`.*
