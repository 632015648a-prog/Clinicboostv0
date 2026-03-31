-- ============================================================
-- ClinicBoost — Esquema completo de negocio
-- Versión : 20260329000003
-- Depende : 20260329000001 (tenants, patients, appointments,
--                           processed_events, audit_logs)
--
-- Tablas nuevas en este fichero:
--   tenant_users, patient_consents, calendar_connections,
--   appointment_events, conversations, messages,
--   waitlist_entries, rule_configs, revenue_events,
--   automation_runs, webhook_events
--
-- Convenciones globales (NO se reabren):
--   · Toda tabla de negocio lleva tenant_id UUID NOT NULL
--   · Fechas: TIMESTAMPTZ, siempre UTC
--   · Claves primarias: UUID v4
--   · Nombres: snake_case
--   · El campo updated_at se mantiene mediante trigger set_updated_at()
--     (ya creado en 0001)
-- ============================================================


-- ══════════════════════════════════════════════════════════════
-- 1. TENANT_USERS
-- ══════════════════════════════════════════════════════════════
-- Propósito: Vincula usuarios de Supabase Auth con un tenant
-- y define su rol dentro de la clínica (admin, therapist,
-- receptionist). Un mismo auth.user puede pertenecer a varios
-- tenants con roles distintos.
-- ══════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS tenant_users (
  id            UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id     UUID        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,

  -- auth_user_id referencia a auth.users de Supabase (sin FK hard para no
  -- acoplar el esquema a la tabla interna de GoTrue)
  auth_user_id  UUID        NOT NULL,

  role          TEXT        NOT NULL DEFAULT 'therapist'
                  CHECK (role IN ('owner', 'admin', 'therapist', 'receptionist')),
  -- 'owner'       → propietario de la clínica, puede borrar el tenant
  -- 'admin'       → gestión completa sin poder borrar el tenant
  -- 'therapist'   → solo ve sus propias citas y pacientes asignados
  -- 'receptionist'→ gestión de agenda, sin acceso a configuración

  full_name     TEXT        NOT NULL,
  email         TEXT        NOT NULL,
  is_active     BOOLEAN     NOT NULL DEFAULT TRUE,

  -- Auditoría de último acceso (útil para detección de cuentas abandonadas)
  last_login_at TIMESTAMPTZ,

  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),

  -- Un auth_user solo puede tener un rol por tenant
  UNIQUE (tenant_id, auth_user_id)
);

CREATE INDEX idx_tenant_users_tenant_id    ON tenant_users(tenant_id);
CREATE INDEX idx_tenant_users_auth_user_id ON tenant_users(auth_user_id);
CREATE INDEX idx_tenant_users_role         ON tenant_users(tenant_id, role)
  WHERE is_active = TRUE;

CREATE TRIGGER trg_tenant_users_updated_at
  BEFORE UPDATE ON tenant_users
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();


-- ══════════════════════════════════════════════════════════════
-- 2. PATIENT_CONSENTS
-- ══════════════════════════════════════════════════════════════
-- Propósito: Registro granular de consentimientos RGPD por
-- paciente y tipo de comunicación (Flow 00). Cada fila es un
-- consentimiento inmutable; la revocación se hace con una nueva
-- fila action='revoked'. Nunca se actualiza ni borra.
-- ══════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS patient_consents (
  id              UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id       UUID        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  patient_id      UUID        NOT NULL REFERENCES patients(id) ON DELETE CASCADE,

  consent_type    TEXT        NOT NULL
                    CHECK (consent_type IN (
                      'whatsapp_marketing',   -- envío de mensajes comerciales
                      'whatsapp_transactional',-- recordatorios y confirmaciones
                      'email_marketing',
                      'email_transactional',
                      'nps_survey',           -- encuestas NPS (Flow 05)
                      'data_processing'       -- tratamiento genérico de datos
                    )),

  action          TEXT        NOT NULL CHECK (action IN ('granted', 'revoked')),
  -- 'granted' = paciente ha otorgado el consentimiento
  -- 'revoked' = paciente ha retirado el consentimiento

  consent_version TEXT        NOT NULL,       -- e.g. 'v1.0', 'v2.0'
  channel         TEXT        NOT NULL DEFAULT 'whatsapp'
                    CHECK (channel IN ('whatsapp', 'web', 'in_person', 'email')),
  ip_address      INET,                       -- solo si el canal es web
  user_agent      TEXT,                       -- idem

  -- Texto literal del aviso legal presentado (hash SHA-256)
  legal_text_hash TEXT,

  created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
  -- Sin updated_at: este registro es inmutable
);

CREATE INDEX idx_patient_consents_patient_id   ON patient_consents(patient_id);
CREATE INDEX idx_patient_consents_tenant_id    ON patient_consents(tenant_id);
CREATE INDEX idx_patient_consents_type_action  ON patient_consents(patient_id, consent_type, action);
-- Índice parcial para consultar el estado activo de cada tipo de consentimiento
CREATE INDEX idx_patient_consents_granted      ON patient_consents(patient_id, consent_type)
  WHERE action = 'granted';


-- ══════════════════════════════════════════════════════════════
-- 3. CALENDAR_CONNECTIONS
-- ══════════════════════════════════════════════════════════════
-- Propósito: Almacena la configuración de integración con cada
-- ERP / sistema de agenda externo. Un tenant puede conectar
-- varios calendarios (p.ej. Clinicalia + Google Calendar).
-- Los tokens OAuth se guardan cifrados; la app nunca los expone
-- en texto plano en logs ni en la API pública.
-- ══════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS calendar_connections (
  id                UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id         UUID        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,

  provider          TEXT        NOT NULL
                      CHECK (provider IN (
                        'ical',           -- iCal feed de solo lectura
                        'google',         -- Google Calendar API
                        'clinicalia',     -- ERP Clinicalia
                        'fisify',         -- ERP Fisify
                        'janeapp',        -- Jane App
                        'custom_ical'     -- cualquier feed iCal genérico
                      )),

  display_name      TEXT        NOT NULL,   -- nombre amigable para la UI
  ical_url          TEXT,                   -- para provider = 'ical' / 'custom_ical'

  -- OAuth tokens (cifrados en reposo por pgcrypto; la app descifra en memoria)
  access_token_enc  BYTEA,
  refresh_token_enc BYTEA,
  token_expires_at  TIMESTAMPTZ,

  -- Caché de disponibilidad (se invalida tras cada sync)
  last_synced_at    TIMESTAMPTZ,
  sync_error        TEXT,                   -- último error de sincronización
  sync_status       TEXT        NOT NULL DEFAULT 'pending'
                      CHECK (sync_status IN ('pending', 'ok', 'error', 'disabled')),

  is_primary        BOOLEAN     NOT NULL DEFAULT FALSE,  -- calendario principal del tenant
  is_active         BOOLEAN     NOT NULL DEFAULT TRUE,

  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_calendar_connections_tenant_id   ON calendar_connections(tenant_id);
CREATE INDEX idx_calendar_connections_active      ON calendar_connections(tenant_id)
  WHERE is_active = TRUE;
CREATE INDEX idx_calendar_connections_sync_due    ON calendar_connections(last_synced_at)
  WHERE sync_status = 'ok' AND is_active = TRUE;

CREATE TRIGGER trg_calendar_connections_updated_at
  BEFORE UPDATE ON calendar_connections
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();


-- ══════════════════════════════════════════════════════════════
-- 4. APPOINTMENT_EVENTS
-- ══════════════════════════════════════════════════════════════
-- Propósito: Log de eventos inmutables sobre el ciclo de vida
-- de una cita (creada, confirmada, cancelada, recordatorio
-- enviado, etc.). Permite auditoría completa y reconstrucción
-- del estado sin depender de campos mutables en appointments.
-- Patrón: event sourcing ligero — jamás se actualiza ni borra.
-- ══════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS appointment_events (
  id               UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id        UUID        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  appointment_id   UUID        NOT NULL REFERENCES appointments(id) ON DELETE CASCADE,

  event_type       TEXT        NOT NULL
                     CHECK (event_type IN (
                       'created',
                       'confirmed',
                       'cancelled',
                       'completed',
                       'no_show_marked',
                       'reminder_sent',
                       'reschedule_requested',
                       'rescheduled',
                       'recovered'             -- marcada como recuperada por ClinicBoost
                     )),

  -- Actor que originó el evento
  actor_type       TEXT        NOT NULL DEFAULT 'system'
                     CHECK (actor_type IN ('system', 'patient', 'therapist', 'admin', 'ai')),
  actor_id         UUID,                       -- tenant_user.id o patient.id según actor_type

  -- Payload libre para datos adicionales del evento
  payload          JSONB       NOT NULL DEFAULT '{}',

  -- Metadatos de trazabilidad (correlación con logs de Serilog)
  correlation_id   UUID,
  flow_id          TEXT,                       -- 'flow_01', 'flow_03', etc.

  created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
  -- Sin updated_at: inmutable
);

CREATE INDEX idx_appointment_events_appointment_id ON appointment_events(appointment_id);
CREATE INDEX idx_appointment_events_tenant_id      ON appointment_events(tenant_id);
CREATE INDEX idx_appointment_events_type_date      ON appointment_events(tenant_id, event_type, created_at DESC);
CREATE INDEX idx_appointment_events_flow           ON appointment_events(tenant_id, flow_id)
  WHERE flow_id IS NOT NULL;


-- ══════════════════════════════════════════════════════════════
-- 5. CONVERSATIONS
-- ══════════════════════════════════════════════════════════════
-- Propósito: Agrupa los mensajes de WhatsApp (o cualquier
-- canal) de un paciente en un contexto conversacional con
-- estado. Un patient puede tener varias conversaciones en el
-- tiempo (una por flujo activo simultáneamente).
-- La IA lee el estado y el historial de la conversación para
-- decidir la siguiente acción; NUNCA confirma citas por sí
-- sola — solo el backend ejecuta.
-- ══════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS conversations (
  id                UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id         UUID        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  patient_id        UUID        NOT NULL REFERENCES patients(id) ON DELETE CASCADE,

  channel           TEXT        NOT NULL DEFAULT 'whatsapp'
                      CHECK (channel IN ('whatsapp', 'sms', 'email', 'web_chat')),

  flow_id           TEXT        NOT NULL
                      CHECK (flow_id IN (
                        'flow_00', -- RGPD onboarding
                        'flow_01', -- Llamada perdida
                        'flow_02', -- Gap detection
                        'flow_03', -- Recordatorio / no-show
                        'flow_04', -- Lead fuera de horario
                        'flow_05', -- NPS + referidos
                        'flow_06', -- Reactivación
                        'flow_07'  -- Reprogramación
                      )),

  status            TEXT        NOT NULL DEFAULT 'open'
                      CHECK (status IN (
                        'open',        -- conversación activa esperando respuesta
                        'waiting_ai',  -- pendiente de procesamiento por IA
                        'waiting_human',-- escalada a un humano
                        'resolved',    -- objetivo completado
                        'expired',     -- sin respuesta en tiempo definido
                        'opted_out'    -- paciente pidió no recibir más mensajes
                      )),

  -- Contexto serializado para la IA (JSON; no contiene PII directa)
  ai_context        JSONB       NOT NULL DEFAULT '{}',

  -- Límites para evitar loops y spam
  message_count     INTEGER     NOT NULL DEFAULT 0,
  last_message_at   TIMESTAMPTZ,

  -- Cita asociada al flujo (si aplica)
  appointment_id    UUID        REFERENCES appointments(id),

  -- Ventana de sesión de WhatsApp (24h según política de Meta)
  session_expires_at TIMESTAMPTZ,

  resolved_at       TIMESTAMPTZ,
  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_conversations_tenant_id      ON conversations(tenant_id);
CREATE INDEX idx_conversations_patient_id     ON conversations(patient_id);
CREATE INDEX idx_conversations_status         ON conversations(tenant_id, status)
  WHERE status IN ('open', 'waiting_ai', 'waiting_human');
CREATE INDEX idx_conversations_flow_status    ON conversations(tenant_id, flow_id, status);
CREATE INDEX idx_conversations_appointment_id ON conversations(appointment_id)
  WHERE appointment_id IS NOT NULL;
-- Índice para expiración de sesiones WhatsApp (job periódico)
CREATE INDEX idx_conversations_session_expiry ON conversations(session_expires_at)
  WHERE status = 'open' AND session_expires_at IS NOT NULL;

CREATE TRIGGER trg_conversations_updated_at
  BEFORE UPDATE ON conversations
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();


-- ══════════════════════════════════════════════════════════════
-- 6. MESSAGES
-- ══════════════════════════════════════════════════════════════
-- Propósito: Cada mensaje individual dentro de una conversación.
-- Inmutable: no se modifica ni borra (trazabilidad RGPD).
-- El campo body puede contener texto plano o un template_id de
-- Twilio/WhatsApp. Los campos de media se usan cuando el
-- paciente envía imágenes o documentos.
-- ══════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS messages (
  id                UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id         UUID        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  conversation_id   UUID        NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,

  direction         TEXT        NOT NULL CHECK (direction IN ('inbound', 'outbound')),
  -- 'inbound'  → mensaje enviado por el paciente
  -- 'outbound' → mensaje enviado por ClinicBoost

  channel           TEXT        NOT NULL DEFAULT 'whatsapp'
                      CHECK (channel IN ('whatsapp', 'sms', 'email', 'web_chat')),

  -- Identificador del mensaje en el proveedor (Twilio MessageSid, etc.)
  provider_message_id TEXT      UNIQUE,        -- NULL hasta que el proveedor confirme

  body              TEXT,                       -- texto plano del mensaje
  template_id       TEXT,                       -- ID de plantilla WhatsApp aprobada
  template_vars     JSONB,                      -- variables de la plantilla

  -- Media adjunta (imagen, audio, documento)
  media_url         TEXT,
  media_type        TEXT,                       -- MIME type

  status            TEXT        NOT NULL DEFAULT 'pending'
                      CHECK (status IN (
                        'pending',    -- pendiente de envío
                        'sent',       -- entregado al proveedor
                        'delivered',  -- entregado al dispositivo del paciente
                        'read',       -- leído por el paciente
                        'failed',     -- error de envío
                        'received'    -- inbound recibido correctamente
                      )),

  -- Trazabilidad IA
  generated_by_ai   BOOLEAN     NOT NULL DEFAULT FALSE,
  ai_model          TEXT,                       -- 'claude-3-5-sonnet', 'gpt-4o', etc.
  ai_prompt_tokens  INTEGER,
  ai_completion_tokens INTEGER,

  error_code        TEXT,                       -- código de error del proveedor
  error_message     TEXT,

  sent_at           TIMESTAMPTZ,
  delivered_at      TIMESTAMPTZ,
  read_at           TIMESTAMPTZ,
  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
  -- Sin updated_at: inmutable
);

CREATE INDEX idx_messages_conversation_id    ON messages(conversation_id, created_at DESC);
CREATE INDEX idx_messages_tenant_id          ON messages(tenant_id);
CREATE INDEX idx_messages_provider_id        ON messages(provider_message_id)
  WHERE provider_message_id IS NOT NULL;
CREATE INDEX idx_messages_status             ON messages(tenant_id, status)
  WHERE status IN ('pending', 'sent', 'failed');
-- Índice para analítica de uso de IA
CREATE INDEX idx_messages_ai                 ON messages(tenant_id, created_at DESC)
  WHERE generated_by_ai = TRUE;


-- ══════════════════════════════════════════════════════════════
-- 7. WAITLIST_ENTRIES
-- ══════════════════════════════════════════════════════════════
-- Propósito: Lista de espera inteligente (Flow 02 y 03).
-- Cuando se detecta un hueco en la agenda o un paciente
-- cancela, el sistema consulta esta tabla para ofertar el
-- hueco al paciente con mayor prioridad. La prioridad se
-- calcula dinámicamente pero puede ser sobreescrita por el
-- terapeuta.
-- ══════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS waitlist_entries (
  id               UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id        UUID        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  patient_id       UUID        NOT NULL REFERENCES patients(id) ON DELETE CASCADE,

  -- Preferencias del paciente para la cita deseada
  preferred_therapist_name TEXT,
  preferred_days    TEXT[],                    -- ['monday','wednesday','friday']
  preferred_time_from TIME,                    -- hora mínima deseada (hora local del tenant)
  preferred_time_to   TIME,                    -- hora máxima deseada
  notes             TEXT,                      -- observaciones del terapeuta

  -- Control de prioridad (menor número = mayor prioridad)
  priority          INTEGER     NOT NULL DEFAULT 100,

  status            TEXT        NOT NULL DEFAULT 'waiting'
                      CHECK (status IN (
                        'waiting',     -- en espera activa
                        'offered',     -- se le ha ofrecido un hueco, esperando respuesta
                        'accepted',    -- aceptó y se creó la cita
                        'declined',    -- rechazó el hueco ofrecido
                        'expired',     -- sin respuesta en tiempo límite
                        'cancelled'    -- el paciente se borró de la lista
                      )),

  -- Tracking de la oferta activa
  offered_appointment_id UUID  REFERENCES appointments(id),
  offered_at        TIMESTAMPTZ,
  offer_expires_at  TIMESTAMPTZ,               -- ventana de respuesta

  -- Cuántas veces se ha ofrecido un hueco (para evitar spam)
  offer_count       INTEGER     NOT NULL DEFAULT 0,

  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_waitlist_tenant_status      ON waitlist_entries(tenant_id, status, priority)
  WHERE status = 'waiting';
CREATE INDEX idx_waitlist_patient_id         ON waitlist_entries(patient_id);
CREATE INDEX idx_waitlist_offer_expires      ON waitlist_entries(offer_expires_at)
  WHERE status = 'offered';

CREATE TRIGGER trg_waitlist_entries_updated_at
  BEFORE UPDATE ON waitlist_entries
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();


-- ══════════════════════════════════════════════════════════════
-- 8. RULE_CONFIGS
-- ══════════════════════════════════════════════════════════════
-- Propósito: Configuración de reglas de negocio por tenant y
-- por flujo. Permite que cada clínica personalice el
-- comportamiento de la automatización sin tocar código:
-- ventanas de tiempo para recordatorios, umbral de inactividad
-- para reactivación, topes de descuento (DiscountGuard), etc.
-- Una sola fila por (tenant_id, flow_id, rule_key).
-- ══════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS rule_configs (
  id          UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id   UUID        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,

  -- Ámbito de la regla
  flow_id     TEXT        NOT NULL
                CHECK (flow_id IN (
                  'flow_00','flow_01','flow_02','flow_03',
                  'flow_04','flow_05','flow_06','flow_07',
                  'global'    -- reglas que aplican a todos los flujos
                )),

  rule_key    TEXT        NOT NULL,
  -- Ejemplos de rule_key:
  --   flow_03.reminder_hours_before     → 24 (enviar recordatorio 24h antes)
  --   flow_03.no_show_wait_minutes      → 15 (esperar 15 min antes de marcar no-show)
  --   flow_06.inactive_days_threshold   → 60 (reactivar si no ha venido en 60 días)
  --   flow_02.gap_min_duration_minutes  → 30 (hueco mínimo para ofertar)
  --   global.discount_max_pct           → 20 (DiscountGuard: descuento máximo)
  --   global.max_daily_outbound_msgs    → 10 (límite de mensajes salientes por día)

  rule_value  TEXT        NOT NULL,             -- siempre texto; el backend castea
  value_type  TEXT        NOT NULL DEFAULT 'string'
                CHECK (value_type IN ('string','integer','decimal','boolean','json')),
  description TEXT,                             -- documentación de la regla

  is_active   BOOLEAN     NOT NULL DEFAULT TRUE,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),

  UNIQUE (tenant_id, flow_id, rule_key)
);

CREATE INDEX idx_rule_configs_tenant_flow  ON rule_configs(tenant_id, flow_id)
  WHERE is_active = TRUE;

CREATE TRIGGER trg_rule_configs_updated_at
  BEFORE UPDATE ON rule_configs
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();


-- ══════════════════════════════════════════════════════════════
-- 9. REVENUE_EVENTS
-- ══════════════════════════════════════════════════════════════
-- Propósito: Registro contable-inmutable de cada ingreso
-- recuperado o atribuido a ClinicBoost. Base del dashboard de
-- ROI y del cálculo del success fee (15 % primeros 90 días).
-- Una fila = un evento monetario atómico. Nunca se actualiza.
-- ══════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS revenue_events (
  id               UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id        UUID        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  appointment_id   UUID        REFERENCES appointments(id),
  patient_id       UUID        REFERENCES patients(id),

  event_type       TEXT        NOT NULL
                     CHECK (event_type IN (
                       'missed_call_converted',   -- Flow 01
                       'gap_filled',              -- Flow 02
                       'no_show_recovered',       -- Flow 03
                       'waitlist_booked',         -- Flow 02/03
                       'reactivation_booked',     -- Flow 06
                       'reschedule_saved',        -- Flow 07
                       'lead_converted'           -- Flow 04
                     )),

  flow_id          TEXT        NOT NULL,
  amount           NUMERIC(10,2) NOT NULL CHECK (amount >= 0),
  currency         CHAR(3)     NOT NULL DEFAULT 'EUR',

  -- DiscountGuard: si se aplicó descuento, registrar importe original y descuento
  original_amount  NUMERIC(10,2),
  discount_pct     NUMERIC(5,2) CHECK (discount_pct BETWEEN 0 AND 100),

  -- Periodo de success fee: los primeros 90 días desde la activación del tenant
  is_success_fee_eligible BOOLEAN NOT NULL DEFAULT FALSE,
  success_fee_amount      NUMERIC(10,2),  -- 15% de amount si eligible

  -- Metadatos de atribución
  attribution_data JSONB       NOT NULL DEFAULT '{}',

  created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
  -- Sin updated_at: inmutable
);

CREATE INDEX idx_revenue_events_tenant_id     ON revenue_events(tenant_id);
CREATE INDEX idx_revenue_events_appointment   ON revenue_events(appointment_id)
  WHERE appointment_id IS NOT NULL;
CREATE INDEX idx_revenue_events_type_date     ON revenue_events(tenant_id, event_type, created_at DESC);
-- Índice para cálculo de success fee pendiente de facturar
CREATE INDEX idx_revenue_events_success_fee   ON revenue_events(tenant_id, created_at DESC)
  WHERE is_success_fee_eligible = TRUE;
-- Índice para dashboard de ROI (rango de fechas)
CREATE INDEX idx_revenue_events_dashboard     ON revenue_events(tenant_id, created_at DESC);


-- ══════════════════════════════════════════════════════════════
-- 10. AUTOMATION_RUNS
-- ══════════════════════════════════════════════════════════════
-- Propósito: Registro de cada ejecución de un flujo automático
-- (job periódico o evento disparado). Permite detectar
-- ejecuciones duplicadas, fallos silenciosos y tiempos de
-- proceso. Sirve como observabilidad de negocio complementaria
-- a los logs de Serilog.
-- ══════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS automation_runs (
  id                UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id         UUID        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,

  flow_id           TEXT        NOT NULL,
  trigger_type      TEXT        NOT NULL
                      CHECK (trigger_type IN (
                        'scheduled',    -- cron job
                        'event',        -- disparado por un evento (ej: cita cancelada)
                        'manual'        -- ejecutado manualmente desde la UI de admin
                      )),
  trigger_ref       TEXT,               -- ID del evento o nombre del job que lo disparó

  status            TEXT        NOT NULL DEFAULT 'running'
                      CHECK (status IN ('running','completed','failed','skipped')),
  -- 'skipped' = se detectó que ya estaba corriendo (idempotencia)

  -- Resumen de la ejecución
  items_processed   INTEGER     NOT NULL DEFAULT 0,
  items_succeeded   INTEGER     NOT NULL DEFAULT 0,
  items_failed      INTEGER     NOT NULL DEFAULT 0,

  -- Error capturado (si status = 'failed')
  error_message     TEXT,
  error_detail      JSONB,

  -- Trazabilidad
  correlation_id    UUID,

  started_at        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  finished_at       TIMESTAMPTZ,
  duration_ms       INTEGER     GENERATED ALWAYS AS (
                      CAST(EXTRACT(EPOCH FROM (finished_at - started_at)) * 1000 AS INTEGER)
                    ) STORED,

  created_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_automation_runs_tenant_flow   ON automation_runs(tenant_id, flow_id, started_at DESC);
CREATE INDEX idx_automation_runs_status        ON automation_runs(tenant_id, status)
  WHERE status IN ('running', 'failed');
-- Índice para detectar runs colgados (running > umbral de tiempo)
CREATE INDEX idx_automation_runs_running       ON automation_runs(started_at)
  WHERE status = 'running';


-- ══════════════════════════════════════════════════════════════
-- 11. WEBHOOK_EVENTS
-- ══════════════════════════════════════════════════════════════
-- Propósito: Cola de entrada de webhooks externos (Twilio,
-- Supabase Auth, integraciones de ERP). Permite procesar
-- de forma asíncrona y garantizar idempotencia cruzando con
-- processed_events. Los webhooks se validan criptográficamente
-- antes de insertarse aquí; si la validación falla, se
-- rechaza en el middleware HTTP y NO se inserta.
-- ══════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS webhook_events (
  id              UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),

  -- tenant_id puede ser NULL si aún no se ha podido resolver el tenant
  -- (p.ej. webhook de signup de Supabase antes de crear el tenant)
  tenant_id       UUID        REFERENCES tenants(id) ON DELETE SET NULL,

  source          TEXT        NOT NULL
                    CHECK (source IN (
                      'twilio',         -- mensajes y status callbacks de WhatsApp/SMS
                      'supabase_auth',  -- eventos de login/signup
                      'calendar_sync',  -- callbacks de sync de agenda
                      'stripe',         -- pagos y suscripciones
                      'internal'        -- eventos internos entre servicios
                    )),

  event_type      TEXT        NOT NULL,   -- tipo específico del proveedor
  payload         JSONB       NOT NULL,   -- payload crudo del webhook (ya validado)
  headers         JSONB,                  -- cabeceras relevantes (sin Authorization)

  -- Estado de procesamiento
  status          TEXT        NOT NULL DEFAULT 'pending'
                    CHECK (status IN (
                      'pending',     -- recibido, pendiente de procesar
                      'processing',  -- en proceso (lock optimista)
                      'processed',   -- procesado con éxito
                      'failed',      -- falló tras reintentos
                      'skipped'      -- duplicado detectado vía processed_events
                    )),

  -- Reintentos
  attempt_count   INTEGER     NOT NULL DEFAULT 0,
  max_attempts    INTEGER     NOT NULL DEFAULT 3,
  next_attempt_at TIMESTAMPTZ,
  last_error      TEXT,

  -- Referencia a processed_events para idempotencia
  idempotency_key TEXT        UNIQUE,     -- event_type + provider event_id hasheado

  -- Trazabilidad
  correlation_id  UUID,

  received_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  processed_at    TIMESTAMPTZ
  -- Sin updated_at porque el campo status se actualiza directamente
);

CREATE INDEX idx_webhook_events_tenant_id      ON webhook_events(tenant_id)
  WHERE tenant_id IS NOT NULL;
CREATE INDEX idx_webhook_events_pending        ON webhook_events(received_at ASC)
  WHERE status = 'pending';
CREATE INDEX idx_webhook_events_retry          ON webhook_events(next_attempt_at ASC)
  WHERE status = 'failed' AND attempt_count < max_attempts;
CREATE INDEX idx_webhook_events_source_type    ON webhook_events(source, event_type, received_at DESC);
CREATE INDEX idx_webhook_events_idempotency    ON webhook_events(idempotency_key)
  WHERE idempotency_key IS NOT NULL;
