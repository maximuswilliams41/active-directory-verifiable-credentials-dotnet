
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace AspNetCoreVerifiableCredentials
{
    [ApiController]
    public class AyaProfileController : ControllerBase
    {
        private readonly IHttpClientFactory _clients;

        public AyaProfileController(IHttpClientFactory clients)
        {
            _clients = clients;
        }

        [HttpGet("/api/aya/my-profile")]
        public async Task<IActionResult> GetProfile()
        {
            // Identity and token supplied by Azure App Service.
            // This endpoint requires App Service authentication.
            string principal = Request.Headers[
                "X-MS-CLIENT-PRINCIPAL"].ToString();

            string token = Request.Headers[
                "X-MS-TOKEN-AAD-ACCESS-TOKEN"].ToString();

            if (string.IsNullOrWhiteSpace(principal))
                return Unauthorized();

            if (string.IsNullOrWhiteSpace(token))
                return StatusCode(503, new {
                    error = "graph_access_token_missing"
                });

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://graph.microsoft.com/v1.0/me" +
                "?$select=id,displayName,givenName,surname," +
                "employeeId,jobTitle,mail,companyName," +
                "officeLocation");

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            using var response = await
                _clients.CreateClient().SendAsync(request);

            if (!response.IsSuccessStatusCode)
                return StatusCode(502, new {
                    error = "graph_profile_lookup_failed",
                    status = (int)response.StatusCode
                });

            var user = JObject.Parse(
                await response.Content.ReadAsStringAsync());

            string employeeId = (string)user["employeeId"];

            if (string.IsNullOrWhiteSpace(employeeId))
                return Conflict(new {
                    error = "employee_id_missing"
                });

            return Ok(new {
                id = (string)user["id"],
                firstName = (string)user["givenName"],
                lastName = (string)user["surname"],
                displayName = (string)user["displayName"],
                employeeId = employeeId,
                jobTitle = (string)user["jobTitle"],
                email = (string)user["mail"],
                companyName = (string)user["companyName"],
                officeLocation = (string)user["officeLocation"]
            });
        }
    }
}
