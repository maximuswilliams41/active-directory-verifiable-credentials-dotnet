using System;
using Microsoft.AspNetCore.WebUtilities;

namespace AspNetCoreVerifiableCredentials
{
    public static class AyaRequestLinks
    {
        public static bool Valid(string url, string kind)
        {
            var prefix = kind == "issuance" ? "openid://vc?" : "openid-vc://?";
            if (url == null || url.Length > 8192 || !url.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var query = QueryHelpers.ParseQuery(url.Substring(prefix.Length));
            return query.TryGetValue("request_uri", out var values) && values.Count == 1 &&
                Uri.TryCreate(values[0], UriKind.Absolute, out var target) && target.Scheme == "https" &&
                target.Host == "verifiedid.did.msidentity.com" && target.IsDefaultPort && target.UserInfo == "" && target.Fragment == "";
        }
    }
}
