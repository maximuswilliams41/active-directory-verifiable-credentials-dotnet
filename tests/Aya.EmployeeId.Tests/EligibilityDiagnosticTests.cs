using System.Net;
using System.Text;
using AspNetCoreVerifiableCredentials;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Aya.EmployeeId.Tests;

public sealed class EligibilityDiagnosticTests
{
    private const string Canary = "SYNTHETIC-PRIVATE-DATA-DO-NOT-LOG";
    private static void Header(HttpClient client, string name, params string[] values)
    {
        client.DefaultRequestHeaders.Remove(name);
        if (values.Length > 0) client.DefaultRequestHeaders.TryAddWithoutValidation(name, values);
    }
    private static void Claims(HttpClient client, Action<JArray> edit)
    {
        var principal = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(IssuanceTests.Principal(IssuanceTests.Owner))));
        edit((JArray)principal["claims"]);
        Header(client, "X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(Encoding.UTF8.GetBytes(principal.ToString())));
    }
    private static string AssertDiagnostics(IssuanceTests.TestApp app, AyaEligibilityReason expected)
    {
        var events = app.Logs.Events.Where(e => e.EventId == 7100).ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal(AyaEligibilityReason.CheckStarted, events[0].Fields["ReasonCode"]);
        Assert.Equal(expected, events[1].Fields["ReasonCode"]);
        foreach (var entry in events) {
            Assert.Null(entry.Error);
            Assert.Equal(new[] { "EligibilityRequestId", "ReasonCode", "{OriginalFormat}" }, entry.Fields.Keys.Order(StringComparer.Ordinal).ToArray());
            Assert.Matches("^[a-f0-9]{32}$", Assert.IsType<string>(entry.Fields["EligibilityRequestId"]));
        }
        Assert.Equal(events[0].Fields["EligibilityRequestId"], events[1].Fields["EligibilityRequestId"]);
        foreach (var entry in app.Logs.Entries) {
            Assert.DoesNotContain(Canary, entry);
            Assert.DoesNotContain(IssuanceTests.Owner, entry);
            Assert.DoesNotContain(IssuanceTests.Tenant, entry);
            Assert.DoesNotContain(IssuanceTests.EmployeeKey, entry);
            Assert.DoesNotContain("Synthetic Fixture", entry);
            Assert.DoesNotContain("SYNTHETIC-001", entry);
            Assert.DoesNotContain("Aya Living Inc.", entry);
            Assert.DoesNotContain("synthetic-profile-token", entry);
        }
        return (string)events[0].Fields["EligibilityRequestId"];
    }

    [Theory]
    [InlineData("principal_missing", AyaEligibilityReason.PrincipalMissing)]
    [InlineData("principal_large", AyaEligibilityReason.PrincipalTooLarge)]
    [InlineData("principal_malformed", AyaEligibilityReason.PrincipalMalformed)]
    [InlineData("provider_missing", AyaEligibilityReason.ProviderIdMissing)]
    [InlineData("provider_blank", AyaEligibilityReason.ProviderIdMissing)]
    [InlineData("provider_multiple", AyaEligibilityReason.ProviderIdAmbiguous)]
    [InlineData("oid_missing", AyaEligibilityReason.ObjectIdMissing)]
    [InlineData("oid_invalid", AyaEligibilityReason.ObjectIdInvalid)]
    [InlineData("oid_empty", AyaEligibilityReason.ObjectIdInvalid)]
    [InlineData("oid_conflict", AyaEligibilityReason.ObjectIdConflicting)]
    [InlineData("role_missing", AyaEligibilityReason.RequiredRoleMissing)]
    [InlineData("tenant_configuration", AyaEligibilityReason.ConfiguredTenantInvalid)]
    [InlineData("tid_missing", AyaEligibilityReason.TenantClaimMissing)]
    [InlineData("tid_invalid", AyaEligibilityReason.TenantClaimInvalid)]
    [InlineData("tid_mismatch", AyaEligibilityReason.TenantMismatch)]
    [InlineData("registry_empty", AyaEligibilityReason.RegistryEmpty)]
    [InlineData("registry_other", AyaEligibilityReason.RegistryNoMatch)]
    [InlineData("registry_inactive", AyaEligibilityReason.RegistryInactive)]
    [InlineData("token_missing", AyaEligibilityReason.GraphTokenMissing)]
    public async Task Pre_Graph_denials_keep_403_body_and_emit_only_fixed_reason_and_correlation(string scenario, AyaEligibilityReason reason)
    {
        using var app = new IssuanceTests.TestApp();
        if (scenario == "tenant_configuration") app.Overrides["VerifiedID:TenantId"] = Canary;
        if (scenario == "registry_empty") app.Registry = new AyaEmployeeRegistry(new ConfigurationBuilder().Build());
        if (scenario == "registry_other") app.Overrides["Aya:EmployeeRegistry:0:ObjectId"] = IssuanceTests.Other;
        if (scenario == "registry_inactive") app.Overrides["Aya:EmployeeRegistry:0:Active"] = "false";
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        Header(client, "X-Correlation-ID", Canary);
        switch (scenario) {
            case "principal_missing": Header(client, "X-MS-CLIENT-PRINCIPAL"); break;
            case "principal_large": Header(client, "X-MS-CLIENT-PRINCIPAL", new string('A', 32769)); break;
            case "principal_malformed": Header(client, "X-MS-CLIENT-PRINCIPAL", Canary); break;
            case "provider_missing": Header(client, "X-MS-CLIENT-PRINCIPAL-ID"); break;
            case "provider_blank": Header(client, "X-MS-CLIENT-PRINCIPAL-ID", " "); break;
            case "provider_multiple": Header(client, "X-MS-CLIENT-PRINCIPAL-ID", Canary, "second-synthetic-id"); break;
            case "oid_missing": Claims(client, c => c[0].Remove()); break;
            case "oid_invalid": Claims(client, c => c[0]["val"] = Canary); break;
            case "oid_empty": Claims(client, c => c[0]["val"] = Guid.Empty.ToString()); break;
            case "oid_conflict": Claims(client, c => c.Add(new JObject { ["typ"] = "oid", ["val"] = IssuanceTests.Other })); break;
            case "role_missing": Claims(client, c => c[2]["val"] = Canary); break;
            case "tid_missing": Claims(client, c => c[1].Remove()); break;
            case "tid_invalid": Claims(client, c => c[1]["val"] = Canary); break;
            case "tid_mismatch": Claims(client, c => c[1]["val"] = IssuanceTests.Other); break;
            case "token_missing": Header(client, "X-MS-TOKEN-AAD-ACCESS-TOKEN"); break;
        }
        var response = await client.GetAsync("/api/aya/issuance-eligibility");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("{\"eligible\":false}", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl.NoStore);
        AssertDiagnostics(app, reason);
        Assert.Equal(0, app.Outbound.GraphCalls); Assert.Equal(0, app.Tokens.Calls);
    }

    [Theory]
    [InlineData("mismatch", AyaEligibilityReason.GraphObjectIdMismatch, 403)]
    [InlineData("guest", AyaEligibilityReason.GraphUserTypeNotMember, 403)]
    [InlineData("unknown_enabled", AyaEligibilityReason.GraphAccountStateUnconfirmed, 403)]
    [InlineData("disabled", AyaEligibilityReason.GraphAccountDisabled, 403)]
    [InlineData("company", AyaEligibilityReason.CompanyMismatch, 403)]
    [InlineData("missing_attribute", AyaEligibilityReason.EmployeeIdMissing, 409)]
    [InlineData("missing_company", AyaEligibilityReason.CompanyNameMissing, 409)]
    [InlineData("missing_name", AyaEligibilityReason.DisplayNameMissing, 409)]
    [InlineData("graph_error", AyaEligibilityReason.GraphLookupFailed, 502)]
    public async Task Graph_results_have_specific_safe_diagnostics_and_unchanged_responses(string mode, AyaEligibilityReason reason, int status)
    {
        using var app = new IssuanceTests.TestApp(); app.Outbound.Mode = mode;
        app.Outbound.ProfileTransform = profile => {
            profile["mail"] = Canary;
            if (mode == "company") profile["companyName"] = Canary;
            if (mode == "missing_company") profile["companyName"] = " ";
            if (mode == "missing_name") profile["displayName"] = " ";
        };
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var response = await client.GetAsync("/api/aya/issuance-eligibility");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(status == 403 ? "{\"eligible\":false}" : status == 409 ?
            "{\"eligible\":false,\"reason\":\"incomplete_employee_profile\"}" : "{\"error\":\"graph_lookup_failed\"}",
            await response.Content.ReadAsStringAsync());
        AssertDiagnostics(app, reason);
        Assert.Equal(1, app.Outbound.GraphCalls); Assert.Equal(0, app.Outbound.IssuanceCalls);
    }

    [Theory]
    [InlineData("invalid", AyaEligibilityReason.RegistryInvalidEntry)]
    [InlineData("key", AyaEligibilityReason.RegistryDuplicateKey)]
    [InlineData("account", AyaEligibilityReason.RegistryDuplicateAccount)]
    public async Task Invalid_registry_is_diagnosed_even_when_extra_row_is_inactive(string problem, AyaEligibilityReason reason)
    {
        using var app = new IssuanceTests.TestApp();
        app.Overrides["Aya:EmployeeRegistry:1:TenantId"] = IssuanceTests.Tenant;
        app.Overrides["Aya:EmployeeRegistry:1:ObjectId"] = problem == "account" ? IssuanceTests.Owner : IssuanceTests.Other;
        app.Overrides["Aya:EmployeeRegistry:1:EmployeeKey"] = problem == "key" ? IssuanceTests.EmployeeKey : problem == "invalid" ? Canary : IssuanceTests.Other;
        app.Overrides["Aya:EmployeeRegistry:1:Active"] = "false";
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var response = await client.GetAsync("/api/aya/issuance-eligibility");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("{\"eligible\":false,\"reason\":\"incomplete_employee_profile\"}", await response.Content.ReadAsStringAsync());
        AssertDiagnostics(app, reason); Assert.Equal(0, app.Outbound.GraphCalls);
    }

    [Theory]
    [InlineData(false, "7", AyaEligibilityReason.IssuanceDisabled)]
    [InlineData(false, null, AyaEligibilityReason.IssuanceDisabled)]
    [InlineData(true, null, AyaEligibilityReason.ValidityUnconfigured)]
    public async Task Not_ready_returns_503_before_identity_registry_or_Graph_checks(bool enabled, string days, AyaEligibilityReason reason)
    {
        using var app = new IssuanceTests.TestApp { Enabled = enabled };
        app.Overrides["Aya:CredentialValidityDays"] = days;
        app.Overrides["Aya:VerificationEnabled"] = "false";
        app.Overrides["Aya:EmployeeRegistry:0:EmployeeKey"] = Canary;
        using var client = app.CreateClient();
        var response = await client.GetAsync("/api/aya/issuance-eligibility");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"eligible\":false,\"error\":\"issuance_disabled\"}", await response.Content.ReadAsStringAsync());
        AssertDiagnostics(app, reason); Assert.Equal(0, app.Outbound.GraphCalls);
    }

    [Fact]
    public async Task Successful_requests_have_distinct_server_generated_ids_and_no_response_diagnostics()
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient(); IssuanceTests.Identity(client);
        Header(client, "X-Request-ID", Canary); Header(client, "X-Correlation-ID", Canary);
        var response = await client.GetAsync("/api/aya/issuance-eligibility");
        Assert.Equal("{\"eligible\":true}", await response.Content.ReadAsStringAsync());
        var first = AssertDiagnostics(app, AyaEligibilityReason.Eligible);
        app.Logs.Events.Clear(); app.Logs.Entries.Clear();
        response = await client.GetAsync("/api/aya/issuance-eligibility");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(first, AssertDiagnostics(app, AyaEligibilityReason.Eligible));
        Assert.Equal(0, app.Outbound.IssuanceCalls); Assert.Equal(0, app.Tokens.Calls);
    }

    [Fact]
    public void Environment_names_bind_registry_and_registry_remains_a_snapshot()
    {
        // Isolated prefix avoids changing the running test hosts' configuration.
        var prefix = "AYA_SYNTHETIC_" + Guid.NewGuid().ToString("N") + "_";
        var values = new Dictionary<string, string> {
            ["Aya__EmployeeRegistry__0__TenantId"] = IssuanceTests.Tenant,
            ["Aya__EmployeeRegistry__0__ObjectId"] = IssuanceTests.Owner,
            ["Aya__EmployeeRegistry__0__EmployeeKey"] = IssuanceTests.EmployeeKey,
            ["Aya__EmployeeRegistry__0__Active"] = "true"
        };
        try {
            foreach (var pair in values) Environment.SetEnvironmentVariable(prefix + pair.Key, pair.Value);
            var config = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var registry = new AyaEmployeeRegistry(config);
            Assert.Equal(IssuanceTests.EmployeeKey, registry.RequireActiveKey(Guid.Parse(IssuanceTests.Tenant), Guid.Parse(IssuanceTests.Owner)));
            config["Aya:EmployeeRegistry:0:Active"] = "false";
            Assert.Equal(IssuanceTests.EmployeeKey, registry.RequireActiveKey(Guid.Parse(IssuanceTests.Tenant), Guid.Parse(IssuanceTests.Owner)));
            Assert.ThrowsAny<UnauthorizedAccessException>(() => new AyaEmployeeRegistry(config)
                .RequireActiveKey(Guid.Parse(IssuanceTests.Tenant), Guid.Parse(IssuanceTests.Owner)));
        }
        finally { foreach (var pair in values) Environment.SetEnvironmentVariable(prefix + pair.Key, null); }
    }

    [Theory]
    [InlineData("authorization", AyaEligibilityReason.AuthorizationDeniedUnclassified, 403)]
    [InlineData("profile", AyaEligibilityReason.ProfileInvalidUnclassified, 409)]
    [InlineData("http", AyaEligibilityReason.GraphLookupFailed, 502)]
    public async Task Fallback_diagnostics_never_log_exception_messages_or_objects(string failure, AyaEligibilityReason reason, int status)
    {
        using var app = new IssuanceTests.TestApp();
        app.Outbound.ProfileTransform = _ => throw failure switch {
            "authorization" => new UnauthorizedAccessException(Canary),
            "profile" => new InvalidOperationException(Canary),
            _ => new HttpRequestException(Canary)
        };
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var response = await client.GetAsync("/api/aya/issuance-eligibility");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.DoesNotContain(Canary, await response.Content.ReadAsStringAsync());
        AssertDiagnostics(app, reason);
    }
}
