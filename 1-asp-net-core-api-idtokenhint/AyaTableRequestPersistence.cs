using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Azure;
using Azure.Data.Tables;
using Newtonsoft.Json;

namespace AspNetCoreVerifiableCredentials
{
    // Small mockable boundary. Implementations must use primary-endpoint, strongly consistent reads,
    // insert-only chunks and conditional Replace (never Upsert / ETag.All) for the publication pointer.
    public interface IAyaTableOperations
    {
        TableEntity Get(string partition, string row, CancellationToken cancellation);
        void Add(TableEntity entity, CancellationToken cancellation);
        void Replace(TableEntity entity, ETag expected, CancellationToken cancellation);
    }

    public sealed class AzureAyaTableOperations : IAyaTableOperations
    {
        private readonly TableClient _client;
        public AzureAyaTableOperations(TableClient client) => _client = client;
        public TableEntity Get(string partition, string row, CancellationToken cancellation) =>
            _client.GetEntity<TableEntity>(partition, row, cancellationToken: cancellation).Value;
        public void Add(TableEntity entity, CancellationToken cancellation) => _client.AddEntity(entity, cancellation);
        public void Replace(TableEntity entity, ETag expected, CancellationToken cancellation) =>
            _client.UpdateEntity(entity, expected, TableUpdateMode.Replace, cancellation);
    }

    // Keeps the original whole-dictionary transaction contract. Immutable chunks are staged first;
    // one conditional head replacement is the linearization point for ALL records in the snapshot.
    // No initialization, migration, table creation, deletion or volatile/file fallback at runtime.
    public sealed class AyaTableRequestPersistence : IAyaRequestPersistence
    {
        public const int ChunkBytes = 48 * 1024; // below the 64 KiB binary-property limit
        public const int MaxBytes = 4 * 1024 * 1024;
        public const int ConflictAttempts = 3;
        private readonly IAyaTableOperations _table;
        private readonly string _partition;
        private readonly object _gate = new();
        private static readonly JsonSerializerSettings JsonSettings = new() { MaxDepth = 12, DateParseHandling = DateParseHandling.DateTimeOffset };

        public AyaTableRequestPersistence(IAyaTableOperations table, string partition)
        {
            if (table == null || !Guid.TryParseExact(partition, "D", out var id) || id == Guid.Empty || partition != id.ToString("D"))
                throw new AyaStorageException();
            _table = table; _partition = partition;
        }

        public T Read<T>(Func<Dictionary<string, AyaRequestRecord>, T> read)
        {
            lock (_gate)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { return read(Load(deadline.Token).Rows); }
                catch { throw new AyaStorageException(); }
            }
        }

        public T Commit<T>(Func<Dictionary<string, AyaRequestRecord>, T> change)
        {
            lock (_gate)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    for (int attempt = 0; attempt < ConflictAttempts; attempt++)
                    {
                        var current = Load(deadline.Token);
                        // Delegates are pure local state transformations; a definitive ETag conflict
                        // re-evaluates them against fresh state, never repeats an external operation.
                        var result = change(current.Rows);
                        Validate(current.Rows);
                        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(current.Rows));
                        if (bytes.Length > MaxBytes) throw new IOException();
                        if (current.Bytes.SequenceEqual(bytes)) return result;
                        string generation = Guid.NewGuid().ToString("N");
                        int count = (bytes.Length + ChunkBytes - 1) / ChunkBytes;
                        for (int i = 0; i < count; i++)
                        {
                            var chunk = new TableEntity(_partition, ChunkKey(generation, i)) {
                                ["Data"] = bytes.Skip(i * ChunkBytes).Take(ChunkBytes).ToArray()
                            };
                            _table.Add(chunk, deadline.Token);
                        }
                        var head = new TableEntity(_partition, "head") {
                            ["Schema"] = 1, ["Generation"] = generation, ["Chunks"] = count,
                            ["Length"] = bytes.Length, ["Hash"] = Hash(bytes)
                        };
                        try { _table.Replace(head, current.Head.ETag, deadline.Token); return result; }
                        catch (RequestFailedException ex) when (ex.Status == 412)
                        {
                            // Only a definitive precondition failure is safe to replay. A timeout,
                            // disconnect or 5xx may have committed; fail closed and let a later read
                            // recover the authoritative head. Never undo a possibly committed write.
                        }
                    }
                    throw new IOException();
                }
                catch { throw new AyaStorageException(); }
            }
        }

        private (TableEntity Head, byte[] Bytes, Dictionary<string, AyaRequestRecord> Rows) Load(CancellationToken cancellation)
        {
            var head = _table.Get(_partition, "head", cancellation);
            if (head.PartitionKey != _partition || head.RowKey != "head" || string.IsNullOrWhiteSpace(head.ETag.ToString()) ||
                head.ETag == ETag.All || head.GetInt32("Schema") != 1) throw new IOException();
            var generation = head.GetString("Generation");
            int count = head.GetInt32("Chunks") ?? -1, length = head.GetInt32("Length") ?? -1;
            byte[] bytes;
            if (generation == "initial" && count == 0 && length == 0 && head.GetString("Hash") == "")
                bytes = Encoding.UTF8.GetBytes("{}"); // explicit owner-provisioned bootstrap marker only
            else
            {
                if (!Guid.TryParseExact(generation, "N", out var id) || id == Guid.Empty || generation != id.ToString("N") ||
                    length < 2 || length > MaxBytes || count != (length + ChunkBytes - 1) / ChunkBytes) throw new IOException();
                using var payload = new MemoryStream(length);
                for (int i = 0; i < count; i++)
                {
                    var chunk = _table.Get(_partition, ChunkKey(generation, i), cancellation);
                    var data = chunk.GetBinary("Data");
                    if (chunk.PartitionKey != _partition || chunk.RowKey != ChunkKey(generation, i) || data == null ||
                        data.Length != Math.Min(ChunkBytes, length - i * ChunkBytes)) throw new IOException();
                    payload.Write(data);
                }
                bytes = payload.ToArray();
                if (Hash(bytes) != head.GetString("Hash")) throw new IOException();
            }
            var rows = JsonConvert.DeserializeObject<Dictionary<string, AyaRequestRecord>>(Encoding.UTF8.GetString(bytes), JsonSettings);
            Validate(rows);
            return (head, bytes, rows);
        }

        private static string ChunkKey(string generation, int index) => generation + "-" + index.ToString("D3");
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        private static void Validate(Dictionary<string, AyaRequestRecord> rows)
        {
            if (rows == null || rows.Count > 2000 || rows.Any(p => p.Value == null || p.Key != p.Value.Id ||
                !Guid.TryParseExact(p.Key, "D", out var id) || id == Guid.Empty || string.IsNullOrWhiteSpace(p.Value.Owner) ||
                p.Value.Pending == null || p.Value.Pending.Count > 4 ||
                (p.Value.Kind != "issuance" && p.Value.Kind != "presentation") ||
                p.Value.Created >= p.Value.AcceptUntil || p.Value.AcceptUntil > p.Value.RetainUntil)) throw new IOException();
        }
    }
}
