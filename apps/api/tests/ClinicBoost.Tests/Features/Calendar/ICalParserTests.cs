using System.Net;
using System.Text;
using ClinicBoost.Api.Features.Calendar;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace ClinicBoost.Tests.Features.Calendar;

// ════════════════════════════════════════════════════════════════════════════
// ICalParserTests
//
// Tests del HttpICalReader: parsing iCal, timeout, 304 Not Modified,
// errores HTTP, límite de eventos y peticiones condicionales.
// ════════════════════════════════════════════════════════════════════════════

public sealed class ICalParserTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpICalReader CreateReader(
        HttpMessageHandler handler,
        ICalOptions?       opts = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("ICalReader").Returns(new HttpClient(handler)
        {
            BaseAddress = null,
        });

        var options = Options.Create(opts ?? new ICalOptions
        {
            FreshnessTtl     = TimeSpan.FromMinutes(15),
            HttpTimeout      = TimeSpan.FromSeconds(5),
            MaxStaleAge      = TimeSpan.FromHours(24),
            MaxEventsPerFeed = 5_000,
        });

        return new HttpICalReader(factory, options, NullLogger<HttpICalReader>.Instance);
    }

    private static string BuildIcs(params (string uid, string dtstart, string dtend, string summary, string transp)[] events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//ClinicBoost//Test//ES");

        foreach (var (uid, dtstart, dtend, summary, transp) in events)
        {
            sb.AppendLine("BEGIN:VEVENT");
            sb.AppendLine($"UID:{uid}");
            sb.AppendLine($"DTSTART:{dtstart}");
            sb.AppendLine($"DTEND:{dtend}");
            sb.AppendLine($"SUMMARY:{summary}");
            sb.AppendLine($"TRANSP:{transp}");
            sb.AppendLine("END:VEVENT");
        }

        sb.AppendLine("END:VCALENDAR");
        return sb.ToString();
    }

    // ── Tests de parsing ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_ValidIcs_ReturnsSlotsOrderedByStart()
    {
        // Arrange
        var ics = BuildIcs(
            ("uid-2", "20260402T100000Z", "20260402T110000Z", "Sesión B", "OPAQUE"),
            ("uid-1", "20260401T090000Z", "20260401T100000Z", "Sesión A", "OPAQUE")
        );

        var handler = new FakeHttpHandler(HttpStatusCode.OK, ics);
        var reader  = CreateReader(handler);

        // Act
        var result = await reader.ReadAsync("http://test.example/cal.ics");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Slots.Should().HaveCount(2);
        result.Slots[0].Summary.Should().Be("Sesión A"); // ordenado por StartsAtUtc
        result.Slots[1].Summary.Should().Be("Sesión B");
    }

    [Fact]
    public async Task ReadAsync_OpaqueEvent_IsOpaqueTrue()
    {
        // Arrange
        var ics = BuildIcs(("uid-1", "20260401T090000Z", "20260401T100000Z", "Ocupado", "OPAQUE"));
        var reader = CreateReader(new FakeHttpHandler(HttpStatusCode.OK, ics));

        // Act
        var result = await reader.ReadAsync("http://test.example/cal.ics");

        // Assert
        result.Slots[0].IsOpaque.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_TransparentEvent_IsOpaqueFalse()
    {
        // Arrange
        var ics = BuildIcs(("uid-1", "20260401T090000Z", "20260401T100000Z", "Libre", "TRANSPARENT"));
        var reader = CreateReader(new FakeHttpHandler(HttpStatusCode.OK, ics));

        // Act
        var result = await reader.ReadAsync("http://test.example/cal.ics");

        // Assert
        result.Slots[0].IsOpaque.Should().BeFalse();
    }

    [Fact]
    public async Task ReadAsync_UtcTimestamp_ParsedCorrectly()
    {
        // Arrange
        var ics = BuildIcs(("uid-1", "20260401T090000Z", "20260401T100000Z", "Test", "OPAQUE"));
        var reader = CreateReader(new FakeHttpHandler(HttpStatusCode.OK, ics));

        // Act
        var result = await reader.ReadAsync("http://test.example/cal.ics");

        // Assert
        var slot = result.Slots[0];
        slot.StartsAtUtc.Should().Be(new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero));
        slot.EndsAtUtc  .Should().Be(new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task ReadAsync_EmptyCalendar_ReturnsEmptySlots()
    {
        // Arrange
        var ics    = "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//\nEND:VCALENDAR\n";
        var reader = CreateReader(new FakeHttpHandler(HttpStatusCode.OK, ics));

        // Act
        var result = await reader.ReadAsync("http://test.example/cal.ics");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Slots.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_MultiLineUnfolded_ParsedCorrectly()
    {
        // iCal RFC 5545: líneas que empiezan con espacio son continuación
        var ics = "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//Test//\n" +
                  "BEGIN:VEVENT\nUID:uid-fold\nDTSTART:20260401T090000Z\nDTEND:20260401T100000Z\n" +
                  "SUMMARY:Sesión con\n  continuación\nTRANSP:OPAQUE\nEND:VEVENT\nEND:VCALENDAR\n";

        var reader = CreateReader(new FakeHttpHandler(HttpStatusCode.OK, ics));

        // Act
        var result = await reader.ReadAsync("http://test.example/cal.ics");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Slots[0].Summary.Should().Contain("continuación");
    }

    // ── Tests de condiciones HTTP ─────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_HttpError500_ReturnsFailure()
    {
        // Arrange
        var reader = CreateReader(new FakeHttpHandler(HttpStatusCode.InternalServerError, ""));

        // Act
        var result = await reader.ReadAsync("http://test.example/cal.ics");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("500");
    }

    [Fact]
    public async Task ReadAsync_Http404_ReturnsFailure()
    {
        // Arrange
        var reader = CreateReader(new FakeHttpHandler(HttpStatusCode.NotFound, ""));

        // Act
        var result = await reader.ReadAsync("http://test.example/cal.ics");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("404");
    }

    [Fact]
    public async Task ReadAsync_304NotModified_ReturnsNotModified()
    {
        // Arrange
        var reader = CreateReader(new FakeHttpHandler(HttpStatusCode.NotModified, ""));

        // Act
        var result = await reader.ReadAsync(
            "http://test.example/cal.ics",
            etag: "\"abc123\"");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.IsNotModified.Should().BeTrue();
        result.Slots.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_Timeout_ReturnsFailureWithTimeoutMessage()
    {
        // Arrange: handler que tarda más que el timeout configurado (100 ms)
        var opts   = new ICalOptions { HttpTimeout = TimeSpan.FromMilliseconds(100) };
        var reader = CreateReader(new SlowHttpHandler(delay: TimeSpan.FromSeconds(10)), opts);

        // Act
        var result = await reader.ReadAsync("http://test.example/cal.ics");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ReadAsync_ContentHashSet_WhenFeedIsValid()
    {
        // Arrange
        var ics    = BuildIcs(("uid-1", "20260401T090000Z", "20260401T100000Z", "Test", "OPAQUE"));
        var reader = CreateReader(new FakeHttpHandler(HttpStatusCode.OK, ics));

        // Act
        var result = await reader.ReadAsync("http://test.example/cal.ics");

        // Assert
        result.ContentHash.Should().NotBeNullOrEmpty();
        result.ContentHash!.Length.Should().Be(64); // SHA-256 hex = 64 chars
    }

    [Fact]
    public async Task ReadAsync_MaxEventsPerFeed_LimitsResults()
    {
        // Arrange: feed con 10 eventos pero límite de 3
        var events = Enumerable.Range(1, 10)
            .Select(i => ($"uid-{i}",
                          $"202604{i:D2}T090000Z",
                          $"202604{i:D2}T100000Z",
                          $"Sesión {i}",
                          "OPAQUE"))
            .ToArray();

        // Solo los primeros 5 días válidos (01-09 son válidos en abril)
        var validEvents = Enumerable.Range(1, 9)
            .Select(i => ($"uid-{i}",
                          $"20260401T0{i}0000Z",
                          $"20260401T{i + 1:D2}0000Z",
                          $"Sesión {i}",
                          "OPAQUE"))
            .ToArray();

        var ics  = BuildIcs(validEvents);
        var opts = new ICalOptions { MaxEventsPerFeed = 3, HttpTimeout = TimeSpan.FromSeconds(5) };
        var reader = CreateReader(new FakeHttpHandler(HttpStatusCode.OK, ics), opts);

        // Act
        var result = await reader.ReadAsync("http://test.example/cal.ics");

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Slots.Count.Should().BeLessThanOrEqualTo(3);
    }

    // ── Fake handlers ─────────────────────────────────────────────────────────

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string         _body;

        public FakeHttpHandler(HttpStatusCode code, string body)
        {
            _code = code;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(_code)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/calendar"),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class SlowHttpHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;

        public SlowHttpHandler(TimeSpan delay) => _delay = delay;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(_delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("BEGIN:VCALENDAR\nEND:VCALENDAR\n"),
            };
        }
    }
}
