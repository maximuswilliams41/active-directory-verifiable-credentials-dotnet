using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AspNetCoreVerifiableCredentials
{
    [ApiController]
    public class CallbackController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly AyaIssuanceStateStore _states;
        public CallbackController(IConfiguration configuration, IMemoryCache cache, ILogger<CallbackController> log, AyaIssuanceStateStore states)
        { _configuration = configuration; _states = states; }

        private async Task<ActionResult> Handle(string kind)
        {
            var expected = _configuration["Aya:CallbackApiKey"];
            var supplied = Request.Headers["api-key"];
            if (string.IsNullOrWhiteSpace(expected) || supplied.Count != 1 || string.IsNullOrWhiteSpace(supplied[0]) || supplied[0].Length > 1024 ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(supplied[0]))))
                return Unauthorized();
            try
            {
                // Explicit bounded read also applies to hosts that do not enforce RequestSizeLimit.
                using var reader = new StreamReader(Request.Body);
                var buffer = new char[16385]; int length = 0, count;
                while (length < buffer.Length && (count = await reader.ReadAsync(buffer.AsMemory(length))) > 0) length += count;
                if (length > 16384) return StatusCode(413);
                var callback = JsonConvert.DeserializeObject<CallbackEvent>(new string(buffer, 0, length), new JsonSerializerSettings { MaxDepth = 16 });
                return _states.Apply(callback, kind) ? Ok() : BadRequest(new { error = "invalid_callback" });
            }
            catch (AyaStorageException) { throw; }
            catch (JsonException) { return BadRequest(new { error = "invalid_callback" }); }
        }
        [AllowAnonymous, HttpPost("/api/issuer/issuecallback"), RequestSizeLimit(16384)]
        public Task<ActionResult> IssuanceCallback() => Handle("issuance");
        [AllowAnonymous, HttpPost("/api/verifier/presentationcallback"), RequestSizeLimit(16384)]
        public Task<ActionResult> PresentationCallback() => Handle("presentation");

        [HttpGet("/api/request-status"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public ActionResult RequestStatus() => Status("issuance");
        [HttpGet("/api/verifier/request-status"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public ActionResult PresentationStatus() => Status("presentation");
        private ActionResult Status(string kind)
        {
            string owner;
            try { owner = new AyaRequestConfiguration(_configuration).Owner(HttpContext); }
            catch (UnauthorizedAccessException) { return Unauthorized(); }
            if (!_states.TryGetStatus(Request.Query["id"], owner, kind, out var status)) return NotFound();
            return Ok(new { status, message = status switch {
                "issuance_successful" => "Issuance successful",
                "presentation_verified" => "Aya credential verified",
                "issuance_error" or "presentation_error" => "Request was not completed successfully",
                "request_retrieved" => "Complete the steps in Authenticator",
                "outcome_unknown" => "Outcome unconfirmed. Contact the pilot administrator before trying again",
                _ => "Waiting for Authenticator"
            }});
        }
        // Old generic cache entries cannot become trusted Aya results.
        [NonAction]
        public bool PollRequestStatus(out JObject result)
        { result = JObject.FromObject(new { status = "presentation_error", message = "Legacy request state is not trusted." }); return false; }
        [HttpPost("/api/issuer/selfie/{id}")]
        public ActionResult setSelfie(string id) => NotFound();
    }
}
