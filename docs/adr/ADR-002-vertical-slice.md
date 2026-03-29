# ADR-002: Vertical Slice Architecture en el backend

**Estado:** Aceptado  
**Fecha:** 2026-03-29

## Contexto

El backend tiene 8 flujos funcionales independientes (Flow 00-07). Necesitamos una arquitectura que permita desarrollar cada flujo de forma aislada sin que los cambios en un flujo afecten a otros.

## Decisión

**Vertical Slice Architecture** en `ClinicBoost.Api`:

```
Features/
├── Appointments/
│   ├── CreateAppointment.cs     (command + handler + endpoint en el mismo archivo)
│   ├── GetAppointments.cs
│   └── CancelAppointment.cs
├── Patients/
│   ├── GetPatients.cs
│   └── ReactivatePatient.cs
├── Flow01_MissedCalls/
│   ├── HandleInboundWhatsApp.cs
│   └── ConvertCallToAppointment.cs
└── Health/
    └── HealthEndpoints.cs
```

## Reglas de implementación

- **NO MediatR**: Los handlers se llaman directamente desde el endpoint
- **NO repositorios genéricos**: Acceso directo a `AppDbContext` o queries específicas
- **NO AutoMapper**: Mapeos manuales con métodos estáticos o record constructors
- Cada slice puede tener sus propios DTOs, validators (FluentValidation) y lógica
- Los tests se escriben por slice, no por capa

## Estructura de un slice típico

```csharp
// Features/Appointments/CreateAppointment.cs
public static class CreateAppointment
{
    public record Request(Guid PatientId, DateTimeOffset StartsAtUtc, string TherapistName);
    public record Response(Guid Id, DateTimeOffset StartsAtUtc);

    public static async Task<IResult> Handle(
        Request req,
        AppDbContext db,
        HttpContext ctx,
        CancellationToken ct)
    {
        var tenantId = (Guid)ctx.Items["TenantId"]!;
        // lógica directa, sin mediator
        var appointment = new Appointment { TenantId = tenantId, ... };
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/appointments/{appointment.Id}", new Response(...));
    }
}
```
