using System.Net;
using System.Text;
using AspNetCoreVerifiableCredentials;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Aya.EmployeeId.Tests;

public sealed class TablePersistenceTests
{
    internal const string Ledger = "55555555-5555-4555-8555-555555555555";
    private static string Id() => Guid.NewGuid().ToString("D");
    private static AyaTableRequestPersistence Adapter(FakeTable table) => new(table, Ledger);
    private static AyaIssuanceStateStore Store(FakeTable table, TimeProvider clock = null) => new(Adapter(table), clock ?? TimeProvider.System);
    private static CallbackEvent Issued(string id) => new() { state = id, requestId = "synthetic-request", requestStatus = "issuance_successful" };

    // All operations stay in memory, including authentication/HTTP integration tests below.
    internal sealed class FakeTable : IAyaTableOperations
    {
        private readonly object _gate = new();
        public readonly Dictionary<string, TableEntity> Entities = new();
        public Action BeforeReplace;
        public Action<string> BeforeGet;
        public bool FailReads, LoseReplaceResponse;
        public int ReplaceStatus, FailAddAt, Adds, Replaces;
        private int _revision;
        public FakeTable() => Entities["head"] = new TableEntity(Ledger, "head") {
            ETag = new ETag("synthetic-0"), ["Schema"] = 1, ["Generation"] = "initial", ["Chunks"] = 0, ["Length"] = 0, ["Hash"] = ""
        };
        private static TableEntity Clone(TableEntity e)
        {
            var clone = new TableEntity(e.PartitionKey, e.RowKey) { ETag = e.ETag };
            foreach (var pair in e) clone[pair.Key] = pair.Value is byte[] bytes ? bytes.ToArray() : pair.Value;
            return clone;
        }
        public TableEntity Get(string partition, string row, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); BeforeGet?.Invoke(row);
            lock (_gate) {
                if (FailReads) throw new RequestFailedException(503, "synthetic-private-storage-detail");
                if (partition != Ledger || !Entities.TryGetValue(row, out var entity)) throw new RequestFailedException(404, "missing");
                return Clone(entity);
            }
        }
        public void Add(TableEntity entity, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            lock (_gate) {
                Adds++;
                if (Adds == FailAddAt) throw new IOException("synthetic interrupted stage");
                Assert.Equal(Ledger, entity.PartitionKey); Assert.InRange(entity.GetBinary("Data").Length, 1, AyaTableRequestPersistence.ChunkBytes);
                if (!Entities.TryAdd(entity.RowKey, Clone(entity))) throw new RequestFailedException(409, "already exists");
            }
        }
        public void Replace(TableEntity entity, ETag expected, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); BeforeReplace?.Invoke();
            lock (_gate) {
                Replaces++; Assert.NotEqual(ETag.All, expected); Assert.Equal("head", entity.RowKey);
                if (ReplaceStatus != 0) throw new RequestFailedException(ReplaceStatus, "synthetic-private-storage-detail");
                if (!Entities.TryGetValue("head", out var head)) throw new RequestFailedException(404, "missing");
                if (head.ETag != expected) throw new RequestFailedException(412, "conflict");
                entity = Clone(entity); entity.ETag = new ETag("synthetic-" + ++_revision); Entities["head"] = entity;
                if (LoseReplaceResponse) throw new IOException("synthetic lost acknowledgement");
            }
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Early_and_duplicate_callbacks_survive_adapter_restart_and_remain_owner_bound()
    {
        var table = new FakeTable(); var id = Id(); var store = Store(table);
        Assert.True(store.Begin(id, "synthetic-owner", "issuance", "did:example:synthetic"));
        Assert.True(store.Apply(Issued(id))); Assert.True(store.Apply(Issued(id)));
        store = Store(table);
        Assert.True(store.TryGetStatus(id, "synthetic-owner", out var pending)); Assert.Equal("request_pending", pending);
        Assert.False(store.TryGetStatus(id, "other-owner", out _));
        Assert.True(store.Bind(id, "synthetic-request", DateTimeOffset.UtcNow.AddMinutes(5)));
        store = Store(table); Assert.True(store.Apply(Issued(id)));
        Assert.True(store.TryGetStatus(id, "synthetic-owner", out var status)); Assert.Equal("issuance_successful", status);
        var wrong = Issued(id); wrong.requestId = "wrong"; Assert.False(store.Apply(wrong));
    }

    [Fact]
    public void Conflict_rechecks_the_active_owner_guard_and_never_overwrites_the_winner()
    {
        var table = new FakeTable(); var winner = Id(); var loser = Id();
        table.BeforeReplace = () => { table.BeforeReplace = null; Assert.True(Store(table).Begin(winner, "same-owner", "issuance", "did:example:synthetic")); };
        Assert.False(Store(table).Begin(loser, "same-owner", "issuance", "did:example:synthetic"));
        Assert.True(Store(table).TryGetStatus(winner, "same-owner", out _));
        Assert.False(Store(table).TryGetStatus(loser, "same-owner", out _));
        Assert.Single(Adapter(table).Read(rows => rows));
    }

    [Fact]
    public void Conflict_retries_merge_an_independent_request_without_lost_updates()
    {
        var table = new FakeTable(); var winner = Id(); var other = Id();
        table.BeforeReplace = () => { table.BeforeReplace = null; Assert.True(Store(table).Begin(winner, "first-owner", "issuance", "did:example:synthetic")); };
        Assert.True(Store(table).Begin(other, "second-owner", "issuance", "did:example:synthetic"));
        Assert.Equal(2, Adapter(table).Read(rows => rows.Count)); Assert.Equal(3, table.Replaces);
    }

    [Fact]
    public void Conflicts_are_bounded_and_cannot_publish_a_partial_result()
    {
        var table = new FakeTable { ReplaceStatus = 412 };
        Assert.Throws<AyaStorageException>(() => Store(table).Begin(Id(), "owner", "issuance", "did:example:synthetic"));
        Assert.Equal(AyaTableRequestPersistence.ConflictAttempts, table.Replaces);
        Assert.Empty(Adapter(table).Read(rows => rows));
    }

    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(409)] [InlineData(429)] [InlineData(500)] [InlineData(503)]
    public void Nonconflict_failures_are_not_blindly_retried_or_reported_as_committed(int status)
    {
        var table = new FakeTable { ReplaceStatus = status };
        var ex = Assert.Throws<AyaStorageException>(() => Store(table).Begin(Id(), "owner", "issuance", "did:example:synthetic"));
        Assert.DoesNotContain("synthetic-private", ex.ToString()); Assert.Equal(1, table.Replaces);
        Assert.Empty(Adapter(table).Read(rows => rows));
    }

    [Fact]
    public void Lost_commit_acknowledgement_recovers_pending_request_and_blocks_repeat_submission()
    {
        var table = new FakeTable { LoseReplaceResponse = true }; var id = Id();
        Assert.Throws<AyaStorageException>(() => Store(table).Begin(id, "owner", "issuance", "did:example:synthetic"));
        table.LoseReplaceResponse = false; var recovered = Store(table);
        Assert.True(recovered.TryGetStatus(id, "owner", out var status)); Assert.Equal("request_pending", status);
        Assert.False(recovered.Begin(Id(), "owner", "issuance", "did:example:synthetic"));
        Assert.Equal(1, table.Replaces);
    }

    [Fact]
    public void Lost_terminal_acknowledgement_can_be_safely_acknowledged_on_duplicate_callback()
    {
        var table = new FakeTable(); var id = Id(); var store = Store(table);
        Assert.True(store.Begin(id, "owner", "issuance", "did:example:synthetic"));
        Assert.True(store.Bind(id, "synthetic-request", DateTimeOffset.UtcNow.AddMinutes(5)));
        table.LoseReplaceResponse = true; Assert.Throws<AyaStorageException>(() => store.Apply(Issued(id)));
        table.LoseReplaceResponse = false; Assert.True(Store(table).Apply(Issued(id)));
        Assert.True(Store(table).TryGetStatus(id, "owner", out var status)); Assert.Equal("issuance_successful", status);
    }

    [Fact]
    public void Incomplete_multichunk_stage_is_invisible_and_old_committed_state_survives()
    {
        var table = new FakeTable(); var id = Id(); Store(table).Begin(id, "owner", "issuance", "did:example:synthetic");
        var previous = table.Entities["head"].ETag; table.FailAddAt = table.Adds + 2;
        Assert.Throws<AyaStorageException>(() => Adapter(table).Commit(rows => { rows[id].Owner = new string('s', 150000); return true; }));
        Assert.Equal(previous, table.Entities["head"].ETag);
        Assert.True(Store(table).TryGetStatus(id, "owner", out var status)); Assert.Equal("request_pending", status);
    }

    [Fact]
    public void Multi_record_publication_is_atomic_and_chunk_sizes_stay_within_service_limits()
    {
        var table = new FakeTable(); var first = Id(); var second = Id();
        Store(table).Begin(first, "first-owner", "issuance", "did:example:synthetic");
        Store(table).Begin(second, "second-owner", "issuance", "did:example:synthetic");
        var before = table.Entities["head"].ETag;
        Adapter(table).Commit(rows => { rows[first].Issuer = new string('a', 100000); rows[second].Issuer = new string('b', 100000); return true; });
        Assert.NotEqual(before, table.Entities["head"].ETag);
        Assert.True(table.Entities["head"].GetInt32("Chunks") > 1);
        var recovered = Adapter(table).Read(rows => rows);
        Assert.Equal(100000, recovered[first].Issuer.Length); Assert.Equal(100000, recovered[second].Issuer.Length);
    }

    [Theory]
    [InlineData("head")] [InlineData("chunk")] [InlineData("checksum")] [InlineData("version")] [InlineData("count")] [InlineData("etag")]
    public void Missing_or_damaged_committed_state_is_never_reinitialized(string failure)
    {
        var table = new FakeTable(); Store(table).Begin(Id(), "owner", "issuance", "did:example:synthetic");
        var adds = table.Adds;
        if (failure == "head") table.Entities.Remove("head");
        if (failure == "chunk") table.Entities.Remove(table.Entities.Keys.First(key => key != "head"));
        if (failure == "checksum") table.Entities["head"]["Hash"] = "invalid";
        if (failure == "version") table.Entities["head"]["Schema"] = 2;
        if (failure == "count") table.Entities["head"]["Chunks"] = 999;
        if (failure == "etag") table.Entities["head"].ETag = ETag.All;
        Assert.Throws<AyaStorageException>(() => Adapter(table).Read(rows => rows.Count));
        Assert.Throws<AyaStorageException>(() => Store(table).Begin(Id(), "another", "issuance", "did:example:synthetic"));
        Assert.Equal(adds, table.Adds);
    }

    [Fact]
    public void Snapshot_read_stays_consistent_when_another_writer_publishes_during_chunk_reads()
    {
        var table = new FakeTable(); var first = Id(); var second = Id();
        Store(table).Begin(first, "owner1", "issuance", "did:example:synthetic");
        table.BeforeGet = key => { if (key != "head") { table.BeforeGet = null; Store(table).Begin(second, "owner2", "issuance", "did:example:synthetic"); } };
        Assert.Single(Adapter(table).Read(rows => rows));
        Assert.Equal(2, Adapter(table).Read(rows => rows.Count));
    }

    [Fact]
    public void Expiry_is_rechecked_after_a_conflict_and_retention_stays_fixed()
    {
        var table = new FakeTable(); var clock = new Clock(); var id = Id(); var store = Store(table, clock);
        store.Begin(id, "owner", "issuance", "did:example:synthetic");
        store.Bind(id, "synthetic-request", clock.Now.AddMinutes(1));
        var retain = Adapter(table).Read(rows => rows[id].RetainUntil);
        table.BeforeReplace = () => { table.BeforeReplace = null; clock.Now = clock.Now.AddMinutes(4); throw new RequestFailedException(412, "conflict"); };
        Assert.False(store.Apply(Issued(id)));
        Assert.True(store.TryGetStatus(id, "owner", out var status)); Assert.Equal("outcome_unknown", status);
        Assert.Equal(retain, Adapter(table).Read(rows => rows[id].RetainUntil));
        clock.Now = retain; Assert.False(store.TryGetStatus(id, "owner", out _));
    }

    [Fact]
    public void Oversize_snapshot_fails_before_staging_and_preserves_previous_state()
    {
        var table = new FakeTable(); var id = Id(); Store(table).Begin(id, "owner", "issuance", "did:example:synthetic");
        int adds = table.Adds;
        Assert.Throws<AyaStorageException>(() => Adapter(table).Commit(rows => { rows[id].Owner = new string('x', AyaTableRequestPersistence.MaxBytes); return true; }));
        Assert.Equal(adds, table.Adds); Assert.True(Store(table).TryGetStatus(id, "owner", out _));
    }

    [Fact]
    public async Task Storage_outage_blocks_both_controllers_before_external_operations_and_can_recover()
    {
        var table = new FakeTable { FailReads = true };
        using var app = new IssuanceTests.TestApp { Persistence = Adapter(table) }; using var client = app.CreateClient(); IssuanceTests.Identity(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await IssuanceTests.Issue(client)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await RequestReliabilityTests.Present(client)).StatusCode);
        Assert.Equal(0, app.Tokens.Calls); Assert.Equal(0, app.Outbound.GraphCalls);
        table.FailReads = false;
        Assert.Equal(HttpStatusCode.OK, (await IssuanceTests.Issue(client)).StatusCode);
    }

    [Theory]
    [InlineData("issuance")] [InlineData("presentation")]
    public async Task Early_controller_callbacks_and_status_survive_restart_using_table_adapter(string kind)
    {
        var table = new FakeTable(); string id;
        using (var app = new IssuanceTests.TestApp { Persistence = Adapter(table) }) {
            using var client = app.CreateClient(); IssuanceTests.Identity(client);
            app.Outbound.BeforeResponse = async request => {
                id = request["callback"]["state"].ToString();
                var callback = kind == "issuance" ? Issued(id) : new CallbackEvent {
                    state = id, requestId = "synthetic-presentation", requestStatus = "presentation_verified",
                    verifiedCredentialsData = [new ClaimsIssuer { issuer = "did:example:synthetic", type = ["AyaEmployeeId"],
                        expirationDate = DateTimeOffset.UtcNow.AddDays(1).ToString("O"), credentialState = new() { revocationStatus = "VALID" },
                        claims = new Dictionary<string,string> { ["displayName"] = "Synthetic", ["employeeId"] = "SYNTHETIC", ["companyName"] = "Aya Living Inc.", ["ayaEmployeeKey"] = IssuanceTests.EmployeeKey } }]
                };
                using var message = new HttpRequestMessage(HttpMethod.Post, kind == "issuance" ? "/api/issuer/issuecallback" : "/api/verifier/presentationcallback");
                message.Headers.Add("api-key", IssuanceTests.CallbackKey);
                message.Content = new StringContent(JsonConvert.SerializeObject(callback), Encoding.UTF8, "application/json");
                Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(message)).StatusCode);
            };
            var response = kind == "issuance" ? await IssuanceTests.Issue(client) : await RequestReliabilityTests.Present(client);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); id = JObject.Parse(await response.Content.ReadAsStringAsync()).Value<string>("id");
        }
        using var restarted = new IssuanceTests.TestApp { Persistence = Adapter(table) }; using var poll = restarted.CreateClient(); IssuanceTests.Identity(poll);
        string route = (kind == "issuance" ? "/api/request-status?id=" : "/api/verifier/request-status?id=") + id;
        Assert.Equal(kind == "issuance" ? "issuance_successful" : "presentation_verified", JObject.Parse(await poll.GetStringAsync(route))["status"]);
        IssuanceTests.Identity(poll, IssuanceTests.Other); Assert.Equal(HttpStatusCode.NotFound, (await poll.GetAsync(route)).StatusCode);
    }

    [Theory]
    [InlineData(null)] [InlineData("http://synthetic.table.core.windows.net")]
    [InlineData("https://synthetic.table.core.windows.net/?sig=synthetic")]
    [InlineData("https://synthetic-secondary.table.core.windows.net")]
    [InlineData("https://localhost/")] [InlineData("https://synthetic.table.core.windows.net/path")]
    public void Missing_or_unsupported_configuration_fails_closed_without_credentials(string endpoint)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string> {
            ["Aya:RequestTable:Endpoint"] = endpoint, ["Aya:RequestTable:TableName"] = "SyntheticRequests", ["Aya:RequestTable:LedgerId"] = Ledger,
            ["Aya:RequestStorePath"] = "/tmp/legacy-must-not-activate"
        }).Build();
        Assert.False(AyaRequestStorageConfiguration.IsConfigured(config));
        Assert.IsType<UnconfiguredAyaRequestPersistence>(AyaRequestStorageConfiguration.Create(config));
    }
}
