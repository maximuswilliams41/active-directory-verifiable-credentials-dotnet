using System.Linq;

namespace AspNetCoreVerifiableCredentials
{
    public static class AyaPresentationPolicy
    {
        // A success event is not enough: every returned credential needs an explicit valid status.
        public static bool HasValidCredentialStatuses(CallbackEvent callback) =>
            callback?.verifiedCredentialsData?.Length > 0 &&
            callback.verifiedCredentialsData.All(c => c?.credentialState?.revocationStatus == "VALID");

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
