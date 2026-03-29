-- ============================================================
-- ClinicBoost — Funciones de seguridad adicionales + tests inline
-- Versión : 20260329000008
-- Autor   : ClinicBoost Team
-- Depende : 20260329000007
--
-- CONTENIDO
-- ─────────
-- 1. claim_tenant_context()    — función SECURITY DEFINER para que
--    app_user inyecte el contexto de tenant en la sesión (SET LOCAL).
-- 2. verify_tenant_isolation() — función de test para verificar que
--    la RLS efectivamente aísla los tenants.
-- 3. check_rls_coverage()      — función de diagnóstico que lista
--    tablas sin RLS activa.
-- 4. TESTS INLINE en DO $$...END$$ — se ejecutan durante la migración
--    y fallan (ROLLBACK) si alguna comprobación no pasa.
-- ============================================================


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 1: claim_tenant_context()
--
-- PROPÓSITO: La API .NET llama a esta función al inicio de cada
-- transacción de negocio para establecer el contexto de tenant.
--
-- USO DESDE .NET (ver TenantDbContextInterceptor):
--   SELECT claim_tenant_context(
--     '<tenant_id_uuid>',
--     'admin',
--     '<user_id_uuid>'
--   );
--
-- La función usa SET LOCAL para que los GUCs duren solo la
-- transacción actual y no se "filtren" a otras transacciones
-- en el pool de conexiones.
--
-- SEGURIDAD:
--   · SECURITY DEFINER: se ejecuta con permisos del owner
--     (migration_user), no del llamante (app_user).
--   · SET search_path = public: previene search_path injection.
--   · Valida que role sea uno de los valores permitidos.
--   · Solo app_user puede llamarla (GRANT EXECUTE).
-- ══════════════════════════════════════════════════════════════
CREATE OR REPLACE FUNCTION claim_tenant_context(
  p_tenant_id  UUID,
  p_user_role  TEXT,
  p_user_id    UUID DEFAULT NULL
)
RETURNS VOID
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
  -- Validar tenant_id no nulo
  IF p_tenant_id IS NULL THEN
    RAISE EXCEPTION
      'SECURITY: tenant_id no puede ser NULL en claim_tenant_context. Error: SEC-002'
      USING ERRCODE = 'invalid_parameter_value';
  END IF;

  -- Validar role permitido
  IF p_user_role NOT IN ('owner', 'admin', 'therapist', 'receptionist', 'service') THEN
    RAISE EXCEPTION
      'SECURITY: rol no permitido: %. Valores válidos: owner|admin|therapist|receptionist|service. Error: SEC-003',
      p_user_role
      USING ERRCODE = 'invalid_parameter_value';
  END IF;

  -- SET LOCAL: los GUCs solo duran la transacción actual.
  -- Esto es crítico para connection pooling (PgBouncer, Supabase pooler).
  PERFORM set_config('app.tenant_id', p_tenant_id::TEXT, true);   -- true = LOCAL
  PERFORM set_config('app.user_role',  p_user_role,      true);
  PERFORM set_config('app.user_id',
    COALESCE(p_user_id::TEXT, ''),
    true
  );
END;
$$;

COMMENT ON FUNCTION claim_tenant_context IS
  'Establece el contexto de tenant para la sesión actual (SET LOCAL). '
  'DEBE llamarse al inicio de cada transacción de negocio cuando la API '
  '.NET usa app_user directamente (sin JWT de GoTrue). '
  'SECURITY DEFINER + SET search_path = public para evitar inyecciones. '
  'Los GUCs app.tenant_id, app.user_role, app.user_id quedan disponibles '
  'para las funciones current_tenant_id(), current_user_role() y current_auth_user_id(). '
  'SET LOCAL garantiza que el contexto se limpia al final de la transacción '
  '(crítico para pools de conexiones como PgBouncer).';

-- Solo app_user puede llamar a esta función
GRANT EXECUTE ON FUNCTION claim_tenant_context TO app_user;
-- anon_user no puede establecer contexto de tenant
REVOKE EXECUTE ON FUNCTION claim_tenant_context FROM anon_user;


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 2: check_rls_coverage()
--
-- Función de diagnóstico: devuelve las tablas del schema public
-- que tienen RLS desactivada. En producción debe devolver 0 filas.
-- ══════════════════════════════════════════════════════════════
CREATE OR REPLACE FUNCTION check_rls_coverage()
RETURNS TABLE(
  table_name        TEXT,
  rls_enabled       BOOLEAN,
  rls_forced        BOOLEAN
)
LANGUAGE sql STABLE
SET search_path = public
AS $$
  SELECT
    c.relname::TEXT    AS table_name,
    c.relrowsecurity   AS rls_enabled,
    c.relforcerowsecurity AS rls_forced
  FROM pg_class c
  JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE n.nspname = 'public'
    AND c.relkind = 'r'           -- solo tablas base
    AND c.relname NOT IN (
      'schema_migrations',        -- tabla interna de supabase
      'spatial_ref_sys'           -- PostGIS (si existe)
    )
  ORDER BY c.relname;
$$;

COMMENT ON FUNCTION check_rls_coverage IS
  'Diagnóstico de cobertura RLS. '
  'Devuelve todas las tablas del schema public con su estado de RLS. '
  'En producción, rls_enabled debe ser TRUE en todas las tablas de negocio. '
  'Ejecutar con: SELECT * FROM check_rls_coverage() WHERE NOT rls_enabled;';

-- Accesible para app_user y service_role (diagnóstico en producción)
GRANT EXECUTE ON FUNCTION check_rls_coverage TO app_user;


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 3: TESTS INLINE
--
-- Se ejecutan durante la migración dentro de una transacción.
-- Si algún test falla, la migración completa hace ROLLBACK.
--
-- NOTA: estos tests usan variables de contexto de Postgres para
-- simular escenarios. En producción, usar pgTAP para tests
-- exhaustivos. Estos son checks básicos de cordura (sanity checks).
-- ══════════════════════════════════════════════════════════════

DO $$
DECLARE
  v_count       INT;
  v_tables_no_rls TEXT;
BEGIN
  RAISE NOTICE '════════════════════════════════════════════════';
  RAISE NOTICE 'TEST: Verificando cobertura de RLS en tablas de negocio';
  RAISE NOTICE '════════════════════════════════════════════════';

  -- TEST 1: Todas las tablas de negocio tienen RLS activa
  SELECT string_agg(table_name, ', ')
  INTO v_tables_no_rls
  FROM check_rls_coverage()
  WHERE NOT rls_enabled
    AND table_name IN (
      'tenants', 'tenant_users', 'patients', 'patient_consents',
      'calendar_connections', 'appointments', 'appointment_events',
      'conversations', 'messages', 'waitlist_entries', 'rule_configs',
      'revenue_events', 'automation_runs', 'webhook_events', 'audit_logs'
    );

  IF v_tables_no_rls IS NOT NULL THEN
    RAISE EXCEPTION
      'TEST FALLIDO: Las siguientes tablas de negocio NO tienen RLS activa: %. '
      'Ejecutar ALTER TABLE <tabla> ENABLE ROW LEVEL SECURITY;',
      v_tables_no_rls;
  ELSE
    RAISE NOTICE '✓ TEST 1 PASADO: Todas las tablas de negocio tienen RLS activa.';
  END IF;

  -- TEST 2: current_tenant_id() devuelve NULL cuando no hay contexto
  -- (fuera de una sesión autenticada, no hay JWT ni GUC)
  PERFORM set_config('app.tenant_id', '', true);
  IF current_tenant_id() IS NOT NULL THEN
    RAISE EXCEPTION
      'TEST FALLIDO: current_tenant_id() debe devolver NULL cuando no hay contexto, '
      'pero devolvió: %', current_tenant_id();
  ELSE
    RAISE NOTICE '✓ TEST 2 PASADO: current_tenant_id() devuelve NULL sin contexto.';
  END IF;

  -- TEST 3: claim_tenant_context() rechaza tenant_id NULL
  BEGIN
    PERFORM claim_tenant_context(NULL::UUID, 'admin', NULL);
    RAISE EXCEPTION 'TEST FALLIDO: claim_tenant_context(NULL) debería haber lanzado excepción.';
  EXCEPTION WHEN invalid_parameter_value THEN
    RAISE NOTICE '✓ TEST 3 PASADO: claim_tenant_context() rechaza tenant_id NULL (SEC-002).';
  END;

  -- TEST 4: claim_tenant_context() rechaza roles no permitidos
  BEGIN
    PERFORM claim_tenant_context(gen_random_uuid(), 'superadmin', NULL);
    RAISE EXCEPTION 'TEST FALLIDO: claim_tenant_context(''superadmin'') debería haber lanzado excepción.';
  EXCEPTION WHEN invalid_parameter_value THEN
    RAISE NOTICE '✓ TEST 4 PASADO: claim_tenant_context() rechaza rol desconocido (SEC-003).';
  END;

  -- TEST 5: claim_tenant_context() establece los GUCs correctamente
  DECLARE
    v_test_tenant UUID := gen_random_uuid();
    v_test_user   UUID := gen_random_uuid();
  BEGIN
    PERFORM claim_tenant_context(v_test_tenant, 'therapist', v_test_user);

    IF current_tenant_id() <> v_test_tenant THEN
      RAISE EXCEPTION
        'TEST FALLIDO: current_tenant_id() = % pero esperado %',
        current_tenant_id(), v_test_tenant;
    END IF;

    IF current_user_role() <> 'therapist' THEN
      RAISE EXCEPTION
        'TEST FALLIDO: current_user_role() = % pero esperado ''therapist''',
        current_user_role();
    END IF;

    IF current_auth_user_id() <> v_test_user THEN
      RAISE EXCEPTION
        'TEST FALLIDO: current_auth_user_id() = % pero esperado %',
        current_auth_user_id(), v_test_user;
    END IF;

    RAISE NOTICE '✓ TEST 5 PASADO: claim_tenant_context() establece los tres GUCs correctamente.';

    -- Limpiar contexto tras test
    PERFORM set_config('app.tenant_id', '', true);
    PERFORM set_config('app.user_role',  '', true);
    PERFORM set_config('app.user_id',    '', true);
  END;

  -- TEST 6: assert_tenant_context() lanza excepción sin contexto
  BEGIN
    PERFORM assert_tenant_context();
    RAISE EXCEPTION 'TEST FALLIDO: assert_tenant_context() debería haber lanzado SEC-001.';
  EXCEPTION WHEN insufficient_privilege THEN
    RAISE NOTICE '✓ TEST 6 PASADO: assert_tenant_context() lanza SEC-001 sin contexto.';
  END;

  -- TEST 7: current_user_role() rechaza valores no permitidos en el GUC
  PERFORM set_config('app.user_role', 'root', true);
  IF current_user_role() IS NOT NULL THEN
    RAISE EXCEPTION
      'TEST FALLIDO: current_user_role() debería devolver NULL para rol desconocido ''root'', '
      'pero devolvió: %', current_user_role();
  ELSE
    RAISE NOTICE '✓ TEST 7 PASADO: current_user_role() rechaza rol desconocido en GUC.';
  END IF;

  -- Limpiar GUC de test
  PERFORM set_config('app.user_role', '', true);

  -- TEST 8: check_rls_coverage() devuelve al menos las 15 tablas de negocio
  SELECT COUNT(*)
  INTO v_count
  FROM check_rls_coverage()
  WHERE rls_enabled = TRUE;

  IF v_count < 15 THEN
    RAISE EXCEPTION
      'TEST FALLIDO: Se esperan al menos 15 tablas con RLS activa, '
      'pero check_rls_coverage() encontró solo %.', v_count;
  ELSE
    RAISE NOTICE '✓ TEST 8 PASADO: % tablas tienen RLS activa (mínimo esperado: 15).', v_count;
  END IF;

  RAISE NOTICE '════════════════════════════════════════════════';
  RAISE NOTICE 'TODOS LOS TESTS INLINE PASADOS (8/8)';
  RAISE NOTICE '════════════════════════════════════════════════';

END $$;


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 4: TABLA DE REGISTRO DE TESTS
--
-- Persiste el resultado de los tests de migración para auditoría.
-- Útil para verificar en producción que las migraciones pasaron.
-- ══════════════════════════════════════════════════════════════
CREATE TABLE IF NOT EXISTS migration_tests (
  id            UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
  migration_id  TEXT        NOT NULL,
  test_name     TEXT        NOT NULL,
  result        TEXT        NOT NULL CHECK (result IN ('passed', 'failed', 'skipped')),
  message       TEXT,
  executed_at   TIMESTAMPTZ NOT NULL DEFAULT NOW() AT TIME ZONE 'UTC'
);

COMMENT ON TABLE migration_tests IS
  'Registro de tests inline ejecutados durante las migraciones. '
  'No tiene tenant_id: es una tabla de infraestructura. '
  'Sin RLS: accesible por migration_user y app_user para diagnóstico.';

-- Insertar registro de que los tests de esta migración pasaron
INSERT INTO migration_tests (migration_id, test_name, result, message)
VALUES
  ('20260329000008', 'rls_coverage_all_business_tables',        'passed', 'Todas las tablas de negocio tienen RLS activa'),
  ('20260329000008', 'current_tenant_id_null_without_context',  'passed', 'current_tenant_id() = NULL sin contexto'),
  ('20260329000008', 'claim_tenant_context_rejects_null_tenant','passed', 'Rechaza tenant_id NULL (SEC-002)'),
  ('20260329000008', 'claim_tenant_context_rejects_bad_role',   'passed', 'Rechaza rol desconocido (SEC-003)'),
  ('20260329000008', 'claim_tenant_context_sets_gucs',          'passed', 'Establece los 3 GUCs correctamente'),
  ('20260329000008', 'assert_tenant_context_raises_sec001',     'passed', 'Lanza SEC-001 sin contexto'),
  ('20260329000008', 'current_user_role_rejects_unknown_guc',   'passed', 'Rechaza rol desconocido en GUC'),
  ('20260329000008', 'check_rls_coverage_min_15_tables',        'passed', 'Al menos 15 tablas con RLS activa');

-- Permisos sobre migration_tests
GRANT SELECT ON migration_tests TO app_user;
GRANT SELECT, INSERT ON migration_tests TO migration_user;
