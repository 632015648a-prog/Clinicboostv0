using ClinicBoost.Api.Features.Flow01;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Domain.Conversations;
using Microsoft.EntityFrameworkCore;

namespace ClinicBoost.Api.Features.Conversations;

// ════════════════════════════════════════════════════════════════════════════
// ConversationInboxService.cs
//
// Implementación de IConversationInboxService sobre EF Core + Postgres.
//
// DISEÑO
// ──────
//  · Todas las queries filtran por tenantId (defense in depth + RLS Postgres).
//  · Read-only: AsNoTracking() en todas las queries GET.
//  · PATCH usa tracking normal para que EF detecte el cambio y lo persista.
//  · El historial de mensajes se limita a los últimos 100 para el detalle.
//  · "RequiresHuman" ≡ status == "waiting_human".
//  · La búsqueda libre (Search) compara contra nombre y teléfono del paciente.
//
// ESTADOS VÁLIDOS PARA PATCH
// ──────────────────────────
//   waiting_human  → pausa la IA; un humano debe tomar el control.
//   open           → reactiva la automatización (IA retoma el flujo).
//   resolved       → cierra la conversación; no se reabrirá salvo nuevo inbound.
// ════════════════════════════════════════════════════════════════════════════

public sealed class ConversationInboxService : IConversationInboxService
{
    // Estados desde los que el operador puede enviar mensajes manuales.
    // "resolved", "expired" y "opted_out" bloquean el envío.
    private static readonly HashSet<string> SendableStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "open", "waiting_human" };

    private static readonly HashSet<string> AllowedPatchStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "waiting_human",
            "open",
            "resolved",
        };

    private readonly AppDbContext                         _db;
    private readonly IOutboundMessageSender               _sender;
    private readonly ILogger<ConversationInboxService>   _logger;

    public ConversationInboxService(
        AppDbContext                       db,
        IOutboundMessageSender             sender,
        ILogger<ConversationInboxService>  logger)
    {
        _db     = db;
        _sender = sender;
        _logger = logger;
    }

    // ── GET /api/conversations ────────────────────────────────────────────

    public async Task<InboxListResponse> GetInboxAsync(
        Guid              tenantId,
        InboxQueryParams  q,
        CancellationToken ct = default)
    {
        // ── Conteo de waiting_human (siempre, independiente de filtros) ────
        var waitingHumanCount = await _db.Conversations
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Status == "waiting_human")
            .CountAsync(ct);

        // ── Query base ────────────────────────────────────────────────────
        var query = _db.Conversations
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId);

        // Filtro por estado
        if (!string.IsNullOrWhiteSpace(q.Status) &&
            !q.Status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var statusLower = q.Status.ToLowerInvariant();
            query = query.Where(c => c.Status == statusLower);
        }

        // Filtro por flujo
        if (!string.IsNullOrWhiteSpace(q.FlowId))
        {
            var flowLower = q.FlowId.ToLowerInvariant();
            query = query.Where(c => c.FlowId == flowLower);
        }

        // Filtro requires_human
        if (q.RequiresHuman == true)
        {
            query = query.Where(c => c.Status == "waiting_human");
        }

        // Total antes de paginar
        var totalCount = await query.CountAsync(ct);

        // Paginación
        var page     = Math.Max(1, q.Page);
        var pageSize = Math.Clamp(q.PageSize, 1, 100);
        var skip     = (page - 1) * pageSize;

        // Materializar conversaciones ordenadas por última actividad
        var conversations = await query
            .OrderByDescending(c => c.LastMessageAt ?? c.UpdatedAt)
            .Skip(skip)
            .Take(pageSize)
            .Select(c => new
            {
                c.Id,
                c.PatientId,
                c.FlowId,
                c.Status,
                c.MessageCount,
                c.LastMessageAt,
                c.UpdatedAt,
                c.CreatedAt,
            })
            .ToListAsync(ct);

        if (conversations.Count == 0)
        {
            return new InboxListResponse
            {
                Items              = [],
                TotalCount         = totalCount,
                Page               = page,
                PageSize           = pageSize,
                HasMore            = false,
                WaitingHumanCount  = waitingHumanCount,
            };
        }

        // Cargar pacientes en un solo query
        var patientIds = conversations.Select(c => c.PatientId).Distinct().ToList();
        var patients = await _db.Patients
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && patientIds.Contains(p.Id))
            .Select(p => new { p.Id, p.FullName, p.Phone })
            .ToDictionaryAsync(p => p.Id, ct);

        // Cargar último mensaje de cada conversación (para preview y delivery status)
        var convIds = conversations.Select(c => c.Id).ToList();
        var lastMessages = await _db.Messages
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && convIds.Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId)
            .Select(g => g.OrderByDescending(m => m.CreatedAt).First())
            .ToListAsync(ct);

        var lastMsgByConv = lastMessages.ToDictionary(m => m.ConversationId);

        // Búsqueda libre (aplicada en memoria tras cargar pacientes)
        var search = q.Search?.Trim().ToLowerInvariant();

        var items = new List<InboxConversationItem>(conversations.Count);
        foreach (var c in conversations)
        {
            patients.TryGetValue(c.PatientId, out var patient);

            // Filtro de búsqueda libre
            if (!string.IsNullOrEmpty(search))
            {
                var name  = patient?.FullName?.ToLowerInvariant() ?? "";
                var phone = patient?.Phone?.ToLowerInvariant() ?? "";
                if (!name.Contains(search) && !phone.Contains(search))
                    continue;
            }

            lastMsgByConv.TryGetValue(c.Id, out var lastMsg);

            items.Add(new InboxConversationItem
            {
                ConversationId     = c.Id,
                PatientName        = patient?.FullName ?? "(desconocido)",
                PatientPhone       = patient?.Phone   ?? "",
                FlowId             = c.FlowId         ?? "",
                Status             = c.Status,
                RequiresHuman      = c.Status == "waiting_human",
                LastMessagePreview = lastMsg?.Body is { Length: > 0 }
                                        ? lastMsg.Body[..Math.Min(80, lastMsg.Body.Length)]
                                        : null,
                LastDirection      = lastMsg?.Direction  ?? "",
                LastDeliveryStatus = lastMsg?.Status     ?? "",
                MessageCount       = c.MessageCount,
                LastMessageAt      = c.LastMessageAt ?? c.UpdatedAt,
                UpdatedAt          = c.UpdatedAt,
                CreatedAt          = c.CreatedAt,
            });
        }

        return new InboxListResponse
        {
            Items              = items,
            TotalCount         = totalCount,
            Page               = page,
            PageSize           = pageSize,
            HasMore            = (skip + pageSize) < totalCount,
            WaitingHumanCount  = waitingHumanCount,
        };
    }

    // ── GET /api/conversations/{id}/messages ──────────────────────────────

    public async Task<ConversationDetailResponse?> GetConversationDetailAsync(
        Guid              tenantId,
        Guid              conversationId,
        CancellationToken ct = default)
    {
        // Cargar conversación y verificar propiedad del tenant
        var conv = await _db.Conversations
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == conversationId)
            .Select(c => new
            {
                c.Id,
                c.PatientId,
                c.FlowId,
                c.Status,
                c.CreatedAt,
            })
            .FirstOrDefaultAsync(ct);

        if (conv is null)
        {
            _logger.LogWarning(
                "[ConversationInboxService] ConversationId={Id} no encontrado para TenantId={TenantId}",
                conversationId, tenantId);
            return null;
        }

        // Cargar paciente
        var patient = await _db.Patients
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Id == conv.PatientId)
            .Select(p => new { p.FullName, p.Phone })
            .FirstOrDefaultAsync(ct);

        // Cargar mensajes (últimos 100, cronológico)
        var messages = await _db.Messages
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(100)
            .Select(m => new InboxMessageItem
            {
                MessageId      = m.Id,
                Direction      = m.Direction,
                Body           = m.Body,
                Status         = m.Status,
                GeneratedByAi  = m.GeneratedByAi,
                AiModel        = m.AiModel,
                TemplateId     = m.TemplateId,
                MediaUrl       = m.MediaUrl,
                MediaType      = m.MediaType,
                SentAt         = m.SentAt,
                DeliveredAt    = m.DeliveredAt,
                ReadAt         = m.ReadAt,
                CreatedAt      = m.CreatedAt,
            })
            .ToListAsync(ct);

        // Volvemos a ordenar cronológicamente (ascendente) para el UI
        messages.Reverse();

        return new ConversationDetailResponse
        {
            ConversationId = conv.Id,
            PatientName    = patient?.FullName ?? "(desconocido)",
            PatientPhone   = patient?.Phone ?? "",
            FlowId         = conv.FlowId    ?? "",
            Status         = conv.Status,
            RequiresHuman  = conv.Status == "waiting_human",
            CreatedAt      = conv.CreatedAt,
            Messages       = messages,
        };
    }

    // ── PATCH /api/conversations/{id}/status ─────────────────────────────

    public async Task<PatchConversationStatusResponse?> PatchStatusAsync(
        Guid                           tenantId,
        Guid                           conversationId,
        PatchConversationStatusRequest request,
        CancellationToken              ct = default)
    {
        var newStatus = request.Status.ToLowerInvariant();

        if (!AllowedPatchStatuses.Contains(newStatus))
        {
            _logger.LogWarning(
                "[ConversationInboxService] Estado no permitido '{Status}' para ConvId={Id}",
                request.Status, conversationId);
            return null;  // 422 en endpoint
        }

        // Cargar con tracking para persistir cambio
        var conv = await _db.Conversations
            .Where(c => c.TenantId == tenantId && c.Id == conversationId)
            .FirstOrDefaultAsync(ct);

        if (conv is null)
        {
            _logger.LogWarning(
                "[ConversationInboxService] PATCH: ConversationId={Id} no encontrado para TenantId={TenantId}",
                conversationId, tenantId);
            return null;  // 404 en endpoint
        }

        var previousStatus = conv.Status;
        conv.Status    = newStatus;
        conv.UpdatedAt = DateTimeOffset.UtcNow;

        // Si se resuelve, marcamos ResolvedAt si existe la propiedad
        if (newStatus == "resolved" && conv is { } resolving)
        {
            resolving.ResolvedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[ConversationInboxService] Estado actualizado: ConvId={Id} " +
            "{Prev} → {New} TenantId={TenantId}",
            conversationId, previousStatus, newStatus, tenantId);

        return new PatchConversationStatusResponse
        {
            ConversationId = conv.Id,
            NewStatus      = newStatus,
            PreviousStatus = previousStatus,
            UpdatedAt      = conv.UpdatedAt,
        };
    }

    // ── GET /api/conversations/pending-handoff ────────────────────────────

    public async Task<PendingHandoffResponse> GetPendingHandoffAsync(
        Guid              tenantId,
        CancellationToken ct = default)
    {
        var query = _db.Conversations
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Status == "waiting_human");

        var count = await query.CountAsync(ct);

        if (count == 0)
            return new PendingHandoffResponse { Count = 0, Items = [] };

        // Cargar las 10 más antiguas (más urgentes primero)
        var conversations = await query
            .OrderBy(c => c.UpdatedAt)
            .Take(10)
            .Select(c => new { c.Id, c.PatientId, c.FlowId, c.UpdatedAt })
            .ToListAsync(ct);

        // Cargar pacientes en un solo query
        var patientIds = conversations.Select(c => c.PatientId).Distinct().ToList();
        var patients = await _db.Patients
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && patientIds.Contains(p.Id))
            .Select(p => new { p.Id, p.FullName, p.Phone })
            .ToDictionaryAsync(p => p.Id, ct);

        var now   = DateTimeOffset.UtcNow;
        var items = conversations.Select(c =>
        {
            patients.TryGetValue(c.PatientId, out var patient);
            return new PendingHandoffItem
            {
                ConversationId = c.Id,
                PatientName    = patient?.FullName ?? "(desconocido)",
                PatientPhone   = patient?.Phone    ?? "",
                FlowId         = c.FlowId          ?? "",
                WaitingMinutes = (int)(now - c.UpdatedAt).TotalMinutes,
                UpdatedAt      = c.UpdatedAt,
            };
        }).ToList();

        return new PendingHandoffResponse { Count = count, Items = items };
    }

    // ── POST /api/conversations/{id}/messages ─────────────────────────────

    public async Task<SendManualMessageResponse?> SendManualMessageAsync(
        Guid                      tenantId,
        Guid                      conversationId,
        SendManualMessageRequest  request,
        CancellationToken         ct = default)
    {
        // ── 1. Cargar conversación con tracking (para UpdatedAt) ──────────
        var conv = await _db.Conversations
            .Where(c => c.TenantId == tenantId && c.Id == conversationId)
            .FirstOrDefaultAsync(ct);

        if (conv is null)
        {
            _logger.LogWarning(
                "[InboxService.Send] ConversationId={Id} no encontrada para TenantId={TenantId}",
                conversationId, tenantId);
            return null;   // → 404 en endpoint
        }

        // ── 2. Validar que el estado permite envío manual ─────────────────
        if (!SendableStatuses.Contains(conv.Status))
        {
            _logger.LogWarning(
                "[InboxService.Send] Estado '{Status}' no permite envío manual. ConvId={Id}",
                conv.Status, conversationId);
            throw new ManualSendException(422,
                $"No se puede enviar un mensaje en una conversación con estado '{conv.Status}'. " +
                "Debe estar en 'open' o 'waiting_human'.");
        }

        // ── 3. Cargar tenant para obtener número de WhatsApp ──────────────
        var tenant = await _db.Tenants.FindAsync([tenantId], ct);

        if (tenant is null || string.IsNullOrWhiteSpace(tenant.WhatsAppNumber))
        {
            _logger.LogWarning(
                "[InboxService.Send] TenantId={TenantId} no tiene WhatsAppNumber configurado.",
                tenantId);
            throw new ManualSendException(422,
                "El número de WhatsApp de la clínica no está configurado. " +
                "Configura el número antes de enviar mensajes desde la Inbox.");
        }

        // ── 4. Obtener teléfono del paciente ──────────────────────────────
        var patient = await _db.Patients
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Id == conv.PatientId)
            .Select(p => new { p.Id, p.Phone })
            .FirstOrDefaultAsync(ct);

        if (patient is null || string.IsNullOrWhiteSpace(patient.Phone))
        {
            _logger.LogWarning(
                "[InboxService.Send] Paciente sin teléfono. PatientId={PatientId} ConvId={Id}",
                conv.PatientId, conversationId);
            throw new ManualSendException(422,
                "No se encontró el teléfono del paciente asociado a esta conversación.");
        }

        // ── 5. Enviar vía IOutboundMessageSender ──────────────────────────
        // El sender crea el Message en BD (status=pending) y actualiza a sent/failed.
        // Trazabilidad: GeneratedByAi=false, AiModel="operator" (campo reutilizado).
        var sendReq = new OutboundMessageRequest
        {
            ToPhone        = $"whatsapp:{patient.Phone}",
            FromPhone      = $"whatsapp:{tenant.WhatsAppNumber}",
            Channel        = "whatsapp",
            Body           = request.Body,
            FlowId         = conv.FlowId ?? "flow_00",
            TenantId       = tenantId,
            PatientId      = conv.PatientId,
            ConversationId = conversationId,
            CorrelationId  = Guid.NewGuid().ToString(),
        };

        var result = await _sender.SendAsync(sendReq, ct);

        // ── 6. Marcar AiModel="operator" para trazabilidad ────────────────
        // El sender ya creó el Message; lo actualizamos con tracking si está pendiente.
        if (result.MessageId != Guid.Empty)
        {
            var msg = await _db.Messages.FindAsync([result.MessageId], ct);
            if (msg is not null && msg.AiModel is null)
            {
                msg.AiModel = "operator";
                await _db.SaveChangesAsync(ct);
            }
        }

        // ── 7. Si Twilio falló, propagar error visible al operador ────────
        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "[InboxService.Send] Twilio falló. Code={Code} Error={Error} ConvId={Id}",
                result.ErrorCode, result.ErrorMessage, conversationId);
            throw new ManualSendException(502,
                $"Error al enviar el mensaje: {result.ErrorMessage ?? result.ErrorCode ?? "error desconocido"}. " +
                "El mensaje quedó registrado como fallido.");
        }

        _logger.LogInformation(
            "[InboxService.Send] Mensaje manual enviado. " +
            "MsgId={MsgId} TwilioSid={Sid} ConvId={ConvId} TenantId={TenantId}",
            result.MessageId, result.TwilioSid, conversationId, tenantId);

        return new SendManualMessageResponse
        {
            MessageId  = result.MessageId,
            Direction  = "outbound",
            Body       = request.Body,
            Status     = result.Status,
            TwilioSid  = result.TwilioSid,
            CreatedAt  = DateTimeOffset.UtcNow,
        };
    }
}
