using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Identity.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Security.Policy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Azure.Core;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;

namespace AspNetCoreVerifiableCredentials
{
    [Route("api/[action]")]
    [ApiController]
    public class CallbackController : Controller
    {
        private enum RequestType {
            Unknown,
            Presentation,
            Issuance,
            Selfie
        };
        protected IMemoryCache _cache;
        protected readonly ILogger<CallbackController> _log;
        private string _apiKey;
        private IConfiguration _configuration;
        private readonly AyaIssuanceStateStore _issuanceStates;
        public CallbackController(IConfiguration configuration, IMemoryCache memoryCache, ILogger<CallbackController> log, AyaIssuanceStateStore issuanceStates)
        {
            _configuration = configuration;
            _issuanceStates = issuanceStates;
            _cache = memoryCache;
            _log = log;
            
_apiKey = _configuration["Aya:CallbackApiKey"];

        }

        private async Task<ActionResult> HandleRequestCallback(
    RequestType requestType, string body ) {
            try {
                
if (requestType != RequestType.Selfie)
{
    string suppliedKey =
        Request.Headers["api-key"].ToString();

    if (string.IsNullOrWhiteSpace(_apiKey) ||
        string.IsNullOrWhiteSpace(suppliedKey))
    {
        return Unauthorized();
    }

    byte[] expected = SHA256.HashData(
        Encoding.UTF8.GetBytes(_apiKey));

    byte[] received = SHA256.HashData(
        Encoding.UTF8.GetBytes(suppliedKey));

    if (!CryptographicOperations.FixedTimeEquals(
        expected, received))
    {
        return Unauthorized();
    }
}

                if ( body == null) {
                    body = await new System.IO.StreamReader( this.Request.Body ).ReadToEndAsync();
                }

                bool rc = false;
                string errorMessage = null;
                List<string> presentationStatus = new List<string>() { "request_retrieved", "presentation_verified", "presentation_error" };
                List<string> issuanceStatus = new List<string>() { "request_retrieved", "issuance_successful", "issuance_error" };
                List<string> selfieStatus = new List<string>() { "selfie_taken" };

                CallbackEvent callback = JsonConvert.DeserializeObject<CallbackEvent>(body,
                    new JsonSerializerSettings { MaxDepth = 16 });
                if (requestType == RequestType.Issuance)
                    return _issuanceStates.Apply(callback) ? Ok() : BadRequest(new { error = "invalid_callback" });
                if (callback == null || string.IsNullOrWhiteSpace(callback.state))
                    return BadRequest(new { error = "invalid_callback" });

                if (   (requestType == RequestType.Presentation && presentationStatus.Contains( callback.requestStatus ))
                    || (requestType == RequestType.Issuance && issuanceStatus.Contains( callback.requestStatus ))
                    || (requestType == RequestType.Selfie && selfieStatus.Contains( callback.requestStatus ))) {
                    if (!_cache.TryGetValue( callback.state, out string requestState )) {
                        errorMessage = $"Invalid state '{callback.state}'";
                    } else {
                        JObject reqState = JObject.Parse( requestState );
                        reqState["status"] = callback.requestStatus;
                        if (reqState.ContainsKey( "callback" )) {
                            reqState["callback"] = body;
                        } else {
                            reqState.Add( "callback", body );
                        }
                        _cache.Set( callback.state, JsonConvert.SerializeObject( reqState )
                            , DateTimeOffset.Now.AddSeconds( _configuration.GetValue<int>( "AppSettings:CacheExpiresInSeconds", 300 ) ) );
                        rc = true;
                    }
                } else {
                    errorMessage = $"Unknown request status '{callback.requestStatus}'";
                }
                if (!rc ) {
                    return BadRequest( new { error = "400", error_description = errorMessage } );
                }
                return new OkResult();
            } catch (Exception) {
                return BadRequest( new { error = "400", error_description = "Request could not be completed" } );
            }
        }

        [AllowAnonymous]
        [HttpPost( "/api/issuer/issuecallback" )]
        [RequestSizeLimit(16384)]
        public async Task<ActionResult> IssuanceCallback() {
            return await HandleRequestCallback( RequestType.Issuance, null );
        }

        [AllowAnonymous]
        [HttpPost( "/api/verifier/presentationcallback" )]
        public async Task<ActionResult> PresentationCallback() {
            return await HandleRequestCallback( RequestType.Presentation, null );
        }

        [HttpGet("/api/request-status")]
        public ActionResult RequestStatus()
        {
            string owner;
            try { owner = AyaRequestIdentity.RequireOwner(HttpContext); }
            catch (UnauthorizedAccessException) { return Unauthorized(); }
            if (!_issuanceStates.TryGetStatus(Request.Query["id"], owner, out var status))
                return NotFound();
            string message = status switch
            {
                "issuance_successful" => "Issuance successful",
                "issuance_error" => "Issuance failed",
                "request_retrieved" => "QR code is scanned. Waiting for user action...",
                _ => "Waiting to scan QR code"
            };
            return Ok(new { status, message });
        }

        [NonAction]
        public bool PollRequestStatus( out JObject result ) {
            result = null;
            string state = this.Request.Query["id"];
            if (string.IsNullOrEmpty( state )) {
                result = JObject.FromObject( new { status = "error", message = "Missing argument 'id'" } );
                return false;
            }
            bool rc = true;
            if (_cache.TryGetValue( state, out string requestState )) {
                JObject reqState = JObject.Parse(requestState);
                string requestStatus = reqState["status"].ToString();
                CallbackEvent callback = null;
                switch ( requestStatus ) {
                    case "request_created":
                        result = JObject.FromObject( new { status = requestStatus, message = "Waiting to scan QR code" } );
                        break;
                    case "request_retrieved":
                        result = JObject.FromObject( new { status = requestStatus, message = "QR code is scanned. Waiting for user action..." } );
                        break;
                    case "issuance_error":
                        callback = JsonConvert.DeserializeObject<CallbackEvent>( reqState["callback"].ToString() );
                        result = JObject.FromObject( new { status = requestStatus, message = "Issuance failed: " + callback.error.message } );
                        break;
                    case "issuance_successful":
                        result = JObject.FromObject( new { status = requestStatus, message = "Issuance successful" } );
                        break;
                    case "presentation_error":
                        callback = JsonConvert.DeserializeObject<CallbackEvent>( reqState["callback"].ToString() );                        
                        result = JObject.FromObject( new { status = requestStatus, message = "Presentation failed:" + callback.error.message } );
                        break;
                    case "presentation_verified":
                        callback = JsonConvert.DeserializeObject<CallbackEvent>(reqState["callback"].ToString() );
                        JObject resp = JObject.Parse( JsonConvert.SerializeObject( new {
                                                                                    status = requestStatus,
                                                                                    message = "Presentation verified",
                                                                                    type = callback.verifiedCredentialsData[0].type,
                                                                                    claims = callback.verifiedCredentialsData[0].claims,
                                                                                    subject = callback.subject,
                                                                                    payload = callback.verifiedCredentialsData,
                                                                                }, Newtonsoft.Json.Formatting.None, new JsonSerializerSettings {
                                                                                                                NullValueHandling = NullValueHandling.Ignore
                                                                            } ) );
                        if (null != callback.receipt && null != callback.receipt.vp_token ) {
                            JObject vpToken = GetJsonFromJwtToken( callback.receipt.vp_token[0] );
                            JObject vc = GetJsonFromJwtToken( vpToken["vp"]["verifiableCredential"][0].ToString() );
                            resp.Add( new JProperty( "jti", vc["jti"].ToString() ) );
                        }
                        if (!string.IsNullOrWhiteSpace( callback.verifiedCredentialsData[0].expirationDate )) {
                            resp.Add( new JProperty( "expirationDate", callback.verifiedCredentialsData[0].expirationDate ) );
                        }
                        if (!string.IsNullOrWhiteSpace( callback.verifiedCredentialsData[0].issuanceDate )) {
                            resp.Add( new JProperty( "issuanceDate ", callback.verifiedCredentialsData[0].issuanceDate ) );
                        }
                        result = resp;
                        break;
                    case "selfie_taken":
                        callback = JsonConvert.DeserializeObject<CallbackEvent>( reqState["callback"].ToString() );
                        result = JObject.FromObject( new { status = requestStatus, message = "Selfie taken", photo = callback.photo } );
                        break;
                    default:
                        result = JObject.FromObject( new { status = "error", message = $"Invalid requestStatus '{requestStatus}'" } );
                        rc = false;
                        break;
                }
            } else {
                result = JObject.FromObject( new { status = "request_not_created", message = "No data" } );
                rc = false;
            }
            return rc;
        }

        [AllowAnonymous]
        [HttpPost( "/api/issuer/selfie/{id}" )]
        public async Task<ActionResult> setSelfie( string id ) {
            try {
                string body = new System.IO.StreamReader( this.Request.Body ).ReadToEnd();
                string dataImage = "data:image/jpeg;base64,";
                int idx = body.IndexOf( ";base64," );
                if (-1 == idx) {
                    return BadRequest( new { error = "400", error_description = $"Image must be {dataImage}" } );
                }
                string photo = body.Substring( idx + 8 );
                CallbackEvent callback = new CallbackEvent() {
                    requestId = id,
                    state = id,
                    requestStatus = "selfie_taken",
                    photo = photo
                };
                return await HandleRequestCallback( RequestType.Selfie, JsonConvert.SerializeObject( callback ) );
            } catch (Exception) {
                return BadRequest( new { error = "400", error_description = "Request could not be completed" } );
            }
        }

        public JObject GetJsonFromJwtToken(string jwtToken)
        {            
            jwtToken = jwtToken.Replace("_", "/").Replace("-", "+").Split(".")[1];
            jwtToken = jwtToken.PadRight(4 * ((jwtToken.Length + 3) / 4), '=');
            return JObject.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(jwtToken)) );
        }

    } // cls
} // ns
