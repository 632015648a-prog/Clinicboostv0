-- ════════════════════════════════════════════════════════════════════════════
-- Migration: 20260329000010_missed_call_webhook
-- Feature:   flow_00 — Llamada perdida (webhook POST /webhooks/twilio/voice)
-- ════════════════════════════════════════════════════════════════════════════

-- ── Índice para resolución de tenant por número de teléfono ──────────────────
-- El TenantPhoneResolver consulta: WHERE whatsapp_number = $1 AND is_active = true
-- Sin este índice, sería un full-scan de tenants en cada webhook.
CREATE INDEX IF NOT EXISTS ix_tenants_whatsapp_number
    ON public.tenants (whatsapp_number)
    WHERE is_active = true;

-- ── Índice adicional en webhook_events para trazabilidad de voz ──────────────
-- Permite recuperar todos los webhooks de un tenant ordenados por recepción.
CREATE INDEX IF NOT EXISTS ix_webhook_events_tenant_received
    ON public.webhook_events (tenant_id, received_at DESC)
    WHERE tenant_id IS NOT NULL;

-- ── Índice en automation_runs para observabilidad de flow_00 ─────────────────
CREATE INDEX IF NOT EXISTS ix_automation_runs_flow_tenant
    ON public.automation_runs (tenant_id, flow_id, started_at DESC);

-- ── Comentarios descriptivos ──────────────────────────────────────────────────
COMMENT ON INDEX ix_tenants_whatsapp_number IS
    'Búsqueda de tenant por número E.164 asignado (usado en resolución de webhooks de voz/WhatsApp)';

COMMENT ON INDEX ix_webhook_events_tenant_received IS
    'Trazabilidad de webhooks por tenant ordenados por recepción';

COMMENT ON INDEX ix_automation_runs_flow_tenant IS
    'Consulta de ejecuciones de automatización por flow_id y tenant para el dashboard';
