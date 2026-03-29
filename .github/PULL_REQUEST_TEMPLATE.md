## Descripción

<!-- ¿Qué hace este PR? ¿A qué Flow o feature corresponde? -->

## Tipo de cambio

- [ ] 🐛 Bug fix
- [ ] ✨ Nueva feature (Flow 0X)
- [ ] 🔒 Mejora de seguridad
- [ ] ♻️ Refactor
- [ ] 📝 Documentación / ADR
- [ ] 🗄️ Migración de base de datos

## Checklist obligatorio

### General
- [ ] El código compila sin errores (`dotnet build` / `npm run build`)
- [ ] Los tests pasan (`dotnet test` / `npm test`)
- [ ] No hay secrets ni credenciales en el código

### Backend (.NET)
- [ ] Toda tabla nueva incluye `tenant_id`
- [ ] Si hay acceso a BD, el `TenantId` viene de `HttpContext.Items["TenantId"]`
- [ ] No se usa `AddHours()` manual para timezones
- [ ] Integraciones externas tienen timeout + retry + circuit breaker
- [ ] Si hay webhook nuevo: es idempotente y valida firma criptográfica
- [ ] No se usa MediatR ni AutoMapper

### Base de datos
- [ ] Migración nueva en `supabase/migrations/`
- [ ] RLS activada en tablas nuevas
- [ ] Políticas RLS probadas con usuarios de distintos tenants

### Frontend
- [ ] No se guardan tokens en localStorage
- [ ] Llamadas a API usan `withCredentials: true`

## Tests añadidos / modificados

<!-- Describe qué tests cubre este PR -->

## Screenshots (si aplica)

<!-- Para cambios de UI -->
