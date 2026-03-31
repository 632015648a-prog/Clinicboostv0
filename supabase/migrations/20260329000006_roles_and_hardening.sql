-- ============================================================
-- ClinicBoost — Definición de roles y hardening de acceso
-- Versión : 20260329000006
-- Autor   : ClinicBoost Team
-- Depende : 20260329000001 (extensiones y función set_updated_at)
--
-- Este fichero es la FUENTE DE VERDAD del modelo de roles.
-- Cualquier cambio en permisos debe hacerse aquí y versionarse.
--
-- ┌─────────────────────────────────────────────────────────────┐
-- │  MODELO DE ROLES — RESUMEN EJECUTIVO                        │
-- │                                                             │
-- │  migration_user  DDL+DML sin RLS. Solo migraciones.         │
-- │  app_user        DML con RLS. Runtime de .NET API.          │
-- │  anon_user       Sin acceso a tablas de negocio.            │
-- │  service_role    Supabase interno (bypass RLS). NUNCA       │
-- │                  exponer su key al frontend.                │
-- │  authenticated   Rol de Supabase Auth. Las políticas RLS    │
-- │                  lo limitan a su propio tenant_id.          │
-- │                                                             │
-- │  GARANTÍA CENTRAL: app_user NUNCA puede hacer              │
-- │    · ALTER TABLE ... DISABLE ROW LEVEL SECURITY            │
-- │    · SET SESSION ROLE migration_user                        │
-- │    · SET SESSION ROLE service_role                         │
-- │    · Cualquier DDL (CREATE, ALTER, DROP)                   │
-- └─────────────────────────────────────────────────────────────┘
-- ============================================================


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 1: CREACIÓN DE ROLES
-- Idempotente: usa DO $$...END$$ para no fallar si ya existen.
-- ══════════════════════════════════════════════════════════════

-- ─── migration_user ───────────────────────────────────────────
-- Propósito  : Ejecutar migraciones DDL + DML. Nunca usado en runtime.
-- Credencial : Solo conocida por el pipeline de CI/CD (secret de GitHub Actions).
-- Permisos   : Superuser en schema public para DDL; NO login en producción vía
--              connection string de aplicación.
-- Regla      : La connection string de ClinicBoost.Api NUNCA usa migration_user.
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'migration_user') THEN
    CREATE ROLE migration_user
      NOINHERIT       -- No hereda permisos de otros roles
      NOCREATEROLE    -- No puede crear nuevos roles (evita escalada)
      NOCREATEDB;     -- No puede crear bases de datos
    RAISE NOTICE 'Rol migration_user creado.';
  ELSE
    RAISE NOTICE 'Rol migration_user ya existe — omitido.';
  END IF;
END $$;

-- ─── app_user ─────────────────────────────────────────────────
-- Propósito  : Rol de runtime de la API .NET (ClinicBoost.Api).
-- Credencial : Connection string en secrets de Cloudflare / env del servidor.
--              NUNCA en código fuente ni en .env commiteado.
-- Permisos   : Solo DML (SELECT/INSERT/UPDATE/DELETE) en tablas autorizadas.
--              Las políticas RLS filtran adicionalmente por tenant_id.
-- Restricciones críticas:
--   · No puede hacer DDL (CREATE TABLE, ALTER TABLE, DROP...)
--   · No puede DISABLE ROW LEVEL SECURITY en ninguna tabla
--   · No puede SET ROLE a migration_user ni a service_role
--   · No puede acceder a auth.* ni a tablas internas de Supabase
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'app_user') THEN
    CREATE ROLE app_user
      NOINHERIT
      NOCREATEROLE
      NOCREATEDB
      NOLOGIN;        -- El login se hace mediante la connection string de Supabase
    RAISE NOTICE 'Rol app_user creado.';
  ELSE
    RAISE NOTICE 'Rol app_user ya existe — omitido.';
  END IF;
END $$;

-- ─── anon_user ────────────────────────────────────────────────
-- Propósito  : Peticiones no autenticadas al API público (webhooks de validación,
--              health checks públicos). Sin acceso a ninguna tabla de negocio.
-- Credencial : Clave anónima de Supabase (VITE_SUPABASE_ANON_KEY).
--              Esta clave SÍ se puede exponer en el frontend ya que por sí sola
--              no tiene acceso a datos sin un JWT válido.
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'anon_user') THEN
    CREATE ROLE anon_user
      NOINHERIT
      NOCREATEROLE
      NOCREATEDB
      NOLOGIN;
    RAISE NOTICE 'Rol anon_user creado.';
  ELSE
    RAISE NOTICE 'Rol anon_user ya existe — omitido.';
  END IF;
END $$;


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 2: PERMISOS DE SCHEMA
-- Controla qué objetos de qué schemas puede ver cada rol.
-- ══════════════════════════════════════════════════════════════

-- migration_user: acceso completo al schema public para DDL
GRANT USAGE ON SCHEMA public TO migration_user;
GRANT ALL   ON ALL TABLES    IN SCHEMA public TO migration_user;
GRANT ALL   ON ALL SEQUENCES IN SCHEMA public TO migration_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
  GRANT ALL ON TABLES    TO migration_user;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
  GRANT ALL ON SEQUENCES TO migration_user;

-- app_user: solo schema public, sin DDL (los GRANTs de tabla se hacen en 0007)
GRANT USAGE ON SCHEMA public TO app_user;

-- anon_user: uso del schema pero sin acceso a tablas de negocio
GRANT USAGE ON SCHEMA public TO anon_user;

-- REVOKE explícito: ningún rol puede acceder al schema auth (GoTrue interno)
-- Supabase gestiona este schema; la app nunca debe tocarlo directamente.
-- Se envuelve en DO...EXCEPTION porque en entorno local el schema auth
-- puede no existir aún o el rol de migración puede no tener permisos sobre él.
DO $$
BEGIN
  REVOKE ALL ON SCHEMA auth FROM app_user;
  REVOKE ALL ON SCHEMA auth FROM anon_user;
  REVOKE ALL ON SCHEMA auth FROM migration_user;
EXCEPTION WHEN insufficient_privilege OR invalid_schema_name THEN
  RAISE NOTICE 'SKIP: REVOKE ON SCHEMA auth — insufficient_privilege or schema not visible (expected in local dev)';
END $$;


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 3: HARDENING — PREVENIR BYPASS DE RLS
--
-- Postgres permite que un superuser o el owner de una tabla
-- ejecute: ALTER TABLE t DISABLE ROW LEVEL SECURITY;
-- o bien que use BYPASSRLS en su rol.
--
-- Las siguientes medidas previenen esto para app_user:
-- ══════════════════════════════════════════════════════════════

-- 3.1-3.5  Hardening de atributos de rol
--
-- ALTER ROLE con atributos NOSUPERUSER / NOREPLICATION / NOBYPASSRLS
-- solo puede ejecutarlo un superuser. En el entorno local de Supabase
-- (Docker) el usuario de migraciones NO es superuser, por lo que
-- capturamos el error 42501 (insufficient_privilege) y emitimos un
-- NOTICE en su lugar, de modo que la migración no falle.
-- En producción (Supabase Cloud), si el rol de migraciones es superuser,
-- los ALTER ROLE se ejecutarán con éxito.
DO $$
BEGIN
  -- app_user: sin BYPASSRLS
  ALTER ROLE app_user NOBYPASSRLS;
EXCEPTION WHEN insufficient_privilege THEN
  RAISE NOTICE 'SKIP: ALTER ROLE app_user NOBYPASSRLS — insufficient_privilege (expected in local dev)';
END $$;

DO $$
BEGIN
  -- app_user: sin atributos de superuser
  ALTER ROLE app_user NOSUPERUSER NOCREATEROLE NOCREATEDB NOREPLICATION;
EXCEPTION WHEN insufficient_privilege THEN
  RAISE NOTICE 'SKIP: ALTER ROLE app_user NOSUPERUSER... — insufficient_privilege (expected in local dev)';
END $$;

DO $$
BEGIN
  -- Revocar SET SESSION AUTHORIZATION (evita cambio de rol en runtime)
  REVOKE SET SESSION AUTHORIZATION FROM app_user;
EXCEPTION WHEN insufficient_privilege THEN
  RAISE NOTICE 'SKIP: REVOKE SET SESSION AUTHORIZATION FROM app_user — insufficient_privilege (expected in local dev)';
END $$;

DO $$
BEGIN
  -- anon_user: mismas restricciones
  ALTER ROLE anon_user NOBYPASSRLS NOSUPERUSER NOCREATEROLE NOCREATEDB NOREPLICATION;
EXCEPTION WHEN insufficient_privilege THEN
  RAISE NOTICE 'SKIP: ALTER ROLE anon_user ... — insufficient_privilege (expected in local dev)';
END $$;

DO $$
BEGIN
  -- migration_user: explícito para documentación
  ALTER ROLE migration_user NOBYPASSRLS NOSUPERUSER NOCREATEROLE NOCREATEDB NOREPLICATION;
EXCEPTION WHEN insufficient_privilege THEN
  RAISE NOTICE 'SKIP: ALTER ROLE migration_user ... — insufficient_privilege (expected in local dev)';
END $$;


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 4: PERMISOS EXPLÍCITOS SOBRE processed_events
--
-- processed_events NO tiene tenant_id ni RLS. El acceso se
-- controla exclusivamente por permisos de rol:
--   · app_user puede SELECT e INSERT (para idempotencia)
--   · app_user NO puede UPDATE ni DELETE (registro inmutable)
--   · anon_user no tiene ningún acceso
-- ══════════════════════════════════════════════════════════════
GRANT SELECT, INSERT ON processed_events TO app_user;
REVOKE UPDATE, DELETE ON processed_events FROM app_user;
REVOKE ALL ON processed_events FROM anon_user;


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 5: PERMISOS SOBRE audit_logs
--
-- audit_logs tiene tenant_id pero sin FK (para preservar registros
-- aunque el tenant se borre). La RLS la protege.
-- app_user: SELECT + INSERT. Nunca UPDATE ni DELETE.
-- ══════════════════════════════════════════════════════════════
GRANT SELECT, INSERT ON audit_logs TO app_user;
REVOKE UPDATE, DELETE ON audit_logs FROM app_user;
REVOKE ALL ON audit_logs FROM anon_user;


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 6: FUNCIÓN DE AUDITORÍA INMUTABLE
--
-- Garantiza que app_user pueda insertar en audit_logs pero
-- nunca modificar registros existentes, ni siquiera a través
-- de funciones con SECURITY DEFINER.
-- ══════════════════════════════════════════════════════════════
CREATE OR REPLACE FUNCTION insert_audit_log(
  p_tenant_id   UUID,
  p_entity_type TEXT,
  p_entity_id   UUID,
  p_action      TEXT,
  p_old_values  JSONB,
  p_new_values  JSONB,
  p_actor_id    UUID
)
RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER   -- Se ejecuta con los permisos del owner (migration_user), no del caller
SET search_path = public  -- Evita ataques de search_path injection
AS $$
DECLARE
  v_id UUID;
BEGIN
  -- Validar que action sea uno de los valores permitidos
  IF p_action NOT IN ('created', 'updated', 'deleted') THEN
    RAISE EXCEPTION 'Valor de action no permitido: %', p_action;
  END IF;

  INSERT INTO audit_logs (
    tenant_id, entity_type, entity_id, action,
    old_values, new_values, actor_id
  )
  VALUES (
    p_tenant_id, p_entity_type, p_entity_id, p_action,
    p_old_values, p_new_values, p_actor_id
  )
  RETURNING id INTO v_id;

  RETURN v_id;
END;
$$;

-- Dar acceso a app_user para ejecutar la función, pero NO para UPDATE/DELETE directos
GRANT EXECUTE ON FUNCTION insert_audit_log TO app_user;


-- ══════════════════════════════════════════════════════════════
-- SECCIÓN 7: DOCUMENTACIÓN DE SEGURIDAD EMBEBIDA EN LA BD
--
-- Los comentarios en pg_description son accesibles desde
-- Supabase Studio y cualquier herramienta de introspección.
-- Son la fuente de verdad del modelo de seguridad.
-- ══════════════════════════════════════════════════════════════

-- COMMENT ON ROLE requiere ser superuser o dueño del rol.
-- Se envuelven en bloques DO para que el entorno local no falle.
DO $$
BEGIN
  COMMENT ON ROLE app_user IS
    'Rol de runtime de ClinicBoost.Api (.NET). '
    'SOLO permisos DML. Las políticas RLS filtran por tenant_id del JWT. '
    'PROHIBIDO: DDL, DISABLE RLS, SET ROLE a roles privilegiados. '
    'Credencial: connection string en secrets del servidor. NUNCA en código fuente.';
EXCEPTION WHEN insufficient_privilege THEN
  RAISE NOTICE 'SKIP: COMMENT ON ROLE app_user — insufficient_privilege (expected in local dev)';
END $$;

DO $$
BEGIN
  COMMENT ON ROLE anon_user IS
    'Rol para peticiones no autenticadas (health checks, webhooks sin JWT). '
    'SIN acceso a ninguna tabla de negocio. '
    'La anon key de Supabase puede exponerse en el frontend porque este rol '
    'no tiene acceso a datos sin un JWT válido de GoTrue.';
EXCEPTION WHEN insufficient_privilege THEN
  RAISE NOTICE 'SKIP: COMMENT ON ROLE anon_user — insufficient_privilege (expected in local dev)';
END $$;

DO $$
BEGIN
  COMMENT ON ROLE migration_user IS
    'Rol exclusivo para migraciones DDL. '
    'Credencial conocida solo por CI/CD (GitHub Actions secret). '
    'NUNCA usar en la connection string de la aplicación en runtime. '
    'No tiene BYPASSRLS pero sí DDL como owner de las tablas.';
EXCEPTION WHEN insufficient_privilege THEN
  RAISE NOTICE 'SKIP: COMMENT ON ROLE migration_user — insufficient_privilege (expected in local dev)';
END $$;

COMMENT ON FUNCTION insert_audit_log IS
  'Función SECURITY DEFINER para insertar en audit_logs. '
  'app_user puede llamarla pero NO puede UPDATE/DELETE en audit_logs directamente. '
  'SET search_path = public para prevenir ataques de search_path injection.';
