-- =============================================================================
-- Migration: 20260331000090_restrict_variant_funnel_view
--
-- N-P1-02: La vista v_variant_conversion_funnel no tiene RLS activa y
--   devuelve datos de todos los tenants si se consulta directamente.
--   Se revocan los permisos de SELECT para los roles públicos y se documenta
--   que el código de aplicación SIEMPRE debe usar get_variant_funnel().
--
-- POLÍTICA:
--   · anon       → sin SELECT (sin autenticación, sin datos)
--   · authenticated → sin SELECT (usar get_variant_funnel con tenant_id)
--   · service_role  → mantiene acceso (migraciones, jobs internos)
--
-- La función get_variant_funnel() ya aplica SECURITY DEFINER + validación
-- de current_setting('app.tenant_id'), por lo que es el punto de entrada
-- seguro para cualquier consulta de funnel desde código de aplicación.
-- =============================================================================

-- Revocar SELECT en la vista a roles no privilegiados
REVOKE SELECT ON v_variant_conversion_funnel FROM anon;
REVOKE SELECT ON v_variant_conversion_funnel FROM authenticated;

-- service_role mantiene acceso para tareas administrativas internas
-- (ya lo tiene por defecto en Supabase; se documenta explícitamente)
GRANT SELECT ON v_variant_conversion_funnel TO service_role;

-- Añadir comentario explícito de política de acceso
COMMENT ON VIEW v_variant_conversion_funnel IS
    'Vista de funnel de variantes A/B. ACCESO RESTRINGIDO: solo service_role. '
    'El código de aplicación DEBE usar get_variant_funnel(p_tenant_id, p_from, p_to) '
    'para garantizar aislamiento multi-tenant mediante RLS. '
    'La consulta directa a esta vista desde código externo está prohibida (N-P1-02).';
