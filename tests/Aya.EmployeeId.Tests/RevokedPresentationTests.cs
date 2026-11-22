using System.Net;
using System.Text;
using AspNetCoreVerifiableCredentials;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Aya.EmployeeId.Tests;

public sealed class RevokedPresentationTests
{
    [Theory]
    [InlineData("VALID", "presentation_verified")]
    [InlineData("REVOKED", "presentation_error")]
    [InlineData("UNKNOWN", "presentation_error")]
    [InlineData(null, "presentation_error")]
    [InlineData("valid", "presentation_error")]
    public async Task Presentation_callback_accepts_only_explicit_valid_status(string status, string expected)
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<AyaIssuanceStateStore>();
        string state = Guid.NewGuid().ToString();
        Assert.True(store.Begin(state, IssuanceTests.Owner, "presentation", "did:example:synthetic"));
        Assert.True(store.Bind(state, "synthetic-presentation", DateTimeOffset.UtcNow.AddMinutes(5)));
        var callback = Event(status); callback.state = state;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/verifier/presentationcallback");
        request.Headers.Add("api-key", IssuanceTests.CallbackKey);
        request.Content = new StringContent(JsonConvert.SerializeObject(callback), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        Assert.True(store.TryGetStatus(state, IssuanceTests.Owner, "presentation", out var saved));
        Assert.Equal(expected, saved);
        if (expected == "presentation_error")
        {
            Assert.DoesNotContain("synthetic-claim-value", File.ReadAllText(app.StorePath));
            Assert.DoesNotContain("synthetic-receipt", File.ReadAllText(app.StorePath));
        }
        Assert.Equal(0, app.Outbound.IssuanceCalls); Assert.Equal(0, app.Tokens.Calls);
    }

    [Fact]
    public void Empty_or_mixed_credential_statuses_are_not_valid()
    {
        Assert.False(AyaPresentationPolicy.HasValidCredentialStatuses(new CallbackEvent()));
        Assert.False(AyaPresentationPolicy.HasValidCredentialStatuses(new CallbackEvent { verifiedCredentialsData = [] }));
        var callback = Event("VALID");
        callback.verifiedCredentialsData = [callback.verifiedCredentialsData[0], Event("REVOKED").verifiedCredentialsData[0]];
        Assert.False(AyaPresentationPolicy.HasValidCredentialStatuses(callback));
    }

    [Fact]
    public void Configuration_templates_and_helper_arguments_cannot_enable_revoked_presentations()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Aya:PublicOrigin"] = "https://pilot.example.invalid",
            ["VerifiedID:allowRevoked"] = "true", ["VerifiedID:CredentialType"] = "SyntheticAyaEmployeeId",
            ["VerifiedID:DidAuthority"] = "did:example:synthetic", ["VerifiedID:client_name"] = "Synthetic verifier"
        }).Build();
        var controller = new VerifierController(configuration, cache, NullLogger<VerifierController>.Instance, new IssuanceTests.FakeOutbound(), new IssuanceTests.FakeTokens(), new AyaIssuanceStateStore(new SyntheticPersistence(), TimeProvider.System))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var configured = controller.CreatePresentationRequest();
        Assert.False(configured.requestedCredentials.Single().configuration.validation.allowRevoked);
        controller.AddRequestedCredential(configured, "SyntheticOther", [], true);
        Assert.All(configured.requestedCredentials, c => Assert.False(c.configuration.validation.allowRevoked));
        var template = controller.CreatePresentationRequestFromTemplate("""
            {"requestedCredentials":[{"type":"SyntheticAyaEmployeeId","configuration":{"validation":{"allowRevoked":true}}},{"type":"SyntheticOther"}]}
            """);
        Assert.All(template.requestedCredentials, c => Assert.False(c.configuration.validation.allowRevoked));
    }

    [Fact]
    public void Previously_cached_revoked_presentation_is_not_reported_as_verified()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        string state = Guid.NewGuid().ToString();
        cache.Set(state, JsonConvert.SerializeObject(new { status = "presentation_verified", callback = JsonConvert.SerializeObject(Event("REVOKED")) }));
        var controller = new CallbackController(new ConfigurationBuilder().Build(), cache,
            NullLogger<CallbackController>.Instance, new AyaIssuanceStateStore(new SyntheticPersistence(), TimeProvider.System))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.QueryString = new QueryString("?id=" + state);
        Assert.False(controller.PollRequestStatus(out var result));
        Assert.Equal("presentation_error", result.Value<string>("status"));
    }

    private static CallbackEvent Event(string status) => new()
    {
        requestId = "synthetic-presentation", requestStatus = "presentation_verified",
        verifiedCredentialsData = [new ClaimsIssuer {
            type = ["AyaEmployeeId"], expirationDate = DateTimeOffset.UtcNow.AddDays(1).ToString("O"), issuer = "did:example:synthetic",
            claims = new Dictionary<string, string> { ["displayName"] = "synthetic-claim-value", ["employeeId"] = "SYNTHETIC-001", ["companyName"] = "Aya Living Inc.", ["ayaEmployeeKey"] = IssuanceTests.EmployeeKey },
            credentialState = status == null ? null : new CredentialState { revocationStatus = status }
        }],
        receipt = new Receipt { vp_token = ["synthetic-receipt"] }
    };
}
