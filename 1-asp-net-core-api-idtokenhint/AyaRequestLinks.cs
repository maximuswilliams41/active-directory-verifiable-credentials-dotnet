using System;
using Microsoft.AspNetCore.WebUtilities;

namespace AspNetCoreVerifiableCredentials
{
    public enum AyaResponseValidationReason
    {
        None, UrlValueInvalid, UrlMissing, UrlTooLong, UrlPrefixRejected, UrlQueryInvalid,
        RequestUriMissing, RequestUriAmbiguous, RequestUriInvalid, RequestUriSchemeRejected,
        RequestUriHostRejected, RequestUriPortRejected, RequestUriUserInfoRejected, RequestUriFragmentRejected,
        ExpiryMissing, ExpiryNotInteger, ExpiryOutOfRange
    }

    public static class AyaRequestLinks
    {
        public static bool Valid(string url, string kind) => Valid(url, kind, out _);

        // Same predicates and short-circuit order as the original validator. The reason is
        // a closed diagnostic enum, never a parsed value or exception message.
        public static bool Valid(string url, string kind, out AyaResponseValidationReason reason)
        {
            var prefix = kind == "issuance" ? "openid://vc?" : "openid-vc://?";
            reason = AyaResponseValidationReason.UrlMissing;
            if (url == null) return false;
            reason = AyaResponseValidationReason.UrlTooLong;
            if (url.Length > 8192) return false;
            reason = AyaResponseValidationReason.UrlPrefixRejected;
            if (!url.StartsWith(prefix, StringComparison.Ordinal)) return false;
            reason = AyaResponseValidationReason.UrlQueryInvalid;
            var query = QueryHelpers.ParseQuery(url.Substring(prefix.Length));
            reason = AyaResponseValidationReason.RequestUriMissing;
            if (!query.TryGetValue("request_uri", out var values)) return false;
            reason = AyaResponseValidationReason.RequestUriAmbiguous;
            if (values.Count != 1) return false;
            reason = AyaResponseValidationReason.RequestUriInvalid;
            if (!Uri.TryCreate(values[0], UriKind.Absolute, out var target)) return false;
            reason = AyaResponseValidationReason.RequestUriSchemeRejected;
            if (target.Scheme != "https") return false;
            reason = AyaResponseValidationReason.RequestUriHostRejected;
            if (target.Host != "verifiedid.did.msidentity.com") return false;
            reason = AyaResponseValidationReason.RequestUriPortRejected;
            if (!target.IsDefaultPort) return false;
            reason = AyaResponseValidationReason.RequestUriUserInfoRejected;
            if (target.UserInfo != "") return false;
            reason = AyaResponseValidationReason.RequestUriFragmentRejected;
            if (target.Fragment != "") return false;
            reason = AyaResponseValidationReason.None;
            return true;
        }
    }
}
