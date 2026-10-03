using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;

namespace SqlObserver.ApiContractTests;

public sealed class AlertAssignmentEndpointsWafTests(M8AlertApiFactory factory) : IClassFixture<M8AlertApiFactory>
{
    private static readonly string[] ReceiptFields = ["appliedAtUtc", "assigneeSid", "auditId", "operationId", "recordedAtUtc", "revision"];
    private static string Path => $"/api/v1/observation-targets/{M8AlertApiFactory.TargetId:D}/alerts/{M8AlertApiFactory.AlertId:D}/assignment";
    private HttpClient Client(string identity) { HttpClient client = factory.CreateClient(); client.DefaultRequestHeaders.Add(M8AlertTestAuthenticationHandler.IdentityHeader, identity); return client; }
    private static Dictionary<string, object?> Body() => new()
    {
        ["operationId"] = "cccccccc-cccc-4ccc-8ccc-cccccccccccc", ["expectedEpisodeId"] = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
        ["expectedRevision"] = 7, ["assigneeSid"] = "S-1-5-21-2201", ["reason"] = "Synthetic triage",
    };
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssignAndExplicitNullUnassignReturnBoundImmutableReceipt(bool clear)
    {
        var body = Body(); if (clear) body["assigneeSid"] = null;
        using HttpClient client = Client("admin"); using HttpResponseMessage response = await client.PostAsJsonAsync(Path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.IsType<SqlObserver.Application.Ports.AlertAssignmentWriteRequest>(factory.Administration.LastAssignmentWriteRequest);
        Assert.Equal(clear ? AdministrativeAuditAction.UnassignAlert : AdministrativeAuditAction.AssignAlert, request.Audit.Action);
        Assert.Equal(AlertAssignmentContract.ComputeRequestDigest(request.Audit.TargetId, request.AlertId, request.ExpectedEpisodeId,
            request.ExpectedRevision, request.AssigneeSid, request.Reason), request.RequestDigest);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ReceiptFields,
            json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(8, json.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(clear, json.RootElement.GetProperty("assigneeSid").ValueKind == JsonValueKind.Null);
    }
    [Fact]
    public async Task MissingAssigneeCannotAccidentallyUnassign()
    {
        var body = Body(); body.Remove("assigneeSid"); int before = factory.Administration.AssignmentCalls;
        using HttpClient client = Client("admin"); Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, body)).StatusCode);
        Assert.Equal(before, factory.Administration.AssignmentCalls);
    }
    [Theory]
    [InlineData("admin", true)]
    [InlineData("viewer", false)]
    public async Task CurrentReadSeparatesAssignmentFromWriterOnlyApprovedRoster(string identity, bool mayAssign)
    {
        using HttpClient client = Client(identity); using HttpResponseMessage response = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("firing", json.RootElement.GetProperty("state").GetString()); Assert.Equal(7, json.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(mayAssign, json.RootElement.GetProperty("canAssign").GetBoolean());
        Assert.Equal(mayAssign ? 1 : 0, json.RootElement.GetProperty("eligibleAssignees").GetArrayLength());
        Assert.Equal("Approved responder", json.RootElement.GetProperty("assigneeName").GetString());
    }
    [Fact]
    public async Task HistoryUsesNativeOperationAnchorWithoutExposingDatabaseSequence()
    {
        using HttpClient client = Client("viewer"); using HttpResponseMessage response = await client.GetAsync(Path + "/history?limit=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        string raw = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("sequence", raw, StringComparison.OrdinalIgnoreCase);
        using JsonDocument json = JsonDocument.Parse(raw); string cursor = json.RootElement.GetProperty("nextBeforeOperationId").GetString()!;
        using HttpResponseMessage next = await client.GetAsync(Path + "/history?limit=1&beforeOperationId=" + cursor);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode); Assert.Equal(Guid.Parse(cursor), factory.Query.LastAssignmentAnchor);
        using JsonDocument nextJson = JsonDocument.Parse(await next.Content.ReadAsStringAsync()); Assert.Equal(0, nextJson.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, nextJson.RootElement.GetProperty("nextBeforeOperationId").ValueKind);
    }
    [Fact]
    public async Task MissingReadIs404AnonymousIs401AndReaderCannotWrite()
    {
        using HttpClient anonymous = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Path)).StatusCode);
        using HttpClient viewer = Client("viewer"); int before = factory.Administration.AssignmentCalls;
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Path, Body())).StatusCode); Assert.Equal(before, factory.Administration.AssignmentCalls);
        factory.Query.AssignmentMissing = true;
        try { Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(Path)).StatusCode); } finally { factory.Query.AssignmentMissing = false; }
    }
    [Theory]
    [InlineData("operationId", "bad")]
    [InlineData("expectedEpisodeId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("expectedRevision", 0)]
    [InlineData("assigneeSid", "invented-account")]
    [InlineData("reason", "bad\nreason")]
    [InlineData("reason", " ")]
    [InlineData("assigneeName", "Caller-authored label")]
    [InlineData("assigneeEligible", true)]
    [InlineData("requestDigest", "Caller-authored digest")]
    public async Task InvalidOrServerOwnedPayloadCannotReachMutation(string field, object value)
    {
        var body = Body(); body[field] = value; int before = factory.Administration.AssignmentCalls;
        using HttpClient client = Client("admin"); Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, body)).StatusCode);
        Assert.Equal(before, factory.Administration.AssignmentCalls);
    }
    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=51")]
    [InlineData("beforeOperationId=bad")]
    [InlineData("beforeOperationId=00000000-0000-0000-0000-000000000000")]
    public async Task InvalidHistoryBoundsAreRejected(string query)
    { using HttpClient client = Client("viewer"); Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Path + "/history?" + query)).StatusCode); }
    [Theory]
    [InlineData(1, HttpStatusCode.Conflict)]
    [InlineData(2, HttpStatusCode.ServiceUnavailable)]
    [InlineData(3, HttpStatusCode.UnprocessableEntity)]
    public async Task NativeRepositoryOutcomePreservesItsHttpStatus(int mode, HttpStatusCode expected)
    {
        factory.Administration.Mode = (FakeAlertAdministrationMode)mode;
        try { using HttpClient client = Client("admin"); Assert.Equal(expected, (await client.PostAsJsonAsync(Path, Body())).StatusCode); }
        finally { factory.Administration.Mode = FakeAlertAdministrationMode.Success; }
    }
}
