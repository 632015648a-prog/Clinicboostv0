-- ============================================================
-- ClinicBoost — Row Level Security (RLS)
-- Versión: 20260329000002
-- REGLA: RLS activa en todas las tablas de negocio, sin excepciones.
--        El app_user NO puede hacer ALTER TABLE ... DISABLE ROW LEVEL SECURITY.
-- ============================================================

-- ─── Activar RLS ─────────────────────────────────────────────────────────────
ALTER TABLE tenants      ENABLE ROW LEVEL SECURITY;
ALTER TABLE patients     ENABLE ROW LEVEL SECURITY;
ALTER TABLE appointments ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_logs   ENABLE ROW LEVEL SECURITY;
-- processed_events NO tiene tenant_id — acceso controlado por rol

-- ─── TENANTS ─────────────────────────────────────────────────────────────────
-- Un tenant solo puede verse a sí mismo
CREATE POLICY "tenants_select_own"
  ON tenants FOR SELECT
  USING (id = (auth.jwt() ->> 'tenant_id')::UUID);

CREATE POLICY "tenants_update_own"
  ON tenants FOR UPDATE
  USING (id = (auth.jwt() ->> 'tenant_id')::UUID)
  WITH CHECK (id = (auth.jwt() ->> 'tenant_id')::UUID);

-- Solo service_role puede crear/eliminar tenants
CREATE POLICY "tenants_insert_service"
  ON tenants FOR INSERT
  WITH CHECK (auth.role() = 'service_role');

CREATE POLICY "tenants_delete_service"
  ON tenants FOR DELETE
  USING (auth.role() = 'service_role');

-- ─── PATIENTS ────────────────────────────────────────────────────────────────
CREATE POLICY "patients_tenant_isolation"
  ON patients FOR ALL
  USING      (tenant_id = (auth.jwt() ->> 'tenant_id')::UUID)
  WITH CHECK (tenant_id = (auth.jwt() ->> 'tenant_id')::UUID);

-- ─── APPOINTMENTS ────────────────────────────────────────────────────────────
CREATE POLICY "appointments_tenant_isolation"
  ON appointments FOR ALL
  USING      (tenant_id = (auth.jwt() ->> 'tenant_id')::UUID)
  WITH CHECK (tenant_id = (auth.jwt() ->> 'tenant_id')::UUID);

-- ─── AUDIT LOGS ──────────────────────────────────────────────────────────────
-- Solo lectura para el propio tenant; inserción solo desde service_role o función
CREATE POLICY "audit_logs_select_own"
  ON audit_logs FOR SELECT
  USING (tenant_id = (auth.jwt() ->> 'tenant_id')::UUID);

CREATE POLICY "audit_logs_insert_service"
  ON audit_logs FOR INSERT
  WITH CHECK (
    auth.role() = 'service_role'
    OR tenant_id = (auth.jwt() ->> 'tenant_id')::UUID
  );

-- ─── Permisos para app_user ───────────────────────────────────────────────────
-- app_user solo puede DML — jamás DDL ni DISABLE ROW LEVEL SECURITY
GRANT SELECT, INSERT, UPDATE, DELETE ON tenants          TO app_user;
GRANT SELECT, INSERT, UPDATE, DELETE ON patients         TO app_user;
GRANT SELECT, INSERT, UPDATE, DELETE ON appointments     TO app_user;
GRANT SELECT, INSERT                 ON processed_events TO app_user;
GRANT SELECT, INSERT                 ON audit_logs       TO app_user;

-- Secuencias (si las hay tras usar SERIAL)
GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO app_user;
