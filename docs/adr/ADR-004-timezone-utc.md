# ADR-004: Gestión de fechas y zonas horarias

**Estado:** Aceptado  
**Fecha:** 2026-03-29

## Decisión

1. **Toda fecha se almacena en UTC** en la base de datos (columnas `TIMESTAMPTZ`)
2. **Prohibido** usar `AddHours()` o `AddMinutes()` manual para conversiones de timezone
3. La zona horaria del tenant se guarda en `tenants.time_zone` (ej: `"Europe/Madrid"`)
4. Las conversiones se hacen en la capa de aplicación con `TimeZoneInfo.ConvertTime()` + `TimeZoneInfo.FindSystemTimeZoneById()`

## Implementación en .NET

```csharp
// ✅ CORRECTO
var tzInfo   = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZone);
var localDt  = TimeZoneInfo.ConvertTime(appointment.StartsAtUtc, tzInfo);

// ❌ PROHIBIDO
var localDt = appointment.StartsAtUtc.AddHours(2);
```

## En el frontend

- Recibir siempre UTC del API
- Convertir a local solo para mostrar, usando la timezone del tenant
- Enviar siempre UTC al API

## Rationale

España tiene cambio de hora (CET/CEST). `AddHours(1)` rompería en verano.  
UTC como fuente de verdad evita ambigüedades y errores de DST.
