-- ============================================================================
-- Migración 0009: Servicio de idempotencia transversal
-- Autor: ClinicBoost Team
-- Depende de: 0001-0008
--
-- CAMBIOS
-- ───────
-- 1. Evolución de processed_events: añadir tenant_id y payload_hash.
-- 2. Reemplazar el índice UNIQUE antiguo por uno nuevo con semántica correcta
--    para NULL (NULLS NOT DISTINCT — requiere Postgres ≥ 15, compatible Supabase).
-- 3. Índices de rendimiento para consultas frecuentes.
-- 4. Permisos: app_user puede INSERT y SELECT; nunca UPDATE/DELETE.
-- 5. Tests inline que verifican el comportamiento esperado.
--
-- DISEÑO DE SEGURIDAD
-- ───────────────────
-- · processed_events NO tiene RLS ni requiere tenant_id obligatorio.
--   Permite registrar webhooks entrantes antes de asociarlos a un tenant.
-- · tenant_id nullable: presente cuando se conoce el tenant, NULL para globales.
-- · UNIQUE (event_type, event_id, tenant_id) NULLS NOT DISTINCT:
--   dos filas con tenant_id = NULL y mismo (event_type, event_id) se consideran
--   duplicadas → evita procesamiento doble en webhooks sin tenant resuelto.
-- · payload_hash (SHA-256 hex) detecta re-entregas con cuerpo alterado.
-- · Inmutable: INSERT only; UPDATE y DELETE revocados a app_user.
-- ============================================================================

-- ── 1. Evolucion de la tabla processed_events ────────────────────────────────

-- 1a. Añadir tenant_id (nullable — véase diseño de seguridad arriba)
ALTER TABLE public.processed_events
    ADD COLUMN IF NOT EXISTS tenant_id UUID REFERENCES public.tenants(id)
        ON DELETE SET NULL;

-- 1b. Añadir payload_hash (SHA-256 hex, 64 chars)
ALTER TABLE public.processed_events
    ADD COLUMN IF NOT EXISTS payload_hash TEXT
        CONSTRAINT chk_processed_events_hash_len
            CHECK (payload_hash IS NULL OR char_length(payload_hash) = 64);

-- ── 2. Índice UNIQUE principal con semántica NULL correcta ────────────────────
-- Eliminamos el índice anterior (si existe) y creamos el nuevo.

-- Intentar eliminar el índice antiguo si existe con nombre diferente
DROP INDEX IF EXISTS public.processed_events_event_type_event_id_key;
DROP INDEX IF EXISTS public.idx_processed_events_unique;

-- Índice principal de unicidad:
-- NULLS NOT DISTINCT → dos NULLs en tenant_id se consideran iguales.
-- Requiere Postgres 15+. Supabase usa Postgres 15+ desde 2023.
CREATE UNIQUE INDEX IF NOT EXISTS uq_processed_events_type_id_tenant
    ON public.processed_events (event_type, event_id, tenant_id) NULLS NOT DISTINCT;

-- ── 3. Índices de rendimiento ─────────────────────────────────────────────────

-- Consultas por tenant (listar eventos del tenant, diagnóstico)
CREATE INDEX IF NOT EXISTS idx_processed_events_tenant_id
    ON public.processed_events (tenant_id)
    WHERE tenant_id IS NOT NULL;

-- Consultas por event_type (monitoreo por proveedor)
CREATE INDEX IF NOT EXISTS idx_processed_events_event_type
    ON public.processed_events (event_type);

-- Consultas por processed_at (limpieza GDPR / retención de datos)
CREATE INDEX IF NOT EXISTS idx_processed_events_processed_at
    ON public.processed_events (processed_at DESC);

-- ── 4. Permisos ───────────────────────────────────────────────────────────────

-- app_user: solo INSERT y SELECT (nunca UPDATE/DELETE — tabla inmutable)
GRANT SELECT, INSERT ON public.processed_events TO app_user;
REVOKE UPDATE, DELETE ON public.processed_events FROM app_user;

-- anon_user: sin acceso (webhooks públicos pasan por el API server, no directo)
REVOKE ALL ON public.processed_events FROM anon_user;

-- ── 5. Comentarios actualizados ───────────────────────────────────────────────

COMMENT ON TABLE public.processed_events IS
    'Registro de idempotencia transversal. Inmutable: INSERT only.
     Garantiza que cada evento externo (Twilio, etc.) o interno (job)
     se procesa exactamente una vez. Ver ADR-006.';

COMMENT ON COLUMN public.processed_events.event_type IS
    'Tipo de evento según convención "{proveedor}.{subtipo}".
     Ejemplos: twilio.whatsapp_inbound, twilio.message_status,
               twilio.voice_inbound, internal.appointment_reminder.';

COMMENT ON COLUMN public.processed_events.event_id IS
    'ID único del evento asignado por el proveedor o por el caller.
     Twilio: SID (SMxxx, CAxxx). Jobs: run UUID. Debe ser globalmente único por event_type.';

COMMENT ON COLUMN public.processed_events.tenant_id IS
    'Tenant al que pertenece el evento. NULL para webhooks globales o
     eventos recibidos antes de resolver el tenant.
     El índice uq_processed_events_type_id_tenant usa NULLS NOT DISTINCT:
     dos NULLs con mismo (event_type, event_id) se consideran duplicados.';

COMMENT ON COLUMN public.processed_events.payload_hash IS
    'SHA-256 (hex 64 chars) del payload serializado del evento.
     Detecta re-entregas con mismo ID pero cuerpo alterado (replay attack).
     NULL cuando el caller no proporciona payload.';

COMMENT ON COLUMN public.processed_events.metadata IS
    'JSON opcional para diagnóstico: IP de origen, correlation ID, headers, etc.';

-- ── 6. Tests inline ───────────────────────────────────────────────────────────
-- Verifican el comportamiento del índice UNIQUE y permisos.
-- Usan DO $$ blocks con ASSERT que abortan si falla.

DO $$
DECLARE
    v_tenant_a UUID := '10000000-0000-0000-0000-000000000001';
    v_tenant_b UUID := '10000000-0000-0000-0000-000000000002';
    v_now      TIMESTAMPTZ := NOW();
BEGIN

    -- ── Test 1: Inserción básica funciona ─────────────────────────────────
    INSERT INTO public.processed_events
        (id, event_type, event_id, tenant_id, payload_hash, processed_at)
    VALUES
        ('a0000000-0000-0000-0000-000000000001',
         'test.basic', 'evt-001', v_tenant_a,
         LPAD('a', 64, 'a'), v_now);

    ASSERT (SELECT COUNT(*) FROM public.processed_events
            WHERE event_id = 'evt-001' AND event_type = 'test.basic') = 1,
        'Test 1 FAILED: inserción básica no funcionó';
    RAISE NOTICE 'Test 1 OK: inserción básica funciona';


    -- ── Test 2: ON CONFLICT silencia el duplicado (mismo tenant) ─────────
    INSERT INTO public.processed_events
        (id, event_type, event_id, tenant_id, payload_hash, processed_at)
    VALUES
        ('a0000000-0000-0000-0000-000000000002',
         'test.basic', 'evt-001', v_tenant_a,
         LPAD('b', 64, 'b'), v_now)
    ON CONFLICT (event_type, event_id, tenant_id) DO NOTHING;

    ASSERT (SELECT COUNT(*) FROM public.processed_events
            WHERE event_id = 'evt-001' AND event_type = 'test.basic'
              AND tenant_id = v_tenant_a) = 1,
        'Test 2 FAILED: debería existir solo 1 fila para (test.basic, evt-001, tenant_a)';
    RAISE NOTICE 'Test 2 OK: duplicado silenciado correctamente por ON CONFLICT';


    -- ── Test 3: Mismo event_id, tenant diferente → no es duplicado ────────
    INSERT INTO public.processed_events
        (id, event_type, event_id, tenant_id, payload_hash, processed_at)
    VALUES
        ('a0000000-0000-0000-0000-000000000003',
         'test.basic', 'evt-001', v_tenant_b,
         LPAD('c', 64, 'c'), v_now);

    ASSERT (SELECT COUNT(*) FROM public.processed_events
            WHERE event_id = 'evt-001' AND event_type = 'test.basic') = 2,
        'Test 3 FAILED: eventos con tenants distintos deben coexistir';
    RAISE NOTICE 'Test 3 OK: mismo event_id con tenant diferente es evento distinto';


    -- ── Test 4: tenant_id NULL — dos NULLs con mismo (type, id) → duplicado
    INSERT INTO public.processed_events
        (id, event_type, event_id, tenant_id, payload_hash, processed_at)
    VALUES
        ('a0000000-0000-0000-0000-000000000004',
         'test.global', 'evt-global-001', NULL, NULL, v_now);

    INSERT INTO public.processed_events
        (id, event_type, event_id, tenant_id, payload_hash, processed_at)
    VALUES
        ('a0000000-0000-0000-0000-000000000005',
         'test.global', 'evt-global-001', NULL, NULL, v_now)
    ON CONFLICT (event_type, event_id, tenant_id) DO NOTHING;

    ASSERT (SELECT COUNT(*) FROM public.processed_events
            WHERE event_id = 'evt-global-001' AND event_type = 'test.global') = 1,
        'Test 4 FAILED: dos NULLs con mismo (type, id) deben considerarse duplicados';
    RAISE NOTICE 'Test 4 OK: NULLS NOT DISTINCT funciona correctamente';


    -- ── Test 5: payload_hash debe tener exactamente 64 chars si no es NULL ─
    BEGIN
        INSERT INTO public.processed_events
            (id, event_type, event_id, tenant_id, payload_hash, processed_at)
        VALUES
            ('a0000000-0000-0000-0000-000000000006',
             'test.hash', 'evt-hash-001', NULL, 'short', v_now);

        RAISE EXCEPTION 'Test 5 FAILED: debería haber rechazado hash de longitud incorrecta';
    EXCEPTION
        WHEN check_violation THEN
            RAISE NOTICE 'Test 5 OK: constraint de longitud de payload_hash funciona';
    END;


    -- ── Test 6: event_type distinto con mismo event_id → no es duplicado ──
    INSERT INTO public.processed_events
        (id, event_type, event_id, tenant_id, payload_hash, processed_at)
    VALUES
        ('a0000000-0000-0000-0000-000000000007',
         'test.other', 'evt-001', v_tenant_a, NULL, v_now);

    ASSERT (SELECT COUNT(*) FROM public.processed_events
            WHERE event_id = 'evt-001' AND tenant_id = v_tenant_a) = 2,
        'Test 6 FAILED: mismo event_id con event_type diferente debe ser evento distinto';
    RAISE NOTICE 'Test 6 OK: event_type distinto con mismo event_id es evento distinto';


    -- ── Limpieza de datos de test ──────────────────────────────────────────
    DELETE FROM public.processed_events
    WHERE event_type LIKE 'test.%';

    RAISE NOTICE '=== Todos los tests de idempotencia pasaron (0009) ===';

END $$;

-- (el runner de Supabase gestiona la transacción externamente)
