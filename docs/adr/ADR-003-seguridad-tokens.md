# ADR-003: Gestión segura de tokens de autenticación

**Estado:** Aceptado  
**Fecha:** 2026-03-29

## Decisión

- **Prohibido** guardar tokens en `localStorage` o `sessionStorage`
- Los tokens de sesión se almacenan en **cookies `httpOnly` + `Secure` + `SameSite=Strict`**
- El backend .NET gestiona la emisión y renovación de cookies
- **Refresh token rotation** activado en Supabase Auth
- El token anterior se invalida en cada refresh

## Implementación

```csharp
// En el endpoint de login del backend:
Response.Cookies.Append("sb-access-token", token, new CookieOptions
{
    HttpOnly  = true,
    Secure    = true,
    SameSite  = SameSiteMode.Strict,
    Expires   = DateTimeOffset.UtcNow.AddHours(1)
});
```

## Frontend

El cliente Axios tiene `withCredentials: true` para enviar la cookie automáticamente.  
El cliente Supabase tiene `persistSession: false`.

## Rationale

XSS puede robar tokens de localStorage. Las cookies httpOnly son inmunes a XSS.
