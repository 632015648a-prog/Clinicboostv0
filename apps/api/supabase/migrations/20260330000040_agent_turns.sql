-- ════════════════════════════════════════════════════════════════════════════
-- Migration: 20260330000040_agent_turns
-- Feature:   Agente conversacional — trazabilidad de turnos IA
--
-- OBJETIVO
-- ────────
-- Crear la tabla agent_turns para almacenar cada turno del agente
-- conversacional: intención clasificada, acción tomada, respuesta generada,
-- tokens consumidos y si fue bloqueado por HardLimitGuard.
--
-- DISEÑO
-- ──────
-- · INSERT-only (inmutable): cada turno es un registro histórico permanente.
-- · Una fila por mensaje inbound procesado por el agente IA.
-- · Correlación completa: tenant → conversación → mensaje → turno.
-- · Permite auditar decisiones del agente y detectar violaciones de hard limits.
-- ════════════════════════════════════════════════════════════════════════════

-- ── 1. Tabla principal ───────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS agent_turns (
    id                  UUID            NOT NULL DEFAULT gen_random_uuid(),
    tenant_id           UUID            NOT NULL,
    conversation_id     UUID            NOT NULL,
    message_id          UUID,                        -- FK a messages.id (el inbound que disparó el turno)

    -- Clasificación de intención
    intent_name         VARCHAR(64)     NOT NULL,    -- BookAppointment | CancelAppointment | ...
    intent_confidence   FLOAT           NOT NULL DEFAULT 0,

    -- Resultado del agente
    action_name         VARCHAR(64)     NOT NULL,    -- SendMessage | ProposeAppointment | EscalateToHuman | ...
    response_text       TEXT,                        -- Texto generado para el paciente
    escalation_reason   TEXT,                        -- Motivo de derivación (solo si EscalateToHuman)

    -- Hard limits
    was_blocked         BOOLEAN         NOT NULL DEFAULT FALSE,
    block_reason        TEXT,                        -- Razón del bloqueo si was_blocked = TRUE

    -- Trazabilidad IA
    model_used          VARCHAR(64)     NOT NULL,    -- gpt-4o | gpt-4o-mini | none
    prompt_tokens       INTEGER         NOT NULL DEFAULT 0,
    completion_tokens   INTEGER         NOT NULL DEFAULT 0,

    -- Correlación
    correlation_id      TEXT            NOT NULL,
    occurred_at         TIMESTAMPTZ     NOT NULL DEFAULT now(),

    CONSTRAINT pk_agent_turns PRIMARY KEY (id)
);

COMMENT ON TABLE agent_turns IS
    'Registro inmutable de cada turno del agente conversacional. '
    'Una fila por mensaje inbound procesado. Permite auditar intenciones, '
    'acciones y posibles violaciones de hard limits.';

COMMENT ON COLUMN agent_turns.intent_name IS
    'Intención clasificada: BookAppointment, CancelAppointment, GeneralInquiry, '
    'Complaint, DiscountRequest, EscalateToHuman, AppointmentConfirm, Unknown.';
COMMENT ON COLUMN agent_turns.was_blocked IS
    'TRUE si HardLimitGuard bloqueó la respuesta original y la sustituyó '
    'por una derivación a humano.';
COMMENT ON COLUMN agent_turns.model_used IS
    'Modelo OpenAI utilizado en el ciclo principal. "none" si se derivó sin llamar al LLM.';

-- ── 2. RLS ───────────────────────────────────────────────────────────────────

ALTER TABLE agent_turns ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_agent_turns ON agent_turns
    USING (tenant_id = current_setting('app.current_tenant_id', TRUE)::UUID);

-- ── 3. Índices ────────────────────────────────────────────────────────────────

-- Historial de turnos de una conversación (consulta más común)
CREATE INDEX IF NOT EXISTS ix_agent_turns_conv
    ON agent_turns (tenant_id, conversation_id, occurred_at DESC);

COMMENT ON INDEX ix_agent_turns_conv IS
    'Historial de turnos IA de una conversación, ordenados por tiempo DESC.';

-- Turnos bloqueados por hard limits (monitorización)
CREATE INDEX IF NOT EXISTS ix_agent_turns_blocked
    ON agent_turns (tenant_id, occurred_at DESC)
    WHERE was_blocked = TRUE;

COMMENT ON INDEX ix_agent_turns_blocked IS
    'Turnos bloqueados por HardLimitGuard. Usado para alertas de calidad y auditoría.';

-- Distribución de intenciones por tenant (analytics)
CREATE INDEX IF NOT EXISTS ix_agent_turns_intent
    ON agent_turns (tenant_id, intent_name, occurred_at DESC);

COMMENT ON INDEX ix_agent_turns_intent IS
    'Distribución de intenciones detectadas por tenant. '
    'Permite analizar qué solicitan más los pacientes.';

-- Consumo de tokens por tenant y modelo (control de costes)
CREATE INDEX IF NOT EXISTS ix_agent_turns_model_cost
    ON agent_turns (tenant_id, model_used, occurred_at DESC);

COMMENT ON INDEX ix_agent_turns_model_cost IS
    'Consumo de tokens por modelo y tenant. Control de costes de OpenAI.';

-- ── 4. Vista de resumen de turnos (helper para dashboard) ────────────────────

CREATE OR REPLACE VIEW v_agent_turn_stats AS
SELECT
    tenant_id,
    date_trunc('day', occurred_at AT TIME ZONE 'UTC') AS day_utc,
    intent_name,
    action_name,
    COUNT(*)                                           AS total_turns,
    COUNT(*) FILTER (WHERE was_blocked)                AS blocked_turns,
    COUNT(*) FILTER (WHERE action_name = 'EscalateToHuman') AS escalations,
    SUM(prompt_tokens)                                 AS total_prompt_tokens,
    SUM(completion_tokens)                             AS total_completion_tokens,
    AVG(intent_confidence)                             AS avg_confidence
FROM   agent_turns
GROUP  BY tenant_id, day_utc, intent_name, action_name;

COMMENT ON VIEW v_agent_turn_stats IS
    'Resumen diario de actividad del agente: intenciones, acciones, bloqueos y tokens. '
    'Usado en el dashboard de operaciones.';
