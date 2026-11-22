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
        public static string RequireOwner(HttpContext context)
        {
            try
            {
                var header = context.Request.Headers["X-MS-CLIENT-PRINCIPAL"].ToString();
                if (string.IsNullOrWhiteSpace(header) || header.Length > 32768)
                    throw new UnauthorizedAccessException();
                var claims = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(header)))["claims"] as JArray;
                var ids = claims?.OfType<JObject>().Where(c =>
                    c.Value<string>("typ") == "oid" ||
                    c.Value<string>("typ") == "http://schemas.microsoft.com/identity/claims/objectidentifier")
                    .Select(c => c.Value<string>("val")).ToArray();
                if (ids == null || ids.Length == 0 ||
                    !Guid.TryParse(ids[0], out var owner) || owner == Guid.Empty ||
                    ids.Any(id => !Guid.TryParse(id, out var parsed) || parsed != owner) ||
                    !Guid.TryParse(context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"], out var headerOwner) || headerOwner != owner)
                    throw new UnauthorizedAccessException();
                bool permitted = claims.OfType<JObject>().Any(c =>
                    (c.Value<string>("typ") == "roles" || c.Value<string>("typ") == "role" ||
                     c.Value<string>("typ") == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role") &&
                    c.Value<string>("val") == "Aya.EmployeeID.Request");
                if (!permitted) throw new UnauthorizedAccessException();
                return owner.ToString("D");
            }
            catch (Exception ex) when (ex is FormatException || ex is JsonException || ex is ArgumentException || ex is InvalidCastException)
            {
                throw new UnauthorizedAccessException("Authenticated employee session required.");
            }
        }
    }
}
