
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;

namespace AspNetCoreVerifiableCredentials
{
    public sealed class AyaEmployeeClaimsService
    {
        private readonly IHttpClientFactory _clients;

        public AyaEmployeeClaimsService(
            IHttpClientFactory clients)
        {
            _clients = clients;
        }

        public async Task<Dictionary<string, string>>
            GetClaimsAsync(HttpContext context)
        {
            // Use identity provided by Azure App Service.
            string principal = context.Request.Headers[
                "X-MS-CLIENT-PRINCIPAL"].ToString();

            string token = context.Request.Headers[
                "X-MS-TOKEN-AAD-ACCESS-TOKEN"].ToString();

            if (string.IsNullOrWhiteSpace(principal) ||
                string.IsNullOrWhiteSpace(token))
                throw new UnauthorizedAccessException(
                    "Authenticated Entra session required.");

            // Extract the signed-in user's Entra object ID.
            var decoded = Encoding.UTF8.GetString(
                Convert.FromBase64String(principal));

            var identity = JObject.Parse(decoded);
            var entries = identity["claims"] as JArray;

            string objectId = entries?
                .OfType<JObject>()
                .Where(c =>
                    c.Value<string>("typ") == "oid" ||
                    c.Value<string>("typ") ==
                    "http://schemas.microsoft.com/identity/claims/objectidentifier")
                .Select(c => c.Value<string>("val"))
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(objectId))
                throw new UnauthorizedAccessException(
                    "Entra object ID not available.");

            // Aya Living: require explicit permission to
            // request an employee identification credential.
            bool authorized = entries?
                .OfType<JObject>()
                .Any(c =>
                    (
                        c.Value<string>("typ") == "roles" ||
                        c.Value<string>("typ") == "role" ||
                        c.Value<string>("typ") ==
                        "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
                    ) &&
                    c.Value<string>("val") ==
                        "Aya.EmployeeID.Request"
                ) == true;

            if (!authorized)
            {
                throw new UnauthorizedAccessException(
                    "Employee is not authorized to request an Aya ID.");
            }

            // Graph returns the authenticated user's record.
            string url =
                "https://graph.microsoft.com/v1.0/me" +
                "?$select=id,displayName,givenName,surname," +
                "employeeId,jobTitle,mail,companyName," +
                "officeLocation,userType";

            using var req = new HttpRequestMessage(
                HttpMethod.Get, url);

            req.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", token);

            using var response = await
                _clients.CreateClient().SendAsync(req);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    "Microsoft Graph profile lookup failed.");

            var user = JObject.Parse(
                await response.Content.ReadAsStringAsync());

            // Prevent issuing one employee's details
            // through another employee's session.
            if (!string.Equals(
                objectId,
                user.Value<string>("id"),
                StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(
                    "Employee identity mismatch.");

            if (user.Value<string>("userType") != "Member")
                throw new UnauthorizedAccessException(
                    "Employee account required.");

            string Required(string field)
            {
                string value = user.Value<string>(field);
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException(
                        "Required employee attribute missing: " + field);
                return value;
            }

            string company = Required("companyName");

            if (!string.Equals(
                company, "Aya Living Inc.",
                StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(
                    "Aya employee record required.");

            var claims = new Dictionary<string, string>
            {
                ["firstName"] = Required("givenName"),
                ["lastName"] = Required("surname"),
                ["displayName"] = Required("displayName"),
                ["employeeId"] = Required("employeeId"),
                ["companyName"] = company
            };

            void AddOptional(string claim, string field)
            {
                string value = user.Value<string>(field);
                if (!string.IsNullOrWhiteSpace(value))
                    claims[claim] = value;
            }

            AddOptional("jobTitle", "jobTitle");
            AddOptional("email", "mail");
            AddOptional("officeLocation", "officeLocation");

            // Manager and photograph will be added later.
            return claims;
        }
    }
}
