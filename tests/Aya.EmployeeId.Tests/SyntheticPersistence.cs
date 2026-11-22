using AspNetCoreVerifiableCredentials;
using Newtonsoft.Json;
namespace Aya.EmployeeId.Tests;
// Test-only adapter. The application never falls back to volatile storage.
internal sealed class SyntheticPersistence : IAyaRequestPersistence
{
    private Dictionary<string,AyaRequestRecord> _rows = new();
    private readonly object _gate = new();
    public bool Fail;
    public T Read<T>(Func<Dictionary<string,AyaRequestRecord>,T> read) { lock (_gate) { if (Fail) throw new AyaStorageException(); return read(Clone()); } }
    public T Commit<T>(Func<Dictionary<string,AyaRequestRecord>,T> change) { lock (_gate) { if (Fail) throw new AyaStorageException(); var rows = Clone(); var result = change(rows); _rows = rows; return result; } }
    private Dictionary<string,AyaRequestRecord> Clone() => JsonConvert.DeserializeObject<Dictionary<string,AyaRequestRecord>>(JsonConvert.SerializeObject(_rows));
}
