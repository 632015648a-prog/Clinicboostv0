# Flows — estado canónico

## Regla
Este documento manda sobre la narrativa comercial y sobre lo que la IA puede asumir como existente.

| Flow | Nombre corto | Objetivo | Estado actual | Notas |
|---|---|---|---|---|
| 00 | Inbound / consentimiento / base operativa | gestionar entrada y base conversacional | Implementado | utilizable |
| 01 | Missed call recovery | recuperar oportunidad desde llamada perdida | Implementado | caso principal actual |
| 02 | Gap filling / huecos | rellenar huecos en agenda | No implementado | no vender como existente |
| 03 | Reminder / no-show prevention | reducir no-shows con recordatorios | Implementado | `Flow03Orchestrator` + `AppointmentReminderWorker` + DI + TC-07 (6 tests). Polling configurable, doble idempotencia, RGPD, cooldown, templates Twilio localizados. Pendiente: validación en staging con citas reales. |
| 04 | Out-of-hours / captura | capturar fuera de horario | No implementado | no contar como entregado |
| 05 | NPS / referrals | pedir feedback o referidos | No implementado | backlog |
| 06 | Reactivación pacientes | reactivar pacientes inactivos | No implementado | backlog |
| 07 | Reprogramación conversacional | mover citas de forma guiada | No implementado | backlog |

## Regla comercial
Mientras no cambie este archivo, solo se debe comunicar como realmente operativo:
- Flow 00
- Flow 01
- Flow 03 (recordatorios — implementado, pendiente validación staging)
- dashboard MVP
- inbox operacional (con envío manual)
