using System;

namespace AspNetCoreVerifiableCredentials
{
    // Closed vocabulary only: never derive a diagnostic from a header, claim or exception message.
    public enum AyaEligibilityReason
    {
        CheckStarted, Eligible, IssuanceDisabled, ValidityUnconfigured,
        PrincipalMissing, PrincipalTooLarge, PrincipalMalformed,
        ProviderIdMissing, ProviderIdAmbiguous,
        ObjectIdMissing, ObjectIdInvalid, ObjectIdConflicting, RequiredRoleMissing,
        ConfiguredTenantInvalid, TenantClaimMissing, TenantClaimInvalid, TenantMismatch, TenantPrincipalMalformed,
        RegistryEmpty, RegistryNoMatch, RegistryInactive, RegistryInvalidEntry, RegistryDuplicateKey, RegistryDuplicateAccount,
        GraphTokenMissing, GraphLookupFailed, GraphObjectIdMismatch, GraphUserTypeNotMember,
        GraphAccountStateUnconfirmed, GraphAccountDisabled, CompanyMismatch,
        CompanyNameMissing, DisplayNameMissing, EmployeeIdMissing,
        AuthorizationDeniedUnclassified, ProfileInvalidUnclassified
    }

    // Keep the existing base exception types so all callers retain their response behavior.
    internal sealed class AyaEligibilityDeniedException : UnauthorizedAccessException
    {
        internal AyaEligibilityReason Reason { get; }
        internal AyaEligibilityDeniedException(AyaEligibilityReason reason) : base("Employee eligibility denied.") => Reason = reason;
    }

    internal sealed class AyaEligibilityIncompleteException : InvalidOperationException
    {
        internal AyaEligibilityReason Reason { get; }
        internal AyaEligibilityIncompleteException(AyaEligibilityReason reason) : base("Employee eligibility configuration or profile incomplete.") => Reason = reason;
    }
}
