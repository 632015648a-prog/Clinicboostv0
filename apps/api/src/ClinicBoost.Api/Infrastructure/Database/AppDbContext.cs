using Microsoft.EntityFrameworkCore;
using ClinicBoost.Domain.Tenants;
using ClinicBoost.Domain.Appointments;
using ClinicBoost.Domain.Patients;
using ClinicBoost.Domain.Common;
using ClinicBoost.Domain.Conversations;
using ClinicBoost.Domain.Automation;
using ClinicBoost.Domain.Revenue;
using ClinicBoost.Domain.Webhooks;

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
    public DbSet<Conversation>        Conversations       => Set<Conversation>();
    public DbSet<Message>             Messages            => Set<Message>();

    // ─── Automatización ───────────────────────────────────────────────────────
    public DbSet<RuleConfig>          RuleConfigs         => Set<RuleConfig>();
    public DbSet<AutomationRun>       AutomationRuns      => Set<AutomationRun>();

    // ─── Revenue ──────────────────────────────────────────────────────────────
    public DbSet<RevenueEvent>        RevenueEvents       => Set<RevenueEvent>();

    // ─── Webhooks e idempotencia ──────────────────────────────────────────────
    public DbSet<WebhookEvent>        WebhookEvents       => Set<WebhookEvent>();
    public DbSet<ProcessedEvent>      ProcessedEvents     => Set<ProcessedEvent>();

    // ─── Auditoría ────────────────────────────────────────────────────────────
    public DbSet<AuditLog>            AuditLogs           => Set<AuditLog>();

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
    }
}
