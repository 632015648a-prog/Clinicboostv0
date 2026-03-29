using ClinicBoost.Api.Features.Webhooks.WhatsApp.Inbound;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicBoost.Tests.Features.Webhooks.WhatsApp;

// ════════════════════════════════════════════════════════════════════════════
// ChannelWhatsAppJobQueueTests
//
// Tests de comportamiento de ChannelWhatsAppJobQueue:
//   · Enqueue / dequeue básico
//   · Orden FIFO
//   · Cierre del canal (Complete)
//   · CancellationToken en ReadAllAsync
// ════════════════════════════════════════════════════════════════════════════

public sealed class ChannelWhatsAppJobQueueTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static ChannelWhatsAppJobQueue BuildSut() =>
        new(NullLogger<ChannelWhatsAppJobQueue>.Instance);

    private static WhatsAppInboundJob BuildJob(string messageSid = "SM001") => new(
        TenantId:         TenantId,
        MessageSid:       messageSid,
        CallerPhone:      "+34612000001",
        ClinicPhone:      "+34910000001",
        Body:             "Hola",
        MediaUrl:         null,
        MediaType:        null,
        ProfileName:      "Test User",
        ReceivedAt:       DateTimeOffset.UtcNow,
        ProcessedEventId: Guid.NewGuid(),
        CorrelationId:    Guid.NewGuid().ToString());

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 1: Enqueue y dequeue
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnqueueAsync_ReturnsTrue_ForFirstJob()
    {
        var sut    = BuildSut();
        var result = await sut.EnqueueAsync(BuildJob());
        result.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAllAsync_DeliversEnqueuedJob()
    {
        var sut = BuildSut();
        var job = BuildJob("SM_read_001");

        await sut.EnqueueAsync(job);
        sut.Complete();

        var received = new List<WhatsAppInboundJob>();
        await foreach (var j in sut.ReadAllAsync())
            received.Add(j);

        received.Should().HaveCount(1);
        received[0].MessageSid.Should().Be("SM_read_001");
    }

    [Fact]
    public async Task ReadAllAsync_DeliversMultipleJobs_InFifoOrder()
    {
        var sut  = BuildSut();
        var sids = new[] { "SM001", "SM002", "SM003" };

        foreach (var sid in sids)
            await sut.EnqueueAsync(BuildJob(sid));

        sut.Complete();

        var received = new List<string>();
        await foreach (var j in sut.ReadAllAsync())
            received.Add(j.MessageSid);

        received.Should().Equal(sids, "el canal debe preservar el orden FIFO");
    }

    [Fact]
    public async Task ReadAllAsync_DeliversCapturedJob_WithAllFields()
    {
        var sut          = BuildSut();
        var processedId  = Guid.NewGuid();
        var correlId     = Guid.NewGuid().ToString();
        var job = new WhatsAppInboundJob(
            TenantId:         TenantId,
            MessageSid:       "SMfields001",
            CallerPhone:      "+34612999999",
            ClinicPhone:      "+34910888888",
            Body:             "texto del mensaje",
            MediaUrl:         "https://example.com/media.jpg",
            MediaType:        "image/jpeg",
            ProfileName:      "Ana García",
            ReceivedAt:       DateTimeOffset.UtcNow,
            ProcessedEventId: processedId,
            CorrelationId:    correlId);

        await sut.EnqueueAsync(job);
        sut.Complete();

        WhatsAppInboundJob? dequeued = null;
        await foreach (var j in sut.ReadAllAsync())
            dequeued = j;

        dequeued.Should().NotBeNull();
        dequeued!.MessageSid.Should().Be("SMfields001");
        dequeued.CallerPhone.Should().Be("+34612999999");
        dequeued.ClinicPhone.Should().Be("+34910888888");
        dequeued.Body.Should().Be("texto del mensaje");
        dequeued.MediaUrl.Should().Be("https://example.com/media.jpg");
        dequeued.MediaType.Should().Be("image/jpeg");
        dequeued.ProfileName.Should().Be("Ana García");
        dequeued.ProcessedEventId.Should().Be(processedId);
        dequeued.CorrelationId.Should().Be(correlId);
        dequeued.TenantId.Should().Be(TenantId);
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 2: Comportamiento no bloqueante
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EnqueueAsync_DoesNotBlock_ForNormalJob()
    {
        // Smoke test: verificar que EnqueueAsync no bloquea ni lanza para un
        // job normal en una cola que aún tiene capacidad.
        var sut    = BuildSut();
        var result = await sut.EnqueueAsync(BuildJob());
        result.Should().BeTrue("un job normal en cola vacía siempre entra");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GRUPO 3: Cierre de canal
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReadAllAsync_Completes_AfterComplete()
    {
        var sut = BuildSut();
        sut.Complete();

        var count = 0;
        await foreach (var _ in sut.ReadAllAsync())
            count++;

        count.Should().Be(0, "canal cerrado vacío no debe entregar ningún job");
    }

    [Fact]
    public async Task ReadAllAsync_CancellationToken_StopsIteration()
    {
        var sut = BuildSut();
        // Canal vacío: ReadAllAsync bloqueará hasta cancelar
        using var cts = new CancellationTokenSource(millisecondsDelay: 50);

        var act = async () =>
        {
            await foreach (var _ in sut.ReadAllAsync(cts.Token)) { }
        };

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancelar el token debe detener la iteración");
    }

    [Fact]
    public async Task Complete_CanBeCalledMultipleTimes_WithoutException()
    {
        var sut = BuildSut();

        var act = () =>
        {
            sut.Complete();
            sut.Complete(); // segunda llamada no debe lanzar
        };

        act.Should().NotThrow(
            "Complete() usa TryComplete() que es seguro llamar varias veces");
    }
}
