using System;
using Microsoft.Extensions.Configuration;

namespace AspNetCoreVerifiableCredentials
{
    public sealed class AyaCredentialPolicy
    {
        public const string CredentialType = "AyaEmployeeId";
        public const string IssuerName = "Aya Living";
        private readonly IConfiguration _configuration;
        public AyaCredentialPolicy(IConfiguration configuration) => _configuration = configuration;
        // No implicit validity: the owner must choose an exact day count and configure the contract consistently.
        public int? ValidityDays => int.TryParse(_configuration["Aya:CredentialValidityDays"], out var days)
            && days >= 1 && days <= 366 ? days : null;
        public bool IssuanceEnabled => _configuration.GetValue("Aya:IssuanceEnabled", false);
        public bool Ready => IssuanceEnabled && ValidityDays.HasValue;
        public DateTimeOffset Expiration(DateTimeOffset issuedAt) => issuedAt.AddDays(
            ValidityDays ?? throw new InvalidOperationException("Credential validity requires owner configuration."));
    }
}
