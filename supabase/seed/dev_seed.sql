-- ============================================================
-- ClinicBoost — Seed de desarrollo local
-- Solo para entorno local/dev — NUNCA ejecutar en producción
-- ============================================================

-- Tenant de ejemplo
INSERT INTO tenants (id, name, slug, time_zone, whatsapp_number, plan, is_active,
                     consent_accepted_at, consent_version)
VALUES (
  'a1b2c3d4-0000-0000-0000-000000000001',
  'Fisioterapia Ramírez',
  'fisio-ramirez',
  'Europe/Madrid',
  '+34600000001',
  1,  -- Starter
  TRUE,
  NOW() AT TIME ZONE 'UTC',
  'v1.0'
)
ON CONFLICT (slug) DO NOTHING;

-- Pacientes de ejemplo
INSERT INTO patients (tenant_id, full_name, phone, email, status, rgpd_consent, rgpd_consent_at)
VALUES
  ('a1b2c3d4-0000-0000-0000-000000000001', 'Ana García López',     '+34611111111', 'ana@example.com',    1, TRUE, NOW()),
  ('a1b2c3d4-0000-0000-0000-000000000001', 'Carlos Martínez Ruiz', '+34622222222', 'carlos@example.com', 1, TRUE, NOW()),
  ('a1b2c3d4-0000-0000-0000-000000000001', 'María Sánchez Pérez',  '+34633333333', NULL,                 2, TRUE, NOW())  -- Inactiva
ON CONFLICT (tenant_id, phone) DO NOTHING;

-- Citas de ejemplo (en UTC)
INSERT INTO appointments (tenant_id, patient_id, therapist_name, starts_at_utc, ends_at_utc, status, source, is_recovered)
SELECT
  'a1b2c3d4-0000-0000-0000-000000000001',
  p.id,
  'Dr. Ramírez',
  NOW() AT TIME ZONE 'UTC' + INTERVAL '1 day',
  NOW() AT TIME ZONE 'UTC' + INTERVAL '1 day' + INTERVAL '1 hour',
  1,     -- Scheduled
  2,     -- WhatsApp
  TRUE
FROM patients p
WHERE p.tenant_id = 'a1b2c3d4-0000-0000-0000-000000000001'
  AND p.phone = '+34611111111'
LIMIT 1
ON CONFLICT DO NOTHING;
