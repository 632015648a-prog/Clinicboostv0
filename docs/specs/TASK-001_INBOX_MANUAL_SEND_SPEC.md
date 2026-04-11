# TASK-001 — Envío manual desde Inbox

## 1. Estado
Draft

## 2. Contexto
ClinicBoost ya tiene Inbox y detalle de conversación, pero todavía no permite cerrar bien una conversación escalada a humano desde la propia interfaz.
Eso reduce mucho la credibilidad del piloto.

## 3. Objetivo
Permitir que el operador envíe un mensaje manual desde Inbox y que ese mensaje quede reflejado correctamente en la conversación.

## 4. Alcance
- campo de texto
- acción de enviar
- validación básica
- persistencia del mensaje
- asociación al tenant y a la conversación correctos
- visibilidad en historial

## 5. Fuera de alcance
- adjuntos
- plantillas avanzadas
- programación de mensajes
- sugerencias IA
- tiempo real sofisticado

## 6. Estado actual
Hay Inbox operativa parcial, pero falta la acción mínima que permite trabajar una conversación escalada.

## 7. Propuesta funcional
Desde el detalle de conversación, el usuario podrá redactar un mensaje y enviarlo.
El mensaje deberá aparecer en el historial y quedar trazado como intervención manual.

## 8. Impacto técnico esperado
Afectará a endpoint de conversaciones/mensajes, servicio de envío, modelo de historial y UI del detalle.

## 9. Dependencias
- autenticación operativa
- tenant resuelto
- pipeline de envío funcional
- conversación accesible desde Inbox

## 10. Riesgos
- mezclar mensajes manuales y automáticos sin trazabilidad clara
- enviar en conversación incorrecta
- relajar aislamiento tenant

## 11. Criterios de aceptación
1. se puede escribir un mensaje manual desde Inbox
2. se puede enviar
3. aparece en el historial correcto
4. respeta tenant
5. muestra error si falla
6. no reactiva IA automáticamente si la conversación está en waiting_human

## 12. Casos de prueba
- envío correcto
- validación de mensaje vacío
- error del proveedor
- conversación waiting_human
- acceso cross-tenant denegado

## 13. Preguntas abiertas
- ¿guardar autor humano concreto o solo “manual”?
- ¿permitir envío en conversaciones cerradas?
- ¿necesita evento para analytics?

## 14. Decisión
Pendiente de aprobación antes de programar.
