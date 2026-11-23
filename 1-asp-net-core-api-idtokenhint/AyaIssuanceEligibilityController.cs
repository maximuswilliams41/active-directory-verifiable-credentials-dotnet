
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AspNetCoreVerifiableCredentials
{
    [ApiController]
    public class AyaIssuanceEligibilityController : ControllerBase
    {
        private readonly AyaEmployeeClaimsService _claims;
        private readonly AyaCredentialPolicy _policy;
        private readonly ILogger<AyaIssuanceEligibilityController> _log;

        public AyaIssuanceEligibilityController(
            AyaEmployeeClaimsService claims, AyaCredentialPolicy policy, ILogger<AyaIssuanceEligibilityController> log)
        {
            _claims = claims;
            _policy = policy;
            _log = log;
        }

        [HttpGet("/api/aya/issuance-eligibility")]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> Check()
        {
            // Independent of incoming headers/trace IDs. Never log exception objects or request data.
            var correlationId = Guid.NewGuid().ToString("N");
            void Record(AyaEligibilityReason reason) => _log.LogInformation(
                new EventId(7100, "AyaEligibility"), "Aya eligibility {ReasonCode}; RequestId={EligibilityRequestId}", reason, correlationId);
            Record(AyaEligibilityReason.CheckStarted);
            if (!_policy.Ready) {
                Record(_policy.IssuanceEnabled ? AyaEligibilityReason.ValidityUnconfigured : AyaEligibilityReason.IssuanceDisabled);
                return StatusCode(503, new { eligible = false, error = "issuance_disabled" });
            }
            try
            {
                await _claims.GetClaimsAsync(HttpContext);
                Record(AyaEligibilityReason.Eligible);

                return Ok(new
                {
                    eligible = true
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                Record(ex is AyaEligibilityDeniedException denied ? denied.Reason : AyaEligibilityReason.AuthorizationDeniedUnclassified);
                return StatusCode(403, new
                {
                    eligible = false
                });
            }
            catch (InvalidOperationException ex)
            {
                Record(ex is AyaEligibilityIncompleteException incomplete ? incomplete.Reason : AyaEligibilityReason.ProfileInvalidUnclassified);
                return Conflict(new
                {
                    eligible = false,
                    reason = "incomplete_employee_profile"
                });
            }
            catch (HttpRequestException)
            {
                Record(AyaEligibilityReason.GraphLookupFailed);
                return StatusCode(502, new
                {
                    error = "graph_lookup_failed"
                });
            }
        }
    }
}
