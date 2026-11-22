
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace AspNetCoreVerifiableCredentials
{
    [ApiController]
    public class AyaIssuanceEligibilityController : ControllerBase
    {
        private readonly AyaEmployeeClaimsService _claims;
        private readonly AyaCredentialPolicy _policy;

        public AyaIssuanceEligibilityController(
            AyaEmployeeClaimsService claims, AyaCredentialPolicy policy)
        {
            _claims = claims;
            _policy = policy;
        }

        [HttpGet("/api/aya/issuance-eligibility")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> Check()
        {
            if (!_policy.Ready) return StatusCode(503, new { eligible = false, error = "issuance_disabled" });
            try
            {
                await _claims.GetClaimsAsync(HttpContext);

                return Ok(new
                {
                    eligible = true
                });
            }
            catch (UnauthorizedAccessException)
            {
                return StatusCode(403, new
                {
                    eligible = false
                });
            }
            catch (InvalidOperationException)
            {
                return Conflict(new
                {
                    eligible = false,
                    reason = "incomplete_employee_profile"
                });
            }
            catch (HttpRequestException)
            {
                return StatusCode(502, new
                {
                    error = "graph_lookup_failed"
                });
            }
        }
    }
}
