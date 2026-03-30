using ClinicBoost.Api.Features.Agent;
using ClinicBoost.Api.Features.Calendar;
using ClinicBoost.Api.Features.Flow01;
using Microsoft.EntityFrameworkCore;
using ClinicBoost.Domain.Tenants;
using ClinicBoost.Domain.Appointments;
using ClinicBoost.Domain.Patients;
using ClinicBoost.Domain.Common;
using ClinicBoost.Domain.Conversations;
using ClinicBoost.Domain.Automation;
using ClinicBoost.Domain.Revenue;
using ClinicBoost.Domain.Webhooks;
using ClinicBoost.Domain.Security;

namespace ClinicBoost.Api.Infrastructure.Database;

/// <summary>
/// DbContext principal de ClinicBoost.
///
/// Principios de diseño:
///   · NO usa repositorios genéricos (decisión ADR-002).
///   · Cada feature accede directamente al contexto o a métodos de consulta propios.
///   · RLS activa en Postgres; el app_user NO puede desactivarla.
///   · Todas las entidades de negocio tienen tenant_id.
///   · Naming convention: snake_case en BD (EFCore.NamingConventions).
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // ─── Infraestructura del tenant ───────────────────────────────────────────
    public DbSet<Tenant>              Tenants             => Set<Tenant>();
    public DbSet<TenantUser>          TenantUsers         => Set<TenantUser>();
    public DbSet<CalendarConnection>  CalendarConnections => Set<CalendarConnection>();

    // ─── Pacientes ────────────────────────────────────────────────────────────
    public DbSet<Patient>             Patients            => Set<Patient>();
    public DbSet<PatientConsent>      PatientConsents     => Set<PatientConsent>();

    // ─── Agenda ───────────────────────────────────────────────────────────────
    public DbSet<Appointment>         Appointments        => Set<Appointment>();
    public DbSet<AppointmentEvent>    AppointmentEvents   => Set<AppointmentEvent>();
    public DbSet<WaitlistEntry>       WaitlistEntries     => Set<WaitlistEntry>();

    // ─── Conversaciones ───────────────────────────────────────────────────────
    public DbSet<Conversation>        Conversations         => Set<Conversation>();
    public DbSet<Message>             Messages              => Set<Message>();
    public DbSet<MessageDeliveryEvent> MessageDeliveryEvents => Set<MessageDeliveryEvent>();

    // ─── Automatización ───────────────────────────────────────────────────────
    public DbSet<RuleConfig>          RuleConfigs         => Set<RuleConfig>();
    public DbSet<AutomationRun>       AutomationRuns      => Set<AutomationRun>();

    // ─── Revenue ──────────────────────────────────────────────────────────────
    public DbSet<RevenueEvent>        RevenueEvents       => Set<RevenueEvent>();

    // ─── Webhooks e idempotencia ──────────────────────────────────────────────
    public DbSet<WebhookEvent>        WebhookEvents       => Set<WebhookEvent>();
    public DbSet<ProcessedEvent>      ProcessedEvents     => Set<ProcessedEvent>();

    // ─── Agente conversacional ───────────────────────────────────────────────
    public DbSet<AgentTurn>           AgentTurns          => Set<AgentTurn>();

    // ─── Métricas de flujos ───────────────────────────────────────────────
    public DbSet<FlowMetricsEvent>    FlowMetricsEvents   => Set<FlowMetricsEvent>();

    // ─── Caché de calendarios iCal ────────────────────────────────────────────
    public DbSet<CalendarCache>       CalendarCaches      => Set<CalendarCache>();

    // ─── Auditoría ────────────────────────────────────────────────────────────
    public DbSet<AuditLog>            AuditLogs           => Set<AuditLog>();

    // ─── Seguridad: tokens y sesiones ────────────────────────────────────────
    public DbSet<RefreshToken>        RefreshTokens       => Set<RefreshToken>();
    public DbSet<SessionRevocation>   SessionRevocations  => Set<SessionRevocation>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Todas las configuraciones de entidad se aplican automáticamente
        // desde las clases IEntityTypeConfiguration<T> en este assembly
        model.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Entidades sin IEntityTypeConfiguration explícita reciben
        // configuración mínima aquí:
        ConfigureImmutableEntities(model);

        base.OnModelCreating(model);
    }

    /// <summary>
    /// Configura entidades inmutables: desactiva los setters de updated_at
    /// y previene UpdateRange / ExecuteUpdate sobre ellas a nivel EF.
    /// </summary>
    private static void ConfigureImmutableEntities(ModelBuilder model)
    {
        // ProcessedEvent — tabla de idempotencia, sin RLS, sin tenant_id obligatorio
        // INSERT only: no se actualiza ni elimina nunca.
        model.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EventType).HasColumnName("event_type").IsRequired()
             .HasMaxLength(100);
            e.Property(x => x.EventId).HasColumnName("event_id").IsRequired()
             .HasMaxLength(255);
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PayloadHash).HasColumnName("payload_hash")
             .HasMaxLength(64);
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            e.Property(x => x.Metadata).HasColumnName("metadata")
             .HasColumnType("text");

            // Índice de rendimiento para consultas de deduplicación.
            // La constraint UNIQUE real con NULLS NOT DISTINCT está en Postgres
            // (migración 0009). No marcamos IsUnique() aquí para evitar que
            // EF InMemory (usado en tests unitarios) bloquee inserciones
            // multi-tenant con tenant_id nullable.
            e.HasIndex(x => new { x.EventType, x.EventId, x.TenantId })
             .HasDatabaseName("uq_processed_events_type_id_tenant");
        });


        // PatientConsents — inmutable
        model.Entity<PatientConsent>(e =>
        {
            e.ToTable("patient_consents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.PatientId).HasColumnName("patient_id").IsRequired();
            e.Property(x => x.ConsentType).HasColumnName("consent_type").IsRequired();
            e.Property(x => x.Action).HasColumnName("action").IsRequired();
            e.Property(x => x.ConsentVersion).HasColumnName("consent_version").IsRequired();
            e.Property(x => x.Channel).HasColumnName("channel").IsRequired();
            e.Property(x => x.IpAddress).HasColumnName("ip_address");
            e.Property(x => x.UserAgent).HasColumnName("user_agent");
            e.Property(x => x.LegalTextHash).HasColumnName("legal_text_hash");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        // AppointmentEvent — inmutable
        model.Entity<AppointmentEvent>(e =>
        {
            e.ToTable("appointment_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.AppointmentId).HasColumnName("appointment_id").IsRequired();
            e.Property(x => x.EventType).HasColumnName("event_type").IsRequired();
            e.Property(x => x.ActorType).HasColumnName("actor_type").IsRequired();
            e.Property(x => x.ActorId).HasColumnName("actor_id");
            e.Property(x => x.Payload).HasColumnName("payload")
             .HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.FlowId).HasColumnName("flow_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        // Message — inmutable
        model.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.ConversationId).HasColumnName("conversation_id").IsRequired();
            e.Property(x => x.Direction).HasColumnName("direction").IsRequired();
            e.Property(x => x.Channel).HasColumnName("channel").IsRequired();
            e.Property(x => x.ProviderMessageId).HasColumnName("provider_message_id");
            e.Property(x => x.Body).HasColumnName("body");
            e.Property(x => x.TemplateId).HasColumnName("template_id");
            e.Property(x => x.TemplateVars).HasColumnName("template_vars")
             .HasColumnType("jsonb");
            e.Property(x => x.MediaUrl).HasColumnName("media_url");
            e.Property(x => x.MediaType).HasColumnName("media_type");
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.GeneratedByAi).HasColumnName("generated_by_ai");
            e.Property(x => x.AiModel).HasColumnName("ai_model");
            e.Property(x => x.AiPromptTokens).HasColumnName("ai_prompt_tokens");
            e.Property(x => x.AiCompletionTokens).HasColumnName("ai_completion_tokens");
            e.Property(x => x.ErrorCode).HasColumnName("error_code");
            e.Property(x => x.ErrorMessage).HasColumnName("error_message");
            e.Property(x => x.SentAt).HasColumnName("sent_at");
            e.Property(x => x.DeliveredAt).HasColumnName("delivered_at");
            e.Property(x => x.ReadAt).HasColumnName("read_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        // RevenueEvent — inmutable
        model.Entity<RevenueEvent>(e =>
        {
            e.ToTable("revenue_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.AppointmentId).HasColumnName("appointment_id");
            e.Property(x => x.PatientId).HasColumnName("patient_id");
            e.Property(x => x.EventType).HasColumnName("event_type").IsRequired();
            e.Property(x => x.FlowId).HasColumnName("flow_id").IsRequired();
            e.Property(x => x.Amount).HasColumnName("amount")
             .HasColumnType("numeric(10,2)");
            e.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3);
            e.Property(x => x.OriginalAmount).HasColumnName("original_amount")
             .HasColumnType("numeric(10,2)");
            e.Property(x => x.DiscountPct).HasColumnName("discount_pct")
             .HasColumnType("numeric(5,2)");
            e.Property(x => x.IsSuccessFeeEligible).HasColumnName("is_success_fee_eligible");
            e.Property(x => x.SuccessFeeAmount).HasColumnName("success_fee_amount")
             .HasColumnType("numeric(10,2)");
            e.Property(x => x.AttributionData).HasColumnName("attribution_data")
             .HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        // AutomationRun — updated_at calculado; duration_ms es columna generada
        model.Entity<AutomationRun>(e =>
        {
            e.ToTable("automation_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.FlowId).HasColumnName("flow_id").IsRequired();
            e.Property(x => x.TriggerType).HasColumnName("trigger_type").IsRequired();
            e.Property(x => x.TriggerRef).HasColumnName("trigger_ref");
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.ItemsProcessed).HasColumnName("items_processed");
            e.Property(x => x.ItemsSucceeded).HasColumnName("items_succeeded");
            e.Property(x => x.ItemsFailed).HasColumnName("items_failed");
            e.Property(x => x.ErrorMessage).HasColumnName("error_message");
            e.Property(x => x.ErrorDetail).HasColumnName("error_detail")
             .HasColumnType("jsonb");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            // duration_ms es columna generada en Postgres; se ignora en EF para evitar conflictos
            e.Ignore(x => x.DurationMs);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        // MessageDeliveryEvent — inmutable, INSERT-only, agrupable
        model.Entity<MessageDeliveryEvent>(e =>
        {
            e.ToTable("message_delivery_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.MessageId).HasColumnName("message_id");
            e.Property(x => x.ConversationId).HasColumnName("conversation_id");
            e.Property(x => x.ProviderMessageId).HasColumnName("provider_message_id")
             .IsRequired().HasMaxLength(64);
            e.Property(x => x.Status).HasColumnName("status").IsRequired()
             .HasMaxLength(32);
            e.Property(x => x.FlowId).HasColumnName("flow_id").HasMaxLength(32);
            e.Property(x => x.TemplateId).HasColumnName("template_id").HasMaxLength(128);
            e.Property(x => x.MessageVariant).HasColumnName("message_variant")
             .HasMaxLength(16);
            e.Property(x => x.Channel).HasColumnName("channel").IsRequired()
             .HasMaxLength(32);
            e.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(16);
            e.Property(x => x.ErrorMessage).HasColumnName("error_message");
            e.Property(x => x.ProviderTimestamp).HasColumnName("provider_timestamp");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");

            // Índice principal para queries de deduplicación
            // (provider_message_id + status = clave lógica de unicidad por transición)
            e.HasIndex(x => new { x.TenantId, x.ProviderMessageId, x.Status })
             .HasDatabaseName("ix_mde_tenant_sid_status");
        });

        // AgentTurn — INSERT-only, observabilidad del agente conversacional
        model.Entity<AgentTurn>(e =>
        {
            e.ToTable("agent_turns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.ConversationId).HasColumnName("conversation_id").IsRequired();
            e.Property(x => x.MessageId).HasColumnName("message_id");
            e.Property(x => x.IntentName).HasColumnName("intent_name").IsRequired().HasMaxLength(64);
            e.Property(x => x.IntentConfidence).HasColumnName("intent_confidence");
            e.Property(x => x.ActionName).HasColumnName("action_name").IsRequired().HasMaxLength(64);
            e.Property(x => x.ResponseText).HasColumnName("response_text");
            e.Property(x => x.EscalationReason).HasColumnName("escalation_reason");
            e.Property(x => x.WasBlocked).HasColumnName("was_blocked");
            e.Property(x => x.BlockReason).HasColumnName("block_reason");
            e.Property(x => x.ModelUsed).HasColumnName("model_used").IsRequired().HasMaxLength(64);
            e.Property(x => x.PromptTokens).HasColumnName("prompt_tokens");
            e.Property(x => x.CompletionTokens).HasColumnName("completion_tokens");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id").IsRequired();
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.HasIndex(x => new { x.TenantId, x.ConversationId, x.OccurredAt })
             .HasDatabaseName("ix_agent_turns_conv");
        });

        // FlowMetricsEvent — métricas KPI por flujo (INSERT-only)
        model.Entity<FlowMetricsEvent>(e =>
        {
            e.ToTable("flow_metrics_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.PatientId).HasColumnName("patient_id");
            e.Property(x => x.AppointmentId).HasColumnName("appointment_id");
            e.Property(x => x.FlowId).HasColumnName("flow_id").IsRequired().HasMaxLength(32);
            e.Property(x => x.MetricType).HasColumnName("metric_type").IsRequired().HasMaxLength(64);
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
            e.Property(x => x.RecoveredRevenue).HasColumnName("recovered_revenue")
             .HasColumnType("numeric(10,2)");
            e.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3);
            e.Property(x => x.TwilioMessageSid).HasColumnName("twilio_message_sid").HasMaxLength(64);
            e.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(32);
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id").IsRequired().HasMaxLength(128);
            e.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");

            // Índice principal para queries de KPI por tenant+flow+tipo+fecha
            e.HasIndex(x => new { x.TenantId, x.FlowId, x.MetricType, x.OccurredAt })
             .HasDatabaseName("ix_flow_metrics_tenant_flow_type_date");

            // Índice para correlación end-to-end
            e.HasIndex(x => x.CorrelationId)
             .HasDatabaseName("ix_flow_metrics_correlation");
        });

        // CalendarCache — caché persistida de feeds iCal
        model.Entity<CalendarCache>(e =>
        {
            e.ToTable("calendar_cache");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.ConnectionId).HasColumnName("connection_id").IsRequired();
            e.Property(x => x.SlotsJson).HasColumnName("slots_json")
             .HasColumnType("jsonb").IsRequired();
            e.Property(x => x.FetchedAtUtc).HasColumnName("fetched_at_utc");
            e.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
            e.Property(x => x.ETag).HasColumnName("etag").HasMaxLength(255);
            e.Property(x => x.LastModifiedUtc).HasColumnName("last_modified_utc");
            e.Property(x => x.ContentHash).HasColumnName("content_hash").HasMaxLength(64);
            e.Property(x => x.LastErrorMessage).HasColumnName("last_error_message");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            // Unicidad: una entrada por (tenant, connection)
            e.HasIndex(x => new { x.TenantId, x.ConnectionId })
             .IsUnique()
             .HasDatabaseName("uq_calendar_cache_tenant_connection");

            // Índice para expiración pasiva (job de limpieza)
            e.HasIndex(x => x.ExpiresAtUtc)
             .HasDatabaseName("ix_calendar_cache_expires_at");
        });

        // WebhookEvent — tenant_id nullable
        model.Entity<WebhookEvent>(e =>
        {
            e.ToTable("webhook_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Source).HasColumnName("source").IsRequired();
            e.Property(x => x.EventType).HasColumnName("event_type").IsRequired();
            e.Property(x => x.Payload).HasColumnName("payload")
             .HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Headers).HasColumnName("headers").HasColumnType("jsonb");
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.AttemptCount).HasColumnName("attempt_count");
            e.Property(x => x.MaxAttempts).HasColumnName("max_attempts");
            e.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.ReceivedAt).HasColumnName("received_at");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        });

        // ── Security: RefreshToken ────────────────────────────────────────────
        model.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            e.Property(x => x.TokenHash).HasColumnName("token_hash").IsRequired().HasMaxLength(64);
            e.Property(x => x.FamilyId).HasColumnName("family_id").IsRequired();
            e.Property(x => x.IssuedAt).HasColumnName("issued_at");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.UsedAt).HasColumnName("used_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.RevokedReason).HasColumnName("revoked_reason").HasMaxLength(64);
            e.Property(x => x.IsRevoked).HasColumnName("is_revoked");
            e.Property(x => x.IsCompromised).HasColumnName("is_compromised");
            e.Property(x => x.ReplacedByTokenId).HasColumnName("replaced_by_token_id").HasMaxLength(36);
            e.Property(x => x.IpAddress).HasColumnName("ip_address").HasMaxLength(45);
            e.Property(x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(512);

            e.HasIndex(x => x.TokenHash).IsUnique()
             .HasDatabaseName("uq_refresh_tokens_hash");
            e.HasIndex(x => x.FamilyId)
             .HasDatabaseName("ix_refresh_tokens_family");
            e.HasIndex(x => new { x.UserId, x.TenantId, x.IsRevoked })
             .HasDatabaseName("ix_refresh_tokens_user_tenant_active");
        });

        // ── Security: SessionRevocation (JWT JTI blacklist) ───────────────────
        model.Entity<SessionRevocation>(e =>
        {
            e.ToTable("session_revocations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            e.Property(x => x.Jti).HasColumnName("jti").IsRequired().HasMaxLength(128);
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.JwtExpiresAt).HasColumnName("jwt_expires_at");
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(128);

            e.HasIndex(x => x.Jti).IsUnique()
             .HasDatabaseName("uq_session_revocations_jti");
            e.HasIndex(x => x.JwtExpiresAt)
             .HasDatabaseName("ix_session_revocations_expires_at");
        });
    }
}
