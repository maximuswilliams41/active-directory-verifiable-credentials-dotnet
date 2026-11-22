using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Identity.Client;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using System.Reflection.Metadata.Ecma335;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http;

namespace AspNetCoreVerifiableCredentials
{
   
[Route("api/[controller]/[action]")]
[ApiController]
public class IssuerController : ControllerBase
{
    
        protected IMemoryCache _cache;
        protected readonly ILogger<IssuerController> _log;
        private IHttpClientFactory _httpClientFactory;
        private IConfiguration _configuration;
        private readonly AyaEmployeeClaimsService _ayaClaims;
        private string _apiKey;
        private readonly AyaIssuanceStateStore _issuanceStates;
        private readonly IVerifiedIdTokenProvider _tokens;
        private readonly AyaCredentialPolicy _policy;
        private readonly TimeProvider _time;

        public IssuerController(
            IConfiguration configuration,
            IMemoryCache memoryCache,
            ILogger<IssuerController> log,
            IHttpClientFactory httpClientFactory,
            AyaEmployeeClaimsService ayaClaims, AyaIssuanceStateStore issuanceStates, IVerifiedIdTokenProvider tokens, AyaCredentialPolicy policy, TimeProvider time)
        {
            _cache = memoryCache;
            _log = log;
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
            _ayaClaims = ayaClaims;
            _issuanceStates = issuanceStates;
            _tokens = tokens;
            _policy = policy;
            _time = time;
            
_apiKey = _configuration["Aya:CallbackApiKey"];

        }

        public static string Sha256Hash( string source ) {
            string hash = null;
            using (SHA256 sha256Hash = SHA256.Create()) {
                byte[] bytes = sha256Hash.ComputeHash( Encoding.UTF8.GetBytes( source ) );
                hash = Convert.ToBase64String( bytes );
            }
            return hash;
        }

        /// <summary>
        /// This method is called from the UI to initiate the issuance of the verifiable credential
        /// </summary>
        /// <returns>JSON object with the address to the presentation request and optionally a QR code and a state value which can be used to check on the response status</returns>
        
[HttpPost("/api/issuer/issuance-request")]
[ValidateAntiForgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public async Task<ActionResult> IssuanceRequest()
        {
            if (!_policy.IssuanceEnabled) return StatusCode(503, new { error = "issuance_disabled" });
            if (!_policy.ValidityDays.HasValue) return StatusCode(503, new { error = "validity_not_configured" });
            var configuration = new AyaRequestConfiguration(_configuration);
            if (!configuration.Ready(true)) return StatusCode(503, new { error = "request_not_configured" });
            try { AyaRequestIdentity.RequireOwner(HttpContext); }
            catch (UnauthorizedAccessException) { return Unauthorized(); }
            string pendingId = null;
            try
            {
                // Probe persistence before any remote operation, including the profile lookup.
                _issuanceStates.CheckStorage();
                var claims = await _ayaClaims.GetClaimsAsync(HttpContext);
                var owner = configuration.Owner(HttpContext);
                var request = CreateIssuanceRequest(out string pinCode);
                if (!_issuanceStates.Begin(request.callback.state, owner, "issuance", request.authority))
                    return Conflict(new { error = "request_already_active" });
                pendingId = request.callback.state;
                var accessToken = await _tokens.GetAccessToken();
                if (string.IsNullOrWhiteSpace(accessToken.token))
                { _issuanceStates.Unknown(pendingId); return StatusCode(502, new { error = "token_unavailable", id = pendingId }); }
                request.claims = claims;
                using var message = new HttpRequestMessage(HttpMethod.Post, $"{_configuration["VerifiedID:ApiEndpoint"]}createIssuanceRequest");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.token);
                message.Content = new StringContent(JsonConvert.SerializeObject(request, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }), Encoding.UTF8, "application/json");
                using var response = await _httpClientFactory.CreateClient().SendAsync(message);
                if (response.StatusCode != HttpStatusCode.Created)
                { _issuanceStates.Unknown(pendingId); return BadRequest(new { error = "request_outcome_unknown", id = pendingId }); }
                var data = JObject.Parse(await response.Content.ReadAsStringAsync());
                if (!AyaRequestLinks.Valid(data.Value<string>("url"), "issuance") ||
                    !long.TryParse(data["expiry"]?.ToString(), out var expiry) ||
                    !_issuanceStates.Bind(pendingId, data.Value<string>("requestId"), DateTimeOffset.FromUnixTimeSeconds(expiry)))
                { _issuanceStates.Unknown(pendingId); return StatusCode(502, new { error = "invalid_service_response", id = pendingId }); }
                return Ok(new { id = pendingId, url = data.Value<string>("url"), expiry, statusExpiry = expiry + 120, pin = pinCode });
            }
            catch (AyaStorageException) { throw; }
            catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "employee_not_eligible" }); }
            catch (InvalidOperationException) when (pendingId == null) { return Conflict(new { error = "incomplete_employee_profile" }); }
            catch (Exception)
            {
                if (pendingId != null) _issuanceStates.Unknown(pendingId);
                return StatusCode(502, new { error = "request_outcome_unknown", id = pendingId });
            }
        }

        private bool GetCredentialManifest( out string manifest, out string error) {
            error = null;
            if (!_cache.TryGetValue( "manifest", out manifest )) {
                string manifestUrl = _configuration["VerifiedID:CredentialManifest"];
                if (string.IsNullOrWhiteSpace( manifestUrl )) {
                    error = $"Manifest missing in config file";                    
                    return false;
                }
                var client = _httpClientFactory.CreateClient();
                HttpResponseMessage res = client.GetAsync( manifestUrl ).Result;
                string response = res.Content.ReadAsStringAsync().Result;
                if (res.StatusCode != HttpStatusCode.OK) {
                    error = $"HTTP status {(int)res.StatusCode} retrieving manifest from URL {manifestUrl}";
                    return false;
                }
                JObject resp = JObject.Parse( response );
                if ( resp.ContainsKey("token")) {
                    string jwtToken = resp["token"].ToString();
                    jwtToken = jwtToken.Replace( "_", "/" ).Replace( "-", "+" ).Split( "." )[1];
                    jwtToken = jwtToken.PadRight( 4 * ((jwtToken.Length + 3) / 4), '=' );
                    manifest = System.Text.Encoding.UTF8.GetString( Convert.FromBase64String( jwtToken ) );
                } else {
                    manifest = response;
                }
                _cache.Set( "manifest", manifest );
            }
            return true;
        }
        [HttpGet("/api/issuer/get-manifest")]
        public ActionResult getManifest() {
            if (!_policy.Ready) return StatusCode(503, new { error = "issuance_disabled" });
            try {
                if ( !GetCredentialManifest( out string manifest, out string errmsg ) ) {
                    _log.LogError("Credential request could not be completed.");
                    return BadRequest( new { error = "400", error_description = errmsg } );
                }
                return new ContentResult { ContentType = "application/json", Content = manifest };
            } catch (Exception) {
                return BadRequest(new { error = "400", error_description = "Request could not be completed" });
            }
        }

        [HttpGet( "/api/issuer/selfie-request" )]
        public ActionResult SelfieRequest() => NotFound();

        [HttpPost("/api/issuer/userphoto")]
        public ActionResult SetUserPhoto() => NotFound();

        //some helper functions
        protected string GetRequestHostName()
        {
            string scheme = "https";// : this.Request.Scheme;
            string originalHost = this.Request.Headers["x-original-host"];
            string hostname = "";
            if (!string.IsNullOrEmpty(originalHost))
                hostname = string.Format("{0}://{1}", scheme, originalHost);
            else hostname = string.Format("{0}://{1}", scheme, this.Request.Host);
            return hostname;
        }

        protected bool IsMobile()
        {
            string userAgent = this.Request.Headers["User-Agent"];
            return (userAgent != null && (userAgent.Contains("Android") || userAgent.Contains("iPhone")));
        }

        [NonAction]
        public IssuanceRequest CreateIssuanceRequest( out string pinCode ) {
            pinCode = null;
            IssuanceRequest request = new IssuanceRequest() {
                includeQRCode = _configuration.GetValue( "VerifiedID:includeQRCode", false ),
                authority = _configuration["VerifiedID:DidAuthority"],
                registration = new Registration() {
                    clientName = AyaCredentialPolicy.IssuerName,
                    purpose = _configuration.GetValue( "VerifiedID:purpose", "" )
                },
                callback = new Callback() {
                    url = new AyaRequestConfiguration(_configuration).CallbackOrigin() + "/api/issuer/issuecallback",
                    state = Guid.NewGuid().ToString(),
                    headers = new Dictionary<string, string>() { { "api-key", this._apiKey } }
                },
                type = AyaCredentialPolicy.CredentialType,
                manifest = _configuration["VerifiedID:CredentialManifest"],
                pin = null
            };
            if ("" == request.registration.purpose) {
                request.registration.purpose = null;
            }
            if (!IsMobile())
            {
                pinCode = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6");
                var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                SetPinCode(request, Sha256Hash(salt + pinCode), 6, salt);
            }
            request.expirationDate = _policy.Expiration(_time.GetUtcNow()).ToString("O");
            return request;
        }
        [NonAction]
        public IssuanceRequest SetPinCode( IssuanceRequest request, string pinCode = null, int pinCodeLength = 0, string salt = null) {
            if (string.IsNullOrWhiteSpace( pinCode )) {
                request.pin = null;
            } else {
                request.pin = new Pin() {
                    length = pinCodeLength,
                    value = pinCode
                };
                // if hashed pin code
                if ( !string.IsNullOrWhiteSpace( salt )) { 
                    request.pin.salt = salt;
                    request.pin.alg = "sha256";
                    request.pin.iterations = 1;
                }
            }
            return request;
        }

    }
}
