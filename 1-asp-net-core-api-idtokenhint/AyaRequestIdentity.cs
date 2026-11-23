using System;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AspNetCoreVerifiableCredentials
{
    // These headers must be supplied exclusively by the trusted App Service auth boundary.
    public static class AyaRequestIdentity
    {
        public static Guid RequireTenant(HttpContext context, string configuredTenant)
        {
            try
            {
                var claims = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(
                    context.Request.Headers["X-MS-CLIENT-PRINCIPAL"].ToString())))["claims"] as JArray;
                var ids = claims?.OfType<JObject>().Where(c => c.Value<string>("typ") == "tid" ||
                    c.Value<string>("typ") == "http://schemas.microsoft.com/identity/claims/tenantid")
                    .Select(c => c.Value<string>("val")).ToArray();
                if (!Guid.TryParse(configuredTenant, out var tenant) || tenant == Guid.Empty)
                    throw new AyaEligibilityDeniedException(AyaEligibilityReason.ConfiguredTenantInvalid);
                if (ids == null || ids.Length == 0)
                    throw new AyaEligibilityDeniedException(AyaEligibilityReason.TenantClaimMissing);
                if (ids.Any(id => !Guid.TryParse(id, out _)))
                    throw new AyaEligibilityDeniedException(AyaEligibilityReason.TenantClaimInvalid);
                if (ids.Any(id => Guid.Parse(id) != tenant))
                    throw new AyaEligibilityDeniedException(AyaEligibilityReason.TenantMismatch);
                return tenant;
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is ArgumentException)
            {
                throw new AyaEligibilityDeniedException(AyaEligibilityReason.TenantPrincipalMalformed);
            }
        }

        public static string RequireOwner(HttpContext context)
        {
            try
            {
                var header = context.Request.Headers["X-MS-CLIENT-PRINCIPAL"].ToString();
                // Easy Auth's provider identifier is opaque, not necessarily the Entra oid.
                // Require its presence, but derive ownership only from validated Entra claims.
                var providerId = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"];
                if (string.IsNullOrWhiteSpace(header))
                    throw new AyaEligibilityDeniedException(AyaEligibilityReason.PrincipalMissing);
                if (header.Length > 32768)
                    throw new AyaEligibilityDeniedException(AyaEligibilityReason.PrincipalTooLarge);
                if (providerId.Count > 1)
                    throw new AyaEligibilityDeniedException(AyaEligibilityReason.ProviderIdAmbiguous);
                if (providerId.Count == 0 || string.IsNullOrWhiteSpace(providerId[0]))
                    throw new AyaEligibilityDeniedException(AyaEligibilityReason.ProviderIdMissing);
                var claims = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(header)))["claims"] as JArray;
                var ids = claims?.OfType<JObject>().Where(c =>
                    c.Value<string>("typ") == "oid" ||
                    c.Value<string>("typ") == "http://schemas.microsoft.com/identity/claims/objectidentifier")
                    .Select(c => c.Value<string>("val")).ToArray();
                if (ids == null || ids.Length == 0)
                    throw new AyaEligibilityDeniedException(AyaEligibilityReason.ObjectIdMissing);
                if (!Guid.TryParse(ids[0], out var owner) || owner == Guid.Empty ||
                    ids.Any(id => !Guid.TryParse(id, out _)))
                    throw new AyaEligibilityDeniedException(AyaEligibilityReason.ObjectIdInvalid);
                if (ids.Any(id => Guid.Parse(id) != owner))
                    throw new AyaEligibilityDeniedException(AyaEligibilityReason.ObjectIdConflicting);
                bool permitted = claims.OfType<JObject>().Any(c =>
                    (c.Value<string>("typ") == "roles" || c.Value<string>("typ") == "role" ||
                     c.Value<string>("typ") == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role") &&
                    c.Value<string>("val") == "Aya.EmployeeID.Request");
                if (!permitted) throw new AyaEligibilityDeniedException(AyaEligibilityReason.RequiredRoleMissing);
                return owner.ToString("D");
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is ArgumentException || ex is InvalidCastException)
            {
                throw new AyaEligibilityDeniedException(AyaEligibilityReason.PrincipalMalformed);
            }
        }
    }
}
