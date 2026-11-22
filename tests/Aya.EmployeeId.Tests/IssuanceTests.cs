using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AspNetCoreVerifiableCredentials;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Aya.EmployeeId.Tests;

// All identifiers, keys, tokens and profiles in this suite are synthetic.
public sealed class IssuanceTests
{
    internal const string Tenant = "33333333-3333-4333-8333-333333333333";
    internal const string EmployeeKey = "44444444-4444-4444-8444-444444444444";
    internal const string Owner = "11111111-1111-4111-8111-111111111111";
    internal const string Other = "22222222-2222-4222-8222-222222222222";
    internal const string CallbackKey = "synthetic-test-callback-key";

    internal static string Principal(string owner, bool role = true) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        JsonConvert.SerializeObject(new { claims = new[] {
            new { typ = "oid", val = owner }, new { typ = "tid", val = Tenant }, new { typ = "roles", val = role ? "Aya.EmployeeID.Request" : "Other.Role" }
        } })));

    internal static void Identity(HttpClient client, string owner = Owner, bool role = true)
    {
        client.DefaultRequestHeaders.Remove("X-MS-CLIENT-PRINCIPAL");
        client.DefaultRequestHeaders.Remove("X-MS-CLIENT-PRINCIPAL-ID");
        client.DefaultRequestHeaders.Remove("X-MS-TOKEN-AAD-ACCESS-TOKEN");
        client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Principal(owner, role));
        client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-ID", owner);
        client.DefaultRequestHeaders.Add("X-MS-TOKEN-AAD-ACCESS-TOKEN", "synthetic-profile-token");
    }
    internal static async Task<HttpResponseMessage> Issue(HttpClient client)
    {
        var page = await client.GetAsync("/Issuer");
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/issuer/issuance-request");
        request.Headers.Add("RequestVerificationToken", token);
        request.Headers.Add("Cookie", string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
        return await client.SendAsync(request);
    }
    private static Task<HttpResponseMessage> Callback(HttpClient client, string state, string status, string requestId = "synthetic-request", string key = CallbackKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/issuer/issuecallback");
        if (key != null) request.Headers.Add("api-key", key);
        request.Content = new StringContent(JsonConvert.SerializeObject(new { state, requestId, requestStatus = status }), Encoding.UTF8, "application/json");
        return client.SendAsync(request);
    }
    private static async Task<string> Create(HttpClient client)
    {
        Identity(client);
        var response = await Issue(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JObject.Parse(await response.Content.ReadAsStringAsync()).Value<string>("id");
    }

    [Fact]
    public async Task Authorized_owner_can_observe_success_and_duplicate_delivery()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var state = await Create(client);
        Assert.Equal("request_created", JObject.Parse(await client.GetStringAsync("/api/request-status?id=" + state))["status"]);
        Assert.Equal(HttpStatusCode.OK, (await Callback(client, state, "request_retrieved")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Callback(client, state, "issuance_successful")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Callback(client, state, "issuance_successful")).StatusCode);
        Assert.Equal("issuance_successful", JObject.Parse(await client.GetStringAsync("/api/request-status?id=" + state))["status"]);
        Assert.Equal(1, app.Outbound.IssuanceCalls);
        Assert.Equal("Synthetic Fixture", app.Outbound.LastClaims.Value<string>("displayName"));
        Assert.Equal("SYNTHETIC-001", app.Outbound.LastClaims.Value<string>("employeeId"));
        Assert.DoesNotContain(app.Logs.Entries, value => value.Contains(CallbackKey) || value.Contains("synthetic-profile-token") || value.Contains("SYNTHETIC-001"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("role")]
    [InlineData("malformed")]
    [InlineData("owner_mismatch")]
    public async Task Invalid_identity_cannot_issue_or_poll(string scenario)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var state = await Create(client);
        client.DefaultRequestHeaders.Clear();
        if (scenario != "missing") Identity(client, Owner, scenario != "role");
        if (scenario == "malformed") { client.DefaultRequestHeaders.Remove("X-MS-CLIENT-PRINCIPAL"); client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", "invalid-base64"); }
        if (scenario == "owner_mismatch") { client.DefaultRequestHeaders.Remove("X-MS-CLIENT-PRINCIPAL-ID"); client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-ID", Other); }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Issue(client)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/request-status?id=" + state)).StatusCode);
        Assert.Equal(1, app.Outbound.IssuanceCalls);
    }

    [Fact]
    public async Task Another_authorized_owner_and_unknown_state_are_not_disclosed()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        var state = await Create(client); Identity(client, Other);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/request-status?id=" + state)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/request-status?id=" + Guid.NewGuid())).StatusCode);
    }

    [Theory]
    [InlineData("guest", 403)]
    [InlineData("company", 403)]
    [InlineData("mismatch", 403)]
    [InlineData("missing_attribute", 409)]
    [InlineData("graph_error", 502)]
    public async Task Ineligible_profiles_do_not_acquire_application_tokens_or_issue(string mode, int expected)
    {
        using var app = new TestApp(); app.Outbound.Mode = mode; using var client = app.CreateClient(); Identity(client);
        Assert.Equal(expected, (int)(await Issue(client)).StatusCode);
        Assert.Equal(0, app.Outbound.IssuanceCalls); Assert.Equal(0, app.Tokens.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("synthetic-wrong-key")]
    public async Task Callback_requires_configured_key(string key)
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var state = await Create(client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Callback(client, state, "issuance_successful", key: key)).StatusCode);
        Assert.Equal("request_created", JObject.Parse(await client.GetStringAsync("/api/request-status?id=" + state))["status"]);
    }

    [Theory]
    [InlineData("unknown_state")]
    [InlineData("wrong_request")]
    [InlineData("wrong_kind")]
    [InlineData("missing_request")]
    public async Task Invalid_correlation_does_not_update_state(string mode)
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var state = await Create(client);
        var response = await Callback(client, mode == "unknown_state" ? Guid.NewGuid().ToString() : state,
            mode == "wrong_kind" ? "presentation_verified" : "issuance_successful",
            mode == "wrong_request" ? "other-synthetic-request" : mode == "missing_request" ? "" : "synthetic-request");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request_created", JObject.Parse(await client.GetStringAsync("/api/request-status?id=" + state))["status"]);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("not-json")]
    public async Task Invalid_callback_body_is_rejected(string body)
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("api-key", CallbackKey);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/issuer/issuecallback", new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Theory]
    [InlineData("issuance_successful", "request_retrieved")]
    [InlineData("issuance_successful", "issuance_error")]
    [InlineData("issuance_error", "issuance_successful")]
    public async Task Terminal_result_cannot_change(string terminal, string later)
    {
        using var app = new TestApp(); using var client = app.CreateClient(); var state = await Create(client);
        Assert.Equal(HttpStatusCode.OK, (await Callback(client, state, terminal)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Callback(client, state, terminal)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Callback(client, state, later)).StatusCode);
        Assert.Equal(terminal, JObject.Parse(await client.GetStringAsync("/api/request-status?id=" + state))["status"]);
    }

    [Fact]
    public async Task Issuance_requires_antiforgery_and_remains_disabled_by_default()
    {
        using var app = new TestApp { Enabled = false }; using var client = app.CreateClient(); Identity(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/issuer/issuance-request", null)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Issue(client)).StatusCode);
        Assert.Equal(0, app.Outbound.IssuanceCalls);
    }

    [Fact]
    public async Task Missing_profile_token_does_not_acquire_application_token()
    {
        using var app = new TestApp(); using var client = app.CreateClient(); Identity(client);
        client.DefaultRequestHeaders.Remove("X-MS-TOKEN-AAD-ACCESS-TOKEN");
        Assert.Equal(HttpStatusCode.Forbidden, (await Issue(client)).StatusCode);
        Assert.Equal(0, app.Tokens.Calls);
    }

    [Fact]
    public async Task Missing_callback_configuration_fails_closed()
    {
        using var app = new TestApp { ConfiguredKey = "" }; using var client = app.CreateClient(); Identity(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Issue(client)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Callback(client, Guid.NewGuid().ToString(), "issuance_successful")).StatusCode);
        Assert.Equal(0, app.Tokens.Calls);
    }

    [Theory]
    [InlineData("service_error", 400)]
    [InlineData("missing_request_id", 502)]
    [InlineData("expired_response", 502)]
    public async Task Upstream_failure_or_invalid_metadata_is_not_exposed(string mode, int status)
    {
        using var app = new TestApp(); app.Outbound.Mode = mode; using var client = app.CreateClient(); Identity(client);
        var response = await Issue(client);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.DoesNotContain("synthetic-private-detail", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(app.Logs.Entries, value => value.Contains("synthetic-private-detail") || value.Contains(CallbackKey) || value.Contains("SYNTHETIC-001"));
    }

    [Fact]
    public void Issuer_helpers_are_not_http_actions()
    {
        using var app = new TestApp(); using var client = app.CreateClient();
        Assert.DoesNotContain(app.Actions(), name => name == "CreateIssuanceRequest" || name == "SetPinCode");
        Assert.Contains("IssuanceRequest", app.Actions());
    }

    internal sealed class TestApp : IDisposable
    {
        public readonly FakeOutbound Outbound = new();
        public readonly FakeTokens Tokens = new();
        public readonly CapturedLogs Logs = new();
        public bool Enabled = true;
        public IAyaRequestPersistence Persistence;
        public string ConfiguredKey = CallbackKey;
        public readonly Dictionary<string,string> Overrides = new();
        private IHost _host;
        public string StorePath = Path.Combine(Path.GetTempPath(), "aya-synthetic-" + Guid.NewGuid(), "requests.json");
        internal IServiceProvider Services => _host.Services;
        public IEnumerable<string> Actions() => _host.Services
            .GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()
            .Where(action => action.ControllerTypeInfo.AsType() == typeof(IssuerController)).Select(action => action.ActionName);
        public void Dispose() { _host?.Dispose(); }
        public HttpClient CreateClient()
        {
            _host = Host.CreateDefaultBuilder().ConfigureWebHost(builder =>
            {
            builder.UseTestServer().UseStartup<Startup>();
            builder.UseEnvironment("Production");
            builder.UseContentRoot(Path.GetFullPath("../../../../../1-asp-net-core-api-idtokenhint", AppContext.BaseDirectory));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string,string> {
                ["Aya:IssuanceEnabled"] = Enabled.ToString(), ["Aya:CallbackApiKey"] = ConfiguredKey,
                ["Aya:CredentialValidityDays"] = "180",
                ["Aya:RequestTable:Endpoint"] = "https://synthetic.table.core.windows.net",
                ["Aya:RequestTable:TableName"] = "SyntheticRequests",
                ["Aya:RequestTable:LedgerId"] = "55555555-5555-4555-8555-555555555555",
                ["Aya:PublicOrigin"] = "https://pilot.example.invalid", ["Aya:VerificationEnabled"] = "true",
                ["Aya:EmployeeRegistry:0:TenantId"] = Tenant, ["Aya:EmployeeRegistry:0:ObjectId"] = Owner,
                ["Aya:EmployeeRegistry:0:EmployeeKey"] = EmployeeKey, ["Aya:EmployeeRegistry:0:Active"] = "true",
                ["VerifiedID:CredentialManifest"] = "https://verifiedid.did.msidentity.com/v1.0/tenants/" + Tenant + "/verifiableCredentials/contracts/synthetic/manifest",
                ["VerifiedID:TenantId"] = Tenant, ["VerifiedID:ApiEndpoint"] = "https://service.invalid/",
                ["VerifiedID:DidAuthority"] = "did:example:synthetic", ["VerifiedID:IssuancePinCodeLength"] = "0"
            }).AddInMemoryCollection(Overrides));
            builder.ConfigureLogging(log => { log.ClearProviders(); log.AddProvider(Logs); log.SetMinimumLevel(LogLevel.Trace); });
            builder.ConfigureServices(services => {
                services.RemoveAll<IAyaRequestPersistence>();
                services.AddSingleton<IAyaRequestPersistence>(sp => Persistence ??
                    (AyaRequestStorageConfiguration.IsConfigured(sp.GetRequiredService<IConfiguration>())
                        ? new FileAyaRequestPersistence(StorePath) : new UnconfiguredAyaRequestPersistence()));
                services.RemoveAll<IHttpClientFactory>(); services.AddSingleton<IHttpClientFactory>(Outbound);
                services.RemoveAll<IVerifiedIdTokenProvider>(); services.AddSingleton<IVerifiedIdTokenProvider>(Tokens);
            });
            }).Start();
            var client = _host.GetTestClient();
            client.BaseAddress = new Uri("https://localhost");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SyntheticBrowser/1.0");
            return client;
        }
    }
    internal sealed class FakeTokens : IVerifiedIdTokenProvider
    {
        public int Calls;
        public Task<(string token,string error,string error_description)> GetAccessToken() { Calls++; return Task.FromResult(("synthetic-service-token", "", "")); }
    }
    internal sealed class FakeOutbound : HttpMessageHandler, IHttpClientFactory
    {
        public int IssuanceCalls;
        public string Mode;
        public JObject LastClaims;
        public JObject LastRequest;
        public int GraphCalls;
        public int PresentationCalls;
        public Func<JObject, Task> BeforeResponse;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri.Host == "graph.microsoft.com" && request.RequestUri.AbsolutePath == "/v1.0/me")
            {
                GraphCalls++;
                if (Mode == "graph_error") return new(HttpStatusCode.ServiceUnavailable);
                return Json(HttpStatusCode.OK, new { accountEnabled = Mode == "unknown_enabled" ? (bool?)null : Mode != "disabled", id = Mode == "mismatch" ? Other : Owner, userType = Mode == "guest" ? "Guest" : "Member",
                    companyName = Mode == "company" ? "Synthetic Other" : "Aya Living Inc.", givenName = "Synthetic", surname = "Fixture", displayName = "Synthetic Fixture",
                    employeeId = Mode == "missing_attribute" ? "" : "SYNTHETIC-001", mail = "fixture@example.invalid" });
            }
            if (request.RequestUri.Host == "service.invalid" && request.RequestUri.AbsolutePath == "/createIssuanceRequest")
            {
                IssuanceCalls++; LastRequest = JObject.Parse(await request.Content.ReadAsStringAsync(cancellationToken)); LastClaims = (JObject)LastRequest["claims"];
                if (BeforeResponse != null) await BeforeResponse(LastRequest);
                if (Mode == "service_error") return Json(HttpStatusCode.BadRequest, new { error = "synthetic-private-detail" });
                return Json(HttpStatusCode.Created, new { requestId = Mode == "missing_request_id" ? null : "synthetic-request",
                    expiry = DateTimeOffset.UtcNow.AddMinutes(Mode == "expired_response" ? -1 : 5).ToUnixTimeSeconds(), url = "openid://vc?request_uri=https://verifiedid.did.msidentity.com/synthetic" });
            }
            if (request.RequestUri.Host == "service.invalid" && request.RequestUri.AbsolutePath == "/createPresentationRequest")
            {
                PresentationCalls++; LastRequest = JObject.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                if (BeforeResponse != null) await BeforeResponse(LastRequest);
                if (Mode == "service_error") return Json(HttpStatusCode.BadRequest, new { error = "synthetic-private-detail" });
                return Json(HttpStatusCode.Created, new { requestId = "synthetic-presentation",
                    expiry = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), url = "openid-vc://?request_uri=https://verifiedid.did.msidentity.com/synthetic" });
            }
            throw new InvalidOperationException("Unexpected outbound request in synthetic test.");
        }
        private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status) { Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json") };
    }
    internal sealed class CapturedLogs : ILoggerProvider
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Entries = new();
        public ILogger CreateLogger(string category) => new Capture(Entries, category.StartsWith("AspNetCoreVerifiableCredentials"));
        public void Dispose() { }
        private sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<string> entries, bool capture) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel level) => capture;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception exception, Func<TState,Exception,string> formatter) { if (capture) entries.Enqueue(formatter(state, exception)); }
        }
    }
}
