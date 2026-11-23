using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Aya.EmployeeId.Tests;

// Synthetic Easy Auth headers at the TestServer boundary; no Microsoft calls or tokens.
public sealed class EasyAuthIdentityTests
{
    private const string ProviderId = "synthetic-pairwise-subject_not-an-object-id";
    private const string ObjectClaim = "http://schemas.microsoft.com/identity/claims/objectidentifier";
    private const string TenantClaim = "http://schemas.microsoft.com/identity/claims/tenantid";
    private const string RoleClaim = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

    private static void Provider(HttpClient client, params string[] values)
    {
        client.DefaultRequestHeaders.Remove("X-MS-CLIENT-PRINCIPAL-ID");
        if (values.Length > 0) client.DefaultRequestHeaders.TryAddWithoutValidation("X-MS-CLIENT-PRINCIPAL-ID", values);
    }

    private static void Claims(HttpClient client, Action<JArray> edit)
    {
        var principal = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(IssuanceTests.Principal(IssuanceTests.Owner))));
        edit((JArray)principal["claims"]);
        client.DefaultRequestHeaders.Remove("X-MS-CLIENT-PRINCIPAL");
        client.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(Encoding.UTF8.GetBytes(principal.ToString())));
    }

    [Theory]
    [InlineData(ProviderId, false)]
    [InlineData(ProviderId, true)]
    [InlineData(IssuanceTests.Other, false)]
    [InlineData(IssuanceTests.Owner, false)]
    public async Task Provider_identifier_does_not_replace_Entra_claims_for_eligibility_issuance_or_polling(string providerId, bool mapped)
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient();
        IssuanceTests.Identity(client); Provider(client, providerId);
        if (mapped) Claims(client, claims => {
            claims[0]["typ"] = ObjectClaim; claims[1]["typ"] = TenantClaim; claims[2]["typ"] = RoleClaim;
        });

        var eligibility = await client.GetAsync("/api/aya/issuance-eligibility");
        Assert.Equal(HttpStatusCode.OK, eligibility.StatusCode);
        Assert.True(JObject.Parse(await eligibility.Content.ReadAsStringAsync()).Value<bool>("eligible"));
        var issuance = await IssuanceTests.Issue(client);
        Assert.Equal(HttpStatusCode.OK, issuance.StatusCode);
        var state = JObject.Parse(await issuance.Content.ReadAsStringAsync()).Value<string>("id");
        Assert.Equal(IssuanceTests.EmployeeKey, app.Outbound.LastClaims.Value<string>("ayaEmployeeKey"));
        Assert.Equal(2, app.Outbound.GraphCalls);

        // Ownership is stable across provider identifiers for the same Entra tenant/object.
        Provider(client, "another-synthetic-provider-subject");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/request-status?id=" + state)).StatusCode);
        IssuanceTests.Identity(client, IssuanceTests.Other); Provider(client, providerId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/request-status?id=" + state)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/aya/issuance-eligibility")).StatusCode);
        Assert.Equal(2, app.Outbound.GraphCalls); // No registry entry for the other account.
        Assert.Equal(1, app.Outbound.IssuanceCalls);
        Assert.DoesNotContain(app.Logs.Entries, entry => entry.Contains(ProviderId));
    }

    [Theory]
    [InlineData("missing_oid")]
    [InlineData("invalid_oid")]
    [InlineData("empty_oid")]
    [InlineData("conflicting_oid")]
    [InlineData("missing_tid")]
    [InlineData("invalid_tid")]
    [InlineData("empty_tid")]
    [InlineData("wrong_tid")]
    [InlineData("conflicting_tid")]
    [InlineData("missing_role")]
    [InlineData("wrong_role")]
    public async Task Provider_identifier_cannot_substitute_for_required_Entra_identity(string scenario)
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient();
        IssuanceTests.Identity(client); Provider(client, IssuanceTests.Owner);
        Claims(client, claims => {
            switch (scenario) {
                case "missing_oid": claims[0].Remove(); break;
                case "invalid_oid": claims[0]["val"] = "not-a-guid"; break;
                case "empty_oid": claims[0]["val"] = Guid.Empty.ToString(); break;
                case "conflicting_oid": claims.Add(new JObject { ["typ"] = ObjectClaim, ["val"] = IssuanceTests.Other }); break;
                case "missing_tid": claims[1].Remove(); break;
                case "invalid_tid": claims[1]["val"] = "not-a-guid"; break;
                case "empty_tid": claims[1]["val"] = Guid.Empty.ToString(); break;
                case "wrong_tid": claims[1]["val"] = IssuanceTests.Other; break;
                case "conflicting_tid": claims.Add(new JObject { ["typ"] = TenantClaim, ["val"] = IssuanceTests.Other }); break;
                case "missing_role": claims[2].Remove(); break;
                case "wrong_role": claims[2]["val"] = "aya.employeeid.request"; break;
            }
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/aya/issuance-eligibility")).StatusCode);
        Assert.Equal(scenario.EndsWith("_tid") ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized,
            (await IssuanceTests.Issue(client)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/request-status?id=" + Guid.NewGuid())).StatusCode);
        Assert.Equal(0, app.Outbound.GraphCalls); Assert.Equal(0, app.Tokens.Calls);
        Assert.Equal(0, app.Outbound.IssuanceCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("multiple")]
    public async Task Missing_or_ambiguous_provider_identifier_still_denies(string scenario)
    {
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient();
        IssuanceTests.Identity(client);
        if (scenario == "missing") Provider(client);
        if (scenario == "blank") Provider(client, " ");
        if (scenario == "multiple") Provider(client, ProviderId, "another-provider-id");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/aya/issuance-eligibility")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await IssuanceTests.Issue(client)).StatusCode);
        Assert.Equal(0, app.Outbound.GraphCalls); Assert.Equal(0, app.Tokens.Calls);
    }

    [Theory]
    [InlineData("mismatch", 403)]
    [InlineData("disabled", 403)]
    [InlineData("unknown_enabled", 403)]
    [InlineData("guest", 403)]
    [InlineData("company", 403)]
    [InlineData("missing_attribute", 409)]
    [InlineData("graph_error", 502)]
    public async Task Provider_specific_identifier_does_not_bypass_Graph_checks(string mode, int expected)
    {
        using var app = new IssuanceTests.TestApp(); app.Outbound.Mode = mode;
        using var client = app.CreateClient(); IssuanceTests.Identity(client); Provider(client, ProviderId);
        Assert.Equal(expected, (int)(await client.GetAsync("/api/aya/issuance-eligibility")).StatusCode);
        Assert.Equal(expected, (int)(await IssuanceTests.Issue(client)).StatusCode);
        Assert.Equal(2, app.Outbound.GraphCalls); Assert.Equal(0, app.Tokens.Calls);
        Assert.Equal(0, app.Outbound.IssuanceCalls);
    }
}
