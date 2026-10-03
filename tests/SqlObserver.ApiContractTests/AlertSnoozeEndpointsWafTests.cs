using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;

namespace SqlObserver.ApiContractTests;

public sealed class AlertSnoozeEndpointsWafTests(M8AlertApiFactory factory) : IClassFixture<M8AlertApiFactory>
{
    private static readonly string[] ReceiptFields = ["appliedAtUtc", "auditId", "endsAtUtc", "operationId", "recordedAtUtc", "revision"];
    private static string Path(string action = "snooze") => $"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/{M8AlertApiFactory.AlertId:D}/{action}";
    private HttpClient Client(string identity)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(M8AlertTestAuthenticationHandler.IdentityHeader, identity);
        return client;
    }
    private static Dictionary<string, object?> Body() => new()
    {
        ["operationId"] = "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
        ["expectedEpisodeId"] = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
        ["expectedRevision"] = 7,
        ["endsAtUtc"] = "2026-08-25T13:00:00.123456+00:00",
        ["reason"] = "Synthetic incident triage",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandsDeriveDigestAndReturnImmutableReceiptWithoutClaimingCurrentSuppression(bool clear)
    {
        factory.Administration.Mode = FakeAlertAdministrationMode.Success;
        using HttpClient client = Client("admin");
        var body = Body(); if (clear) body.Remove("endsAtUtc");
        using HttpResponseMessage response = await client.PostAsJsonAsync(Path(clear ? "unsnooze" : "snooze"), body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.IsType<SqlObserver.Application.Ports.AlertSnoozeWriteRequest>(factory.Administration.LastSnoozeWriteRequest);
        Assert.Equal(clear ? AdministrativeAuditAction.UnsnoozeAlert : AdministrativeAuditAction.SnoozeAlert, request.Audit.Action);
        Assert.Equal(M8AlertApiFactory.TargetId, request.Audit.TargetId.Value);
        Assert.Equal(7, request.ExpectedRevision);
        Assert.Equal(AlertSnoozeContract.ComputeRequestDigest(request.Audit.TargetId, request.AlertId,
            request.ExpectedEpisodeId, request.ExpectedRevision, request.EndsAtUtc, request.Reason), request.RequestDigest);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ReceiptFields,
            json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(Guid.Parse((string)body["operationId"]!), json.RootElement.GetProperty("operationId").GetGuid());
        Assert.Equal(8, json.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(clear, json.RootElement.GetProperty("endsAtUtc").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task ReadbackUsesNativeClockEpisodeAndStateWithoutChangingInboxState()
    {
        factory.Query.SnoozeMissing = false;
        using HttpClient client = Client("viewer");
        using HttpResponseMessage response = await client.GetAsync(Path());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(M8AlertApiFactory.TargetId, json.RootElement.GetProperty("targetId").GetGuid());
        Assert.Equal(M8AlertApiFactory.AlertId, json.RootElement.GetProperty("alertId").GetGuid());
        Assert.Equal("firing", json.RootElement.GetProperty("state").GetString());
        Assert.True(json.RootElement.GetProperty("snoozed").GetBoolean());
        Assert.True(json.RootElement.GetProperty("deliverySuppressed").GetBoolean());
        Assert.Equal(7, json.RootElement.GetProperty("revision").GetInt64());
        Assert.NotEqual(default, json.RootElement.GetProperty("serverNowUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task MissingReadbackIs404AndAnonymousReadsAre401()
    {
        using HttpClient anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path())).StatusCode);
        factory.Query.SnoozeMissing = true;
        try { using HttpClient viewer = Client("viewer"); Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Path())).StatusCode); }
        finally { factory.Query.SnoozeMissing = false; }
    }

    [Theory]
    [InlineData("snooze")]
    [InlineData("unsnooze")]
    public async Task ReaderCannotMutateAndAnonymousCannotDispatch(string action)
    {
        var body = Body(); if (action == "unsnooze") body.Remove("endsAtUtc");
        int calls = factory.Administration.SnoozeCalls;
        using HttpClient viewer = Client("viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Path(action), body)).StatusCode);
        using HttpClient anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Path(action), body)).StatusCode);
        Assert.Equal(calls, factory.Administration.SnoozeCalls);
    }

    [Theory]
    [InlineData("operationId", "bad")]
    [InlineData("expectedEpisodeId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("expectedRevision", 0)]
    [InlineData("reason", " ")]
    [InlineData("reason", "unsafe\nreason")]
    [InlineData("endsAtUtc", "2026-08-25T13:00:00+01:00")]
    [InlineData("endsAtUtc", "2026-08-25T13:00:00.1234561Z")]
    [InlineData("endsAtUtc", "2026-02-30T13:00:00Z")]
    [InlineData("requestDigest", "caller-authored")]
    public async Task InvalidPayloadCannotReachMutation(string field, object value)
    {
        int calls = factory.Administration.SnoozeCalls;
        var body = Body(); body[field] = value;
        using HttpClient client = Client("admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path(), body)).StatusCode);
        Assert.Equal(calls, factory.Administration.SnoozeCalls);
    }

    [Fact]
    public async Task SnoozeRequiresEndAndUnsnoozeRejectsAnEnd()
    {
        using HttpClient client = Client("admin");
        var body = Body(); Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path("unsnooze"), body)).StatusCode);
        body.Remove("endsAtUtc"); Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path(), body)).StatusCode);
    }

    [Theory]
    [InlineData(1, HttpStatusCode.Conflict)]
    [InlineData(2, HttpStatusCode.ServiceUnavailable)]
    public async Task RepositoryOutcomesRetainTheirStatus(int mode, HttpStatusCode expected)
    {
        factory.Administration.Mode = (FakeAlertAdministrationMode)mode;
        try { using HttpClient client = Client("admin"); Assert.Equal(expected, (await client.PostAsJsonAsync(Path(), Body())).StatusCode); }
        finally { factory.Administration.Mode = FakeAlertAdministrationMode.Success; }
    }
}
