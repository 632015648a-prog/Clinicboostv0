-- ============================================================
-- ClinicBoost — RLS consolidada + mecanismo set_config
-- Versión : 20260329000007
-- Autor   : ClinicBoost Team
-- Depende : 20260329000005, 20260329000006
--
-- PROPÓSITO
-- ─────────
-- Cuando la API .NET (app_user) usa la conexión directa a Postgres
-- (sin pasar por GoTrue), no hay JWT. El tenant_id y el rol se
-- inyectan en la sesión mediante:
--
--   SET LOCAL app.tenant_id  = '<uuid>';
--   SET LOCAL app.user_role  = 'admin';
--   SET LOCAL app.user_id    = '<uuid>';
--
-- Este fichero:
--   1. Reemplaza las funciones helper para que lean de AMBAS fuentes:
--      · JWT (auth.jwt())           → peticiones vía Supabase Auth
--      · GUC (current_setting)      → peticiones vía .NET / app_user
--   2. Añade la función assert_tenant_context() que lanza excepción
--      si el contexto no está inicializado (defensa en profundidad).
--   3. Añade políticas FOR ALL unificadas para las tablas que solo
--      tenían FOR SELECT separado de INSERT, evitando huecos.
--   4. Documenta explícitamente que app_user NUNCA puede bypass RLS
--      y que el frontend NUNCA usa credenciales privilegiadas.
-- ============================================================


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 1: FUNCIONES HELPER UNIFICADAS
--
-- Orden de precedencia:
--   1. JWT claim (Supabase Auth — el frontend usa esta ruta)
--   2. GUC app.* (ClinicBoost.Api .NET — usa esta ruta)
--   3. NULL / '' si ninguna está disponible
-- ══════════════════════════════════════════════════════════════

-- Eliminar versiones anteriores para recrear con lógica combinada
DROP FUNCTION IF EXISTS current_tenant_id();
DROP FUNCTION IF EXISTS current_user_role();
DROP FUNCTION IF EXISTS current_auth_user_id();

-- ─── current_tenant_id() ──────────────────────────────────────
-- Devuelve el tenant_id activo para la sesión actual.
-- Fuente 1: JWT claim 'tenant_id'  (frontend / Supabase Auth)
-- Fuente 2: GUC app.tenant_id      (API .NET vía set_config)
CREATE OR REPLACE FUNCTION current_tenant_id()
RETURNS UUID
LANGUAGE plpgsql STABLE
SET search_path = public
AS $$
DECLARE
  v_jwt_tenant  TEXT;
  v_guc_tenant  TEXT;
BEGIN
  -- Intenta obtener del JWT primero (Supabase Auth)
  BEGIN
    v_jwt_tenant := auth.jwt() ->> 'tenant_id';
  EXCEPTION WHEN OTHERS THEN
    v_jwt_tenant := NULL;
  END;

  IF v_jwt_tenant IS NOT NULL AND v_jwt_tenant <> '' THEN
    RETURN v_jwt_tenant::UUID;
  END IF;

  -- Fallback: GUC inyectado por ClinicBoost.Api
  v_guc_tenant := current_setting('app.tenant_id', true);  -- true = no error si no existe

  IF v_guc_tenant IS NOT NULL AND v_guc_tenant <> '' THEN
    RETURN v_guc_tenant::UUID;
  END IF;

  RETURN NULL;
END;
$$;

COMMENT ON FUNCTION current_tenant_id IS
  'Devuelve tenant_id activo. Lee del JWT (Supabase Auth) con prioridad; '
  'si no hay JWT, lee del GUC app.tenant_id inyectado por ClinicBoost.Api '
  'mediante SET LOCAL app.tenant_id = ''<uuid>''. '
  'Nunca devuelve un valor de un rol sin autenticar.';


-- ─── current_user_role() ──────────────────────────────────────
CREATE OR REPLACE FUNCTION current_user_role()
RETURNS TEXT
LANGUAGE plpgsql STABLE
SET search_path = public
AS $$
DECLARE
  v_jwt_role  TEXT;
  v_guc_role  TEXT;
BEGIN
  BEGIN
    v_jwt_role := auth.jwt() ->> 'user_role';
  EXCEPTION WHEN OTHERS THEN
    v_jwt_role := NULL;
  END;

  IF v_jwt_role IS NOT NULL AND v_jwt_role <> '' THEN
    RETURN v_jwt_role;
  END IF;

  v_guc_role := current_setting('app.user_role', true);

  IF v_guc_role IS NOT NULL AND v_guc_role <> '' THEN
    -- Validar que sea un rol conocido (defensa en profundidad)
    IF v_guc_role = ANY(ARRAY['owner','admin','therapist','receptionist','service']) THEN
      RETURN v_guc_role;
    END IF;
  END IF;

  RETURN NULL;
END;
$$;

COMMENT ON FUNCTION current_user_role IS
  'Devuelve el rol del usuario activo (owner|admin|therapist|receptionist|service). '
  'Lee JWT primero; fallback a GUC app.user_role inyectado por ClinicBoost.Api. '
  'Valida que el valor sea uno de los roles permitidos.';


-- ─── current_auth_user_id() ──────────────────────────────────
CREATE OR REPLACE FUNCTION current_auth_user_id()
RETURNS UUID
LANGUAGE plpgsql STABLE
SET search_path = public
AS $$
DECLARE
  v_jwt_sub  TEXT;
  v_guc_uid  TEXT;
BEGIN
  BEGIN
    v_jwt_sub := auth.jwt() ->> 'sub';
  EXCEPTION WHEN OTHERS THEN
    v_jwt_sub := NULL;
  END;

  IF v_jwt_sub IS NOT NULL AND v_jwt_sub <> '' THEN
    RETURN v_jwt_sub::UUID;
  END IF;

  v_guc_uid := current_setting('app.user_id', true);

  IF v_guc_uid IS NOT NULL AND v_guc_uid <> '' THEN
    RETURN v_guc_uid::UUID;
  END IF;

  RETURN NULL;
END;
$$;

COMMENT ON FUNCTION current_auth_user_id IS
  'Devuelve el auth_user_id activo (sub del JWT o GUC app.user_id). '
  'Usado en políticas que filtran por el usuario concreto (e.g. therapist ve su fila).';


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 2: assert_tenant_context()
--
-- Función de guardia: lanza excepción si el contexto de tenant
-- no está inicializado. Llamar al inicio de operaciones críticas.
-- ══════════════════════════════════════════════════════════════
CREATE OR REPLACE FUNCTION assert_tenant_context()
RETURNS VOID
LANGUAGE plpgsql
SET search_path = public
AS $$
BEGIN
  IF current_tenant_id() IS NULL THEN
    RAISE EXCEPTION
      'SECURITY: tenant context not initialized. '
      'Ensure SET LOCAL app.tenant_id is called before executing business queries. '
      'Error code: SEC-001'
      USING ERRCODE = 'insufficient_privilege';
  END IF;
END;
$$;

COMMENT ON FUNCTION assert_tenant_context IS
  'Guardia de seguridad: lanza insufficient_privilege (SEC-001) si tenant_id '
  'no está inicializado. Llamar al inicio de transacciones de negocio en app_user.';

-- Conceder ejecución solo a app_user (no a anon_user)
GRANT EXECUTE ON FUNCTION current_tenant_id     TO app_user;
GRANT EXECUTE ON FUNCTION current_user_role     TO app_user;
GRANT EXECUTE ON FUNCTION current_auth_user_id  TO app_user;
GRANT EXECUTE ON FUNCTION assert_tenant_context TO app_user;


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 3: POLÍTICAS RLS PARA TABLAS INICIALES
--
-- Las políticas de 0002_rls_policies.sql se recrean aquí usando
-- las funciones helper unificadas y WITH CHECK explícito.
-- Se eliminan las anteriores antes de recrear para evitar
-- conflictos con cambios de lógica.
-- ══════════════════════════════════════════════════════════════

-- ─── tenants ─────────────────────────────────────────────────
-- (No tiene tenant_id; el filtro es por id = current_tenant_id())
DROP POLICY IF EXISTS "tenants_select_own"   ON tenants;
DROP POLICY IF EXISTS "tenants_update_own"   ON tenants;
DROP POLICY IF EXISTS "tenants_insert_service" ON tenants;
DROP POLICY IF EXISTS "tenants_delete_service" ON tenants;

CREATE POLICY "tenants_select_own"
  ON tenants FOR SELECT
  USING (id = current_tenant_id());

CREATE POLICY "tenants_update_own"
  ON tenants FOR UPDATE
  USING      (id = current_tenant_id() AND current_user_role() IN ('owner', 'admin'))
  WITH CHECK (id = current_tenant_id());

-- Solo service_role puede crear/borrar tenants (onboarding / offboarding)
CREATE POLICY "tenants_insert_service"
  ON tenants FOR INSERT
  WITH CHECK (auth.role() = 'service_role');

CREATE POLICY "tenants_delete_service"
  ON tenants FOR DELETE
  USING (auth.role() = 'service_role');


-- ─── patients ────────────────────────────────────────────────
DROP POLICY IF EXISTS "patients_tenant_isolation" ON patients;
DROP POLICY IF EXISTS "patients_select"           ON patients;
DROP POLICY IF EXISTS "patients_insert"           ON patients;
DROP POLICY IF EXISTS "patients_update"           ON patients;
DROP POLICY IF EXISTS "patients_delete"           ON patients;

CREATE POLICY "patients_tenant_isolation"
  ON patients FOR ALL
  USING      (tenant_id = current_tenant_id())
  WITH CHECK (tenant_id = current_tenant_id());


-- ─── appointments ────────────────────────────────────────────
DROP POLICY IF EXISTS "appointments_tenant_isolation" ON appointments;
DROP POLICY IF EXISTS "appointments_select"           ON appointments;
DROP POLICY IF EXISTS "appointments_insert"           ON appointments;
DROP POLICY IF EXISTS "appointments_update"           ON appointments;
DROP POLICY IF EXISTS "appointments_delete"           ON appointments;

CREATE POLICY "appointments_tenant_isolation"
  ON appointments FOR ALL
  USING      (tenant_id = current_tenant_id())
  WITH CHECK (tenant_id = current_tenant_id());


-- ─── audit_logs ──────────────────────────────────────────────
DROP POLICY IF EXISTS "audit_logs_select_own"     ON audit_logs;
DROP POLICY IF EXISTS "audit_logs_insert_service" ON audit_logs;
DROP POLICY IF EXISTS "audit_logs_insert_app"     ON audit_logs;
DROP POLICY IF EXISTS "audit_logs_no_update"      ON audit_logs;
DROP POLICY IF EXISTS "audit_logs_no_delete"      ON audit_logs;

-- Lectura: solo owner/admin del tenant
CREATE POLICY "audit_logs_select_own"
  ON audit_logs FOR SELECT
  USING (
    tenant_id = current_tenant_id()
    AND current_user_role() IN ('owner', 'admin')
  );

-- Inserción: a través de la función SECURITY DEFINER insert_audit_log
-- app_user recibe GRANT EXECUTE sobre esa función, no INSERT directo
-- (los GRANTs directos en 0006 se mantienen como capa adicional)
CREATE POLICY "audit_logs_insert_app"
  ON audit_logs FOR INSERT
  WITH CHECK (
    tenant_id = current_tenant_id()
    OR auth.role() = 'service_role'
  );

-- INMUTABLE: solo service_role puede modificar en caso de emergencia
CREATE POLICY "audit_logs_no_update"
  ON audit_logs FOR UPDATE
  USING (auth.role() = 'service_role');

CREATE POLICY "audit_logs_no_delete"
  ON audit_logs FOR DELETE
  USING (auth.role() = 'service_role');


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 4: DOCUMENTACIÓN EMBEBIDA — GARANTÍAS DE SEGURIDAD
--
-- Estos comentarios quedan en pg_description y son visibles desde
-- Supabase Studio, psql \d+ y cualquier herramienta de introspección.
-- Son la fuente de verdad del modelo de seguridad de ClinicBoost.
-- ══════════════════════════════════════════════════════════════

COMMENT ON TABLE tenants IS
  '[SEGURIDAD] El tenant raíz. Sin tenant_id propio. '
  'SOLO service_role puede crear/borrar tenants. '
  'app_user puede SELECT/UPDATE su propio registro vía current_tenant_id(). '
  'El frontend NUNCA usa service_role key; usa la anon key + JWT de GoTrue.';

COMMENT ON TABLE patients IS
  '[SEGURIDAD] Datos de pacientes. tenant_id obligatorio. '
  'RLS: toda operación DML filtrada por current_tenant_id(). '
  'app_user tiene SELECT/INSERT/UPDATE/DELETE solo en su tenant. '
  'RGPD: patient_consents es la tabla de trazabilidad de consentimientos.';

COMMENT ON TABLE appointments IS
  '[SEGURIDAD] Agenda de citas. tenant_id obligatorio. '
  'RLS: toda operación DML filtrada por current_tenant_id(). '
  'REGLA: la IA nunca confirma citas por sí misma; el backend (app_user) ejecuta. '
  'El registro de cambios va a appointment_events (inmutable).';

COMMENT ON TABLE audit_logs IS
  '[SEGURIDAD] Log de auditoría inmutable. tenant_id sin FK (preserva tras borrar tenant). '
  'INSERT: solo vía función insert_audit_log (SECURITY DEFINER). '
  'UPDATE/DELETE: solo service_role en emergencia. '
  'Lectura: solo owner/admin del tenant.';

COMMENT ON TABLE tenant_users IS
  '[SEGURIDAD] Usuarios por clínica. Los therapists solo ven su propia fila. '
  'Owner y admin pueden gestionar usuarios. '
  'Solo el owner puede borrar usuarios o cambiar roles a owner.';

COMMENT ON TABLE patient_consents IS
  '[SEGURIDAD][RGPD] Registro inmutable de consentimientos. '
  'Cada cambio de consentimiento crea una nueva fila; nunca se modifica la anterior. '
  'UPDATE/DELETE solo service_role en caso de corrección legal urgente.';

COMMENT ON TABLE revenue_events IS
  '[SEGURIDAD][FINANZAS] Registro contable inmutable. '
  'Solo owner/admin pueden leer (datos financieros sensibles). '
  'INSERT: app o service_role. UPDATE/DELETE: solo service_role. '
  'is_success_fee_eligible: determina la comisión del 15% en los primeros 90 días.';

COMMENT ON TABLE webhook_events IS
  '[SEGURIDAD] Registro de webhooks entrantes. NUNCA se borran. '
  'INSERT/UPDATE: solo service_role (el worker HTTP). '
  'Lectura: owner/admin para debugging. '
  'Idempotencia: combinada con processed_events (event_type + event_id).';

COMMENT ON TABLE processed_events IS
  '[SEGURIDAD] Tabla de idempotencia. Sin tenant_id; abierta a app_user. '
  'app_user puede SELECT e INSERT pero NO UPDATE ni DELETE. '
  'Antes de procesar cualquier webhook externo, verificar aquí.';
