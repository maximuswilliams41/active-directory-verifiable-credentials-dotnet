using System.Text.Json;

namespace Aya.Revocation;

// Local synthetic infrastructure only. One machine, exclusive process lock, atomic file replacement.
// Not a shared cloud database or a guarantee against disk/power failure. Never store real identities here.
public sealed class LocalRevocationLedger
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public LocalRevocationLedger(string path) => _path = Path.GetFullPath(path);

    public static async Task<LocalRevocationLedger> CreateAsync(string path,
        IEnumerable<EmployeeRegistration> registrations, DateTimeOffset now)
    {
        var store = new LocalRevocationLedger(path);
        Directory.CreateDirectory(Path.GetDirectoryName(store._path)!);
        using var gate = store.Acquire();
        if (File.Exists(store._path)) throw new InvalidOperationException("Ledger already exists.");
        var document = new LedgerDocument
        {
            CreatedAt = now,
            Employees = registrations.Select(r => new EmployeeEntry { Registration = r }).ToList()
        };
        Validate(document);
        await store.SaveAsync(document);
        return store;
    }

    public async Task<LedgerDocument> ReadAsync()
    {
        using var gate = Acquire();
        return await LoadAsync();
    }

    internal async Task WithLockAsync(Func<LedgerDocument, Func<Task>, Task> action)
    {
        using var gate = Acquire(); // fail visibly if another worker owns this ledger; caller can retry
        var document = await LoadAsync();
        await action(document, () => SaveAsync(document));
    }

    private FileStream Acquire() => new(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private async Task<LedgerDocument> LoadAsync()
    {
        // Missing/corrupt ledgers must not be replaced with empty successful state.
        await using var stream = File.OpenRead(_path);
        var document = await JsonSerializer.DeserializeAsync<LedgerDocument>(stream, Json)
            ?? throw new InvalidDataException("Ledger is invalid.");
        Validate(document);
        return document;
    }

    private async Task SaveAsync(LedgerDocument document)
    {
        Validate(document);
        string temporary = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, document, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void Validate(LedgerDocument document)
    {
        if (document.Version != 1 || document.CreatedAt == default || document.Employees == null || document.Notices == null)
            throw new InvalidDataException("Unsupported or invalid ledger.");
        var keys = new HashSet<Guid>();
        var accounts = new HashSet<(Guid, Guid)>();
        foreach (var employee in document.Employees)
        {
            var r = employee.Registration;
            if (r == null || r.EmployeeKey == Guid.Empty || r.TenantId == Guid.Empty || r.ObjectId == Guid.Empty ||
                !keys.Add(r.EmployeeKey) || !accounts.Add((r.TenantId, r.ObjectId)) ||
                r.Contracts == null || r.Contracts.Count == 0 || r.Contracts.Distinct().Count() != r.Contracts.Count ||
                r.Contracts.Any(c => c == null || string.IsNullOrWhiteSpace(c.AuthorityId) || string.IsNullOrWhiteSpace(c.ContractId)))
                throw new InvalidDataException("Registry identities and contracts must be unambiguous and approved.");
            var job = employee.Job;
            if (job != null && (job.Id == Guid.Empty || job.DetectedAt == default || job.DeadlineAnchor > job.DetectedAt ||
                job.Credentials == null || job.Credentials.Any(c => !r.Contracts.Contains(c.Contract) || string.IsNullOrWhiteSpace(c.CredentialId))))
                throw new InvalidDataException("Revocation job is invalid.");
        }
    }
}
