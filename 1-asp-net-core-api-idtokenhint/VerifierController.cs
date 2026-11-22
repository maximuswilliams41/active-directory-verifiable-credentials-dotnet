using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AspNetCoreVerifiableCredentials
{
    [ApiController]
    public class VerifierController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _clients;
        private readonly IVerifiedIdTokenProvider _tokens;
        private readonly AyaIssuanceStateStore _states;
        private readonly AyaRequestConfiguration _settings;
        public VerifierController(IConfiguration configuration, IMemoryCache cache, ILogger<VerifierController> log,
            IHttpClientFactory clients, IVerifiedIdTokenProvider tokens, AyaIssuanceStateStore states)
        { _configuration = configuration; _clients = clients; _tokens = tokens; _states = states; _settings = new(configuration); }

        [HttpPost("/api/verifier/presentation-request"), ValidateAntiForgeryToken]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<ActionResult> PresentationRequest()
        {
            if (!_settings.VerificationEnabled || !_settings.Ready(false)) return StatusCode(503, new { error = "verification_disabled_or_unconfigured" });
            string owner;
            try { owner = _settings.Owner(HttpContext); }
            catch (UnauthorizedAccessException) { return Unauthorized(); }
            string pendingId = null;
            try
            {
                var request = CreatePresentationRequest();
                if (!_states.Begin(request.callback.state, owner, "presentation", request.authority))
                    return Conflict(new { error = "request_already_active" });
                pendingId = request.callback.state;
                var token = await _tokens.GetAccessToken();
                if (string.IsNullOrWhiteSpace(token.token))
                { _states.Unknown(pendingId); return StatusCode(502, new { error = "token_unavailable", id = pendingId }); }
                using var message = new HttpRequestMessage(HttpMethod.Post, $"{_configuration["VerifiedID:ApiEndpoint"]}createPresentationRequest");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.token);
                message.Content = new StringContent(JsonConvert.SerializeObject(request, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }), Encoding.UTF8, "application/json");
                using var response = await _clients.CreateClient().SendAsync(message);
                if (response.StatusCode != HttpStatusCode.Created)
                { _states.Unknown(pendingId); return StatusCode(502, new { error = "request_outcome_unknown", id = pendingId }); }
                var data = JObject.Parse(await response.Content.ReadAsStringAsync());
                if (!AyaRequestLinks.Valid(data.Value<string>("url"), "presentation") || !long.TryParse(data["expiry"]?.ToString(), out var expiry) ||
                    !_states.Bind(pendingId, data.Value<string>("requestId"), DateTimeOffset.FromUnixTimeSeconds(expiry)))
                { _states.Unknown(pendingId); return StatusCode(502, new { error = "invalid_service_response", id = pendingId }); }
                return Ok(new { id = pendingId, url = data.Value<string>("url"), expiry, statusExpiry = expiry + 120 });
            }
            catch (AyaStorageException) { throw; }
            catch (Exception)
            {
                if (pendingId != null) _states.Unknown(pendingId);
                return StatusCode(502, new { error = "request_outcome_unknown", id = pendingId });
            }
        }
        [NonAction]
        public PresentationRequest CreatePresentationRequest(string stateId = null, string credentialType = null, string[] acceptedIssuers = null)
        {
            var request = new PresentationRequest {
                authority = _configuration["VerifiedID:DidAuthority"], includeReceipt = false, includeQRCode = false,
                registration = new Registration { clientName = AyaCredentialPolicy.IssuerName, purpose = "Verify your Aya Employee ID for the controlled pilot" },
                callback = new Callback { url = _settings.CallbackOrigin() + "/api/verifier/presentationcallback",
                    state = stateId ?? Guid.NewGuid().ToString("D"), headers = new Dictionary<string,string> { ["api-key"] = _configuration["Aya:CallbackApiKey"] } },
                requestedCredentials = new List<RequestedCredential>()
            };
            return AddRequestedCredential(request, AyaCredentialPolicy.CredentialType, null);
        }
        // Retained helper signatures cannot override Aya policy and are not HTTP actions.
        [NonAction]
        public PresentationRequest CreatePresentationRequestFromTemplate(string template, string stateId = null) => CreatePresentationRequest(stateId);
        [NonAction]
        public PresentationRequest AddRequestedCredential(PresentationRequest request, string credentialType, List<string> acceptedIssuers,
            bool allowRevoked = false, bool validateLinkedDomain = true)
        {
            request.requestedCredentials.Add(new RequestedCredential { type = AyaCredentialPolicy.CredentialType,
                acceptedIssuers = new List<string> { _configuration["VerifiedID:DidAuthority"] },
                configuration = new Configuration { validation = new Validation { allowRevoked = false, validateLinkedDomain = true } } });
            return request;
        }
    }
}
