using System;
using Microsoft.Extensions.Configuration;

namespace AspNetCoreVerifiableCredentials
{
    public sealed class AyaRequestConfiguration
    {
        private readonly IConfiguration _config;
        public AyaRequestConfiguration(IConfiguration config) => _config = config;
        public bool VerificationEnabled => _config.GetValue("Aya:VerificationEnabled", false);
        public string CallbackOrigin()
        {
            if (!Uri.TryCreate(_config["Aya:PublicOrigin"], UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "")
                throw new InvalidOperationException("Public origin must be configured.");
            return uri.GetLeftPart(UriPartial.Authority);
        }
        public bool Ready(bool issuance)
        {
            try
            {
                CallbackOrigin();
                if (string.IsNullOrWhiteSpace(_config["Aya:CallbackApiKey"]) || !AyaRequestStorageConfiguration.IsConfigured(_config) ||
                    !Guid.TryParse(_config["VerifiedID:TenantId"], out var tenant) || tenant == Guid.Empty ||
                    string.IsNullOrWhiteSpace(_config["VerifiedID:DidAuthority"])) return false;
                if (issuance)
                {
                    if (!Uri.TryCreate(_config["VerifiedID:CredentialManifest"], UriKind.Absolute, out var manifest) || manifest.Scheme != "https" ||
                        manifest.Host != "verifiedid.did.msidentity.com" || manifest.UserInfo != "" || manifest.Query != "" || manifest.Fragment != "" ||
                        !manifest.AbsolutePath.StartsWith("/v1.0/tenants/" + tenant.ToString("D") + "/verifiableCredentials/contracts/", StringComparison.Ordinal) ||
                        !manifest.AbsolutePath.EndsWith("/manifest", StringComparison.Ordinal)) return false;
                }
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }
        public string Owner(Microsoft.AspNetCore.Http.HttpContext context) =>
            AyaRequestIdentity.RequireTenant(context, _config["VerifiedID:TenantId"]).ToString("D") + ":" + AyaRequestIdentity.RequireOwner(context);
    }
}
