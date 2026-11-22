using System;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace AspNetCoreVerifiableCredentials
{
    // Owner-managed configuration only. No employee input or on-demand assignment of keys.
    // Empty, ambiguous, invalid or inactive mappings fail closed. Never commit real registry entries.
    public sealed class AyaEmployeeRegistry
    {
        private sealed record Entry(Guid Tenant, Guid Account, Guid Key, bool Active);
        private readonly Entry[] _rows;
        public AyaEmployeeRegistry(IConfiguration configuration)
        {
            _rows = configuration.GetSection("Aya:EmployeeRegistry").GetChildren().Select(row => new Entry(
                Parse(row["TenantId"]), Parse(row["ObjectId"]), Parse(row["EmployeeKey"]),
                bool.TryParse(row["Active"], out var active) && active)).ToArray();
        }

        public string RequireActiveKey(Guid tenant, Guid account)
        {
            var rows = _rows;
            if (rows.Any(r => r.Tenant == Guid.Empty || r.Account == Guid.Empty || r.Key == Guid.Empty) ||
                rows.GroupBy(r => r.Key).Any(g => g.Count() != 1) ||
                rows.GroupBy(r => (r.Tenant, r.Account)).Any(g => g.Count() != 1))
                throw new InvalidOperationException("Employee registry requires administrator review.");
            var match = rows.SingleOrDefault(r => r.Tenant == tenant && r.Account == account);
            if (match == null || !match.Active) throw new UnauthorizedAccessException("Employee is not eligible.");
            return match.Key.ToString("D");
        }
        private static Guid Parse(string value) => Guid.TryParseExact(value, "D", out var id) ? id : Guid.Empty;
    }
}
