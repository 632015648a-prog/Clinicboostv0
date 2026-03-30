-- ─────────────────────────────────────────────────────────────────────────────
-- Migration: 20260330000070_flow_metrics_events
--
-- Objetivo:
--   Crear la tabla flow_metrics_events para registrar KPIs del flujo end-to-end:
--     Llamada perdida → WhatsApp saliente → Reserva conversacional
--
-- Diseño:
--   · INSERT-only: cada fila es una medición inmutable.
--   · metric_type enum permite filtrar por evento sin joins complejos.
--   · duration_ms captura el tiempo entre pasos (respuesta, conversión).
--   · recovered_revenue solo se rellena en appointment_booked.
--   · RLS habilitado: tenant_id filtra todas las queries.
-- ─────────────────────────────────────────────────────────────────────────────

-- ── Tabla principal ──────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS flow_metrics_events (
    id                  UUID            NOT NULL DEFAULT gen_random_uuid(),
    tenant_id           UUID            NOT NULL,
    patient_id          UUID,
    appointment_id      UUID,

    -- Identificador del flujo (flow_01, flow_02, etc.)
    flow_id             VARCHAR(32)     NOT NULL,

    -- Tipo de métrica
    -- missed_call_received | outbound_sent | outbound_failed |
    -- patient_replied | appointment_booked | appointment_cancelled | flow_skipped
    metric_type         VARCHAR(64)     NOT NULL,

    -- Duración en ms entre el evento trigger y este paso
    -- outbound_sent      → ms desde llamada hasta envío WA
    -- appointment_booked → ms desde envío WA hasta reserva
    duration_ms         BIGINT,

    -- Revenue recuperado (solo en appointment_booked)
    recovered_revenue   NUMERIC(10, 2),
    currency            CHAR(3)         NOT NULL DEFAULT 'EUR',

    -- SID de Twilio del mensaje saliente (correlación)
    twilio_message_sid  VARCHAR(64),

    -- Código de error de Twilio (para outbound_failed)
    error_code          VARCHAR(32),

    -- ID de correlación end-to-end (CallSid → MessageSid → AppointmentId)
    correlation_id      VARCHAR(128)    NOT NULL,

    -- Metadatos adicionales en JSON
    metadata            JSONB           NOT NULL DEFAULT '{}'::jsonb,

    -- Timestamp del evento (UTC)
    occurred_at         TIMESTAMPTZ     NOT NULL DEFAULT NOW(),

    CONSTRAINT pk_flow_metrics_events PRIMARY KEY (id),

    CONSTRAINT fk_flow_metrics_tenant
        FOREIGN KEY (tenant_id)
        REFERENCES tenants(id)
        ON DELETE CASCADE
);

-- ── Índices ──────────────────────────────────────────────────────────────────

-- Índice principal para queries de KPI (tenant + flow + tipo + rango de fechas)
CREATE INDEX IF NOT EXISTS ix_flow_metrics_tenant_flow_type_date
    ON flow_metrics_events (tenant_id, flow_id, metric_type, occurred_at DESC);

-- Índice para trazabilidad end-to-end por correlación
CREATE INDEX IF NOT EXISTS ix_flow_metrics_correlation
    ON flow_metrics_events (correlation_id);

-- Índice para tiempo de respuesta: consultar solo outbound_sent con duration_ms
CREATE INDEX IF NOT EXISTS ix_flow_metrics_response_time
    ON flow_metrics_events (tenant_id, flow_id, occurred_at DESC)
    WHERE metric_type = 'outbound_sent' AND duration_ms IS NOT NULL;

-- Índice para revenue: consultar appointment_booked con recovered_revenue
CREATE INDEX IF NOT EXISTS ix_flow_metrics_revenue
    ON flow_metrics_events (tenant_id, flow_id, occurred_at DESC)
    WHERE metric_type = 'appointment_booked' AND recovered_revenue IS NOT NULL;

-- ── Row Level Security ───────────────────────────────────────────────────────

ALTER TABLE flow_metrics_events ENABLE ROW LEVEL SECURITY;

CREATE POLICY flow_metrics_tenant_isolation
    ON flow_metrics_events
    USING (tenant_id = current_setting('app.tenant_id', TRUE)::UUID);

-- ── Vistas de KPI ────────────────────────────────────────────────────────────

-- Vista: resumen diario por tenant y tipo de métrica
CREATE OR REPLACE VIEW v_flow01_daily_kpi AS
SELECT
    tenant_id,
    DATE_TRUNC('day', occurred_at)::DATE AS day,
    COUNT(*) FILTER (WHERE metric_type = 'missed_call_received')     AS missed_calls,
    COUNT(*) FILTER (WHERE metric_type = 'outbound_sent')            AS outbound_sent,
    COUNT(*) FILTER (WHERE metric_type = 'outbound_failed')          AS outbound_failed,
    COUNT(*) FILTER (WHERE metric_type = 'patient_replied')          AS patient_replies,
    COUNT(*) FILTER (WHERE metric_type = 'appointment_booked')       AS appointments_booked,
    COUNT(*) FILTER (WHERE metric_type = 'flow_skipped')             AS flow_skipped,
    -- Tasa de conversión (outbound → reserva)
    CASE
        WHEN COUNT(*) FILTER (WHERE metric_type = 'outbound_sent') > 0
        THEN ROUND(
            COUNT(*) FILTER (WHERE metric_type = 'appointment_booked')::NUMERIC /
            COUNT(*) FILTER (WHERE metric_type = 'outbound_sent')::NUMERIC, 4
        )
        ELSE 0
    END                                                              AS conversion_rate,
    -- Tiempo de respuesta promedio (llamada → WA enviado)
    ROUND(AVG(duration_ms) FILTER (
        WHERE metric_type = 'outbound_sent' AND duration_ms IS NOT NULL
    ))                                                               AS avg_response_time_ms,
    -- Revenue recuperado
    COALESCE(SUM(recovered_revenue) FILTER (
        WHERE metric_type = 'appointment_booked'
    ), 0)                                                            AS total_recovered_revenue
FROM flow_metrics_events
WHERE flow_id = 'flow_01'
GROUP BY tenant_id, DATE_TRUNC('day', occurred_at)::DATE
ORDER BY day DESC;

-- Vista: tiempo de respuesta p50/p95 por tenant (últimos 30 días)
CREATE OR REPLACE VIEW v_flow01_response_time_percentiles AS
SELECT
    tenant_id,
    COUNT(*)                                              AS sample_count,
    ROUND(PERCENTILE_CONT(0.50) WITHIN GROUP (
        ORDER BY duration_ms
    ))                                                    AS p50_ms,
    ROUND(PERCENTILE_CONT(0.95) WITHIN GROUP (
        ORDER BY duration_ms
    ))                                                    AS p95_ms,
    ROUND(PERCENTILE_CONT(0.99) WITHIN GROUP (
        ORDER BY duration_ms
    ))                                                    AS p99_ms,
    MIN(duration_ms)                                      AS min_ms,
    MAX(duration_ms)                                      AS max_ms
FROM flow_metrics_events
WHERE
    flow_id     = 'flow_01'
    AND metric_type = 'outbound_sent'
    AND duration_ms IS NOT NULL
    AND occurred_at >= NOW() - INTERVAL '30 days'
GROUP BY tenant_id;

COMMENT ON TABLE flow_metrics_events IS
    'Eventos KPI inmutables de los flujos de ClinicBoost. '
    'INSERT-only: nunca se actualiza ni borra. '
    'Base del dashboard de ROI, tiempo de respuesta y recovered revenue.';

COMMENT ON COLUMN flow_metrics_events.duration_ms IS
    'Duración en ms entre el evento trigger y este paso. '
    'Para outbound_sent: ms desde la llamada perdida hasta el envío del WhatsApp. '
    'Para appointment_booked: ms desde el envío del WA hasta la reserva de la cita.';

COMMENT ON COLUMN flow_metrics_events.recovered_revenue IS
    'Revenue recuperado atribuido a ClinicBoost. '
    'Solo se rellena en metric_type=appointment_booked. '
    'Lógica económica exclusivamente en backend (nunca en prompts ni frontend).';
