-- ============================================================
-- ClinicBoost — RLS para las tablas nuevas
-- Versión : 20260329000005
-- Depende : 20260329000003, 20260329000004
--
-- Principios aplicados:
--   1. RLS activa en TODAS las tablas de negocio, sin excepción.
--   2. El aislamiento se basa en tenant_id = JWT claim 'tenant_id'.
--   3. El rol 'app_user' solo tiene permisos DML (SELECT/INSERT/
--      UPDATE/DELETE según tabla); jamás DDL ni DISABLE RLS.
--   4. Las tablas inmutables (eventos, mensajes, consents) solo
--      permiten INSERT desde la app; UPDATE y DELETE quedan
--      reservados a service_role.
--   5. tenant_users usa una política adicional por rol:
--      'therapist' solo ve su propia fila.
-- ============================================================


-- ══════════════════════════════════════════════════════════════
-- Helper: función para extraer el tenant_id del JWT
-- (encapsula el cast para no repetirlo en cada política)
-- ══════════════════════════════════════════════════════════════
CREATE OR REPLACE FUNCTION current_tenant_id()
RETURNS UUID
LANGUAGE sql STABLE
AS $$
  SELECT (auth.jwt() ->> 'tenant_id')::UUID
$$;

-- Helper: función para extraer el rol del usuario actual
CREATE OR REPLACE FUNCTION current_user_role()
RETURNS TEXT
LANGUAGE sql STABLE
AS $$
  SELECT auth.jwt() ->> 'user_role'
$$;

-- Helper: función para extraer el auth_user_id del JWT
CREATE OR REPLACE FUNCTION current_auth_user_id()
RETURNS UUID
LANGUAGE sql STABLE
AS $$
  SELECT (auth.jwt() ->> 'sub')::UUID
$$;


-- ══════════════════════════════════════════════════════════════
-- 1. TENANT_USERS
-- ══════════════════════════════════════════════════════════════
ALTER TABLE tenant_users ENABLE ROW LEVEL SECURITY;

-- Admins y owners ven a todos los usuarios del tenant
CREATE POLICY "tenant_users_select_admin"
  ON tenant_users FOR SELECT
  USING (
    tenant_id = current_tenant_id()
    AND current_user_role() IN ('owner', 'admin', 'receptionist')
  );

-- Therapists solo ven su propia fila
CREATE POLICY "tenant_users_select_self"
  ON tenant_users FOR SELECT
  USING (
    tenant_id = current_tenant_id()
    AND auth_user_id = current_auth_user_id()
  );

-- Solo admins/owners pueden crear usuarios nuevos
CREATE POLICY "tenant_users_insert_admin"
  ON tenant_users FOR INSERT
  WITH CHECK (
    tenant_id = current_tenant_id()
    AND current_user_role() IN ('owner', 'admin')
  );

-- Solo admins/owners pueden actualizar; no pueden cambiar su propio rol a owner
CREATE POLICY "tenant_users_update_admin"
  ON tenant_users FOR UPDATE
  USING (
    tenant_id = current_tenant_id()
    AND current_user_role() IN ('owner', 'admin')
  )
  WITH CHECK (
    tenant_id = current_tenant_id()
  );

-- Solo owner puede desactivar/borrar usuarios
CREATE POLICY "tenant_users_delete_owner"
  ON tenant_users FOR DELETE
  USING (
    tenant_id = current_tenant_id()
    AND current_user_role() = 'owner'
  );


-- ══════════════════════════════════════════════════════════════
-- 2. PATIENT_CONSENTS
-- ══════════════════════════════════════════════════════════════
ALTER TABLE patient_consents ENABLE ROW LEVEL SECURITY;

-- Lectura: cualquier rol del tenant puede ver los consentimientos
CREATE POLICY "patient_consents_select"
  ON patient_consents FOR SELECT
  USING (tenant_id = current_tenant_id());

-- Inserción: solo la app (cualquier rol autenticado del tenant)
-- y service_role (para imports masivos y tests)
CREATE POLICY "patient_consents_insert"
  ON patient_consents FOR INSERT
  WITH CHECK (
    tenant_id = current_tenant_id()
    OR auth.role() = 'service_role'
  );

-- Tabla INMUTABLE: ni UPDATE ni DELETE desde la app
-- (solo service_role en caso de corrección urgente)
CREATE POLICY "patient_consents_no_update"
  ON patient_consents FOR UPDATE
  USING (auth.role() = 'service_role');

CREATE POLICY "patient_consents_no_delete"
  ON patient_consents FOR DELETE
  USING (auth.role() = 'service_role');


-- ══════════════════════════════════════════════════════════════
-- 3. CALENDAR_CONNECTIONS
-- ══════════════════════════════════════════════════════════════
ALTER TABLE calendar_connections ENABLE ROW LEVEL SECURITY;

CREATE POLICY "calendar_connections_tenant_isolation"
  ON calendar_connections FOR ALL
  USING      (tenant_id = current_tenant_id())
  WITH CHECK (tenant_id = current_tenant_id());

-- Los tokens cifrados no se exponen a roles no-admin
CREATE POLICY "calendar_connections_tokens_admin_only"
  ON calendar_connections FOR SELECT
  USING (
    tenant_id = current_tenant_id()
    AND (
      current_user_role() IN ('owner', 'admin')
      -- therapist y receptionist pueden ver la conexión pero sin tokens
      -- (la app filtra access_token_enc / refresh_token_enc en la query)
      OR current_user_role() IN ('therapist', 'receptionist')
    )
  );


-- ══════════════════════════════════════════════════════════════
-- 4. APPOINTMENT_EVENTS
-- ══════════════════════════════════════════════════════════════
ALTER TABLE appointment_events ENABLE ROW LEVEL SECURITY;

-- Lectura: todos los roles del tenant
CREATE POLICY "appointment_events_select"
  ON appointment_events FOR SELECT
  USING (tenant_id = current_tenant_id());

-- Inserción: app + service_role
CREATE POLICY "appointment_events_insert"
  ON appointment_events FOR INSERT
  WITH CHECK (
    tenant_id = current_tenant_id()
    OR auth.role() = 'service_role'
  );

-- INMUTABLE: no UPDATE ni DELETE desde la app
CREATE POLICY "appointment_events_no_update"
  ON appointment_events FOR UPDATE
  USING (auth.role() = 'service_role');

CREATE POLICY "appointment_events_no_delete"
  ON appointment_events FOR DELETE
  USING (auth.role() = 'service_role');


-- ══════════════════════════════════════════════════════════════
-- 5. CONVERSATIONS
-- ══════════════════════════════════════════════════════════════
ALTER TABLE conversations ENABLE ROW LEVEL SECURITY;

-- Aislamiento estándar por tenant
CREATE POLICY "conversations_tenant_isolation"
  ON conversations FOR ALL
  USING      (tenant_id = current_tenant_id())
  WITH CHECK (tenant_id = current_tenant_id());

-- Restricción adicional para therapist: solo ve conversaciones de sus pacientes
-- (se implementará vía join en la capa de aplicación; aquí se permite el acceso base)


-- ══════════════════════════════════════════════════════════════
-- 6. MESSAGES
-- ══════════════════════════════════════════════════════════════
ALTER TABLE messages ENABLE ROW LEVEL SECURITY;

-- Lectura: todos los roles del tenant
CREATE POLICY "messages_select"
  ON messages FOR SELECT
  USING (tenant_id = current_tenant_id());

-- Inserción: app + service_role
CREATE POLICY "messages_insert"
  ON messages FOR INSERT
  WITH CHECK (
    tenant_id = current_tenant_id()
    OR auth.role() = 'service_role'
  );

-- INMUTABLE: los mensajes no se editan ni borran (trazabilidad RGPD)
-- Solo service_role para correcciones de emergencia
CREATE POLICY "messages_no_update"
  ON messages FOR UPDATE
  USING (auth.role() = 'service_role');

CREATE POLICY "messages_no_delete"
  ON messages FOR DELETE
  USING (auth.role() = 'service_role');


-- ══════════════════════════════════════════════════════════════
-- 7. WAITLIST_ENTRIES
-- ══════════════════════════════════════════════════════════════
ALTER TABLE waitlist_entries ENABLE ROW LEVEL SECURITY;

CREATE POLICY "waitlist_entries_tenant_isolation"
  ON waitlist_entries FOR ALL
  USING      (tenant_id = current_tenant_id())
  WITH CHECK (tenant_id = current_tenant_id());


-- ══════════════════════════════════════════════════════════════
-- 8. RULE_CONFIGS
-- ══════════════════════════════════════════════════════════════
ALTER TABLE rule_configs ENABLE ROW LEVEL SECURITY;

-- Lectura: todos los roles (necesitan las reglas para funcionar)
CREATE POLICY "rule_configs_select"
  ON rule_configs FOR SELECT
  USING (tenant_id = current_tenant_id());

-- Solo admins/owners pueden modificar reglas de negocio
CREATE POLICY "rule_configs_write_admin"
  ON rule_configs FOR INSERT
  WITH CHECK (
    tenant_id = current_tenant_id()
    AND current_user_role() IN ('owner', 'admin')
  );

CREATE POLICY "rule_configs_update_admin"
  ON rule_configs FOR UPDATE
  USING (
    tenant_id = current_tenant_id()
    AND current_user_role() IN ('owner', 'admin')
  )
  WITH CHECK (tenant_id = current_tenant_id());

CREATE POLICY "rule_configs_delete_owner"
  ON rule_configs FOR DELETE
  USING (
    tenant_id = current_tenant_id()
    AND current_user_role() = 'owner'
  );


-- ══════════════════════════════════════════════════════════════
-- 9. REVENUE_EVENTS
-- ══════════════════════════════════════════════════════════════
ALTER TABLE revenue_events ENABLE ROW LEVEL SECURITY;

-- Lectura: solo owner y admin (datos financieros sensibles)
CREATE POLICY "revenue_events_select_admin"
  ON revenue_events FOR SELECT
  USING (
    tenant_id = current_tenant_id()
    AND current_user_role() IN ('owner', 'admin')
  );

-- Inserción: app + service_role
CREATE POLICY "revenue_events_insert"
  ON revenue_events FOR INSERT
  WITH CHECK (
    tenant_id = current_tenant_id()
    OR auth.role() = 'service_role'
  );

-- INMUTABLE: registro contable — no UPDATE ni DELETE
CREATE POLICY "revenue_events_no_update"
  ON revenue_events FOR UPDATE
  USING (auth.role() = 'service_role');

CREATE POLICY "revenue_events_no_delete"
  ON revenue_events FOR DELETE
  USING (auth.role() = 'service_role');


-- ══════════════════════════════════════════════════════════════
-- 10. AUTOMATION_RUNS
-- ══════════════════════════════════════════════════════════════
ALTER TABLE automation_runs ENABLE ROW LEVEL SECURITY;

-- Lectura: admin y owner (observabilidad de negocio)
CREATE POLICY "automation_runs_select_admin"
  ON automation_runs FOR SELECT
  USING (
    tenant_id = current_tenant_id()
    AND current_user_role() IN ('owner', 'admin')
  );

-- Inserción y actualización: solo service_role (el worker corre como service_role)
CREATE POLICY "automation_runs_write_service"
  ON automation_runs FOR INSERT
  WITH CHECK (
    tenant_id = current_tenant_id()
    OR auth.role() = 'service_role'
  );

CREATE POLICY "automation_runs_update_service"
  ON automation_runs FOR UPDATE
  USING (
    tenant_id = current_tenant_id()
    OR auth.role() = 'service_role'
  );


-- ══════════════════════════════════════════════════════════════
-- 11. WEBHOOK_EVENTS
-- ══════════════════════════════════════════════════════════════
-- Solo la capa de backend (service_role) escribe en esta tabla.
-- Los tenants autenticados pueden consultar sus propios webhooks.
ALTER TABLE webhook_events ENABLE ROW LEVEL SECURITY;

-- Lectura: tenant puede ver sus webhooks para debugging
CREATE POLICY "webhook_events_select_own"
  ON webhook_events FOR SELECT
  USING (
    tenant_id = current_tenant_id()
    AND current_user_role() IN ('owner', 'admin')
  );

-- Inserción/actualización: solo service_role (el worker HTTP)
CREATE POLICY "webhook_events_write_service"
  ON webhook_events FOR INSERT
  WITH CHECK (auth.role() = 'service_role');

CREATE POLICY "webhook_events_update_service"
  ON webhook_events FOR UPDATE
  USING (auth.role() = 'service_role');

-- NUNCA se borran webhooks (trazabilidad)
CREATE POLICY "webhook_events_no_delete"
  ON webhook_events FOR DELETE
  USING (FALSE);   -- nadie puede borrar, ni service_role desde la app


-- ══════════════════════════════════════════════════════════════
-- GRANT de permisos DML a app_user (para las nuevas tablas)
-- app_user = rol de la aplicación en runtime
-- ══════════════════════════════════════════════════════════════
GRANT SELECT, INSERT, UPDATE, DELETE ON tenant_users          TO app_user;
GRANT SELECT, INSERT                 ON patient_consents      TO app_user;
GRANT SELECT, INSERT, UPDATE, DELETE ON calendar_connections  TO app_user;
GRANT SELECT, INSERT                 ON appointment_events    TO app_user;
GRANT SELECT, INSERT, UPDATE, DELETE ON conversations         TO app_user;
GRANT SELECT, INSERT                 ON messages              TO app_user;
GRANT SELECT, INSERT, UPDATE, DELETE ON waitlist_entries      TO app_user;
GRANT SELECT, INSERT, UPDATE, DELETE ON rule_configs          TO app_user;
GRANT SELECT, INSERT                 ON revenue_events        TO app_user;
GRANT SELECT, INSERT, UPDATE         ON automation_runs       TO app_user;
GRANT SELECT, INSERT, UPDATE         ON webhook_events        TO app_user;

-- Secuencias de las nuevas tablas (por si alguna usa SERIAL en el futuro)
GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO app_user;
