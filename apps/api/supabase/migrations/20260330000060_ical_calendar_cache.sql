-- ─────────────────────────────────────────────────────────────────────────────
-- Migration: 20260330000060_ical_calendar_cache
--
-- Objetivo:
--   Crear la tabla calendar_cache para persistir las lecturas iCal.
--   Esto desacopla la disponibilidad del servicio iCal externo de la capacidad
--   de ClinicBoost de mostrar huecos; si el servidor externo falla, se sirven
--   datos stale con la antigüedad máxima configurable en ICalOptions.MaxStaleAge.
--
-- Diseño:
--   · Una fila por (tenant_id, connection_id) — UNIQUE constraint.
--   · slots_json almacena el array de slots como JSONB para lectura directa
--     sin deserializar en memoria cuando solo se necesitan rangos de tiempo.
--   · ETag / last_modified_utc permiten peticiones condicionales HTTP al servidor
--     iCal (ahorro de ancho de banda y latencia cuando el feed no ha cambiado).
--   · content_hash (SHA-256 del .ics crudo) detecta cambios sin parsear.
--   · expires_at_utc habilita un job de limpieza sin escanear toda la tabla.
--   · RLS habilitado: tenant_id filtra automáticamente todas las queries.
--
-- Índices:
--   · uq_calendar_cache_tenant_connection — unicidad y lookup principal.
--   · ix_calendar_cache_expires_at        — expiración pasiva / limpieza.
--   · ix_calendar_cache_tenant_fetched    — ordenar por frescura por tenant.
-- ─────────────────────────────────────────────────────────────────────────────

-- ── Tabla principal ──────────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS calendar_cache (
    id                  UUID            NOT NULL DEFAULT gen_random_uuid(),
    tenant_id           UUID            NOT NULL,
    connection_id       UUID            NOT NULL,

    -- Slots serializados como JSONB
    -- Estructura: [{"startsAtUtc":"...","endsAtUtc":"...","summary":"...","uid":"...","isOpaque":true}]
    slots_json          JSONB           NOT NULL DEFAULT '[]'::jsonb,

    -- Metadatos de frescura
    fetched_at_utc      TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    expires_at_utc      TIMESTAMPTZ     NOT NULL DEFAULT NOW() + INTERVAL '15 minutes',

    -- Cabeceras HTTP para peticiones condicionales
    etag                TEXT,
    last_modified_utc   TIMESTAMPTZ,

    -- Hash del .ics crudo (SHA-256 hex) para detectar cambios sin parsear
    content_hash        CHAR(64),

    -- Diagnóstico: último error de lectura (null = última lectura OK)
    last_error_message  TEXT,

    -- Auditoría
    created_at          TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    updated_at          TIMESTAMPTZ     NOT NULL DEFAULT NOW(),

    CONSTRAINT pk_calendar_cache PRIMARY KEY (id),

    -- Integridad referencial: la conexión debe existir
    CONSTRAINT fk_calendar_cache_connection
        FOREIGN KEY (connection_id)
        REFERENCES calendar_connections(id)
        ON DELETE CASCADE,

    -- Integridad referencial: el tenant debe existir
    CONSTRAINT fk_calendar_cache_tenant
        FOREIGN KEY (tenant_id)
        REFERENCES tenants(id)
        ON DELETE CASCADE
);

-- ── Índices ──────────────────────────────────────────────────────────────────

-- Lookup principal + constraint de unicidad (una entrada por conexión)
CREATE UNIQUE INDEX IF NOT EXISTS uq_calendar_cache_tenant_connection
    ON calendar_cache (tenant_id, connection_id);

-- Job de limpieza / expiración pasiva
CREATE INDEX IF NOT EXISTS ix_calendar_cache_expires_at
    ON calendar_cache (expires_at_utc);

-- Ordenar entradas por frescura dentro de un tenant
CREATE INDEX IF NOT EXISTS ix_calendar_cache_tenant_fetched
    ON calendar_cache (tenant_id, fetched_at_utc DESC);

-- ── Row Level Security ───────────────────────────────────────────────────────

ALTER TABLE calendar_cache ENABLE ROW LEVEL SECURITY;

-- Política: el app_user solo ve filas de su tenant (set al comienzo del request)
CREATE POLICY calendar_cache_tenant_isolation
    ON calendar_cache
    USING (tenant_id = current_setting('app.tenant_id', TRUE)::UUID);

-- ── Trigger para updated_at automático ──────────────────────────────────────

-- Reutilizamos la función trigger estándar del proyecto si ya existe,
-- o la creamos aquí de forma idempotente.
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at = NOW();
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_calendar_cache_updated_at
    BEFORE UPDATE ON calendar_cache
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

-- ── Vista de diagnóstico: estado de la caché por conexión ────────────────────

CREATE OR REPLACE VIEW v_calendar_cache_status AS
SELECT
    cc.tenant_id,
    cc.connection_id,
    cal.display_name,
    cal.ical_url,
    cal.sync_status,
    cc.fetched_at_utc,
    cc.expires_at_utc,
    NOW() - cc.fetched_at_utc                        AS data_age,
    cc.expires_at_utc > NOW()                         AS is_fresh,
    jsonb_array_length(cc.slots_json)                 AS slot_count,
    cc.content_hash,
    cc.last_error_message,
    cc.updated_at
FROM calendar_cache cc
JOIN calendar_connections cal
    ON cal.id = cc.connection_id;

COMMENT ON VIEW v_calendar_cache_status IS
    'Estado de la caché iCal por conexión: permite diagnosticar frescura, '
    'conteo de slots y errores sin consultar tablas internas.';

-- ── Comentarios de columnas ──────────────────────────────────────────────────

COMMENT ON TABLE calendar_cache IS
    'Caché persistida de lecturas iCal. '
    'Permite servir datos frescos aunque el servidor externo esté caído (fallback stale). '
    'No depende solo de memoria: si el proceso se reinicia, los datos siguen disponibles.';

COMMENT ON COLUMN calendar_cache.slots_json IS
    'Array de ICalSlot serializado como JSONB. '
    'Cada elemento tiene: startsAtUtc, endsAtUtc, summary, uid, isOpaque.';

COMMENT ON COLUMN calendar_cache.expires_at_utc IS
    'Marca de expiración pasiva. Calculado como fetched_at_utc + ICalOptions.FreshnessTtl. '
    'Un job de limpieza puede usar esta columna para purgar entradas expiradas hace más de MaxStaleAge.';

COMMENT ON COLUMN calendar_cache.content_hash IS
    'SHA-256 hex del fichero .ics crudo. '
    'Permite detectar si el feed ha cambiado sin parsear ni comparar los slots.';

COMMENT ON COLUMN calendar_cache.last_error_message IS
    'Último error de lectura de la URL iCal. '
    'NULL si la última lectura fue exitosa. '
    'Se actualiza con MarkErrorAsync sin sobreescribir los slots válidos.';
