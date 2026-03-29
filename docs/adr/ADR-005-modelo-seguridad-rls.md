# ADR-005 — Modelo de Seguridad: RLS Multi-tenant, Roles y Contexto de Tenant

| Campo        | Valor                          |
|--------------|-------------------------------|
| **Estado**   | Aceptado                      |
| **Fecha**    | 2026-03-29                    |
| **Autores**  | ClinicBoost Team               |
| **Depende**  | ADR-001, ADR-003               |

---

## Contexto

ClinicBoost es un SaaS multi-tenant para clínicas de fisioterapia. Cada clínica
(tenant) opera en el mismo schema de Postgres. Los datos de pacientes, citas y
finanzas son extremadamente sensibles (RGPD, secreto profesional sanitario).

Se necesita garantizar que **ningún tenant pueda ver ni modificar datos de otro
tenant**, independientemente de:
- Bugs en la lógica de negocio del backend.
- Errores de configuración del ORM (EF Core).
- Peticiones maliciosas que inyecten un `tenant_id` distinto.

---

## Decisión

### 1. Row-Level Security (RLS) como capa de seguridad primaria en la BD

Toda tabla de negocio tiene `ENABLE ROW LEVEL SECURITY`. Las políticas filtran
todas las operaciones DML por `tenant_id = current_tenant_id()`.

**Esto significa que aunque el código de aplicación tenga un bug y no filtre por
tenant, Postgres rechazará el acceso a datos de otro tenant.**

### 2. Tres roles de Postgres claramente separados

| Rol              | Propósito                                  | Puede desactivar RLS | DDL | Usado desde         |
|------------------|--------------------------------------------|----------------------|-----|---------------------|
| `migration_user` | Migraciones DDL                            | No (NOBYPASSRLS)     | Sí  | CI/CD exclusivamente |
| `app_user`       | Runtime de ClinicBoost.Api                 | **No** (NOBYPASSRLS) | No  | Connection string API |
| `anon_user`      | Health checks / webhooks sin JWT           | No                   | No  | Sin acceso a tablas |
| `service_role`   | Supabase interno (bypass RLS)              | Sí                   | —   | **NUNCA el frontend** |

**Garantías explícitas sobre `app_user`:**
- `NOBYPASSRLS` — nunca puede desactivar RLS.
- `NOSUPERUSER` — no puede hacer DDL.
- `NOCREATEROLE` — no puede crear roles con más privilegios.
- Revocado `SET SESSION AUTHORIZATION` — no puede cambiar de rol en runtime.

### 3. Mecanismo dual de propagación del contexto de tenant

La función `current_tenant_id()` lee el tenant activo de dos fuentes con orden
de precedencia:

```
1. JWT claim 'tenant_id'  (Supabase Auth — peticiones del frontend o API con JWT)
2. GUC app.tenant_id      (SET LOCAL — peticiones directas de ClinicBoost.Api)
```

Cuando la API .NET usa la conexión directa con `app_user` (sin JWT), llama a
`claim_tenant_context()` al inicio de cada transacción:

```sql
SELECT claim_tenant_context('<tenant_uuid>', 'admin', '<user_uuid>');
-- Internamente ejecuta:
-- PERFORM set_config('app.tenant_id', '<uuid>', true);  -- LOCAL a la transacción
-- PERFORM set_config('app.user_role',  'admin',  true);
-- PERFORM set_config('app.user_id',    '<uuid>', true);
```

**`SET LOCAL` (tercer argumento `true`) es crítico:** el GUC se limpia al final
de la transacción, lo que evita contaminación entre conexiones en un pool
(PgBouncer, Supabase Transaction Pooler).

### 4. Función `assert_tenant_context()` como guardia defensiva

Al inicio de operaciones críticas de negocio (antes de cualquier query DML), el
código puede llamar a `assert_tenant_context()`, que lanza:

```
ERROR: SECURITY: tenant context not initialized (SEC-001)
SQLSTATE: insufficient_privilege
```

Esto añade una capa adicional de defensa si `claim_tenant_context()` no fue
llamado por error de programación.

### 5. El interceptor EF Core inyecta el contexto automáticamente

`TenantDbContextInterceptor` (en `Infrastructure/Database/`) intercepta cada
transacción EF Core y ejecuta `claim_tenant_context()` antes del primer comando,
usando el `ITenantContext` (disponible en DI, poblado por `TenantMiddleware`).

Esto significa que el código de features **no necesita gestionar el contexto de
tenant manualmente**; el interceptor lo hace siempre.

### 6. Tablas inmutables para trazabilidad

Las siguientes tablas **solo permiten INSERT**:

| Tabla               | Razón                                          |
|---------------------|------------------------------------------------|
| `patient_consents`  | Trazabilidad legal RGPD — cada cambio = nueva fila |
| `appointment_events`| Log de cambios de citas (event sourcing lite)  |
| `messages`          | Trazabilidad de comunicaciones                 |
| `revenue_events`    | Registro contable inmutable                    |
| `audit_logs`        | Auditoría de cambios críticos                  |

### 7. El frontend nunca usa credenciales privilegiadas

```
Frontend → Supabase anon key + JWT de GoTrue (rol 'authenticated')
         → Políticas RLS limitan al tenant del JWT
         → NUNCA usa service_role key ni migration_user
```

La `SUPABASE_SERVICE_ROLE_KEY` **solo vive en el servidor** (secrets de
infraestructura). Nunca se expone en variables de entorno del frontend
(`VITE_*`).

---

## Flujo completo de una petición autenticada

```
[Browser]
  │  Cookie httpOnly: sb-access-token (JWT)
  ▼
[ClinicBoost.Api — TenantMiddleware]
  │  Extrae tenant_id y user_role del JWT
  │  Almacena en ITenantContext (scoped DI)
  ▼
[EF Core — TenantDbContextInterceptor]
  │  Al inicio de la transacción:
  │  SELECT claim_tenant_context(tenant_id, user_role, user_id)
  ▼
[Postgres — app_user]
  │  current_tenant_id() lee app.tenant_id (GUC)
  │  Políticas RLS filtran todas las queries automáticamente
  ▼
[Resultado] Solo datos del tenant correcto
```

---

## Alternativas consideradas y rechazadas

### Alternativa A: Filtrar solo en la capa de aplicación
- **Rechazada**: un bug en el ORM o la lógica puede exponer datos de otro tenant.
- RLS es la red de seguridad que opera incluso con bugs de aplicación.

### Alternativa B: Schema separado por tenant
- **Rechazada**: complejidad operacional insostenible con cientos de tenants.
- Migraciones requieren ejecutarse N veces (una por schema).

### Alternativa C: Base de datos separada por tenant
- **Rechazada**: coste prohibitivo en el tier de entrada (Starter €149/mes).

### Alternativa D: Usar `session_prelude` de Supabase
- **Rechazada**: solo disponible en planes Enterprise; no portable a self-hosted.
- La solución `claim_tenant_context()` + GUCs es equivalente y portable.

---

## Consecuencias

### Positivas
- Aislamiento garantizado a nivel de BD, independiente de bugs de aplicación.
- El interceptor EF Core automatiza la inyección del contexto (sin boilerplate).
- `check_rls_coverage()` permite auditar el estado de RLS en cualquier momento.
- Tests inline en la migración 0008 validan el modelo en cada despliegue.
- Compatible con connection pooling (SET LOCAL, no SET SESSION).

### Negativas / Trade-offs
- Las políticas RLS añaden complejidad a las migraciones.
- El plan de query puede ser ligeramente menos eficiente (RLS añade predicados).
  → Mitigado con índices específicos en `tenant_id` en todas las tablas.
- Si se añade una tabla nueva sin habilitar RLS, `check_rls_coverage()` lo
  detectará en el siguiente ciclo de monitoreo.

---

## Reglas operativas (no negociables)

1. **Toda tabla de negocio** debe tener `ENABLE ROW LEVEL SECURITY`.
2. **`migration_user`** solo se usa en migraciones. Nunca en connection strings de app.
3. **`service_role` key** solo en secrets del servidor. Nunca en el frontend.
4. **`app_user`** es el único rol usado en `SUPABASE_DB_URL` de ClinicBoost.Api.
5. **`claim_tenant_context()`** debe llamarse antes de cualquier DML en transacciones `app_user`.
6. **Los GUCs** deben establecerse con `SET LOCAL` (tercer argumento `true`), nunca `SET SESSION`.
7. Al añadir una nueva tabla de negocio: (a) habilitar RLS, (b) crear políticas, (c) grant a `app_user`, (d) actualizar `check_rls_coverage()` si es necesario.

---

## Referencias

- [Postgres Row Security Policies](https://www.postgresql.org/docs/current/ddl-rowsecurity.html)
- [Supabase RLS Guide](https://supabase.com/docs/guides/auth/row-level-security)
- [set_config() — SET LOCAL vs SET SESSION](https://www.postgresql.org/docs/current/functions-admin.html#FUNCTIONS-ADMIN-SET)
- Migración `20260329000006_roles_and_hardening.sql` — definición de roles
- Migración `20260329000007_rls_consolidated.sql` — funciones helper y políticas
- Migración `20260329000008_security_functions_and_tests.sql` — `claim_tenant_context()` y tests
