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
            using var diagnostic = new AyaIssuanceDiagnostic(_log, Response);
            ActionResult Done(AyaIssuanceReason reason, int status, ActionResult result)
            { diagnostic.Outcome(reason, status); return result; }
            string pendingId = null;
            void MarkUnknown()
            {
                diagnostic.Stage = AyaIssuanceStage.OutcomeRecording;
                try { _issuanceStates.Unknown(pendingId); }
                catch (AyaStorageException) { diagnostic.Outcome(AyaIssuanceReason.StorageUnavailable, 503); throw; }
            }
            if (!_policy.IssuanceEnabled) return Done(AyaIssuanceReason.IssuanceDisabled, 503, StatusCode(503, new { error = "issuance_disabled" }));
            if (!_policy.ValidityDays.HasValue) return Done(AyaIssuanceReason.ValidityUnconfigured, 503, StatusCode(503, new { error = "validity_not_configured" }));
            var configuration = new AyaRequestConfiguration(_configuration);
            if (!configuration.Ready(true)) return Done(AyaIssuanceReason.RequestNotConfigured, 503, StatusCode(503, new { error = "request_not_configured" }));
            diagnostic.Stage = AyaIssuanceStage.Identity;
            try { AyaRequestIdentity.RequireOwner(HttpContext); }
            catch (UnauthorizedAccessException) { return Done(AyaIssuanceReason.IdentityDenied, 401, Unauthorized()); }
            try
            {
                // Probe persistence before any remote operation, including the profile lookup.
                diagnostic.Stage = AyaIssuanceStage.StorageRead;
                _issuanceStates.CheckStorage();
                diagnostic.Stage = AyaIssuanceStage.EmployeeClaims;
                var claims = await _ayaClaims.GetClaimsAsync(HttpContext);
                var owner = configuration.Owner(HttpContext);
                diagnostic.Stage = AyaIssuanceStage.RequestCreation;
                var request = CreateIssuanceRequest(out string pinCode);
                diagnostic.Stage = AyaIssuanceStage.StateBegin;
                var begin = _issuanceStates.BeginWithResult(request.callback.state, owner, "issuance", request.authority);
                if (begin != AyaRequestBeginResult.Started)
                    return Done(begin switch {
                        AyaRequestBeginResult.AlreadyActive => AyaIssuanceReason.RequestAlreadyActive,
                        AyaRequestBeginResult.CapacityReached => AyaIssuanceReason.StateCapacityReached,
                        AyaRequestBeginResult.DuplicateId => AyaIssuanceReason.StateIdConflict,
                        _ => AyaIssuanceReason.StateArgumentsInvalid
                    }, 409, Conflict(new { error = "request_already_active" }));
                pendingId = request.callback.state;
                diagnostic.Stage = AyaIssuanceStage.TokenAcquisition;
                var accessToken = await _tokens.GetAccessToken();
                if (string.IsNullOrWhiteSpace(accessToken.token))
                {
                    MarkUnknown(); diagnostic.Stage = AyaIssuanceStage.TokenAcquisition;
                    return Done(AyaIssuanceReason.TokenUnavailable, 502, StatusCode(502, new { error = "token_unavailable", id = pendingId }));
                }
                request.claims = claims;
                diagnostic.Stage = AyaIssuanceStage.ServiceRequestPreparation;
                using var message = new HttpRequestMessage(HttpMethod.Post, $"{_configuration["VerifiedID:ApiEndpoint"]}createIssuanceRequest");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.token);
                message.Content = new StringContent(JsonConvert.SerializeObject(request, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }), Encoding.UTF8, "application/json");
                diagnostic.Stage = AyaIssuanceStage.ServiceRequest;
                using var response = await _httpClientFactory.CreateClient().SendAsync(message);
                diagnostic.ServiceHttpStatus = (int)response.StatusCode;
                diagnostic.Stage = AyaIssuanceStage.ServiceResponse;
                if (response.StatusCode != HttpStatusCode.Created)
                {
                    MarkUnknown(); diagnostic.Stage = AyaIssuanceStage.ServiceResponse;
                    return Done(AyaIssuanceReason.ServiceRejected, 400, BadRequest(new { error = "request_outcome_unknown", id = pendingId }));
                }
                var data = JObject.Parse(await response.Content.ReadAsStringAsync());
                diagnostic.Stage = AyaIssuanceStage.ResponseValidation;
                diagnostic.ValidationReason = AyaResponseValidationReason.UrlValueInvalid;
                var url = data.Value<string>("url");
                var linkReason = AyaResponseValidationReason.None;
                bool linkValid;
                try { linkValid = AyaRequestLinks.Valid(url, "issuance", out linkReason); }
                finally { diagnostic.ValidationReason = linkReason; }
                if (!linkValid)
                {
                    MarkUnknown(); diagnostic.Stage = AyaIssuanceStage.ResponseValidation;
                    return Done(AyaIssuanceReason.ResponseValidationFailed, 502, StatusCode(502, new { error = "invalid_service_response", id = pendingId }));
                }
                diagnostic.ValidationReason = data["expiry"] == null || data["expiry"].Type == JTokenType.Null
                    ? AyaResponseValidationReason.ExpiryMissing : AyaResponseValidationReason.ExpiryNotInteger;
                if (!long.TryParse(data["expiry"]?.ToString(), out var expiry))
                {
                    MarkUnknown(); diagnostic.Stage = AyaIssuanceStage.ResponseValidation;
                    return Done(AyaIssuanceReason.ResponseValidationFailed, 502, StatusCode(502, new { error = "invalid_service_response", id = pendingId }));
                }
                diagnostic.ValidationReason = AyaResponseValidationReason.ExpiryOutOfRange;
                var expires = DateTimeOffset.FromUnixTimeSeconds(expiry);
                diagnostic.ValidationReason = AyaResponseValidationReason.None;
                diagnostic.Stage = AyaIssuanceStage.StateBinding;
                if (!_issuanceStates.Bind(pendingId, data.Value<string>("requestId"), expires))
                {
                    MarkUnknown(); diagnostic.Stage = AyaIssuanceStage.StateBinding;
                    return Done(AyaIssuanceReason.StateBindingFailed, 502, StatusCode(502, new { error = "invalid_service_response", id = pendingId }));
                }
                return Done(AyaIssuanceReason.RequestCreated, 200, Ok(new { id = pendingId, url = data.Value<string>("url"), expiry, statusExpiry = expiry + 120, pin = pinCode }));
            }
            catch (AyaStorageException) { diagnostic.Outcome(AyaIssuanceReason.StorageUnavailable, 503); throw; }
            catch (UnauthorizedAccessException) { return Done(AyaIssuanceReason.EmployeeNotEligible, 403, StatusCode(403, new { error = "employee_not_eligible" })); }
            catch (InvalidOperationException) when (pendingId == null)
            {
                return Done(diagnostic.Stage == AyaIssuanceStage.EmployeeClaims ? AyaIssuanceReason.IncompleteEmployeeProfile : AyaIssuanceReason.PreSubmissionConflict,
                    409, Conflict(new { error = "incomplete_employee_profile" }));
            }
            catch (Exception)
            {
                var stage = diagnostic.Stage;
                var reason = stage switch {
                    AyaIssuanceStage.TokenAcquisition => AyaIssuanceReason.TokenAcquisitionFailed,
                    AyaIssuanceStage.ServiceRequestPreparation => AyaIssuanceReason.ServiceRequestPreparationFailed,
                    AyaIssuanceStage.ServiceRequest => AyaIssuanceReason.ServiceTransportFailed,
                    AyaIssuanceStage.ServiceResponse => AyaIssuanceReason.ServiceResponseFailed,
                    AyaIssuanceStage.ResponseValidation => AyaIssuanceReason.ResponseValidationFailed,
                    AyaIssuanceStage.StateBinding => AyaIssuanceReason.StateBindingFailed,
                    AyaIssuanceStage.EmployeeClaims => AyaIssuanceReason.EmployeeClaimsFailed,
                    _ => AyaIssuanceReason.UnexpectedFailure
                };
                if (pendingId != null) MarkUnknown();
                diagnostic.Stage = stage;
                return Done(reason, 502, StatusCode(502, new { error = "request_outcome_unknown", id = pendingId }));
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
            // Omit expirationDate so the selected contract's validityInterval governs expiry.
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
