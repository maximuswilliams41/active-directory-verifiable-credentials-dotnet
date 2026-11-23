using System.Net;
using AspNetCoreVerifiableCredentials;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Aya.EmployeeId.Tests;

public sealed class EmployeeExperienceTests
{
    [Theory]
    [InlineData("disabled")]
    [InlineData("unknown_enabled")]
    public async Task Disabled_or_unknown_account_state_denies_preview_and_issuance(string mode)
    {
        using var app = new IssuanceTests.TestApp(); app.Outbound.Mode = mode;
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/aya/my-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await IssuanceTests.Issue(client)).StatusCode);
        Assert.Equal(0, app.Tokens.Calls); Assert.Equal(0, app.Outbound.IssuanceCalls);
    }

    [Theory]
    [InlineData("Aya:EmployeeRegistry:0:Active", "false", 403)]
    [InlineData("Aya:EmployeeRegistry:0:EmployeeKey", "", 409)]
    [InlineData("Aya:EmployeeRegistry:0:ObjectId", "55555555-5555-4555-8555-555555555555", 403)]
    [InlineData("Aya:EmployeeRegistry:0:TenantId", "55555555-5555-4555-8555-555555555555", 403)]
    public async Task Registry_problems_deny_before_any_external_dependency(string setting, string value, int expected)
    {
        using var app = new IssuanceTests.TestApp(); app.Overrides[setting] = value;
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        Assert.Equal(expected, (int)(await IssuanceTests.Issue(client)).StatusCode);
        Assert.Equal(0, app.Outbound.GraphCalls); Assert.Equal(0, app.Tokens.Calls);
    }

    [Fact]
    public async Task Wrong_tenant_identity_cannot_preview_or_issue()
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var principal = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(IssuanceTests.Principal(IssuanceTests.Owner)));
        client.DefaultRequestHeaders.Remove("X-MS-CLIENT-PRINCIPAL");
        client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            principal.Replace(IssuanceTests.Tenant, "55555555-5555-4555-8555-555555555555"))));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/aya/my-profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await IssuanceTests.Issue(client)).StatusCode);
        Assert.Equal(0, app.Outbound.GraphCalls);
    }

    [Fact]
    public async Task Duplicate_employee_key_is_rejected()
    {
        using var app = new IssuanceTests.TestApp();
        app.Overrides["Aya:EmployeeRegistry:1:TenantId"] = IssuanceTests.Tenant;
        app.Overrides["Aya:EmployeeRegistry:1:ObjectId"] = IssuanceTests.Other;
        app.Overrides["Aya:EmployeeRegistry:1:EmployeeKey"] = IssuanceTests.EmployeeKey;
        app.Overrides["Aya:EmployeeRegistry:1:Active"] = "true";
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        Assert.Equal(HttpStatusCode.Conflict, (await IssuanceTests.Issue(client)).StatusCode);
        Assert.Equal(0, app.Outbound.GraphCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("367")]
    [InlineData("not-a-duration")]
    public async Task Unapproved_or_invalid_validity_does_not_fall_back_to_30_days(string value)
    {
        using var app = new IssuanceTests.TestApp(); app.Overrides["Aya:CredentialValidityDays"] = value;
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await IssuanceTests.Issue(client)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/aya/my-profile")).StatusCode);
        Assert.Equal(0, app.Outbound.GraphCalls); Assert.Equal(0, app.Tokens.Calls);
        Assert.Contains("Awaiting policy approval", await client.GetStringAsync("/Issuer"));
    }

    [Theory]
    [InlineData(180)]
    [InlineData(365)]
    public async Task Configured_test_duration_preserves_claims_type_and_hashed_pin_without_expiry_override(int days)
    {
        using var app = new IssuanceTests.TestApp(); app.Overrides["Aya:CredentialValidityDays"] = days.ToString();
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var response = await IssuanceTests.Issue(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("AyaEmployeeId", app.Outbound.LastRequest.Value<string>("type"));
        Assert.Equal("Aya Living", app.Outbound.LastRequest["registration"].Value<string>("clientName"));
        Assert.Equal(new[] { "ayaEmployeeKey", "companyName", "displayName", "employeeId" }, app.Outbound.LastClaims.Properties().Select(p => p.Name).Order().ToArray());
        Assert.Equal(IssuanceTests.EmployeeKey, app.Outbound.LastClaims.Value<string>("ayaEmployeeKey"));
        Assert.Null(app.Outbound.LastRequest.Property("expirationDate"));
        var pin = JObject.Parse(await response.Content.ReadAsStringAsync()).Value<string>("pin");
        Assert.Matches("^[0-9]{6}$", pin);
        Assert.NotEqual(pin, app.Outbound.LastRequest["pin"].Value<string>("value"));
        Assert.Equal("sha256", app.Outbound.LastRequest["pin"].Value<string>("alg"));
        Assert.True(response.Headers.CacheControl.NoStore);
        Assert.DoesNotContain(app.Logs.Entries, value => value.Contains(IssuanceTests.EmployeeKey) || value.Contains(pin));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seven_day_pilot_serializes_no_expiration_override_on_desktop_or_mobile(bool mobile)
    {
        using var app = new IssuanceTests.TestApp(); app.Overrides["Aya:CredentialValidityDays"] = "7";
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        if (mobile) {
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SyntheticBrowser/1.0 (Android)");
        }

        var response = await IssuanceTests.Issue(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // LastRequest is parsed from the actual serialized outgoing HTTP body.
        // Property(), unlike a null-value check, also rejects expirationDate: null.
        var request = app.Outbound.LastRequest;
        Assert.Null(request.Property("expirationDate"));
        Assert.Equal("AyaEmployeeId", request.Value<string>("type"));
        Assert.Equal("https://verifiedid.did.msidentity.com/v1.0/tenants/" + IssuanceTests.Tenant +
            "/verifiableCredentials/contracts/synthetic/manifest", request.Value<string>("manifest"));
        Assert.Equal(new[] { "ayaEmployeeKey", "companyName", "displayName", "employeeId" },
            app.Outbound.LastClaims.Properties().Select(p => p.Name).Order().ToArray());
        Assert.Equal(IssuanceTests.EmployeeKey, app.Outbound.LastClaims.Value<string>("ayaEmployeeKey"));
        var result = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(result.Value<string>("id"), request["callback"].Value<string>("state"));
        Assert.Equal("https://pilot.example.invalid/api/issuer/issuecallback", request["callback"].Value<string>("url"));
        Assert.Equal(IssuanceTests.CallbackKey, request["callback"]["headers"].Value<string>("api-key"));
        Assert.Equal(mobile, request.Property("pin") == null);
        Assert.Equal(1, app.Outbound.IssuanceCalls);
    }

    [Fact]
    public async Task Preview_discloses_only_approved_visible_fields()
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var response = await client.GetAsync("/api/aya/my-profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "companyName", "displayName", "employeeId" }, profile.Properties().Select(p => p.Name).Order().ToArray());
        Assert.True(response.Headers.CacheControl.NoStore);
    }

    [Fact]
    public async Task Disabled_pages_render_without_manifest_or_identity_service_calls()
    {
        using var app = new IssuanceTests.TestApp { Enabled = false }; app.Overrides["Aya:CredentialValidityDays"] = null;
        using var client = app.CreateClient();
        foreach (var page in new[] { "/", "/Issuer" })
        {
            var html = await client.GetStringAsync(page);
            Assert.Contains("Aya Employee ID", html);
            Assert.DoesNotContain("VerifiedCredentialExpert", html);
            Assert.DoesNotContain("Verifiable Credential Expert", html);
        }
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/issuer/get-manifest")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/aya/issuance-eligibility")).StatusCode);
        Assert.Equal(0, app.Outbound.GraphCalls); Assert.Equal(0, app.Tokens.Calls);
    }

    [Fact]
    public void Contract_template_requires_explicit_validity_and_has_one_immutable_index()
    {
        var root = Path.GetFullPath("../../../../../1-asp-net-core-api-idtokenhint", AppContext.BaseDirectory);
        var rules = JObject.Parse(File.ReadAllText(Path.Combine(root, "CredentialFiles/AyaEmployeeIdRules.template.json")));
        Assert.Equal(JTokenType.Null, rules["validityInterval"].Type);
        Assert.Equal("AyaEmployeeId", rules["vc"]["type"][0].Value<string>());
        var mapping = rules["attestations"]["idTokenHints"][0]["mapping"].Children<JObject>().ToArray();
        Assert.All(mapping, m => { Assert.True(m.Value<bool>("required")); Assert.Equal("$." + m.Value<string>("outputClaim"), m.Value<string>("inputClaim")); });
        Assert.Equal("ayaEmployeeKey", Assert.Single(mapping, m => m.Value<bool>("indexed")).Value<string>("outputClaim"));
        var display = JObject.Parse(File.ReadAllText(Path.Combine(root, "CredentialFiles/AyaEmployeeIdDisplay.json")));
        Assert.Equal("Aya Employee ID", display["card"].Value<string>("title"));
        Assert.Equal(3, display["claims"].Count());
        Assert.DoesNotContain("ayaEmployeeKey", display.ToString());
        var settings = JObject.Parse(File.ReadAllText(Path.Combine(root, "appsettings.json")));
        Assert.False(settings["Aya"].Value<bool>("IssuanceEnabled"));
        Assert.Equal(JTokenType.Null, settings["Aya"]["CredentialValidityDays"].Type);
        Assert.Empty(settings["Aya"]["EmployeeRegistry"]);
    }

    [Fact]
    public void Expiration_uses_explicit_days_across_leap_year_without_calendar_guessing()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["Aya:CredentialValidityDays"] = "365" }).Build();
        var issued = new DateTimeOffset(2028, 2, 29, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(issued.AddDays(365), new AyaCredentialPolicy(config).Expiration(issued));
    }
}
