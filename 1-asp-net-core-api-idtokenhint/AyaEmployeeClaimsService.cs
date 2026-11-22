
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Configuration;

namespace AspNetCoreVerifiableCredentials
{
    public sealed class AyaEmployeeClaimsService
    {
        private readonly IHttpClientFactory _clients;
        private readonly IConfiguration _configuration;
        private readonly AyaEmployeeRegistry _registry;

        public AyaEmployeeClaimsService(
            IHttpClientFactory clients, IConfiguration configuration, AyaEmployeeRegistry registry)
        {
            _clients = clients;
            _configuration = configuration;
            _registry = registry;
        }

        public async Task<Dictionary<string, string>>
            GetClaimsAsync(HttpContext context)
        {
            string objectId = AyaRequestIdentity.RequireOwner(context);
            var tenant = AyaRequestIdentity.RequireTenant(context, _configuration["VerifiedID:TenantId"]);
            var employeeKey = _registry.RequireActiveKey(tenant, Guid.Parse(objectId));
            string token = context.Request.Headers["X-MS-TOKEN-AAD-ACCESS-TOKEN"].ToString();
            if (string.IsNullOrWhiteSpace(token))
                throw new UnauthorizedAccessException("Authenticated employee session required.");

            // Graph returns the authenticated user's record.
            string url =
                "https://graph.microsoft.com/v1.0/me" +
                "?$select=id,displayName,employeeId,companyName,userType,accountEnabled";

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

            if (user.Value<string>("userType") != "Member" ||
                user["accountEnabled"]?.Type != JTokenType.Boolean || user.Value<bool?>("accountEnabled") != true)
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
                ["displayName"] = Required("displayName"),
                ["employeeId"] = Required("employeeId"),
                ["companyName"] = company,
                ["ayaEmployeeKey"] = employeeKey
            };

            return claims;
        }
    }
}
