using System.Net;
using System.Text;
using AspNetCoreVerifiableCredentials;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Aya.EmployeeId.Tests;

public sealed class IssuanceDiagnosticTests
{
    private const string Canary = "SYNTHETIC-PRIVATE-ISSUANCE-DO-NOT-LOG";
    private const string Header = "X-Aya-Issuance-Correlation-ID";
    private static HttpResponseMessage Response(int status = 201, JObject body = null) => new((HttpStatusCode)status) {
        Content = new StringContent((body ?? ServiceBody()).ToString(), Encoding.UTF8, "application/json")
    };
    private static JObject ServiceBody() => new() {
        ["requestId"] = Canary, ["expiry"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
        ["url"] = "openid://vc?request_uri=https://verifiedid.did.msidentity.com/" + Canary,
        ["qrCode"] = Canary, ["privateServiceDetail"] = Canary
    };
    internal static string AssertDiagnostic(IssuanceTests.TestApp app, HttpResponseMessage response,
        AyaIssuanceReason reason, AyaIssuanceStage stage, int? serviceStatus = null)
    {
        var events = app.Logs.Events.Where(e => e.EventId == 7200).ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal(AyaIssuanceReason.Started, events[0].Fields["ReasonCode"]);
        Assert.Null(events[0].Fields["HttpStatus"]);
        var terminal = events[1];
        Assert.Equal(reason, terminal.Fields["ReasonCode"]);
        Assert.Equal(stage, terminal.Fields["Stage"]);
        Assert.Equal((int)response.StatusCode, terminal.Fields["HttpStatus"]);
        Assert.Equal(serviceStatus, terminal.Fields["ServiceHttpStatus"]);
        var id = Assert.Single(response.Headers.GetValues(Header));
        Assert.Matches("^[a-f0-9]{32}$", id);
        foreach (var entry in events) {
            Assert.Null(entry.Error);
            Assert.Equal(id, entry.Fields["IssuanceCorrelationId"]);
            Assert.Equal(new[] { "HttpStatus", "IssuanceCorrelationId", "PrefixCategory", "ReasonCode", "ServiceHttpStatus", "Stage", "ValidationReason", "{OriginalFormat}" },
                entry.Fields.Keys.Order(StringComparer.Ordinal).ToArray());
            Assert.IsType<AyaIssuanceReason>(entry.Fields["ReasonCode"]);
            Assert.IsType<AyaIssuanceStage>(entry.Fields["Stage"]);
            Assert.IsType<AyaResponseValidationReason>(entry.Fields["ValidationReason"]);
            Assert.IsType<AyaRejectedPrefixCategory>(entry.Fields["PrefixCategory"]);
            if (!Equals(entry.Fields["ValidationReason"], AyaResponseValidationReason.UrlPrefixRejected))
                Assert.Equal(AyaRejectedPrefixCategory.NotApplicable, entry.Fields["PrefixCategory"]);
        }
        var forbidden = new List<string> { Canary, IssuanceTests.Owner, IssuanceTests.Tenant, IssuanceTests.EmployeeKey,
            IssuanceTests.CallbackKey, "Synthetic Fixture", "SYNTHETIC-001", "Aya Living Inc.", "synthetic-profile-token",
            "synthetic-service-token", "synthetic-private-token-error", "synthetic-private-token-detail", "openid://", "api-key" };
        if (app.Outbound.LastRequest != null) {
            forbidden.Add(app.Outbound.LastRequest["callback"].Value<string>("state"));
            forbidden.Add(app.Outbound.LastRequest["callback"].Value<string>("url"));
            if (app.Outbound.LastRequest["pin"] is JObject pin) {
                forbidden.Add(pin.Value<string>("value")); forbidden.Add(pin.Value<string>("salt"));
            }
        }
        foreach (var entry in app.Logs.Entries.Concat(app.Logs.Events.SelectMany(e => e.Fields.Values).Select(v => v?.ToString() ?? "")))
            foreach (var value in forbidden.Where(v => !string.IsNullOrEmpty(v))) Assert.DoesNotContain(value, entry);
        return id;
    }
    private static void Clear(IssuanceTests.TestApp app) { app.Logs.Events.Clear(); app.Logs.Entries.Clear(); }

    [Theory]
    [InlineData(false, AyaIssuanceReason.TokenUnavailable, "token_unavailable")]
    [InlineData(true, AyaIssuanceReason.TokenAcquisitionFailed, "request_outcome_unknown")]
    public async Task Token_failures_keep_unknown_state_and_block_duplicates(bool throws, AyaIssuanceReason reason, string error)
    {
        var path = Path.Combine(Path.GetTempPath(), "aya-diagnostic-" + Guid.NewGuid(), "requests.json");
        string state, correlation;
        using (var app = new IssuanceTests.TestApp { StorePath = path }) {
            app.Tokens.Token = ""; if (throws) app.Tokens.Failure = new InvalidOperationException(Canary);
            using var client = app.CreateClient(); IssuanceTests.Identity(client); client.DefaultRequestHeaders.Add(Header, Canary);
            var response = await IssuanceTests.Issue(client);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var json = JObject.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "error", "id" }, json.Properties().Select(p => p.Name).ToArray());
            Assert.Equal(error, json.Value<string>("error")); state = json.Value<string>("id"); Assert.NotNull(state);
            correlation = AssertDiagnostic(app, response, reason, AyaIssuanceStage.TokenAcquisition); Assert.NotEqual(state, correlation);
            Assert.Equal("outcome_unknown", JObject.Parse(await client.GetStringAsync("/api/request-status?id=" + state)).Value<string>("status"));
            Assert.Equal(0, app.Outbound.IssuanceCalls);
        }
        using var restarted = new IssuanceTests.TestApp { StorePath = path }; using var next = restarted.CreateClient(); IssuanceTests.Identity(next);
        var conflict = await IssuanceTests.Issue(next);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("{\"error\":\"request_already_active\"}", await conflict.Content.ReadAsStringAsync());
        Assert.NotEqual(correlation, AssertDiagnostic(restarted, conflict, AyaIssuanceReason.RequestAlreadyActive, AyaIssuanceStage.StateBegin));
        Assert.Equal(0, restarted.Tokens.Calls); Assert.Equal(0, restarted.Outbound.IssuanceCalls);
        Assert.Equal("outcome_unknown", JObject.Parse(await next.GetStringAsync("/api/request-status?id=" + state)).Value<string>("status"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_and_timeout_exceptions_are_not_logged(bool timeout)
    {
        using var app = new IssuanceTests.TestApp();
        app.Outbound.IssuanceResponse = () => throw (timeout ? new TaskCanceledException(Canary) : new HttpRequestException(Canary));
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var response = await IssuanceTests.Issue(client);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("request_outcome_unknown", JObject.Parse(await response.Content.ReadAsStringAsync()).Value<string>("error"));
        AssertDiagnostic(app, response, AyaIssuanceReason.ServiceTransportFailed, AyaIssuanceStage.ServiceRequest);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    public async Task Non_created_service_response_records_only_numeric_status_and_keeps_local_400(int status)
    {
        using var app = new IssuanceTests.TestApp(); app.Outbound.IssuanceResponse = () => Task.FromResult(Response(status));
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var response = await IssuanceTests.Issue(client);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request_outcome_unknown", JObject.Parse(await response.Content.ReadAsStringAsync()).Value<string>("error"));
        AssertDiagnostic(app, response, AyaIssuanceReason.ServiceRejected, AyaIssuanceStage.ServiceResponse, status);
    }

    [Theory]
    [InlineData("url", AyaIssuanceReason.ResponseValidationFailed, AyaIssuanceStage.ResponseValidation, "invalid_service_response")]
    [InlineData("expiry_text", AyaIssuanceReason.ResponseValidationFailed, AyaIssuanceStage.ResponseValidation, "invalid_service_response")]
    [InlineData("expiry_range", AyaIssuanceReason.ResponseValidationFailed, AyaIssuanceStage.ResponseValidation, "request_outcome_unknown")]
    [InlineData("json", AyaIssuanceReason.ServiceResponseFailed, AyaIssuanceStage.ServiceResponse, "request_outcome_unknown")]
    [InlineData("missing_id", AyaIssuanceReason.StateBindingFailed, AyaIssuanceStage.StateBinding, "invalid_service_response")]
    [InlineData("expired", AyaIssuanceReason.StateBindingFailed, AyaIssuanceStage.StateBinding, "invalid_service_response")]
    [InlineData("early_conflict", AyaIssuanceReason.StateBindingFailed, AyaIssuanceStage.StateBinding, "invalid_service_response")]
    public async Task Accepted_service_response_validation_and_binding_failures_are_distinct(string mode, AyaIssuanceReason reason, AyaIssuanceStage stage, string error)
    {
        using var app = new IssuanceTests.TestApp(); var body = ServiceBody();
        if (mode == "url") body["url"] = "https://example.invalid/" + Canary;
        if (mode == "expiry_text") body["expiry"] = Canary;
        if (mode == "expiry_range") body["expiry"] = long.MaxValue;
        if (mode == "missing_id") body.Remove("requestId");
        if (mode == "expired") body["expiry"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        app.Outbound.IssuanceResponse = () => Task.FromResult(mode == "json" ?
            new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(Canary) } : Response(body: body));
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        if (mode == "early_conflict") app.Outbound.BeforeResponse = request => {
            Assert.True(app.Services.GetRequiredService<AyaIssuanceStateStore>().Apply(new CallbackEvent {
                state = request["callback"].Value<string>("state"), requestId = "different-synthetic-request", requestStatus = "issuance_successful"
            })); return Task.CompletedTask;
        };
        var response = await IssuanceTests.Issue(client);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var result = JObject.Parse(await response.Content.ReadAsStringAsync()); Assert.Equal(error, result.Value<string>("error"));
        AssertDiagnostic(app, response, reason, stage, 201);
        Assert.Equal("outcome_unknown", JObject.Parse(await client.GetStringAsync("/api/request-status?id=" + result.Value<string>("id"))).Value<string>("status"));
        Clear(app); Assert.Equal(HttpStatusCode.Conflict, (await IssuanceTests.Issue(client)).StatusCode); Assert.Equal(1, app.Outbound.IssuanceCalls);
    }

    [Theory]
    [InlineData("missing_attribute", 409, AyaIssuanceReason.IncompleteEmployeeProfile)]
    [InlineData("graph_error", 502, AyaIssuanceReason.EmployeeClaimsFailed)]
    [InlineData("guest", 403, AyaIssuanceReason.EmployeeNotEligible)]
    public async Task Profile_failures_are_distinct_from_active_request_conflicts(string mode, int status, AyaIssuanceReason reason)
    {
        using var app = new IssuanceTests.TestApp(); app.Outbound.Mode = mode;
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var response = await IssuanceTests.Issue(client); Assert.Equal(status, (int)response.StatusCode);
        AssertDiagnostic(app, response, reason, AyaIssuanceStage.EmployeeClaims);
        Assert.Equal(0, app.Tokens.Calls); Assert.Equal(0, app.Outbound.IssuanceCalls);
    }

    [Theory]
    [InlineData("disabled", AyaIssuanceReason.IssuanceDisabled, 503)]
    [InlineData("validity", AyaIssuanceReason.ValidityUnconfigured, 503)]
    [InlineData("config", AyaIssuanceReason.RequestNotConfigured, 503)]
    [InlineData("identity", AyaIssuanceReason.IdentityDenied, 401)]
    public async Task Guards_remain_closed(string mode, AyaIssuanceReason reason, int status)
    {
        using var app = new IssuanceTests.TestApp { Enabled = mode != "disabled" };
        if (mode == "validity") app.Overrides["Aya:CredentialValidityDays"] = null;
        if (mode == "config") app.ConfiguredKey = "";
        using var client = app.CreateClient(); if (mode != "identity") IssuanceTests.Identity(client);
        var response = await IssuanceTests.Issue(client); Assert.Equal(status, (int)response.StatusCode);
        AssertDiagnostic(app, response, reason, mode == "identity" ? AyaIssuanceStage.Identity : AyaIssuanceStage.Configuration);
        Assert.Equal(0, app.Tokens.Calls); Assert.Equal(0, app.Outbound.GraphCalls);
    }

    [Theory]
    [InlineData("VerifiedID:includeQRCode", 409, AyaIssuanceReason.PreSubmissionConflict, AyaIssuanceStage.RequestCreation)]
    [InlineData("VerifiedID:ApiEndpoint", 502, AyaIssuanceReason.ServiceRequestPreparationFailed, AyaIssuanceStage.ServiceRequestPreparation)]
    public async Task Local_preparation_errors_do_not_claim_a_service_transport_failure(string key, int status, AyaIssuanceReason reason, AyaIssuanceStage stage)
    {
        using var app = new IssuanceTests.TestApp(); app.Overrides[key] = key.EndsWith("ApiEndpoint") ? "http://[" : Canary;
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var response = await IssuanceTests.Issue(client); Assert.Equal(status, (int)response.StatusCode);
        AssertDiagnostic(app, response, reason, stage); Assert.Equal(0, app.Outbound.IssuanceCalls);
        Assert.Equal(status == 409 ? "incomplete_employee_profile" : "request_outcome_unknown",
            JObject.Parse(await response.Content.ReadAsStringAsync()).Value<string>("error"));
    }

    [Fact]
    public async Task Success_has_response_correlation_without_changing_body_or_logging_pin_or_claims()
    {
        using var app = new IssuanceTests.TestApp(); app.Outbound.IssuanceResponse = () => Task.FromResult(Response());
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        client.DefaultRequestHeaders.Add(Header, Canary); client.DefaultRequestHeaders.Add("X-Correlation-ID", Canary);
        var response = await IssuanceTests.Issue(client); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "id", "url", "expiry", "statusExpiry", "pin" }, body.Properties().Select(p => p.Name).ToArray());
        Assert.True(response.Headers.CacheControl.NoStore);
        AssertDiagnostic(app, response, AyaIssuanceReason.RequestCreated, AyaIssuanceStage.StateBinding, 201);
        foreach (var entry in app.Logs.Entries) Assert.DoesNotContain(body.Value<string>("pin"), entry);
    }

    private sealed class FailingPersistence(int failAt) : IAyaRequestPersistence
    {
        public readonly SyntheticPersistence Inner = new(); private int _commits;
        public T Read<T>(Func<Dictionary<string, AyaRequestRecord>, T> read)
        { if (failAt == 0) throw new AyaStorageException(); return Inner.Read(read); }
        public T Commit<T>(Func<Dictionary<string, AyaRequestRecord>, T> change)
        { if (++_commits == failAt) throw new AyaStorageException(); return Inner.Commit(change); }
    }
    [Theory]
    [InlineData(0, false, AyaIssuanceStage.StorageRead)]
    [InlineData(1, false, AyaIssuanceStage.StateBegin)]
    [InlineData(2, false, AyaIssuanceStage.StateBinding)]
    [InlineData(2, true, AyaIssuanceStage.OutcomeRecording)]
    public async Task Storage_failure_retains_503_including_failure_while_recording_unknown(int failAt, bool tokenFailure, AyaIssuanceStage stage)
    {
        var storage = new FailingPersistence(failAt); using var app = new IssuanceTests.TestApp { Persistence = storage };
        if (tokenFailure) app.Tokens.Failure = new Exception(Canary);
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var response = await IssuanceTests.Issue(client); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"request_storage_unavailable\"}", await response.Content.ReadAsStringAsync());
        AssertDiagnostic(app, response, AyaIssuanceReason.StorageUnavailable, stage, failAt == 2 && !tokenFailure ? 201 : null);
        Assert.Equal(failAt == 2 ? 1 : 0, storage.Inner.Read(rows => rows.Count));
        if (failAt == 2) Assert.Equal("request_pending", storage.Inner.Read(rows => rows.Values.Single().Status));
    }

    [Fact]
    public async Task Capacity_has_distinct_diagnostic_but_preserves_existing_conflict_body()
    {
        var storage = new SyntheticPersistence();
        storage.Commit(rows => {
            for (int i = 0; i < 2000; i++) {
                var id = Guid.NewGuid().ToString(); rows[id] = new AyaRequestRecord {
                    Id = id, Kind = "issuance", Owner = "synthetic-other-owner", Issuer = "did:example:synthetic",
                    Created = DateTimeOffset.UtcNow, AcceptUntil = DateTimeOffset.UtcNow.AddMinutes(10), RetainUntil = DateTimeOffset.UtcNow.AddHours(24)
                };
            } return true;
        });
        using var app = new IssuanceTests.TestApp { Persistence = storage }; using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var response = await IssuanceTests.Issue(client); Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("{\"error\":\"request_already_active\"}", await response.Content.ReadAsStringAsync());
        AssertDiagnostic(app, response, AyaIssuanceReason.StateCapacityReached, AyaIssuanceStage.StateBegin);
        Assert.Equal(0, app.Tokens.Calls); Assert.Equal(2000, storage.Read(rows => rows.Count));
    }
    [Fact]
    public void Detailed_begin_retains_boolean_semantics_for_duplicate_and_invalid_arguments()
    {
        var store = new AyaIssuanceStateStore(new SyntheticPersistence(), TimeProvider.System); var id = Guid.NewGuid().ToString();
        Assert.Equal(AyaRequestBeginResult.InvalidArguments, store.BeginWithResult(Canary, "owner", "issuance", "issuer"));
        Assert.False(store.Begin(Canary, "owner", "issuance", "issuer"));
        Assert.Equal(AyaRequestBeginResult.Started, store.BeginWithResult(id, "owner", "issuance", "issuer"));
        Assert.Equal(AyaRequestBeginResult.DuplicateId, store.BeginWithResult(id, "other", "issuance", "issuer"));
        Assert.False(store.Begin(id, "other", "issuance", "issuer"));
        Assert.Equal(AyaRequestBeginResult.AlreadyActive, store.BeginWithResult(Guid.NewGuid().ToString(), "owner", "issuance", "issuer"));
    }
}
