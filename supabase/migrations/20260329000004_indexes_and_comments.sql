-- ============================================================
-- ClinicBoost — Índices adicionales de rendimiento + comentarios
-- Versión : 20260329000004
-- Depende : 20260329000003
--
-- Este fichero añade:
--   · Índices de cobertura (covering indexes) para las queries
--     más frecuentes de cada flujo de negocio
--   · COMMENT ON TABLE / COLUMN para autodocumentación de la BD
-- ============================================================


-- ══════════════════════════════════════════════════════════════
-- ÍNDICES ADICIONALES DE RENDIMIENTO
-- ══════════════════════════════════════════════════════════════

-- ─── Appointments: queries de agenda (Flow 02 — gap detection) ───────────────
-- Huecos en la agenda: buscar citas completadas/canceladas en rango de tiempo
CREATE INDEX IF NOT EXISTS idx_appointments_gap_detection
  ON appointments (tenant_id, starts_at_utc, ends_at_utc)
  WHERE status IN (3, 4)     -- 3=Cancelled, 4=Completed
    AND is_recovered = FALSE;

-- Citas de hoy por terapeuta (vista de agenda diaria)
CREATE INDEX IF NOT EXISTS idx_appointments_daily_view
  ON appointments (tenant_id, therapist_name, starts_at_utc)
  WHERE status IN (1, 2);    -- 1=Scheduled, 2=Confirmed

-- ─── Patients: reactivación (Flow 06) ────────────────────────────────────────
-- Pacientes inactivos ordenados por fecha de última cita (los más dormidos primero)
CREATE INDEX IF NOT EXISTS idx_patients_reactivation
  ON patients (tenant_id, last_appointment_at ASC NULLS FIRST)
  WHERE status = 2;           -- 2=Inactive

-- ─── Conversations: procesamiento en tiempo real ─────────────────────────────
-- Conversaciones abiertas por paciente (máximo una activa por flow por paciente)
CREATE INDEX IF NOT EXISTS idx_conversations_active_per_patient
  ON conversations (tenant_id, patient_id, flow_id)
  WHERE status IN ('open', 'waiting_ai', 'waiting_human');

-- Conversaciones pendientes de procesar por el worker de IA (cola de trabajo)
CREATE INDEX IF NOT EXISTS idx_conversations_ai_queue
  ON conversations (updated_at ASC)
  WHERE status = 'waiting_ai';

-- ─── Messages: entrega y lectura (webhooks de status de Twilio) ──────────────
-- Mensajes salientes sin confirmación de entrega (para reintento)
CREATE INDEX IF NOT EXISTS idx_messages_undelivered
  ON messages (tenant_id, created_at ASC)
  WHERE direction = 'outbound'
    AND status IN ('sent')
    AND delivered_at IS NULL;

-- ─── Revenue events: facturación de success fee ──────────────────────────────
-- Eventos de ingreso en la ventana de 90 días de activación (join con tenants)
CREATE INDEX IF NOT EXISTS idx_revenue_events_period
  ON revenue_events (tenant_id, event_type, created_at)
  WHERE is_success_fee_eligible = TRUE
    AND success_fee_amount IS NULL;  -- pendientes de calcular fee

-- ─── Webhook events: worker de procesamiento ─────────────────────────────────
-- Prioridad de procesamiento: pendientes más antiguos primero, luego reintentos
CREATE INDEX IF NOT EXISTS idx_webhook_events_work_queue
  ON webhook_events (received_at ASC)
  WHERE status IN ('pending', 'processing');

-- ─── Audit logs: consulta por actor (quién hizo qué) ─────────────────────────
CREATE INDEX IF NOT EXISTS idx_audit_logs_actor
  ON audit_logs (tenant_id, actor_id, created_at DESC)
  WHERE actor_id IS NOT NULL;


-- ══════════════════════════════════════════════════════════════
-- COMENTARIOS DE TABLAS (autodocumentación en pg_description)
-- Accesibles desde Supabase Studio y herramientas de schema
-- ══════════════════════════════════════════════════════════════

-- ─── Tablas de la migración 0001 ─────────────────────────────────────────────
COMMENT ON TABLE tenants IS
  'Clínicas de fisioterapia suscritas a ClinicBoost. '
  'Raíz del modelo multi-tenant. Un tenant = una clínica.';

COMMENT ON COLUMN tenants.slug IS
  'Identificador URL-friendly único. Usado en subdominios y rutas de API.';
COMMENT ON COLUMN tenants.time_zone IS
  'Zona horaria IANA del tenant (ej: Europe/Madrid). '
  'Todas las conversiones de fecha se hacen con este valor. '
  'NUNCA usar AddHours() manual.';
COMMENT ON COLUMN tenants.plan IS
  '1=Starter (149€/mes, 1 terapeuta), 2=Growth (299€/mes, 2-4), '
  '3=Scale (499€/mes, avanzado).';
COMMENT ON COLUMN tenants.whatsapp_number IS
  'Número de WhatsApp Business asignado por Twilio (formato E.164).';

COMMENT ON TABLE patients IS
  'Pacientes de una clínica. Siempre scoped a tenant_id. '
  'El teléfono (E.164) es el identificador principal para conversaciones WhatsApp.';

COMMENT ON COLUMN patients.status IS
  '1=Active, 2=Inactive (sin cita en N días configurables en rule_configs), '
  '3=Blocked (opt-out explícito, NO contactar bajo ningún concepto).';
COMMENT ON COLUMN patients.rgpd_consent IS
  'Flag de conveniencia; la fuente de verdad está en patient_consents. '
  'Se recalcula automáticamente cuando cambia patient_consents.';

COMMENT ON TABLE appointments IS
  'Citas clínicas. El backend es el único que puede confirmar/cancelar; '
  'la IA solo propone. Todas las fechas en UTC.';

COMMENT ON COLUMN appointments.source IS
  '1=Manual, 2=WhatsApp (Flow 01), 3=GapFill (Flow 02), '
  '4=Reactivation (Flow 06), 5=Rescheduled (Flow 07).';
COMMENT ON COLUMN appointments.is_recovered IS
  'TRUE si esta cita fue generada o salvada por algún flujo de ClinicBoost.';

COMMENT ON TABLE processed_events IS
  'Tabla de idempotencia. Antes de procesar cualquier webhook o evento externo, '
  'el backend inserta aquí (event_type, event_id). Si ya existe → skip. '
  'La constraint UNIQUE garantiza que no haya race conditions.';

COMMENT ON TABLE audit_logs IS
  'Log de auditoría para cambios en entidades sensibles (pacientes, citas, '
  'configuración). Separado de los logs de aplicación (Serilog). '
  'Inmutable: sin UPDATE ni DELETE. Sin FK a tenants para preservar registros '
  'aunque el tenant sea eliminado (requisito RGPD).';

-- ─── Tablas de la migración 0003 ─────────────────────────────────────────────
COMMENT ON TABLE tenant_users IS
  'Usuarios de Supabase Auth vinculados a un tenant con un rol específico. '
  'Un mismo usuario puede pertenecer a múltiples tenants (con roles distintos). '
  'Roles: owner > admin > therapist | receptionist.';

COMMENT ON COLUMN tenant_users.auth_user_id IS
  'UUID del usuario en auth.users de Supabase GoTrue. '
  'Sin FK hard para no acoplar el esquema a tablas internas de GoTrue.';
COMMENT ON COLUMN tenant_users.role IS
  'owner: puede borrar el tenant. admin: gestión completa. '
  'therapist: solo sus citas y pacientes. receptionist: agenda sin config.';

COMMENT ON TABLE patient_consents IS
  'Registro inmutable de consentimientos RGPD por paciente y tipo de comunicación '
  '(Flow 00). Una fila = un evento de consentimiento (grant o revoke). '
  'La revocación se registra con una nueva fila, no actualizando la existente. '
  'Cumplimiento: Reglamento (UE) 2016/679 art. 7 y 17.';

COMMENT ON COLUMN patient_consents.legal_text_hash IS
  'SHA-256 del texto literal del aviso legal que el paciente vio al consentir. '
  'Permite demostrar qué texto aceptó en caso de litigio.';

COMMENT ON TABLE calendar_connections IS
  'Integraciones con ERPs y calendarios externos. '
  'Los tokens OAuth se guardan cifrados con pgcrypto; nunca en texto plano. '
  'Soporta lectura de iCal (polling) y APIs nativas (push/pull).';

COMMENT ON COLUMN calendar_connections.is_primary IS
  'El calendario primario es el que se usa como referencia para gap detection (Flow 02). '
  'Solo puede haber uno activo por tenant.';
COMMENT ON COLUMN calendar_connections.sync_status IS
  'Estado del último ciclo de sincronización. '
  'Si sync_status=error, el campo sync_error tiene el detalle.';

COMMENT ON TABLE appointment_events IS
  'Log de eventos inmutables sobre el ciclo de vida de una cita. '
  'Permite auditoría completa y reconstrucción del estado (event sourcing ligero). '
  'Nunca se actualiza ni borra. Complementa el campo status de appointments.';

COMMENT ON COLUMN appointment_events.actor_type IS
  'Quién originó el evento: system=job automático, patient=acción del paciente, '
  'therapist/admin=acción humana, ai=decisión de la IA (sin ejecutar citas directamente).';
COMMENT ON COLUMN appointment_events.flow_id IS
  'Flujo de ClinicBoost que originó el evento (flow_01, flow_03, etc.).';

COMMENT ON TABLE conversations IS
  'Sesiones de conversación entre la clínica y un paciente en un canal y flujo. '
  'La IA lee el estado y el historial para decidir la siguiente acción. '
  'REGLA: la IA nunca confirma citas — solo propone. El backend ejecuta.';

COMMENT ON COLUMN conversations.ai_context IS
  'Contexto serializado que se pasa a la IA en cada turno de conversación. '
  'No debe contener PII directa; usar referencias (patient_id) en su lugar.';
COMMENT ON COLUMN conversations.session_expires_at IS
  'Ventana de sesión de WhatsApp (24h desde el último mensaje del paciente). '
  'Fuera de esta ventana, solo se pueden enviar plantillas aprobadas.';

COMMENT ON TABLE messages IS
  'Mensajes individuales dentro de una conversación. Inmutables (trazabilidad RGPD). '
  'Los mensajes salientes generados por IA tienen generated_by_ai=TRUE.';

COMMENT ON COLUMN messages.provider_message_id IS
  'Identificador del mensaje en el proveedor externo (Twilio MessageSid, etc.). '
  'Se usa para correlacionar callbacks de estado (entregado, leído, fallido).';
COMMENT ON COLUMN messages.template_id IS
  'ID de plantilla WhatsApp aprobada por Meta. Obligatorio fuera de la ventana '
  'de sesión de 24h. Los mensajes de sesión pueden usar texto libre.';

COMMENT ON TABLE waitlist_entries IS
  'Lista de espera inteligente (Flow 02 y 03). Pacientes que quieren una cita '
  'antes de la próxima disponible. Se les oferta automáticamente cuando se detecta '
  'un hueco (cancel o gap). La prioridad es configurable por el terapeuta.';

COMMENT ON COLUMN waitlist_entries.offer_expires_at IS
  'Ventana de respuesta del paciente a la oferta de hueco. '
  'Si expira sin respuesta, el hueco se oferta al siguiente en la lista.';

COMMENT ON TABLE rule_configs IS
  'Configuración de reglas de negocio por tenant y flujo. '
  'Permite personalizar el comportamiento de la automatización sin tocar código. '
  'Una fila por (tenant_id, flow_id, rule_key). Valores siempre en texto; '
  'el backend castea según value_type.';

COMMENT ON COLUMN rule_configs.rule_key IS
  'Clave compuesta por flujo + nombre de parámetro. '
  'Ejemplos: reminder_hours_before, inactive_days_threshold, discount_max_pct.';

COMMENT ON TABLE revenue_events IS
  'Registro contable-inmutable de ingresos recuperados o atribuidos a ClinicBoost. '
  'Base del dashboard de ROI y del cálculo del success fee (15% en los primeros 90 días). '
  'Una fila = un evento monetario atómico. Nunca se actualiza ni borra.';

COMMENT ON COLUMN revenue_events.is_success_fee_eligible IS
  'TRUE si el evento cae dentro de la ventana de 90 días desde la activación '
  'del tenant. El success fee = 15% del amount si eligible.';
COMMENT ON COLUMN revenue_events.discount_pct IS
  'Porcentaje de descuento aplicado (DiscountGuard). '
  'No puede superar global.discount_max_pct definido en rule_configs.';

COMMENT ON TABLE automation_runs IS
  'Registro de cada ejecución de un flujo automático (job o evento). '
  'Observabilidad de negocio complementaria a los logs de Serilog. '
  'Permite detectar duplicados, fallos silenciosos y tiempos de proceso.';

COMMENT ON COLUMN automation_runs.status IS
  'running=en ejecución, completed=OK, failed=error tras reintentos, '
  'skipped=duplicado detectado (idempotencia).';
COMMENT ON COLUMN automation_runs.duration_ms IS
  'Duración calculada automáticamente como columna generada (STORED). '
  'No requiere actualización manual.';

COMMENT ON TABLE webhook_events IS
  'Cola de entrada de webhooks externos ya validados criptográficamente. '
  'Si la validación de firma falla, el middleware HTTP rechaza el request '
  'y NO inserta en esta tabla. El worker procesa en orden FIFO con reintentos.';

COMMENT ON COLUMN webhook_events.idempotency_key IS
  'Hash de (source + provider event_id). Se cruza con processed_events '
  'para garantizar que el mismo webhook no se procese dos veces.';
COMMENT ON COLUMN webhook_events.tenant_id IS
  'Puede ser NULL si aún no se ha resuelto el tenant (ej: webhook de signup).';
