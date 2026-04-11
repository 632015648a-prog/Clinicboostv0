# Current Status — ClinicBoost

## Estado general
- Demo local: ✅
- Staging interno: ⚠️
- Piloto asistido: ⚠️
- Producción: ❌

## Estado real del producto
### Ya implementado
- Arquitectura .NET 10 Minimal API + Vertical Slice
- Supabase/PostgreSQL con RLS
- JWT/Auth base
- Twilio webhook base
- WhatsApp inbound pipeline
- Dashboard MVP
- Inbox MVP parcial
- Flow 00
- Flow 01
- Health checks
- Logging estructurado
- Firma Twilio
- Idempotencia en partes críticas

### Parcial / pendiente
- envío manual desde Inbox
- persistencia de `Note`
- refinamiento de Inbox operativa
- auto-refresh UX
- revisión de `agent_turns.message_id`
- consolidación de algunos placeholders de agent tools

### No implementado aún
- Flow 02
- Flow 03
- Flow 04
- Flow 05
- Flow 06
- Flow 07

## Qué bloquea ahora mismo el piloto
Prioridad inmediata:
1. envío manual desde Inbox
2. persistencia de `Note`
3. revisar `agent_turns.message_id`
4. `.env.local` / `VITE_API_URL` correcto
5. mejorar operativa mínima de Inbox/dashboard

## Qué significa este estado para la comunicación
- Sí se puede enseñar una demo local.
- Sí se puede hablar de arquitectura seria.
- Sí se puede enseñar el caso principal.
- No se debe vender aún como producto totalmente cerrado.
- No se debe prometer Flows 02-07 como disponibles.

## Objetivo de trabajo actual
Cerrar Sprint 1 y dejar el proyecto listo para un piloto asistido controlado.
