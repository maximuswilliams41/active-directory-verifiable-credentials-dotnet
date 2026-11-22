using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

// Retained only to preserve the original local filesystem regression suite. Not shipped in the application.
namespace AspNetCoreVerifiableCredentials
{
    // Single writer only. No claim of multi-instance or arbitrary network-filesystem safety.
    // The selected volume must support durable flush, atomic replacement and exclusive locks.
    public sealed class FileAyaRequestPersistence : IAyaRequestPersistence, IDisposable
    {
        private sealed class Snapshot
        {
            public int Version { get; set; } = 1;
            public string Checksum { get; set; }
            public Dictionary<string, AyaRequestRecord> Requests { get; set; } = new();
        }
        private readonly object _gate = new();
        private readonly string _path;
        private FileStream _lease;
        private bool _faulted;
        public FileAyaRequestPersistence(string path)
        {
            try
            {
                if (!Path.IsPathFullyQualified(path)) throw new IOException();
                _path = path;
                var directory = Path.GetDirectoryName(path);
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
                else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                bool initialized = File.Exists(path + ".lock");
                _lease = OpenPrivate(path + ".lock", FileMode.OpenOrCreate);
                if (!File.Exists(path))
                {
                    if (initialized) throw new IOException(); // never silently reset a lost journal
                    Save(new Snapshot());
                }
                Load();
            }
            catch { _lease?.Dispose(); throw new AyaStorageException(); }
        }
        private Snapshot Load()
        {
            if (_faulted || _lease == null || new FileInfo(_path).Length > 4 * 1024 * 1024) throw new IOException();
            var data = JsonConvert.DeserializeObject<Snapshot>(File.ReadAllText(_path), new JsonSerializerSettings { MaxDepth = 12 });
            if (data?.Version != 1 || data.Requests == null || data.Checksum != Checksum(data.Requests) || data.Requests.Count > 2000 || data.Requests.Any(p =>
                p.Value == null || p.Key != p.Value.Id || !Guid.TryParseExact(p.Key, "D", out _) ||
                string.IsNullOrWhiteSpace(p.Value.Owner) || p.Value.Pending == null || p.Value.Pending.Count > 4 ||
                (p.Value.Kind != "issuance" && p.Value.Kind != "presentation") ||
                p.Value.Created >= p.Value.AcceptUntil || p.Value.AcceptUntil > p.Value.RetainUntil)) throw new IOException();
            return data;
        }
        private static string Checksum(Dictionary<string, AyaRequestRecord> rows) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(rows))));
        private static FileStream OpenPrivate(string path, FileMode mode)
        {
            var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            return new FileStream(path, options);
        }
        private void Save(Snapshot data)
        {
            data.Checksum = Checksum(data.Requests);
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data));
            if (bytes.Length > 4 * 1024 * 1024) throw new IOException();
            using (var file = OpenPrivate(_path + ".next", FileMode.Create))
            { file.Write(bytes); file.Flush(true); }
            File.Move(_path + ".next", _path, true);
        }
        public T Read<T>(Func<Dictionary<string, AyaRequestRecord>, T> read)
        {
            lock (_gate)
            {
                try { return read(Load().Requests); }
                catch { _faulted = true; throw new AyaStorageException(); }
            }
        }
        public T Commit<T>(Func<Dictionary<string, AyaRequestRecord>, T> change)
        {
            lock (_gate)
            {
                try
                {
                    var data = Load(); var result = change(data.Requests);
                    Save(data); return result;
                }
                catch { _faulted = true; throw new AyaStorageException(); }
            }
        }
        public void Dispose() { lock (_gate) { _lease?.Dispose(); _lease = null; } }
    }
}
