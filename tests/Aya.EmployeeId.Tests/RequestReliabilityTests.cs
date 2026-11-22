using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AspNetCoreVerifiableCredentials;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Aya.EmployeeId.Tests;

public sealed class RequestReliabilityTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static string NewPath() => Path.Combine(Path.GetTempPath(), "aya-reliability-" + Guid.NewGuid(), "requests.json");
    private static string NewId() => Guid.NewGuid().ToString("D");
    private static CallbackEvent Event(string id, string status, string requestId = "synthetic-request") => new() { state = id, requestId = requestId, requestStatus = status };
    private static CallbackEvent Presentation(string id, string status = "VALID") => new() {
        state = id, requestId = "synthetic-presentation", requestStatus = "presentation_verified",
        verifiedCredentialsData = [new ClaimsIssuer {
            issuer = "did:example:synthetic", type = ["VerifiableCredential", "AyaEmployeeId"],
            claims = new Dictionary<string,string> { ["displayName"] = "Synthetic Employee", ["employeeId"] = "SYNTHETIC-001", ["companyName"] = "Aya Living Inc.", ["ayaEmployeeKey"] = IssuanceTests.EmployeeKey },
            expirationDate = DateTimeOffset.UtcNow.AddDays(1).ToString("O"), credentialState = new() { revocationStatus = status }
        }], receipt = new Receipt { vp_token = ["synthetic-private-receipt"] }
    };
    internal static async Task<HttpResponseMessage> Present(HttpClient client)
    {
        // The Issuer page supplies a normal antiforgery token even for anonymous/denied cases.
        var page = await client.GetAsync("/Issuer"); var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/verifier/presentation-request");
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("Cookie", string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
        return await client.SendAsync(request);
    }
    private static async Task<HttpResponseMessage> Callback(HttpClient client, string kind, CallbackEvent value, string key = IssuanceTests.CallbackKey)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, kind == "issuance" ? "/api/issuer/issuecallback" : "/api/verifier/presentationcallback");
        if (key != null) message.Headers.Add("api-key", key);
        message.Content = new StringContent(JsonConvert.SerializeObject(value), Encoding.UTF8, "application/json");
        return await client.SendAsync(message);
    }

    [Theory]
    [InlineData("issuance", "issuance_successful")]
    [InlineData("presentation", "presentation_verified")]
    public void Early_callback_is_durable_but_unconfirmed_until_matching_response_after_restart(string kind, string terminal)
    {
        var path = NewPath(); var clock = new Clock(); var id = NewId();
        var callback = kind == "issuance" ? Event(id, terminal) : Presentation(id);
        using (var disk = new FileAyaRequestPersistence(path)) {
            var store = new AyaIssuanceStateStore(disk, clock);
            Assert.True(store.Begin(id, "synthetic-owner", kind, "did:example:synthetic"));
            Assert.True(store.Apply(callback, kind));
            Assert.True(store.TryGetStatus(id, "synthetic-owner", kind, out var pending)); Assert.Equal("request_pending", pending);
        }
        using (var disk = new FileAyaRequestPersistence(path)) {
            var store = new AyaIssuanceStateStore(disk, clock);
            Assert.True(store.Bind(id, callback.requestId, clock.Now.AddMinutes(5)));
            Assert.True(store.TryGetStatus(id, "synthetic-owner", kind, out var result)); Assert.Equal(terminal, result);
        }
        Assert.DoesNotContain("Synthetic Employee", File.ReadAllText(path)); Assert.DoesNotContain("synthetic-private-receipt", File.ReadAllText(path));
        using (var disk = new FileAyaRequestPersistence(path)) {
            var store = new AyaIssuanceStateStore(disk, clock);
            Assert.True(store.Apply(callback, kind));
            Assert.False(store.TryGetStatus(id, "another-owner", kind, out _));
            Assert.False(store.TryGetStatus(id, "synthetic-owner", kind == "issuance" ? "presentation" : "issuance", out _));
        }
    }

    [Fact]
    public void Conflicting_early_request_id_never_becomes_success()
    {
        var clock = new Clock(); var store = new AyaIssuanceStateStore(new SyntheticPersistence(), clock); var id = NewId();
        Assert.True(store.Begin(id, "owner", "issuance", "did:example:synthetic")); Assert.True(store.Apply(Event(id, "issuance_successful")));
        Assert.False(store.Bind(id, "another-service-id", clock.Now.AddMinutes(5)));
        Assert.True(store.TryGetStatus(id, "owner", out var result)); Assert.Equal("outcome_unknown", result);
        Assert.False(store.Apply(Event(id, "issuance_successful", "another-service-id")));
    }

    [Fact]
    public void Request_timeout_callback_grace_and_result_retention_have_fixed_boundaries()
    {
        var clock = new Clock(); var start = clock.Now; var store = new AyaIssuanceStateStore(new SyntheticPersistence(), clock); var id = NewId();
        Assert.True(store.Begin(id, "owner", "issuance", "did:example:synthetic")); Assert.True(store.Bind(id, "synthetic-request", start.AddMinutes(5)));
        clock.Now = start.AddMinutes(6); Assert.True(store.Apply(Event(id, "issuance_successful")));
        clock.Now = start.AddHours(23); Assert.True(store.Apply(Event(id, "issuance_successful"))); // duplicate cannot extend retention
        Assert.True(store.TryGetStatus(id, "owner", out var status)); Assert.Equal("issuance_successful", status);
        clock.Now = start.AddHours(24); Assert.False(store.TryGetStatus(id, "owner", out _)); Assert.False(store.Apply(Event(id, "issuance_successful")));
    }

    [Fact]
    public void Late_unconfirmed_request_cannot_be_marked_success_and_pending_crash_never_retries_it()
    {
        var clock = new Clock(); var store = new AyaIssuanceStateStore(new SyntheticPersistence(), clock); var id = NewId();
        Assert.True(store.Begin(id, "owner", "issuance", "did:example:synthetic")); Assert.True(store.Bind(id, "synthetic-request", clock.Now.AddMinutes(5)));
        Assert.False(store.Begin(NewId(), "owner", "issuance", "did:example:synthetic"));
        clock.Now = clock.Now.AddMinutes(7); Assert.False(store.Apply(Event(id, "issuance_successful")));
        Assert.True(store.TryGetStatus(id, "owner", out var status)); Assert.Equal("outcome_unknown", status);
        var pending = NewId(); Assert.True(store.Begin(pending, "owner", "issuance", "did:example:synthetic"));
        clock.Now = clock.Now.AddMinutes(10); Assert.False(store.Bind(pending, "synthetic-request", clock.Now.AddMinutes(5)));
        Assert.True(store.TryGetStatus(pending, "owner", out status)); Assert.Equal("outcome_unknown", status);
    }

    [Fact]
    public void Terminal_callback_wins_over_delayed_retrieval_and_conflicting_terminal()
    {
        var store = new AyaIssuanceStateStore(new SyntheticPersistence(), TimeProvider.System); var id = NewId();
        Assert.True(store.Begin(id, "owner", "issuance", "did:example:synthetic")); Assert.True(store.Bind(id, "synthetic-request", DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.True(store.Apply(Event(id, "issuance_successful")));
        Assert.False(store.Apply(Event(id, "request_retrieved"))); Assert.False(store.Apply(Event(id, "issuance_error")));
        Assert.True(store.TryGetStatus(id, "owner", out var result)); Assert.Equal("issuance_successful", result);
    }

    [Fact]
    public void File_adapter_enforces_exclusive_writer_and_recovers_after_disposal()
    {
        var path = NewPath();
        using (var first = new FileAyaRequestPersistence(path)) Assert.Throws<AyaStorageException>(() => new FileAyaRequestPersistence(path));
        using var second = new FileAyaRequestPersistence(path); Assert.Equal(0, second.Read(rows => rows.Count));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("missing")]
    [InlineData("version")]
    public void Damaged_existing_journal_is_not_silently_reinitialized(string mode)
    {
        var path = NewPath(); using (var disk = new FileAyaRequestPersistence(path)) { }
        if (mode == "missing") File.Delete(path);
        else File.WriteAllText(path, mode == "corrupt" ? "incomplete-json" : "{\"Version\":99,\"Requests\":{}}");
        Assert.Throws<AyaStorageException>(() => new FileAyaRequestPersistence(path));
    }

    [Fact]
    public void Failed_commit_is_not_acknowledged_or_exposed_and_previous_snapshot_survives()
    {
        var path = NewPath(); var id = NewId();
        using (var disk = new FileAyaRequestPersistence(path)) {
            var store = new AyaIssuanceStateStore(disk, TimeProvider.System);
            Assert.True(store.Create(id, "owner", "synthetic-request", DateTimeOffset.UtcNow.AddMinutes(5)));
            Directory.CreateDirectory(path + ".next");
            Assert.Throws<AyaStorageException>(() => store.Apply(Event(id, "issuance_successful")));
            Assert.Throws<AyaStorageException>(() => store.TryGetStatus(id, "owner", out _));
        }
        Directory.Delete(path + ".next");
        using var reopened = new FileAyaRequestPersistence(path); var recovered = new AyaIssuanceStateStore(reopened, TimeProvider.System);
        Assert.True(recovered.TryGetStatus(id, "owner", out var result)); Assert.Equal("request_created", result);
    }

    [Fact]
    public void Persistence_failure_propagates_without_volatile_fallback()
    {
        var disk = new SyntheticPersistence { Fail = true }; var store = new AyaIssuanceStateStore(disk, TimeProvider.System);
        Assert.Throws<AyaStorageException>(() => store.Begin(NewId(), "owner", "issuance", "did:example:synthetic"));
    }

    [Fact]
    public async Task Abrupt_process_exit_releases_lock_and_preserves_committed_result()
    {
        var path = NewPath(); var id = NewId();
        var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new System.Diagnostics.ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(PersistenceProcess).Assembly.Location); start.ArgumentList.Add(path); start.ArgumentList.Add(id);
        using var child = System.Diagnostics.Process.Start(start);
        try {
            Assert.Equal("committed", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Throws<AyaStorageException>(() => new FileAyaRequestPersistence(path));
        }
        finally { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); }
        using var disk = new FileAyaRequestPersistence(path); var store = new AyaIssuanceStateStore(disk, TimeProvider.System);
        Assert.True(store.TryGetStatus(id, "synthetic-owner", out var result)); Assert.Equal("issuance_successful", result);
    }

    [Fact]
    public void Credential_expiration_is_rechecked_when_polling_a_previously_successful_presentation()
    {
        var clock = new Clock(); var store = new AyaIssuanceStateStore(new SyntheticPersistence(), clock); var id = NewId();
        Assert.True(store.Begin(id, "owner", "presentation", "did:example:synthetic")); Assert.True(store.Bind(id, "synthetic-presentation", clock.Now.AddMinutes(5)));
        var callback = Presentation(id); callback.verifiedCredentialsData[0].expirationDate = clock.Now.AddMinutes(1).ToString("O");
        Assert.True(store.Apply(callback, "presentation")); clock.Now = clock.Now.AddMinutes(1);
        Assert.True(store.TryGetStatus(id, "owner", "presentation", out var result)); Assert.Equal("presentation_error", result);
    }

    [Fact]
    public async Task Full_mocked_issuance_and_presentation_survive_application_restart()
    {
        var path = NewPath(); string issueId, presentationId;
        using (var app = new IssuanceTests.TestApp { StorePath = path }) using (var client = app.CreateClient()) {
            IssuanceTests.Identity(client);
            issueId = JObject.Parse(await (await IssuanceTests.Issue(client)).Content.ReadAsStringAsync()).Value<string>("id"); Assert.NotNull(issueId);
            Assert.Equal(HttpStatusCode.OK, (await Callback(client, "issuance", Event(issueId, "issuance_successful"))).StatusCode);
            var response = await Present(client); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            presentationId = JObject.Parse(await response.Content.ReadAsStringAsync()).Value<string>("id");
            Assert.Equal("AyaEmployeeId", app.Outbound.LastRequest["requestedCredentials"][0].Value<string>("type"));
            Assert.Equal("did:example:synthetic", app.Outbound.LastRequest["requestedCredentials"][0]["acceptedIssuers"][0].Value<string>());
            Assert.Equal(IssuanceTests.CallbackKey, app.Outbound.LastRequest["callback"]["headers"].Value<string>("api-key"));
            Assert.False(app.Outbound.LastRequest.Value<bool>("includeReceipt"));
            Assert.Equal(HttpStatusCode.OK, (await Callback(client, "presentation", Presentation(presentationId))).StatusCode);
            Assert.True(response.Headers.CacheControl.NoStore);
        }
        using var restarted = new IssuanceTests.TestApp { StorePath = path }; using var next = restarted.CreateClient(); IssuanceTests.Identity(next);
        Assert.Equal("issuance_successful", JObject.Parse(await next.GetStringAsync("/api/request-status?id=" + issueId)).Value<string>("status"));
        var result = await next.GetAsync("/api/verifier/request-status?id=" + presentationId);
        Assert.Equal("presentation_verified", JObject.Parse(await result.Content.ReadAsStringAsync()).Value<string>("status")); Assert.True(result.Headers.CacheControl.NoStore);
        Assert.DoesNotContain("SYNTHETIC-001", await result.Content.ReadAsStringAsync());
        IssuanceTests.Identity(next, IssuanceTests.Other);
        Assert.Equal(HttpStatusCode.NotFound, (await next.GetAsync("/api/verifier/request-status?id=" + presentationId)).StatusCode);
        Assert.Equal(0, restarted.Tokens.Calls); Assert.Equal(0, restarted.Outbound.PresentationCalls);
    }

    [Theory]
    [InlineData("issuance")]
    [InlineData("presentation")]
    public async Task Real_controller_accepts_callback_before_service_response_without_losing_terminal_result(string kind)
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient(); IssuanceTests.Identity(client);
        app.Outbound.BeforeResponse = async request => {
            var id = request["callback"].Value<string>("state");
            var callback = kind == "issuance" ? Event(id, "issuance_successful") : Presentation(id);
            Assert.Equal(HttpStatusCode.OK, (await Callback(client, kind, callback)).StatusCode);
        };
        var response = kind == "issuance" ? await IssuanceTests.Issue(client) : await Present(client); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = JObject.Parse(await response.Content.ReadAsStringAsync()).Value<string>("id");
        var status = JObject.Parse(await client.GetStringAsync((kind == "issuance" ? "/api/request-status?id=" : "/api/verifier/request-status?id=") + id)).Value<string>("status");
        Assert.Equal(kind == "issuance" ? "issuance_successful" : "presentation_verified", status);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("type")]
    [InlineData("expired")]
    [InlineData("missing_expiry")]
    [InlineData("missing_claim")]
    [InlineData("key")]
    [InlineData("revoked")]
    [InlineData("unconfirmed")]
    public async Task Unacceptable_presentations_never_report_verification_success(string mode)
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var id = JObject.Parse(await (await Present(client)).Content.ReadAsStringAsync()).Value<string>("id");
        var callback = Presentation(id); var credential = callback.verifiedCredentialsData[0];
        switch (mode) {
            case "issuer": credential.issuer = "did:example:other"; break;
            case "type": credential.type = ["OtherType"]; break;
            case "expired": credential.expirationDate = DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"); break;
            case "missing_expiry": credential.expirationDate = null; break;
            case "missing_claim": credential.claims.Remove("employeeId"); break;
            case "key": credential.claims["ayaEmployeeKey"] = "ambiguous"; break;
            case "revoked": credential.credentialState.revocationStatus = "REVOKED"; break;
            case "unconfirmed": credential.credentialState = null; break;
        }
        Assert.Equal(HttpStatusCode.OK, (await Callback(client, "presentation", callback)).StatusCode);
        Assert.Equal("presentation_error", JObject.Parse(await client.GetStringAsync("/api/verifier/request-status?id=" + id)).Value<string>("status"));
        Assert.DoesNotContain("synthetic-private-receipt", File.ReadAllText(app.StorePath));
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("role")]
    [InlineData("tenant")]
    public async Task Unauthorized_verifier_requests_and_status_are_denied(string mode)
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient();
        if (mode != "anonymous") IssuanceTests.Identity(client, role: mode != "role");
        if (mode == "tenant") {
            client.DefaultRequestHeaders.Remove("X-MS-CLIENT-PRINCIPAL");
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(IssuanceTests.Principal(IssuanceTests.Owner))).Replace(IssuanceTests.Tenant, IssuanceTests.Other);
            client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Present(client)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/verifier/request-status?id=" + NewId())).StatusCode);
        Assert.Equal(0, app.Tokens.Calls);
    }

    [Fact]
    public async Task Verification_is_post_only_protected_by_antiforgery_and_disabled_by_default()
    {
        using var app = new IssuanceTests.TestApp(); app.Overrides["Aya:VerificationEnabled"] = "false";
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/api/verifier/presentation-request")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/verifier/presentation-request", null)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Present(client)).StatusCode); Assert.Equal(0, app.Tokens.Calls);
    }

    [Theory]
    [InlineData("issuance")]
    [InlineData("presentation")]
    public async Task Both_callback_paths_require_key_and_reject_wrong_correlation_and_large_bodies(string kind)
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var response = kind == "issuance" ? await IssuanceTests.Issue(client) : await Present(client);
        var id = JObject.Parse(await response.Content.ReadAsStringAsync()).Value<string>("id");
        var callback = kind == "issuance" ? Event(id, "issuance_successful") : Presentation(id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Callback(client, kind, callback, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Callback(client, kind, callback, "synthetic-wrong")).StatusCode);
        callback.requestId = "other-request"; Assert.Equal(HttpStatusCode.BadRequest, (await Callback(client, kind, callback)).StatusCode);
        client.DefaultRequestHeaders.Add("api-key", IssuanceTests.CallbackKey);
        var route = kind == "issuance" ? "/api/issuer/issuecallback" : "/api/verifier/presentationcallback";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync(route, new StringContent(new string(' ', 17000)))).StatusCode);
    }

    [Fact]
    public async Task Missing_storage_fails_closed_before_profile_or_token_calls()
    {
        using var app = new IssuanceTests.TestApp(); app.Overrides["Aya:RequestStorePath"] = null;
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await IssuanceTests.Issue(client)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Present(client)).StatusCode);
        Assert.Equal(0, app.Tokens.Calls); Assert.Equal(0, app.Outbound.GraphCalls);
    }

    [Fact]
    public async Task Configured_storage_write_failure_prevents_outbound_creation_and_does_not_leak_path()
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient(); IssuanceTests.Identity(client);
        app.Services.GetRequiredService<AyaIssuanceStateStore>().CheckStorage(); Directory.CreateDirectory(app.StorePath + ".next");
        var response = await Present(client); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(app.StorePath, await response.Content.ReadAsStringAsync()); Assert.Equal(0, app.Tokens.Calls);
    }

    [Fact]
    public async Task Uncertain_service_response_does_not_retry_and_duplicate_submissions_are_blocked()
    {
        using var app = new IssuanceTests.TestApp(); app.Outbound.Mode = "service_error";
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var first = await Present(client); Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        var id = JObject.Parse(await first.Content.ReadAsStringAsync()).Value<string>("id");
        Assert.Equal("outcome_unknown", JObject.Parse(await client.GetStringAsync("/api/verifier/request-status?id=" + id)).Value<string>("status"));
        Assert.Equal(HttpStatusCode.Conflict, (await Present(client)).StatusCode); Assert.Equal(1, app.Outbound.PresentationCalls);
    }

    [Fact]
    public async Task Legacy_success_page_redirects_to_authenticated_verifier_and_sample_photo_routes_are_unavailable()
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient();
        var page = await client.GetAsync("/PresentationVerified"); Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/Verifier", page.Headers.Location.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/Verifier")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/issuer/selfie-request")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/issuer/userphoto", null)).StatusCode);
        Assert.Equal(0, app.Tokens.Calls);
    }

    [Fact]
    public async Task Verifier_page_does_not_fetch_templates_or_expose_sample_overrides()
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var html = await client.GetStringAsync("/Verifier?template=https%3A%2F%2Fsynthetic.invalid%2Ffixture&acceptedIssuers=other");
        Assert.Contains("Verify an Aya Employee ID", html); Assert.DoesNotContain("useFaceCheck", html); Assert.DoesNotContain("VerifiedCredentialExpert", html);
        Assert.Equal(0, app.Tokens.Calls); Assert.Equal(0, app.Outbound.GraphCalls);
    }
}
