-- =============================================================================
-- 0080 · message_variants + variant_conversion_events
--
-- PROPÓSITO
-- ─────────
-- Introducir A/B testing estructurado sobre plantillas de mensajes.
-- Permite definir variantes por flujo/plantilla y medir el funnel completo:
--   outbound_sent → delivered → read → reply → booked
--
-- DISEÑO
-- ──────
-- · message_variants      — catálogo de variantes (A/B/control por template+flow)
-- · variant_conversion_events — tabla de eventos inmutable (una fila por evento
--   de conversión de una variante concreta): delivered / read / reply / booked
--
-- RELACIONES
-- ──────────
-- · messages.message_variant_id → message_variants.id
-- · message_delivery_events.message_variant_id → message_variants.id
-- · variant_conversion_events.message_variant_id → message_variants.id
-- · variant_conversion_events.message_id → messages.id
--
-- SEGURIDAD
-- ─────────
-- · RLS activa en ambas tablas.
-- · app_user no puede hacer bypass de RLS.
-- · Política: tenant_id = current_setting('app.tenant_id')::uuid
--
-- CONVENCIONES
-- ────────────
-- · Todo en UTC.
-- · snake_case en columnas.
-- · tenant_id en todas las tablas de negocio (ADR-001).
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 1. message_variants
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS message_variants (
    id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id       uuid        NOT NULL,

    -- Identificadores de agrupación
    flow_id         text        NOT NULL CHECK (flow_id ~ '^flow_0[0-7]$'),
    template_id     text        NOT NULL,   -- p.ej. "missed_call_recovery_v1"

    -- Nombre corto y único dentro del tenant+flow+template
    -- Valores típicos: "A", "B", "control", "v2_emoji", "v2_formal"
    variant_key     text        NOT NULL CHECK (char_length(variant_key) <= 32),

    -- Texto de la plantilla (para comparación de variantes en el dashboard)
    body_preview    text,                   -- primeros 280 chars del cuerpo
    template_vars   jsonb,                  -- variables de plantilla por defecto

    -- Control de activación y peso de distribución (0-100, suma por tenant+flow+template = 100)
    is_active       boolean     NOT NULL DEFAULT true,
    weight_pct      smallint    NOT NULL DEFAULT 50
                                CHECK (weight_pct BETWEEN 0 AND 100),

    -- Metadatos opcionales (tags, notas del editor, versión semántica)
    metadata        jsonb       NOT NULL DEFAULT '{}'::jsonb,

    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT uq_message_variants_tenant_flow_template_key
        UNIQUE (tenant_id, flow_id, template_id, variant_key)
);

COMMENT ON TABLE  message_variants                IS 'Catálogo de variantes A/B por flujo y plantilla. Una fila = una variante activa o histórica.';
COMMENT ON COLUMN message_variants.variant_key    IS 'Clave corta de la variante (A/B/control/…). Única por tenant+flow+template.';
COMMENT ON COLUMN message_variants.weight_pct     IS 'Porcentaje de distribución (0-100). La suma de variantes activas por grupo debe ser 100.';
COMMENT ON COLUMN message_variants.body_preview   IS 'Primeros 280 caracteres del cuerpo del mensaje. Sirve para previsualizar en el dashboard.';

-- Trigger: updated_at automático
CREATE OR REPLACE FUNCTION update_message_variants_updated_at()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_message_variants_updated_at
    BEFORE UPDATE ON message_variants
    FOR EACH ROW EXECUTE FUNCTION update_message_variants_updated_at();

-- Índices
CREATE INDEX IF NOT EXISTS ix_message_variants_tenant_flow
    ON message_variants (tenant_id, flow_id);

CREATE INDEX IF NOT EXISTS ix_message_variants_tenant_flow_template
    ON message_variants (tenant_id, flow_id, template_id)
    WHERE is_active = true;

-- ---------------------------------------------------------------------------
-- 2. variant_conversion_events
--
-- Una fila = un evento de conversión para un mensaje concreto que pertenece
-- a una variante. Los eventos posibles son:
--   outbound_sent  — mensaje enviado (se crea al enviar)
--   delivered      — entregado al dispositivo (callback Twilio)
--   read           — leído por el paciente (callback Twilio)
--   reply          — paciente respondió (inbound correlacionado)
--   booked         — cita reservada atribuida a esta variante
--
-- Diseño INSERT-only: no se actualiza ni borra nunca.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS variant_conversion_events (
    id                   uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id            uuid        NOT NULL,

    -- Variante que generó el mensaje
    message_variant_id   uuid        NOT NULL
                                     REFERENCES message_variants(id),

    -- Mensaje outbound al que se refiere este evento (puede ser null si
    -- el callback llega antes de que el Message se registre en BD)
    message_id           uuid,       -- FK a messages.id; no REFERENCES para evitar bloqueo

    -- Conversación asociada (para JOIN con contexto conversacional)
    conversation_id      uuid,       -- FK a conversations.id

    -- Cita reservada atribuida a esta variante (solo para event_type = 'booked')
    appointment_id       uuid,       -- FK a appointments.id

    -- Tipo de evento del funnel
    -- Valores: outbound_sent | delivered | read | reply | booked
    event_type           text        NOT NULL
                                     CHECK (event_type IN (
                                         'outbound_sent',
                                         'delivered',
                                         'read',
                                         'reply',
                                         'booked'
                                     )),

    -- Correlación con Twilio para joins con message_delivery_events
    provider_message_id  text,       -- Twilio MessageSid ("SM…" / "MM…")

    -- Tiempo en ms desde el outbound_sent hasta este evento.
    -- Null para outbound_sent (es el evento base).
    -- Para booked: ms desde el envío hasta la reserva de la cita.
    elapsed_ms           bigint      CHECK (elapsed_ms >= 0),

    -- Revenue recuperado atribuido a la variante (solo para event_type = 'booked')
    recovered_revenue    numeric(10,2),
    currency             char(3)     DEFAULT 'EUR',

    -- ID de correlación end-to-end (para trazabilidad con flow_metrics_events)
    correlation_id       text        NOT NULL,

    -- Metadatos adicionales (canal, modelo IA usado, etc.)
    metadata             jsonb       NOT NULL DEFAULT '{}'::jsonb,

    occurred_at          timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE  variant_conversion_events                    IS 'Funnel de conversión por variante A/B. Una fila por evento (sent/delivered/read/reply/booked). INSERT-only.';
COMMENT ON COLUMN variant_conversion_events.event_type        IS 'Paso del funnel: outbound_sent → delivered → read → reply → booked.';
COMMENT ON COLUMN variant_conversion_events.elapsed_ms        IS 'Ms desde outbound_sent hasta este evento. Null para outbound_sent.';
COMMENT ON COLUMN variant_conversion_events.recovered_revenue IS 'Revenue recuperado en EUR. Solo se rellena cuando event_type = booked.';

-- Índices para queries de agregación y dashboards
CREATE INDEX IF NOT EXISTS ix_vce_tenant_variant_type
    ON variant_conversion_events (tenant_id, message_variant_id, event_type);

CREATE INDEX IF NOT EXISTS ix_vce_tenant_variant_occurred
    ON variant_conversion_events (tenant_id, message_variant_id, occurred_at);

CREATE INDEX IF NOT EXISTS ix_vce_tenant_flow_occurred
    ON variant_conversion_events (tenant_id, occurred_at)
    INCLUDE (message_variant_id, event_type);

CREATE INDEX IF NOT EXISTS ix_vce_message_id
    ON variant_conversion_events (message_id)
    WHERE message_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_vce_correlation_id
    ON variant_conversion_events (correlation_id);

-- ---------------------------------------------------------------------------
-- 3. Añadir message_variant_id a messages
--
-- Permite correlacionar directamente un mensaje outbound con su variante.
-- FK referencia message_variants.id; nullable (mensajes fuera de A/B = null).
-- ---------------------------------------------------------------------------
ALTER TABLE messages
    ADD COLUMN IF NOT EXISTS message_variant_id uuid
        REFERENCES message_variants(id) ON DELETE SET NULL;

COMMENT ON COLUMN messages.message_variant_id IS
    'FK a message_variants. Null si el mensaje no pertenece a ninguna variante A/B activa.';

CREATE INDEX IF NOT EXISTS ix_messages_variant_id
    ON messages (message_variant_id)
    WHERE message_variant_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- 4. Crear tabla message_delivery_events (si no existe)
--
-- Evento inmutable de entregabilidad de un mensaje outbound.
-- Una fila por callback de estado de Twilio: sent → delivered → read | failed.
-- INSERT-only: nunca se actualiza ni borra (trazabilidad RGPD + auditoría).
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS message_delivery_events (
    id                   uuid        PRIMARY KEY DEFAULT gen_random_uuid(),

    -- Claves de correlación
    tenant_id            uuid        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    message_id           uuid        REFERENCES messages(id) ON DELETE SET NULL,
    conversation_id      uuid        REFERENCES conversations(id) ON DELETE SET NULL,
    provider_message_id  text        NOT NULL,   -- Twilio MessageSid (SM… / MM…)

    -- Estado reportado por Twilio
    status               text        NOT NULL
                           CHECK (status IN ('sent','delivered','read','failed','undelivered')),

    -- Dimensiones de agrupación (analytics por flujo y variante)
    flow_id              text,
    template_id          text,
    message_variant      text,       -- 'A', 'B', 'control', etc.
    message_variant_id   uuid        REFERENCES message_variants(id) ON DELETE SET NULL,

    channel              text        NOT NULL CHECK (channel IN ('whatsapp','sms')),

    -- Error (cuando status = 'failed' | 'undelivered')
    error_code           text,
    error_message        text,

    -- Timestamps
    provider_timestamp   timestamptz,
    occurred_at          timestamptz NOT NULL DEFAULT NOW()
);

COMMENT ON TABLE  message_delivery_events IS
    'Evento inmutable de entregabilidad por callback de Twilio. '
    'INSERT-only. Una fila por transición de estado recibida. '
    'Fuente de verdad para dashboards de entregabilidad por flujo y variante A/B.';

-- Índices de rendimiento
CREATE INDEX IF NOT EXISTS ix_mde_tenant_occurred
    ON message_delivery_events (tenant_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_mde_provider_message_id
    ON message_delivery_events (provider_message_id);
CREATE INDEX IF NOT EXISTS ix_mde_message_id
    ON message_delivery_events (message_id)
    WHERE message_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_mde_flow_template
    ON message_delivery_events (tenant_id, flow_id, template_id, occurred_at DESC)
    WHERE flow_id IS NOT NULL;

-- RLS
ALTER TABLE message_delivery_events ENABLE ROW LEVEL SECURITY;

CREATE POLICY "mde_tenant_isolation"
    ON message_delivery_events FOR ALL
    USING      (tenant_id = current_tenant_id())
    WITH CHECK (tenant_id = current_tenant_id());

-- Permisos
GRANT SELECT, INSERT ON message_delivery_events TO app_user;
REVOKE UPDATE, DELETE ON message_delivery_events FROM app_user;


-- ---------------------------------------------------------------------------
-- 4b. Añadir message_variant_id a message_delivery_events
--     (columna ya incluida en la definición anterior; este bloque es
--      idempotente gracias a IF NOT EXISTS en el ALTER TABLE)
--
-- Permite agregar entregabilidad por variante sin JOIN a messages.
-- Se rellena al insertar el evento si el Message padre tiene variant_id.
-- ---------------------------------------------------------------------------
ALTER TABLE message_delivery_events
    ADD COLUMN IF NOT EXISTS message_variant_id uuid
        REFERENCES message_variants(id) ON DELETE SET NULL;

COMMENT ON COLUMN message_delivery_events.message_variant_id IS
    'FK a message_variants. Copiado del Message padre para agregar sin JOIN.';

CREATE INDEX IF NOT EXISTS ix_mde_variant_id
    ON message_delivery_events (message_variant_id)
    WHERE message_variant_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- 5. RLS en message_variants
-- ---------------------------------------------------------------------------
ALTER TABLE message_variants ENABLE ROW LEVEL SECURITY;

-- app_user solo ve y modifica su propio tenant
CREATE POLICY rls_message_variants_tenant
    ON message_variants
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

-- service_role puede ver todo (para jobs de background)
CREATE POLICY rls_message_variants_service
    ON message_variants
    TO service_role
    USING (true)
    WITH CHECK (true);

-- ---------------------------------------------------------------------------
-- 6. RLS en variant_conversion_events
-- ---------------------------------------------------------------------------
ALTER TABLE variant_conversion_events ENABLE ROW LEVEL SECURITY;

CREATE POLICY rls_variant_conversion_events_tenant
    ON variant_conversion_events
    USING (tenant_id = current_setting('app.tenant_id', true)::uuid);

CREATE POLICY rls_variant_conversion_events_service
    ON variant_conversion_events
    TO service_role
    USING (true)
    WITH CHECK (true);

-- ---------------------------------------------------------------------------
-- 7. Vista de agregación para el dashboard de conversión por variante
--
-- P1-SEC: La vista NO tiene RLS propia (las vistas no soportan RLS directamente).
-- La seguridad se garantiza mediante:
--   a) Usar siempre el filtro WHERE vce.tenant_id = $1 al consultar.
--   b) La función get_variant_funnel() definida a continuación aplica RLS
--      verificando current_setting('app.tenant_id') igual que las tablas base.
--
-- USO SEGURO: SELECT * FROM get_variant_funnel($tenant_id, $from, $to)
-- USO INSEGURO (evitar): SELECT * FROM v_variant_conversion_funnel (sin filtro)
--
-- La vista se mantiene para compatibilidad y reportes internos de admins.
-- ---------------------------------------------------------------------------
CREATE OR REPLACE VIEW v_variant_conversion_funnel AS
SELECT
    vce.tenant_id,
    mv.flow_id,
    mv.template_id,
    mv.variant_key,
    mv.id                                                        AS message_variant_id,
    mv.is_active,
    COUNT(*) FILTER (WHERE vce.event_type = 'outbound_sent')     AS sent_count,
    COUNT(*) FILTER (WHERE vce.event_type = 'delivered')         AS delivered_count,
    COUNT(*) FILTER (WHERE vce.event_type = 'read')              AS read_count,
    COUNT(*) FILTER (WHERE vce.event_type = 'reply')             AS reply_count,
    COUNT(*) FILTER (WHERE vce.event_type = 'booked')            AS booked_count,
    -- Tasas del funnel (0-1)
    ROUND(
        COUNT(*) FILTER (WHERE vce.event_type = 'delivered')::numeric
        / NULLIF(COUNT(*) FILTER (WHERE vce.event_type = 'outbound_sent'), 0),
    4)                                                           AS delivery_rate,
    ROUND(
        COUNT(*) FILTER (WHERE vce.event_type = 'read')::numeric
        / NULLIF(COUNT(*) FILTER (WHERE vce.event_type = 'delivered'), 0),
    4)                                                           AS read_rate,
    ROUND(
        COUNT(*) FILTER (WHERE vce.event_type = 'reply')::numeric
        / NULLIF(COUNT(*) FILTER (WHERE vce.event_type = 'read'), 0),
    4)                                                           AS reply_rate,
    ROUND(
        COUNT(*) FILTER (WHERE vce.event_type = 'booked')::numeric
        / NULLIF(COUNT(*) FILTER (WHERE vce.event_type = 'outbound_sent'), 0),
    4)                                                           AS booking_rate,
    -- Tiempos medianos por etapa (ms)
    PERCENTILE_CONT(0.50) WITHIN GROUP (
        ORDER BY vce.elapsed_ms
    ) FILTER (WHERE vce.event_type = 'delivered')               AS p50_delivered_ms,
    PERCENTILE_CONT(0.50) WITHIN GROUP (
        ORDER BY vce.elapsed_ms
    ) FILTER (WHERE vce.event_type = 'read')                    AS p50_read_ms,
    PERCENTILE_CONT(0.50) WITHIN GROUP (
        ORDER BY vce.elapsed_ms
    ) FILTER (WHERE vce.event_type = 'booked')                  AS p50_booked_ms,
    -- Revenue
    COALESCE(SUM(vce.recovered_revenue)
        FILTER (WHERE vce.event_type = 'booked'), 0)            AS total_recovered_revenue,
    MAX(vce.occurred_at)                                         AS last_event_at
FROM variant_conversion_events vce
JOIN message_variants mv ON mv.id = vce.message_variant_id
GROUP BY
    vce.tenant_id,
    mv.flow_id,
    mv.template_id,
    mv.variant_key,
    mv.id,
    mv.is_active;

COMMENT ON VIEW v_variant_conversion_funnel IS
    'Funnel de conversión agregado por variante A/B. Lee variant_conversion_events + message_variants. SIEMPRE filtrar por tenant_id al consultar. Para RLS garantizada usar get_variant_funnel().';

-- ---------------------------------------------------------------------------
-- 8. Función tenant-safe para el funnel (reemplaza la vista en código de aplicación)
--
-- Aplica el filtro de tenant vía current_setting('app.tenant_id') igual que
-- las políticas RLS de las tablas base. Es la forma correcta de consultar
-- el funnel sin riesgo de cross-tenant data leakage.
--
-- USO: SELECT * FROM get_variant_funnel(
--        p_tenant_id := '...',
--        p_from      := '2026-01-01',
--        p_to        := '2026-12-31',
--        p_flow_id   := 'flow_01'   -- opcional
--      );
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION get_variant_funnel(
    p_tenant_id uuid,
    p_from      timestamptz DEFAULT now() - INTERVAL '30 days',
    p_to        timestamptz DEFAULT now(),
    p_flow_id   text        DEFAULT NULL
)
RETURNS TABLE (
    tenant_id            uuid,
    flow_id              text,
    template_id          text,
    variant_key          text,
    message_variant_id   uuid,
    is_active            boolean,
    sent_count           bigint,
    delivered_count      bigint,
    read_count           bigint,
    reply_count          bigint,
    booked_count         bigint,
    delivery_rate        numeric,
    read_rate            numeric,
    reply_rate           numeric,
    booking_rate         numeric,
    p50_delivered_ms     double precision,
    p50_read_ms          double precision,
    p50_booked_ms        double precision,
    total_recovered_revenue numeric,
    last_event_at        timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
    v_current_tenant uuid;
BEGIN
    -- Validar que el llamante tiene acceso a este tenant
    v_current_tenant := current_setting('app.tenant_id', true)::uuid;
    IF v_current_tenant IS NULL OR v_current_tenant <> p_tenant_id THEN
        RAISE EXCEPTION 'Acceso denegado: tenant_id no coincide con el contexto de sesión.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    RETURN QUERY
    SELECT
        vce.tenant_id,
        mv.flow_id,
        mv.template_id,
        mv.variant_key,
        mv.id                                                            AS message_variant_id,
        mv.is_active,
        COUNT(*) FILTER (WHERE vce.event_type = 'outbound_sent')         AS sent_count,
        COUNT(*) FILTER (WHERE vce.event_type = 'delivered')             AS delivered_count,
        COUNT(*) FILTER (WHERE vce.event_type = 'read')                  AS read_count,
        COUNT(*) FILTER (WHERE vce.event_type = 'reply')                 AS reply_count,
        COUNT(*) FILTER (WHERE vce.event_type = 'booked')                AS booked_count,
        ROUND(
            COUNT(*) FILTER (WHERE vce.event_type = 'delivered')::numeric
            / NULLIF(COUNT(*) FILTER (WHERE vce.event_type = 'outbound_sent'), 0), 4) AS delivery_rate,
        ROUND(
            COUNT(*) FILTER (WHERE vce.event_type = 'read')::numeric
            / NULLIF(COUNT(*) FILTER (WHERE vce.event_type = 'delivered'), 0), 4)     AS read_rate,
        ROUND(
            COUNT(*) FILTER (WHERE vce.event_type = 'reply')::numeric
            / NULLIF(COUNT(*) FILTER (WHERE vce.event_type = 'read'), 0), 4)          AS reply_rate,
        ROUND(
            COUNT(*) FILTER (WHERE vce.event_type = 'booked')::numeric
            / NULLIF(COUNT(*) FILTER (WHERE vce.event_type = 'outbound_sent'), 0), 4) AS booking_rate,
        PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY vce.elapsed_ms)
            FILTER (WHERE vce.event_type = 'delivered')                  AS p50_delivered_ms,
        PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY vce.elapsed_ms)
            FILTER (WHERE vce.event_type = 'read')                       AS p50_read_ms,
        PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY vce.elapsed_ms)
            FILTER (WHERE vce.event_type = 'booked')                     AS p50_booked_ms,
        COALESCE(SUM(vce.recovered_revenue) FILTER (WHERE vce.event_type = 'booked'), 0) AS total_recovered_revenue,
        MAX(vce.occurred_at)                                             AS last_event_at
    FROM variant_conversion_events vce
    JOIN message_variants mv ON mv.id = vce.message_variant_id
    WHERE vce.tenant_id = p_tenant_id
      AND vce.occurred_at BETWEEN p_from AND p_to
      AND (p_flow_id IS NULL OR mv.flow_id = p_flow_id)
    GROUP BY
        vce.tenant_id, mv.flow_id, mv.template_id,
        mv.variant_key, mv.id, mv.is_active;
END;
$$;

COMMENT ON FUNCTION get_variant_funnel IS
    'Función tenant-safe para consultar el funnel de variantes A/B. Verifica current_setting(app.tenant_id) para prevenir cross-tenant leakage. Usar en lugar de la vista v_variant_conversion_funnel en código de aplicación.';
