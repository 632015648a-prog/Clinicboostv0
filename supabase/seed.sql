-- ============================================================
-- ClinicBoost — Seed de desarrollo local
-- Solo para entorno local/dev — NUNCA ejecutar en producción
--
-- Este archivo es el punto de entrada canónico que busca el
-- Supabase CLI por defecto (supabase/seed.sql).
-- El contenido es idéntico a supabase/seed/dev_seed.sql.
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
  NOW(),
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
-- Cita mañana (Flow03 la detectará en la ventana de 24h)
INSERT INTO appointments (tenant_id, patient_id, therapist_name, starts_at_utc, ends_at_utc, status, source, is_recovered)
SELECT
  'a1b2c3d4-0000-0000-0000-000000000001',
  p.id,
  'Dr. Ramírez',
  NOW() + INTERVAL '1 day',
  NOW() + INTERVAL '1 day' + INTERVAL '1 hour',
  1,     -- Scheduled
  2,     -- WhatsApp
  TRUE
FROM patients p
WHERE p.tenant_id = 'a1b2c3d4-0000-0000-0000-000000000001'
  AND p.phone = '+34611111111'
LIMIT 1
ON CONFLICT DO NOTHING;

-- Cita en 23h (Flow03 la detectará inmediatamente — dentro de la ventana de 24h)
INSERT INTO appointments (tenant_id, patient_id, therapist_name, starts_at_utc, ends_at_utc, status, source, is_recovered)
SELECT
  'a1b2c3d4-0000-0000-0000-000000000001',
  p.id,
  'Dra. López',
  NOW() + INTERVAL '23 hours',
  NOW() + INTERVAL '23 hours' + INTERVAL '45 minutes',
  1,     -- Scheduled
  1,     -- Manual
  FALSE
FROM patients p
WHERE p.tenant_id = 'a1b2c3d4-0000-0000-0000-000000000001'
  AND p.phone = '+34622222222'
LIMIT 1
ON CONFLICT DO NOTHING;

-- RuleConfig de ejemplo para Flow03 (configurable por tenant)
INSERT INTO rule_configs (tenant_id, flow_id, rule_key, rule_value, value_type, description, is_active)
VALUES
  ('a1b2c3d4-0000-0000-0000-000000000001', 'flow_03', 'reminder_hours_before', '24', 'integer',
   'Horas antes de la cita para enviar recordatorio', TRUE),
  ('a1b2c3d4-0000-0000-0000-000000000001', 'flow_03', 'cooldown_minutes', '720', 'integer',
   'Minutos de cooldown entre recordatorios al mismo paciente', TRUE)
ON CONFLICT DO NOTHING;
