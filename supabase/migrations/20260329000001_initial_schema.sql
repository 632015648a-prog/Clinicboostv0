-- ============================================================
-- ClinicBoost — Migración inicial
-- Versión: 20260329000001
-- Autor: ClinicBoost Team
-- Nota: Todas las tablas de negocio llevan tenant_id (obligatorio)
--       RLS activa en todas las tablas — sin excepciones
--       Todo en UTC — sin conversiones en capa de datos
-- ============================================================

-- ─── Extensiones ─────────────────────────────────────────────────────────────
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- ─── Roles de base de datos ──────────────────────────────────────────────────
-- migration_user: solo para migraciones (DDL)
-- app_user: runtime de la app (DML únicamente, NO puede desactivar RLS)
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'app_user') THEN
    CREATE ROLE app_user NOINHERIT;
  END IF;
END $$;

-- ─── Función helper: updated_at automático ────────────────────────────────────
CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
  NEW.updated_at = NOW();
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- ─── TENANTS ─────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS tenants (
  id                  UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  name                TEXT        NOT NULL,
  slug                TEXT        NOT NULL UNIQUE,
  time_zone           TEXT        NOT NULL DEFAULT 'Europe/Madrid',
  whatsapp_number     TEXT        NOT NULL,
  plan                SMALLINT    NOT NULL DEFAULT 1
                        CHECK (plan IN (1, 2, 3)),  -- 1=Starter 2=Growth 3=Scale
  is_active           BOOLEAN     NOT NULL DEFAULT TRUE,
  -- RGPD
  consent_accepted_at TIMESTAMPTZ,
  consent_version     TEXT,
  created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TRIGGER trg_tenants_updated_at
  BEFORE UPDATE ON tenants
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- ─── PATIENTS ────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS patients (
  id                    UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id             UUID        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  full_name             TEXT        NOT NULL,
  phone                 TEXT        NOT NULL,                   -- E.164 normalizado
  email                 TEXT,
  status                SMALLINT    NOT NULL DEFAULT 1
                          CHECK (status IN (1, 2, 3)),         -- 1=Active 2=Inactive 3=Blocked
  -- RGPD
  rgpd_consent          BOOLEAN     NOT NULL DEFAULT FALSE,
  rgpd_consent_at       TIMESTAMPTZ,
  -- Reactivación (Flow 06)
  last_appointment_at   TIMESTAMPTZ,
  created_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE (tenant_id, phone)
);

CREATE INDEX idx_patients_tenant_id    ON patients(tenant_id);
CREATE INDEX idx_patients_status       ON patients(tenant_id, status);
CREATE INDEX idx_patients_last_appt    ON patients(tenant_id, last_appointment_at);

CREATE TRIGGER trg_patients_updated_at
  BEFORE UPDATE ON patients
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- ─── APPOINTMENTS ────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS appointments (
  id                  UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id           UUID        NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  patient_id          UUID        NOT NULL REFERENCES patients(id) ON DELETE CASCADE,
  therapist_name      TEXT        NOT NULL,
  starts_at_utc       TIMESTAMPTZ NOT NULL,   -- SIEMPRE UTC
  ends_at_utc         TIMESTAMPTZ NOT NULL,   -- SIEMPRE UTC
  status              SMALLINT    NOT NULL DEFAULT 1
                        CHECK (status IN (1,2,3,4,5)), -- 1=Scheduled 2=Confirmed 3=Cancelled 4=Completed 5=NoShow
  source              SMALLINT    NOT NULL DEFAULT 1
                        CHECK (source IN (1,2,3,4,5)), -- 1=Manual 2=WhatsApp 3=GapFill 4=Reactivation 5=Rescheduled
  -- Recuperación de ingresos
  is_recovered        BOOLEAN     NOT NULL DEFAULT FALSE,
  recovered_revenue   NUMERIC(10,2),
  -- Recordatorios (Flow 03)
  reminder_sent_at    TIMESTAMPTZ,
  no_show             BOOLEAN     NOT NULL DEFAULT FALSE,
  -- Reprogramación (Flow 07)
  rescheduled_from_id UUID        REFERENCES appointments(id),
  created_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at          TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_appointments_tenant_id   ON appointments(tenant_id);
CREATE INDEX idx_appointments_patient_id  ON appointments(patient_id);
CREATE INDEX idx_appointments_starts_at   ON appointments(tenant_id, starts_at_utc);
CREATE INDEX idx_appointments_status      ON appointments(tenant_id, status);

CREATE TRIGGER trg_appointments_updated_at
  BEFORE UPDATE ON appointments
  FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- ─── PROCESSED EVENTS (idempotencia) ─────────────────────────────────────────
CREATE TABLE IF NOT EXISTS processed_events (
  id            UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  event_type    TEXT        NOT NULL,   -- "twilio.message", "supabase.auth.signup", etc.
  event_id      TEXT        NOT NULL,   -- ID del evento según el proveedor externo
  processed_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  metadata      JSONB,
  UNIQUE (event_type, event_id)         -- Garantía de idempotencia
);

CREATE INDEX idx_processed_events_lookup ON processed_events(event_type, event_id);

-- ─── AUDIT LOG ───────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS audit_logs (
  id          UUID        PRIMARY KEY DEFAULT uuid_generate_v4(),
  tenant_id   UUID        NOT NULL,   -- Sin FK para preservar registros aunque se borre el tenant
  entity_type TEXT        NOT NULL,
  entity_id   UUID        NOT NULL,
  action      TEXT        NOT NULL CHECK (action IN ('created', 'updated', 'deleted')),
  old_values  JSONB,
  new_values  JSONB,
  actor_id    UUID,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_audit_logs_tenant_id   ON audit_logs(tenant_id);
CREATE INDEX idx_audit_logs_entity      ON audit_logs(entity_type, entity_id);
CREATE INDEX idx_audit_logs_created_at  ON audit_logs(created_at DESC);
