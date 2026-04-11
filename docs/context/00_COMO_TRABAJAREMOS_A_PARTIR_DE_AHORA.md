# Cómo trabajaremos a partir de ahora

## Idea principal
A partir de ahora no vamos a depender de que la IA “recuerde” el proyecto por conversaciones anteriores.
Vamos a trabajar con **contexto vivo dentro del repositorio**.

Eso significa que:

1. La IA leerá primero unos archivos fijos del proyecto.
2. Después decidirá con nosotros qué tarea toca.
3. Antes de programar, se escribirá un **spec corto**.
4. Solo cuando el spec esté aprobado se tocará código.
5. Al terminar, la IA actualizará el estado del proyecto.

## Regla importante
**No se empieza código nuevo sin spec aprobado.**

## Qué gana el proyecto con esto
- Menos caos entre sesiones.
- Menos repeticiones.
- Menos decisiones contradictorias.
- Menos scope creep.
- Más claridad sobre lo que de verdad está hecho y lo que no.
- Mejor uso de Claude/Genspark/Cursor.

## Cómo será una sesión normal

### Paso 1. La IA lee el contexto mínimo
Siempre leerá:
- `PROJECT_OVERVIEW.md`
- `CURRENT_STATUS.md`
- `AUDIT_LATEST.md`
- `PILOT_DEFINITION.md`
- `OUT_OF_SCOPE.md`
- `WORK_QUEUE.md`
- el spec de la tarea si ya existe

### Paso 2. La IA resume lo entendido
Antes de hacer nada, debe explicar:
- qué entiende del proyecto,
- en qué estado está,
- cuál es la tarea actual,
- qué entra y qué no entra.

### Paso 3. Se crea o revisa el spec
Si la tarea no tiene spec, primero se redacta.
Si ya lo tiene, se revisa.

### Paso 4. Aprobación humana
Hasta que no aprobemos el spec, no se programa.

### Paso 5. Implementación
La IA implementa solo lo descrito en el spec.
No debe “aprovechar” para rehacer otras partes.

### Paso 6. Cierre de sesión
La IA debe actualizar:
- `CURRENT_STATUS.md`
- `WORK_QUEUE.md`
- `CHANGELOG_AI.md`
- y, si procede, el spec de la tarea

## Qué vamos a priorizar ahora en ClinicBoost
El objetivo inmediato no es abrir más scope.
El objetivo es **cerrar el piloto asistido** con una base clara.

Eso significa priorizar:
- lo que bloquea el piloto,
- lo que cierra la operativa humana,
- lo que hace creíble la demo local y el staging,
- y lo que evita que el proyecto se desordene.

## Qué NO vamos a hacer por defecto
- No abrir features nuevas sin necesidad real.
- No asumir que los Flows 02-07 existen si no están implementados.
- No prometer producto completo cuando aún estamos cerrando piloto.
- No mezclar roadmap, deseos y estado real del repositorio.

## Regla de oro
**El estado oficial del proyecto vive en archivos, no en el chat.**

Además de actualizar el estado operativo, la IA debe revisar si el cambio realizado modifica el diagnóstico global del proyecto. Si cambia prioridades, riesgos, veredicto o preparación para demo/staging/piloto, también debe actualizar `AUDIT_LATEST.md`.
