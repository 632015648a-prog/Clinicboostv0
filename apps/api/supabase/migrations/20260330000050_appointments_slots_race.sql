-- ════════════════════════════════════════════════════════════════════════════
-- Migration: 20260330000050_appointments_slots_race.sql
--
-- Objetivo: reforzar la gestión de citas con índices optimizados para:
--   1. Control de race conditions en BookAppointment y RescheduleAppointment
--   2. Detección de overlaps por terapeuta (la clave de negocio)
--   3. Telemetría de revenue: índices en revenue_events para dashboards
--   4. Índices de soporte en appointment_events para auditoría
--
-- ESTRATEGIA ANTI-RACE-CONDITION
-- ────────────────────────────────
--   El control principal es transaccional (IsolationLevel.RepeatableRead).
--   El índice ix_appt_therapist_time_range permite al planner de Postgres
--   hacer un Index Scan eficiente sobre el rango temporal antes del lock.
--
--   Para producción de alta concurrencia se recomienda añadir un advisory lock
--   a nivel de aplicación:
--     SELECT pg_try_advisory_xact_lock(hashtext(tenant_id::text || therapist_name || starts_at_utc::text))
--   Esto se activará en el sprint de escalabilidad (ADR-008).
--
-- ════════════════════════════════════════════════════════════════════════════

-- ── appointments ─────────────────────────────────────────────────────────────

-- Índice principal para detección de overlaps:
-- WHERE tenant_id = X AND therapist_name = Y AND starts_at_utc < Z AND ends_at_utc > W
-- AND status NOT IN ('cancelled', 'completed')
CREATE INDEX IF NOT EXISTS ix_appt_therapist_time_range
    ON appointments (tenant_id, therapist_name, starts_at_utc, ends_at_utc)
    WHERE status NOT IN ('3', '4');  -- Cancelled=3, Completed=4

-- Índice para consultas del paciente (get_patient_appointments)
CREATE INDEX IF NOT EXISTS ix_appt_patient_upcoming
    ON appointments (tenant_id, patient_id, starts_at_utc)
    WHERE status NOT IN ('3', '4');

-- Índice para reschedule: lookup de la nueva cita por rescheduled_from_id
CREATE INDEX IF NOT EXISTS ix_appt_rescheduled_from
    ON appointments (tenant_id, rescheduled_from_id)
    WHERE rescheduled_from_id IS NOT NULL;

-- Índice para KPIs de recovery
CREATE INDEX IF NOT EXISTS ix_appt_is_recovered
    ON appointments (tenant_id, is_recovered, created_at DESC)
    WHERE is_recovered = TRUE;

-- ── revenue_events ────────────────────────────────────────────────────────────

-- Índice principal para dashboard de ROI: totales por tenant y período
CREATE INDEX IF NOT EXISTS ix_rev_tenant_created
    ON revenue_events (tenant_id, created_at DESC);

-- Índice para desglose por flow (KPI por canal de captación)
CREATE INDEX IF NOT EXISTS ix_rev_tenant_flow
    ON revenue_events (tenant_id, flow_id, created_at DESC);

-- Índice para success fee billing (filter: is_success_fee_eligible = TRUE)
CREATE INDEX IF NOT EXISTS ix_rev_success_fee
    ON revenue_events (tenant_id, is_success_fee_eligible, created_at DESC)
    WHERE is_success_fee_eligible = TRUE;

-- Índice para event_type (breakdown por tipo de conversión)
CREATE INDEX IF NOT EXISTS ix_rev_event_type
    ON revenue_events (tenant_id, event_type, created_at DESC);

-- ── appointment_events ────────────────────────────────────────────────────────

-- Índice para historial de una cita (audit trail)
CREATE INDEX IF NOT EXISTS ix_appt_events_appointment
    ON appointment_events (tenant_id, appointment_id, created_at DESC);

-- Índice para auditoría de acciones de IA
CREATE INDEX IF NOT EXISTS ix_appt_events_actor_ai
    ON appointment_events (tenant_id, actor_type, created_at DESC)
    WHERE actor_type = 'ai';

-- ── Vistas de reporting ───────────────────────────────────────────────────────

-- Vista: KPIs de revenue por tenant, flow y período (semana/mes)
CREATE OR REPLACE VIEW v_revenue_by_flow AS
SELECT
    tenant_id,
    flow_id,
    event_type,
    DATE_TRUNC('day', created_at AT TIME ZONE 'UTC') AS day_utc,
    COUNT(*)                                          AS event_count,
    SUM(amount)                                       AS total_amount,
    SUM(CASE WHEN amount > 0 THEN amount ELSE 0 END)  AS recovered_amount,
    SUM(CASE WHEN amount < 0 THEN ABS(amount) ELSE 0 END) AS lost_amount,
    SUM(COALESCE(success_fee_amount, 0))              AS total_success_fee,
    COUNT(CASE WHEN is_success_fee_eligible THEN 1 END) AS eligible_count
FROM revenue_events
GROUP BY
    tenant_id,
    flow_id,
    event_type,
    DATE_TRUNC('day', created_at AT TIME ZONE 'UTC');

COMMENT ON VIEW v_revenue_by_flow IS
    'Agrega ingresos recuperados por flow y día UTC. '
    'Usar para dashboard de ROI. No incluye datos PII.';

-- Vista: resumen de citas por estado y terapeuta (agenda operativa)
CREATE OR REPLACE VIEW v_appointments_summary AS
SELECT
    tenant_id,
    therapist_name,
    DATE_TRUNC('day', starts_at_utc)                            AS day_utc,
    COUNT(*)                                                     AS total,
    COUNT(CASE WHEN status = 1 THEN 1 END)                      AS scheduled,
    COUNT(CASE WHEN status = 2 THEN 1 END)                      AS confirmed,
    COUNT(CASE WHEN status = 3 THEN 1 END)                      AS cancelled,
    COUNT(CASE WHEN status = 4 THEN 1 END)                      AS completed,
    COUNT(CASE WHEN status = 5 THEN 1 END)                      AS no_show,
    COUNT(CASE WHEN is_recovered = TRUE THEN 1 END)             AS recovered,
    SUM(COALESCE(recovered_revenue, 0))                         AS recovered_revenue_eur
FROM appointments
GROUP BY
    tenant_id,
    therapist_name,
    DATE_TRUNC('day', starts_at_utc);

COMMENT ON VIEW v_appointments_summary IS
    'Resumen operativo de citas por terapeuta y día. '
    'Usar para calendar dashboard. No contiene PII de paciente.';
