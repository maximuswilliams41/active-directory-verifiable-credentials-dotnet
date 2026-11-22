using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace AspNetCoreVerifiableCredentials
{
    [ApiController]
    public class AyaProfileController : ControllerBase
    {
        private readonly AyaEmployeeClaimsService _claims;
        private readonly AyaCredentialPolicy _policy;
        public AyaProfileController(AyaEmployeeClaimsService claims, AyaCredentialPolicy policy)
        { _claims = claims; _policy = policy; }

        [HttpGet("/api/aya/my-profile")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> GetProfile()
        {
            if (!_policy.Ready) return StatusCode(503, new { error = "issuance_disabled" });
            try
            {
                var claims = await _claims.GetClaimsAsync(HttpContext);
                return Ok(new { displayName = claims["displayName"], employeeId = claims["employeeId"], companyName = claims["companyName"] });
            }
            catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "employee_not_eligible" }); }
            catch (InvalidOperationException) { return Conflict(new { error = "incomplete_employee_profile" }); }
            catch (HttpRequestException) { return StatusCode(502, new { error = "upstream_unavailable" }); }
        }
    }
}
