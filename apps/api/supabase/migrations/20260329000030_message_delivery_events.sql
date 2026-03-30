-- ════════════════════════════════════════════════════════════════════════════
-- Migration: 20260329000030_message_delivery_events
-- Feature:   Webhook de estado de mensaje — POST /webhooks/twilio/message-status
--
-- OBJETIVO
-- ────────
-- Crear la tabla message_delivery_events y sus índices para:
--   1. Almacenar cada callback de estado de Twilio (sent/delivered/read/failed).
--   2. Permitir queries de agregación por tenant, flujo y variante de mensaje.
--   3. Mantener la correlación MessageSid → Message → Conversation → Tenant.
--
-- TABLA: message_delivery_events
-- ──────────────────────────────
-- Diseño INSERT-only (inmutable). No tiene updated_at.
-- Cada fila representa una transición de estado recibida de Twilio.
-- Un mismo mensaje puede tener múltiples filas: sent → delivered → read.
-- ════════════════════════════════════════════════════════════════════════════

-- ── 1. Tabla principal ───────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS message_delivery_events (
    id                  UUID            NOT NULL DEFAULT gen_random_uuid(),
    tenant_id           UUID            NOT NULL,

    -- Correlación
    message_id          UUID,                        -- FK a messages.id (nullable: race condition)
    conversation_id     UUID,                        -- FK a conversations.id (nullable)
    provider_message_id VARCHAR(64)     NOT NULL,    -- Twilio MessageSid ("SM…")

    -- Estado reportado por Twilio
    status              VARCHAR(32)     NOT NULL,    -- sent | delivered | read | failed | undelivered

    -- Dimensiones de agrupación (para analytics)
    flow_id             VARCHAR(32),                 -- flow_00 … flow_07
    template_id         VARCHAR(128),                -- ID de plantilla Meta/Twilio
    message_variant     VARCHAR(16),                 -- variante A/B ("A", "B", "control")
    channel             VARCHAR(32)     NOT NULL,    -- whatsapp | sms

    -- Error (solo en failed/undelivered)
    error_code          VARCHAR(16),
    error_message       TEXT,

    -- Timestamps
    provider_timestamp  TIMESTAMPTZ,                 -- timestamp del operador (DLR done date)
    occurred_at         TIMESTAMPTZ     NOT NULL DEFAULT now(),

    CONSTRAINT pk_message_delivery_events PRIMARY KEY (id)
);

COMMENT ON TABLE message_delivery_events IS
    'Eventos inmutables de entregabilidad de mensajes outbound. '
    'Una fila por callback de estado recibido de Twilio. '
    'Agregable por tenant, flow_id, template_id, message_variant para analytics.';

COMMENT ON COLUMN message_delivery_events.provider_message_id IS
    'Twilio MessageSid ("SM…"). Correlación primaria con messages.provider_message_id.';
COMMENT ON COLUMN message_delivery_events.message_id IS
    'FK a messages.id. NULL si el callback llegó antes de que el Message se insertara.';
COMMENT ON COLUMN message_delivery_events.flow_id IS
    'Flujo de automatización que generó el mensaje. Dimensión de agrupación.';
COMMENT ON COLUMN message_delivery_events.template_id IS
    'ID de plantilla de mensaje. Permite comparar tasas de entrega por plantilla.';
COMMENT ON COLUMN message_delivery_events.message_variant IS
    'Variante A/B del mensaje. Null si no hay test A/B activo.';
COMMENT ON COLUMN message_delivery_events.error_code IS
    'Código de error de Twilio. Presente solo en failed/undelivered.';

-- ── 2. RLS — tabla de telemetría, acceso controlado por tenant ───────────────

ALTER TABLE message_delivery_events ENABLE ROW LEVEL SECURITY;

-- La política usa SET LOCAL del interceptor de EF, igual que las demás tablas.
CREATE POLICY tenant_isolation_mde ON message_delivery_events
    USING (tenant_id = current_setting('app.current_tenant_id', TRUE)::UUID);

-- ── 3. Índices de correlación ────────────────────────────────────────────────

-- Correlación primaria: MessageSid → eventos de ese mensaje
-- Usado por el webhook de status para verificar si ya fue procesado
-- y por queries de "historial de estados de este mensaje".
CREATE INDEX IF NOT EXISTS ix_mde_tenant_sid
    ON message_delivery_events (tenant_id, provider_message_id, occurred_at DESC);

COMMENT ON INDEX ix_mde_tenant_sid IS
    'Historial de eventos de entregabilidad de un mensaje concreto (MessageSid).';

-- Clave de deduplicación secundaria: (tenant, SID, status) = transición única
-- (el índice de processed_events es la fuente de verdad; este es para JOIN rápido)
CREATE UNIQUE INDEX IF NOT EXISTS ux_mde_tenant_sid_status
    ON message_delivery_events (tenant_id, provider_message_id, status);

COMMENT ON INDEX ux_mde_tenant_sid_status IS
    'Unicidad de transición: un tenant no puede tener dos eventos del mismo '
    'SID con el mismo status. Complementa la idempotencia de processed_events.';

-- ── 4. Índices de agregación (analytics / dashboard) ────────────────────────

-- Agregación por flow + period: "¿cuántos mensajes del flow_00 se entregaron hoy?"
CREATE INDEX IF NOT EXISTS ix_mde_tenant_flow_time
    ON message_delivery_events (tenant_id, flow_id, occurred_at DESC)
    WHERE flow_id IS NOT NULL;

COMMENT ON INDEX ix_mde_tenant_flow_time IS
    'Agregación de entregabilidad por flujo y período. '
    'Dashboard: mensajes enviados/entregados/leídos/fallados por flow_id.';

-- Agregación por template: "¿qué plantilla tiene mayor tasa de lectura?"
CREATE INDEX IF NOT EXISTS ix_mde_tenant_template_status
    ON message_delivery_events (tenant_id, template_id, status)
    WHERE template_id IS NOT NULL;

COMMENT ON INDEX ix_mde_tenant_template_status IS
    'Agrupación por plantilla y estado. Permite calcular tasas de entrega '
    'y lectura por template_id para optimización de contenido.';

-- Errores por tenant: "¿cuántos mensajes fallaron esta semana y por qué?"
CREATE INDEX IF NOT EXISTS ix_mde_tenant_errors
    ON message_delivery_events (tenant_id, error_code, occurred_at DESC)
    WHERE status IN ('failed', 'undelivered');

COMMENT ON INDEX ix_mde_tenant_errors IS
    'Errores de entrega por código de error. '
    'Usado para alertas de calidad de entrega y soporte.';

-- Por conversation: "dame todos los estados de mensajes de esta conversación"
CREATE INDEX IF NOT EXISTS ix_mde_conversation
    ON message_delivery_events (tenant_id, conversation_id, occurred_at DESC)
    WHERE conversation_id IS NOT NULL;

COMMENT ON INDEX ix_mde_conversation IS
    'Todos los eventos de entregabilidad de una conversación, '
    'ordenados por tiempo DESC. JOIN con conversations.';

-- ── 5. Processed_events — índice parcial para callbacks de status ────────────

CREATE INDEX IF NOT EXISTS ix_processed_events_message_status
    ON processed_events (tenant_id, event_id)
    WHERE event_type = 'twilio.message_status';

COMMENT ON INDEX ix_processed_events_message_status IS
    'Deduplicación rápida de callbacks de estado de mensaje por (SID_status). '
    'Complementa el unique index existente en processed_events.';

-- ── 6. WebhookEvents — índice para callbacks de estado ──────────────────────

CREATE INDEX IF NOT EXISTS ix_webhook_events_msg_status_tenant
    ON webhook_events (tenant_id, received_at DESC)
    WHERE source = 'twilio' AND event_type = 'message_status';

COMMENT ON INDEX ix_webhook_events_msg_status_tenant IS
    'Trazabilidad de webhooks de estado de mensaje por tenant. '
    'Auditoría y diagnóstico de callbacks recibidos.';

-- ── 7. Vista de agregación por flujo (helper para el dashboard) ───────────────

CREATE OR REPLACE VIEW v_delivery_stats_by_flow AS
SELECT
    tenant_id,
    flow_id,
    channel,
    date_trunc('day', occurred_at AT TIME ZONE 'UTC') AS day_utc,
    COUNT(*)                                           AS total_events,
    COUNT(*) FILTER (WHERE status = 'sent')            AS sent,
    COUNT(*) FILTER (WHERE status = 'delivered')       AS delivered,
    COUNT(*) FILTER (WHERE status = 'read')            AS read,
    COUNT(*) FILTER (WHERE status = 'failed'
                       OR  status = 'undelivered')     AS failed
FROM   message_delivery_events
WHERE  flow_id IS NOT NULL
GROUP  BY tenant_id, flow_id, channel, day_utc;

COMMENT ON VIEW v_delivery_stats_by_flow IS
    'Estadísticas diarias de entregabilidad agrupadas por tenant, flujo y canal. '
    'Consume directamente message_delivery_events sin joins adicionales.';

-- ── 8. Vista de agregación por plantilla ────────────────────────────────────

CREATE OR REPLACE VIEW v_delivery_stats_by_template AS
SELECT
    tenant_id,
    template_id,
    message_variant,
    channel,
    COUNT(*)                                           AS total_events,
    COUNT(*) FILTER (WHERE status = 'delivered')       AS delivered,
    COUNT(*) FILTER (WHERE status = 'read')            AS read,
    COUNT(*) FILTER (WHERE status = 'failed'
                       OR  status = 'undelivered')     AS failed,
    ROUND(
        100.0 * COUNT(*) FILTER (WHERE status = 'delivered') /
        NULLIF(COUNT(*) FILTER (WHERE status IN ('sent','delivered','read')), 0),
    2)                                                 AS delivery_rate_pct,
    ROUND(
        100.0 * COUNT(*) FILTER (WHERE status = 'read') /
        NULLIF(COUNT(*) FILTER (WHERE status IN ('sent','delivered','read')), 0),
    2)                                                 AS read_rate_pct
FROM   message_delivery_events
WHERE  template_id IS NOT NULL
GROUP  BY tenant_id, template_id, message_variant, channel;

COMMENT ON VIEW v_delivery_stats_by_template IS
    'Tasas de entrega y lectura por plantilla y variante A/B. '
    'Permite comparar rendimiento de diferentes plantillas de mensaje.';
