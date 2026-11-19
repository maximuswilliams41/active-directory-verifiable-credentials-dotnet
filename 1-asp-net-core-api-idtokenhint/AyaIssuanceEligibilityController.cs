
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

        public AyaIssuanceEligibilityController(
            AyaEmployeeClaimsService claims)
        {
            _claims = claims;
        }

        [HttpGet("/api/aya/issuance-eligibility")]
        public async Task<IActionResult> Check()
        {
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
