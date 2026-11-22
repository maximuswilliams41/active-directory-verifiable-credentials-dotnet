using System.Linq;
using System;
using System.Globalization;

namespace AspNetCoreVerifiableCredentials
{
    public static class AyaPresentationPolicy
    {
        // A success event is not enough: every returned credential needs an explicit valid status.
        public static bool HasValidCredentialStatuses(CallbackEvent callback) =>
            callback?.verifiedCredentialsData?.Length > 0 &&
            callback.verifiedCredentialsData.All(c => c?.credentialState?.revocationStatus == "VALID");

        public static bool IsApproved(CallbackEvent callback, string issuer, DateTimeOffset now, out DateTimeOffset expires)
        {
            expires = default;
            if (!HasValidCredentialStatuses(callback) || callback.verifiedCredentialsData.Length != 1) return false;
            var credential = callback.verifiedCredentialsData[0];
            if (credential.issuer != issuer || credential.type == null || !credential.type.Contains(AyaCredentialPolicy.CredentialType) ||
                credential.claims == null || new[] { "displayName", "employeeId", "companyName", "ayaEmployeeKey" }.Any(k =>
                    !credential.claims.TryGetValue(k, out var value) || string.IsNullOrWhiteSpace(value) || value.Length > 256) ||
                !string.Equals(credential.claims["companyName"], "Aya Living Inc.", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(credential.claims["ayaEmployeeKey"], "D", out var key) || key == Guid.Empty ||
                credential.claims["ayaEmployeeKey"] != key.ToString("D")) return false;
            return DateTimeOffset.TryParse(credential.expirationDate, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out expires) && expires > now;
        }

        public static void RequireRevocationChecks(PresentationRequest request)
        {
            if (request?.requestedCredentials == null) return;
            foreach (var credential in request.requestedCredentials)
            {
                credential.configuration ??= new Configuration();
                credential.configuration.validation ??= new Validation { validateLinkedDomain = true };
                credential.configuration.validation.allowRevoked = false;
            }
        }
    }
}
