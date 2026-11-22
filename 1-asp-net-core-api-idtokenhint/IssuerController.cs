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
    // Aya Living: prevent credential issuance until
    // employee identity and authorization are verified.
    if (!_configuration.GetValue<bool>(
        "Aya:IssuanceEnabled", false))
    {
        return StatusCode(503, new
        {
            error = "issuance_disabled",
            message = "Aya employee ID issuance is not yet enabled."
        });
    }
    if (!_policy.ValidityDays.HasValue)
        return StatusCode(503, new { error = "validity_not_configured" });
    if (string.IsNullOrWhiteSpace(_apiKey))
{
    return StatusCode(503, new
    {
        error = "callback_not_configured"
    });
}

string ownerPrincipalId;
try { ownerPrincipalId = AyaRequestIdentity.RequireOwner(HttpContext); }
catch (UnauthorizedAccessException) { return Unauthorized(); }

            try
            {
                string manifestUrl = _configuration["VerifiedID:CredentialManifest"];
                if ( string.IsNullOrWhiteSpace(manifestUrl ) ) {
                    string errmsg = $"Manifest missing in config file";
                    _log.LogError("Credential request could not be completed.");
                    return BadRequest( new { error = "400", error_description = errmsg } );
                }
                string tenantId = _configuration["VerifiedID:TenantId"];
                string manifestTenantId = manifestUrl.Split("/")[5];
                if ( manifestTenantId != tenantId)
                {
                    string errmsg = "Credential configuration mismatch";
                    _log.LogError("Credential request could not be completed.");
                    return BadRequest(new { error = "400", error_description = errmsg });
                }

                try
                {
                    //The VC Request API is an authenticated API. We need to clientid and secret (or certificate) to create an access token which 
                    //needs to be send as bearer to the VC Request API
                    // Complete employee eligibility before acquiring an application token.
                    var claims = await _ayaClaims.GetClaimsAsync(HttpContext);
                    var accessToken = await _tokens.GetAccessToken();
                    if (accessToken.Item1 == String.Empty)
                    {
                        _log.LogError("Credential request could not be completed.");
                        return BadRequest(new { error = accessToken.error, error_description = "Token acquisition failed" });
                    }
                    IssuanceRequest request = CreateIssuanceRequest( out string pinCode );                    

                    // If the credential uses the idTokenHint attestation flow, then you must set the claims before
                    // calling the Request Service API
                    
request.claims = claims;


                    string jsonString = JsonConvert.SerializeObject( request, Newtonsoft.Json.Formatting.None, new JsonSerializerSettings {
                        NullValueHandling = NullValueHandling.Ignore
                    } );

                    var client = _httpClientFactory.CreateClient();
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.token);
                    string url = $"{_configuration["VerifiedID:ApiEndpoint"]}createIssuanceRequest";
                    HttpResponseMessage res = await client.PostAsync(url, new StringContent(jsonString, Encoding.UTF8, "application/json"));
                    string response = await res.Content.ReadAsStringAsync();

                    if (res.StatusCode == HttpStatusCode.Created)
                    {
                        JObject requestConfig = JObject.Parse(response);
                        if (!string.IsNullOrEmpty(pinCode) ) { 
                            requestConfig["pin"] = pinCode;
                        }
                        requestConfig.Add(new JProperty("id", request.callback.state));
                        jsonString = JsonConvert.SerializeObject(requestConfig);

                        if (!long.TryParse(requestConfig["expiry"]?.ToString(), out var expiry))
                            return StatusCode(502, new { error = "invalid_service_response" });
                        var expires = DateTimeOffset.FromUnixTimeSeconds(expiry);
                        var localExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(
                            _configuration.GetValue<int>("AppSettings:CacheExpiresInSeconds", 300), 1, 3600));
                        if (!_issuanceStates.Create(request.callback.state, ownerPrincipalId,
                            requestConfig.Value<string>("requestId"), expires < localExpiry ? expires : localExpiry))
                            return StatusCode(502, new { error = "invalid_service_response" });
                        return new ContentResult { ContentType = "application/json", Content = jsonString };
                    }
                    else
                    {
                        _log.LogError("Credential request could not be completed.");
                        return BadRequest(new { error = "400", error_description = "Credential request could not be completed" });
                    }
                }
                catch (UnauthorizedAccessException) { return StatusCode(403, new { error = "employee_not_eligible" }); }
                catch (InvalidOperationException) { return Conflict(new { error = "incomplete_employee_profile" }); }
                catch (HttpRequestException) { return StatusCode(502, new { error = "upstream_unavailable" }); }
                catch (Exception)
                {
                    return BadRequest(new { error = "400", error_description = "Credential request could not be completed" });
                }
            }
            catch (Exception)
            {
                return BadRequest(new { error = "400", error_description = "Request could not be completed" });
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
        public ActionResult SelfieRequest() {
            try {
                string hostname = GetRequestHostName();
                string id = Guid.NewGuid().ToString();
                var request = new {
                    id = id,
                    url = $"{hostname}/selfie.html?callbackUrl={hostname}/api/issuer/selfie/{id}",
                    expiry = DateTimeOffset.UtcNow.AddMinutes( 5 ).ToUnixTimeSeconds(),
                    photo = "",
                    status = "request_created"
                };
                string resp = _cache.Set( id, JsonConvert.SerializeObject( request )
                    , DateTimeOffset.Now.AddSeconds( _configuration.GetValue<int>( "AppSettings:CacheExpiresInSeconds", 300 ) ) );
                return new ContentResult { StatusCode = (int)HttpStatusCode.Created, ContentType = "application/json", Content = resp };
            } catch (Exception) {
                return BadRequest( new { error = "400", error_description = "Request could not be completed" } );
            }
        }

        [AllowAnonymous]
        [HttpPost( "/api/issuer/userphoto" )]
        public ActionResult SetUserPhoto() {
            try {
                string body = new System.IO.StreamReader( this.Request.Body ).ReadToEndAsync().Result;
                int idx = body.IndexOf( ";base64," );
                if (-1 == idx) {
                    return BadRequest( new { error = "400", error_description = $"Image must be 'data:image/jpeg;base64,'" } );
                }
                string photo = body.Substring( idx + 8 );
                string photoId = this.Request.Headers["rsid"];
                int cacheSeconds = _configuration.GetValue<int>( "AppSettings:CacheExpiresInSeconds", 300 );
                _cache.Set( photoId, photo, DateTimeOffset.Now.AddSeconds( cacheSeconds ) );
                return new ContentResult { StatusCode = (int)HttpStatusCode.Created, ContentType = "application/json"
                                , Content = JsonConvert.SerializeObject( new { id = photoId, message = $"Photo will be cached for {cacheSeconds} seconds" } ) };
            } catch (Exception) {
                return BadRequest( new { error = "400", error_description = "Request could not be completed" } );
            }
        }

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
                    url = $"{GetRequestHostName()}/api/issuer/issuecallback",
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
