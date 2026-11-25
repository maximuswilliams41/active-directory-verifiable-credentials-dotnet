using System.Net;
using AspNetCoreVerifiableCredentials;
using Microsoft.AspNetCore.WebUtilities;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Aya.EmployeeId.Tests;

public sealed class ResponseValidationDiagnosticTests
{
    private const string Private = "synthetic-private-response-value";
    private const string Target = "https://verifiedid.did.msidentity.com/synthetic-private-response-value";
    private const string Prefix = "openid://vc?request_uri=";
    public static IEnumerable<object[]> Links()
    {
        yield return [null, AyaResponseValidationReason.UrlMissing];
        yield return [new string('x', 8193), AyaResponseValidationReason.UrlTooLong];
        yield return ["openid-vc://?request_uri=" + Target, AyaResponseValidationReason.None];
        yield return ["openid://vc?other=" + Private, AyaResponseValidationReason.RequestUriMissing];
        yield return [Prefix + Target + "&request_uri=" + Target, AyaResponseValidationReason.RequestUriAmbiguous];
        yield return [Prefix + Private, AyaResponseValidationReason.RequestUriInvalid];
        yield return [Prefix + "http://verifiedid.did.msidentity.com/" + Private, AyaResponseValidationReason.RequestUriSchemeRejected];
        yield return [Prefix + "https://example.invalid/" + Private, AyaResponseValidationReason.RequestUriHostRejected];
        yield return [Prefix + "https://verifiedid.did.msidentity.com:8443/" + Private, AyaResponseValidationReason.RequestUriPortRejected];
        yield return [Prefix + "https://synthetic@verifiedid.did.msidentity.com/" + Private, AyaResponseValidationReason.RequestUriUserInfoRejected];
        yield return [Prefix + Uri.EscapeDataString(Target + "#synthetic"), AyaResponseValidationReason.RequestUriFragmentRejected];
        yield return [Prefix + Uri.EscapeDataString(Target + "#"), AyaResponseValidationReason.RequestUriFragmentRejected];
        yield return [Prefix + Target, AyaResponseValidationReason.None];
        yield return [Prefix + Uri.EscapeDataString(Target), AyaResponseValidationReason.None];
        yield return [Prefix + "https://verifiedid.did.msidentity.com:443/" + Private + "&other=synthetic", AyaResponseValidationReason.None];
    }

    public static IEnumerable<object[]> BothFormats()
    {
        foreach (var row in Links()) {
            yield return row;
            if (row[0] is string url && url.StartsWith("openid://vc?", StringComparison.Ordinal) && url != Prefix + Target)
                yield return [url.Replace("openid://vc?", "openid-vc://?"), row[1]];
        }
    }

    // Frozen original predicates: normalize only the approved issuance prefix for comparison.
    private static bool PreviousValid(string url, string kind)
    {
        var prefix = kind == "issuance" ? "openid://vc?" : "openid-vc://?";
        if (url == null || url.Length > 8192 || !url.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var query = QueryHelpers.ParseQuery(url.Substring(prefix.Length));
        return query.TryGetValue("request_uri", out var values) && values.Count == 1 &&
            Uri.TryCreate(values[0], UriKind.Absolute, out var target) && target.Scheme == "https" &&
            target.Host == "verifiedid.did.msidentity.com" && target.IsDefaultPort && target.UserInfo == "" && target.Fragment == "";
    }
    [Theory]
    [MemberData(nameof(BothFormats))]
    public async Task Both_issuance_formats_preserve_destination_validation_and_diagnostic_privacy(string url, AyaResponseValidationReason expected)
    {
        foreach (var kind in new[] { "issuance", "presentation" }) {
            var previousUrl = kind == "issuance" && url?.StartsWith("openid-vc://?", StringComparison.Ordinal) == true
                ? "openid://vc?" + url.Substring("openid-vc://?".Length) : url;
            Assert.Equal(PreviousValid(previousUrl, kind), AyaRequestLinks.Valid(url, kind));
        }
        Assert.Equal(expected == AyaResponseValidationReason.None, AyaRequestLinks.Valid(url, "issuance", out var reason));
        Assert.Equal(expected, reason);
        var body = Body(); body["url"] = url;
        await Check(body, expected, expected == AyaResponseValidationReason.None ? 200 : 502,
            expected == AyaResponseValidationReason.None ? AyaIssuanceReason.RequestCreated : AyaIssuanceReason.ResponseValidationFailed,
            expected == AyaResponseValidationReason.None ? AyaIssuanceStage.StateBinding : AyaIssuanceStage.ResponseValidation);
    }

    [Theory]
    [InlineData("slash_query", AyaRejectedPrefixCategory.OpenIdVcSlashQuery)]
    [InlineData("scheme_query", AyaRejectedPrefixCategory.OpenIdVcSchemeQuery)]
    [InlineData("slash_empty_query", AyaRejectedPrefixCategory.OpenIdVcSlashQuery)]
    [InlineData("scheme_empty_query", AyaRejectedPrefixCategory.OpenIdVcSchemeQuery)]
    [InlineData("unknown_private_prefix", AyaRejectedPrefixCategory.Other)]
    [InlineData("embedded_known_prefix", AyaRejectedPrefixCategory.Other)]
    [InlineData("private_suffix_before_query", AyaRejectedPrefixCategory.Other)]
    [InlineData("scheme_private_suffix_before_query", AyaRejectedPrefixCategory.Other)]
    [InlineData("uppercase", AyaRejectedPrefixCategory.Other)]
    [InlineData("uppercase_scheme", AyaRejectedPrefixCategory.Other)]
    [InlineData("extra_slash_scheme", AyaRejectedPrefixCategory.Other)]
    [InlineData("authority_scheme", AyaRejectedPrefixCategory.Other)]
    [InlineData("leading_space", AyaRejectedPrefixCategory.Other)]
    [InlineData("missing_delimiter", AyaRejectedPrefixCategory.Other)]
    [InlineData("extra_slash", AyaRejectedPrefixCategory.Other)]
    public async Task Only_confirmed_prefix_changes_acceptance_and_other_categories_remain_rejected(string scenario, AyaRejectedPrefixCategory category)
    {
        var url = scenario switch {
            "slash_query" => "openid://vc/?request_uri=" + Target + "&private=" + Private,
            "scheme_query" => "openid-vc://?request_uri=" + Target + "&private=" + Private,
            "slash_empty_query" => "openid://vc/?",
            "scheme_empty_query" => "openid-vc://?",
            "unknown_private_prefix" => Private + "://?" + Private,
            "embedded_known_prefix" => Private + "openid://vc/?" + Private,
            "private_suffix_before_query" => "openid://vc/" + Private + "?" + Private,
            "scheme_private_suffix_before_query" => "openid-vc://" + Private + "?" + Private,
            "uppercase" => "OPENID://vc/?" + Private,
            "uppercase_scheme" => "OPENID-VC://?" + Private,
            "extra_slash_scheme" => "openid-vc:///?" + Private,
            "authority_scheme" => "openid-vc://vc?" + Private,
            "leading_space" => " openid://vc/?" + Private,
            "missing_delimiter" => "openid://vc/",
            "extra_slash" => "openid://vc//?" + Private,
            _ => throw new InvalidOperationException()
        };
        Assert.False(PreviousValid(url, "issuance"));
        if (scenario == "scheme_query" || scenario == "scheme_empty_query") {
            var accepted = scenario == "scheme_query";
            var revised = Body(); revised["url"] = url;
            await Check(revised, accepted ? AyaResponseValidationReason.None : AyaResponseValidationReason.RequestUriMissing,
                accepted ? 200 : 502, accepted ? AyaIssuanceReason.RequestCreated : AyaIssuanceReason.ResponseValidationFailed,
                accepted ? AyaIssuanceStage.StateBinding : AyaIssuanceStage.ResponseValidation);
            return;
        }
        Assert.False(AyaRequestLinks.Valid(url, "issuance", out var reason));
        Assert.Equal(AyaResponseValidationReason.UrlPrefixRejected, reason);
        var body = Body(); body["url"] = url;
        await Check(body, reason, 502, AyaIssuanceReason.ResponseValidationFailed,
            AyaIssuanceStage.ResponseValidation, prefixCategory: category);
    }

    [Fact]
    public async Task Length_rejection_before_prefix_check_has_no_prefix_category()
    {
        var body = Body(); body["url"] = "openid://vc/?" + new string('x', 8193);
        await Check(body, AyaResponseValidationReason.UrlTooLong, 502,
            AyaIssuanceReason.ResponseValidationFailed, AyaIssuanceStage.ResponseValidation);
    }

    private static JObject Body() => new() {
        ["requestId"] = Private, ["url"] = Prefix + Target,
        ["expiry"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), ["qrCode"] = Private
    };
    [Theory]
    [InlineData("url_object", AyaResponseValidationReason.UrlValueInvalid, "request_outcome_unknown")]
    [InlineData("missing", AyaResponseValidationReason.ExpiryMissing, "invalid_service_response")]
    [InlineData("null", AyaResponseValidationReason.ExpiryMissing, "invalid_service_response")]
    [InlineData("text", AyaResponseValidationReason.ExpiryNotInteger, "invalid_service_response")]
    [InlineData("object", AyaResponseValidationReason.ExpiryNotInteger, "invalid_service_response")]
    [InlineData("range", AyaResponseValidationReason.ExpiryOutOfRange, "request_outcome_unknown")]
    [InlineData("milliseconds", AyaResponseValidationReason.ExpiryOutOfRange, "request_outcome_unknown")]
    public async Task Field_read_and_expiry_failures_keep_existing_error_bodies(string scenario, AyaResponseValidationReason reason, string error)
    {
        var body = Body();
        switch (scenario) {
            case "url_object": body["url"] = new JObject { ["value"] = Private }; break;
            case "missing": body.Remove("expiry"); break;
            case "null": body["expiry"] = null; break;
            case "text": body["expiry"] = Private; break;
            case "object": body["expiry"] = new JObject { ["value"] = Private }; break;
            case "range": body["expiry"] = long.MaxValue; break;
            case "milliseconds": body["expiry"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(); break;
        }
        await Check(body, reason, 502, AyaIssuanceReason.ResponseValidationFailed, AyaIssuanceStage.ResponseValidation, error);
    }

    [Theory]
    [InlineData("missing_id", false)]
    [InlineData("expired", false)]
    [InlineData("future", false)]
    [InlineData("missing_id", true)]
    [InlineData("expired", true)]
    [InlineData("future", true)]
    public async Task Binding_failures_are_not_misclassified_as_url_or_expiry_parsing_failures(string scenario, bool confirmedPrefix)
    {
        var body = Body();
        if (confirmedPrefix) body["url"] = "openid-vc://?request_uri=" + Target;
        if (scenario == "missing_id") body.Remove("requestId");
        if (scenario == "expired") body["expiry"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        if (scenario == "future") body["expiry"] = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds();
        await Check(body, AyaResponseValidationReason.None, 502, AyaIssuanceReason.StateBindingFailed, AyaIssuanceStage.StateBinding);
    }

    [Fact]
    public async Task Documented_response_shape_passes_with_synthetic_identifiers_and_current_expiry()
    {
        var body = Body();
        body["requestId"] = Guid.NewGuid().ToString();
        body["url"] = Prefix + "https://verifiedid.did.msidentity.com/v1.0/33333333-3333-4333-8333-333333333333/verifiableCredentials/request/11111111-1111-4111-8111-111111111111";
        await Check(body, AyaResponseValidationReason.None, 200, AyaIssuanceReason.RequestCreated, AyaIssuanceStage.StateBinding);
    }

    private static async Task Check(JObject serviceBody, AyaResponseValidationReason validation, int status,
        AyaIssuanceReason reason, AyaIssuanceStage stage, string error = "invalid_service_response",
        AyaRejectedPrefixCategory prefixCategory = AyaRejectedPrefixCategory.NotApplicable)
    {
        using var app = new IssuanceTests.TestApp();
        app.Outbound.IssuanceResponse = () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) {
            Content = new StringContent(serviceBody.ToString())
        });
        using var client = app.CreateClient(); IssuanceTests.Identity(client);
        var response = await IssuanceTests.Issue(client); Assert.Equal(status, (int)response.StatusCode);
        var result = JObject.Parse(await response.Content.ReadAsStringAsync());
        IssuanceDiagnosticTests.AssertDiagnostic(app, response, reason, stage, 201);
        var entries = app.Logs.Events.Where(e => e.EventId == 7200).ToArray();
        Assert.Equal(AyaResponseValidationReason.None, entries[0].Fields["ValidationReason"]);
        Assert.Equal(validation, entries[1].Fields["ValidationReason"]);
        Assert.Equal(AyaRejectedPrefixCategory.NotApplicable, entries[0].Fields["PrefixCategory"]);
        Assert.Equal(prefixCategory, entries[1].Fields["PrefixCategory"]);
        foreach (var line in app.Logs.Entries.Concat(app.Logs.Events.SelectMany(e => e.Fields.Values).Select(v => v?.ToString() ?? ""))) {
            Assert.DoesNotContain(Private, line);
            if (serviceBody["url"]?.Type == JTokenType.String && serviceBody.Value<string>("url") is string url) Assert.DoesNotContain(url, line);
            if (serviceBody["requestId"] != null) Assert.DoesNotContain(serviceBody.Value<string>("requestId"), line);
            if (result["pin"] != null) Assert.DoesNotContain(result.Value<string>("pin"), line);
        }
        if (status == 200) Assert.Equal(serviceBody.Value<string>("url"), result.Value<string>("url"));
        var id = result.Value<string>("id"); Assert.NotNull(id);
        var state = JObject.Parse(await client.GetStringAsync("/api/request-status?id=" + id)).Value<string>("status");
        Assert.Equal(status == 200 ? "request_created" : "outcome_unknown", state);
        if (status != 200) {
            Assert.Equal(error, result.Value<string>("error"));
            Assert.Equal(new[] { "error", "id" }, result.Properties().Select(p => p.Name).ToArray());
            Assert.DoesNotContain(Private, result.ToString());
            if (serviceBody["url"]?.Type == JTokenType.String && serviceBody.Value<string>("url") is string rejectedUrl)
                Assert.DoesNotContain(rejectedUrl, result.ToString());
        }
        app.Logs.Events.Clear(); app.Logs.Entries.Clear();
        var duplicate = await IssuanceTests.Issue(client);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("{\"error\":\"request_already_active\"}", await duplicate.Content.ReadAsStringAsync());
        IssuanceDiagnosticTests.AssertDiagnostic(app, duplicate, AyaIssuanceReason.RequestAlreadyActive, AyaIssuanceStage.StateBegin);
        Assert.Equal(1, app.Outbound.IssuanceCalls);
    }
}
