using System.Security.Cryptography;
using System.Text;

namespace Aya.Revocation;

// These ports have no HTTP, identity, scheduler or cloud implementation in this milestone.
public interface IAccountDirectory
{
    Task<AccountScan> ScanAsync(CancellationToken cancellationToken);
}

public interface ICredentialRevocation
{
    Task<CredentialSearch> SearchAsync(CredentialContract contract, string indexHash, CancellationToken cancellationToken);
    Task<CredentialStatus> ReadStatusAsync(CredentialContract contract, string credentialId, CancellationToken cancellationToken);
    Task RevokeAsync(CredentialContract contract, string credentialId, CancellationToken cancellationToken);
}

public interface IAdministratorNotifications
{
    // Delivery is at least once. A future adapter must deduplicate by Id.
    Task SendAsync(AdministratorNotice notice, CancellationToken cancellationToken);
}

public enum AccountStatus { Unknown, Enabled, Disabled, Deleted }
public enum CredentialStatus { Unknown, Valid, Revoked }
public enum NoticeSeverity { Warning, TargetMissed, Critical }
public enum NoticeCode
{
    IncompleteScan, DirectoryUnavailable, ScanOverdue, MissingAccount, DeletedAccount,
    UnknownAccountState, AmbiguousIdentity, UnknownDisableTime, CredentialLookupFailed,
    EmptyCredentialSearch, IncompleteCredentialSearch, CredentialOperationFailed,
    VerificationPending, Target48Hours, Deadline72Hours
}

public sealed record AccountRecord(Guid TenantId, Guid ObjectId, AccountStatus Status);
public sealed record AccountScan(DateTimeOffset ObservedAt, bool IsComplete, IReadOnlyList<AccountRecord> Accounts);
public sealed record CredentialContract(string AuthorityId, string ContractId);
public sealed record CredentialSearch(bool IsComplete, IReadOnlyList<string> CredentialIds);

// Supplied from an explicitly approved registry, never inferred from name, email or a scan.
public sealed record EmployeeRegistration(Guid EmployeeKey, Guid TenantId, Guid ObjectId,
    IReadOnlyList<CredentialContract> Contracts);

public sealed record AdministratorNotice(Guid Id, Guid? JobId, NoticeCode Code,
    NoticeSeverity Severity, DateTimeOffset CreatedAt);

// Adapters may expose retry guidance, but never an upstream body, token or identity in its message.
public sealed class DependencyFailureException(bool retryable, TimeSpan? retryAfter = null)
    : Exception("Dependency operation failed.")
{
    public bool Retryable { get; } = retryable;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public static class EmployeeCredentialIndex
{
    public const string ClaimName = "ayaEmployeeKey";
    public static string ClaimValue(Guid employeeKey) => employeeKey != Guid.Empty
        ? employeeKey.ToString("D") : throw new ArgumentException("Employee key is required.");

    // Exact UTF-8 concatenation required by the Verified ID Admin API. URL encoding belongs in a future adapter.
    public static string Hash(string contractId, Guid employeeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(contractId + ClaimValue(employeeKey))));
    }
}

public sealed class LedgerDocument
{
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastCompleteScan { get; set; }
    public DateTimeOffset? NextScanAt { get; set; }
    public List<EmployeeEntry> Employees { get; set; } = [];
    public List<NoticeEntry> Notices { get; set; } = [];
}

public sealed class EmployeeEntry
{
    public required EmployeeRegistration Registration { get; set; }
    public DateTimeOffset? LastEnabledAt { get; set; }
    public NoticeCode? AccountProblem { get; set; }
    public RevocationJob? Job { get; set; }
}

public sealed class RevocationJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset DetectedAt { get; set; }
    // Conservative bound: last enabled observation, never the later detection time.
    public DateTimeOffset DeadlineAnchor { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public int Attempts { get; set; }
    public bool NeedsIntervention { get; set; }
    public List<CredentialEntry> Credentials { get; set; } = [];
}

public sealed class CredentialEntry
{
    public required CredentialContract Contract { get; set; }
    public required string CredentialId { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

public sealed class NoticeEntry
{
    public required AdministratorNotice Notice { get; set; }
    public bool Delivered { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
}
