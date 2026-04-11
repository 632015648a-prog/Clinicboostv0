# Prompt para explicarle a la IA qué es este sistema

A partir de ahora vamos a trabajar con un sistema de **Context Engineering + Spec-Driven Development** dentro del repositorio de ClinicBoost.

Quiero que entiendas lo siguiente antes de hacer nada:

1. El contexto oficial del proyecto ya no vive en el chat, sino en archivos Markdown del repo.
2. Tu primera obligación en cada sesión es leer el paquete mínimo de contexto:
   - `00_COMO_TRABAJAREMOS_A_PARTIR_DE_AHORA.md`
   - `PROJECT_OVERVIEW.md`
   - `CURRENT_STATUS.md`
   - `AUDIT_LATEST.md`
   - `PILOT_DEFINITION.md`
   - `OUT_OF_SCOPE.md`
   - `WORK_QUEUE.md`
   - y el spec concreto de la tarea si existe
3. No debes asumir que una funcionalidad existe solo porque aparezca en un roadmap o en una conversación antigua.
4. El estado real del proyecto lo marca `CURRENT_STATUS.md` y el estado oficial de flows lo marca `FLOWS.md`.
5. No debes escribir código nuevo sin que exista un spec aprobado.
6. Si la tarea no tiene spec, primero debes proponerlo.
7. Debes trabajar pensando en el estado actual real de ClinicBoost:
   - demo local sí,
   - staging interno casi,
   - piloto asistido todavía pendiente de cierre,
   - producción no lista,
   - Flow 00 y Flow 01 operativos,
   - Flows 02-07 no deben asumirse como implementados.
8. La prioridad actual no es abrir scope, sino cerrar el piloto asistido con orden.
9. Si detectas contradicción entre documentos largos antiguos y el estado actual, debes priorizar el estado actual documentado.
10. Al terminar cada sesión debes proponer qué documentos hay que actualizar.

Tu forma de trabajar a partir de ahora será esta:
- primero leer,
- luego resumir lo entendido,
- luego proponer o revisar spec,
- después esperar aprobación,
- y solo entonces implementar.

Antes de seguir, devuélveme:
1. un resumen corto de cómo entiendes este sistema,
2. qué documentos vas a tomar como verdad operativa,
3. qué entiendes que está realmente implementado hoy,
4. y cuál crees que debería ser la prioridad actual del proyecto.
