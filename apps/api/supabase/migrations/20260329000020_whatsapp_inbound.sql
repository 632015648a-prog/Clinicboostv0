-- ════════════════════════════════════════════════════════════════════════════
-- Migration: 20260329000020_whatsapp_inbound
-- Feature:   WhatsApp inbound — POST /webhooks/twilio/whatsapp
--
-- OBJETIVO
-- ────────
-- Añadir los índices necesarios para el pipeline de recepción de mensajes
-- WhatsApp inbound:
--   · Resolución de tenant por número de clínica (ya existe de flow_00,
--     reutilizado por WhatsApp).
--   · Correlación MessageSid → Message → Conversation → Tenant.
--   · Búsqueda de conversaciones activas por paciente/canal/flujo.
--   · Deduplicación rápida vía processed_events.
--   · Observabilidad: webhook_events por tenant + received_at.
--
-- NOTA
-- ────
-- El índice ix_tenants_whatsapp_number fue creado en la migración anterior
-- (20260329000010_missed_call_webhook.sql) y no se repite aquí.
-- ════════════════════════════════════════════════════════════════════════════

-- ── 1. Correlación MessageSid → messages ─────────────────────────────────────
-- Permite lookup rápido de un mensaje por su SID de Twilio para deduplicación
-- adicional y trazabilidad (p.ej. cuando Twilio envía status callbacks).
CREATE INDEX IF NOT EXISTS ix_messages_provider_message_id
    ON messages (tenant_id, provider_message_id)
    WHERE provider_message_id IS NOT NULL;

COMMENT ON INDEX ix_messages_provider_message_id IS
    'Correlación MessageSid (Twilio) → Message. '
    'Usado para deduplicación secundaria y trazabilidad de callbacks.';

-- ── 2. Conversaciones activas por paciente/canal/flujo ────────────────────────
-- Optimiza el upsert de conversación en ConversationService.UpsertConversationAsync:
-- busca la conversación más reciente en estado activo para un paciente dado.
CREATE INDEX IF NOT EXISTS ix_conversations_active_patient
    ON conversations (tenant_id, patient_id, channel, flow_id, created_at DESC)
    WHERE status IN ('open', 'waiting_ai', 'waiting_human');

COMMENT ON INDEX ix_conversations_active_patient IS
    'Búsqueda de conversación activa por paciente/canal/flujo. '
    'Optimiza ConversationService.UpsertConversationAsync.';

-- ── 3. Mensajes por conversación (historial del agente IA) ───────────────────
-- El agente IA carga los últimos N mensajes de la conversación activa para
-- construir el contexto. Este índice optimiza esa consulta.
CREATE INDEX IF NOT EXISTS ix_messages_conversation_created
    ON messages (tenant_id, conversation_id, created_at DESC);

COMMENT ON INDEX ix_messages_conversation_created IS
    'Historial de mensajes por conversación, ordenados por tiempo DESC. '
    'Usado por el agente IA para cargar el contexto de la conversación.';

-- ── 4. Deduplicación por tipo de evento WhatsApp ──────────────────────────────
-- processed_events ya tiene un unique index (event_type, event_id, tenant_id).
-- Añadimos un índice parcial para acelerar las consultas de deduplicación
-- específicas del canal WhatsApp inbound.
CREATE INDEX IF NOT EXISTS ix_processed_events_whatsapp_inbound
    ON processed_events (tenant_id, event_id)
    WHERE event_type = 'twilio.whatsapp_inbound';

COMMENT ON INDEX ix_processed_events_whatsapp_inbound IS
    'Deduplicación rápida de mensajes WhatsApp inbound por MessageSid. '
    'Complementa el unique index existente en processed_events.';

-- ── 5. Webhook events WhatsApp por tenant ─────────────────────────────────────
-- Trazabilidad: listar todos los webhooks WhatsApp de un tenant ordenados
-- por fecha de recepción (dashboard de operaciones).
CREATE INDEX IF NOT EXISTS ix_webhook_events_whatsapp_tenant
    ON webhook_events (tenant_id, received_at DESC)
    WHERE source = 'twilio' AND event_type = 'whatsapp_inbound';

COMMENT ON INDEX ix_webhook_events_whatsapp_tenant IS
    'Trazabilidad de webhooks WhatsApp inbound por tenant, ordenados DESC. '
    'Usado en el dashboard de operaciones para auditoría.';

-- ── 6. AutomationRuns WhatsApp por tenant ─────────────────────────────────────
-- Consulta de ejecuciones del flujo flow_00 desencadenadas por mensajes WA.
-- El índice general ix_automation_runs_flow_tenant (de la migración anterior)
-- ya cubre (tenant_id, flow_id, started_at DESC); aquí añadimos uno parcial
-- más selectivo para trigger_type = 'event' (mensajes inbound).
CREATE INDEX IF NOT EXISTS ix_automation_runs_wa_event
    ON automation_runs (tenant_id, flow_id, started_at DESC)
    WHERE trigger_type = 'event';

COMMENT ON INDEX ix_automation_runs_wa_event IS
    'AutomationRuns desencadenados por eventos (mensajes WA inbound). '
    'Complementa ix_automation_runs_flow_tenant para filtra por trigger_type.';
