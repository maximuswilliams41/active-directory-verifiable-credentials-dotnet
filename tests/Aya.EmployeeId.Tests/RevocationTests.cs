using Aya.Revocation;
using AspNetCoreVerifiableCredentials;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aya.EmployeeId.Tests;

public sealed class RevocationTests
{
    private static readonly Guid Employee = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Tenant = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Account = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly CredentialContract Contract = new("synthetic-authority", "synthetic-contract");
    private static readonly CredentialContract OtherContract = new("synthetic-authority", "synthetic-contract-v2");
    private static EmployeeRegistration Registration(params CredentialContract[] contracts) =>
        new(Employee, Tenant, Account, contracts.Length == 0 ? [Contract] : contracts);

    [Fact]
    public async Task Daily_scan_revokes_disabled_employee_and_verifies_every_matching_credential()
    {
        using var f = await Fixture.Create(Registration(Contract, OtherContract));
        f.Api.Ids[Contract] = ["synthetic-1", "synthetic-2"];
        f.Api.Ids[OtherContract] = ["synthetic-3"];
        await f.ObserveEnabled();
        await f.DisableAfter24Hours();
        var job = await f.Job();
        Assert.NotNull(job.VerifiedAt);
        Assert.Equal(3, job.Credentials.Count);
        Assert.All(job.Credentials, c => Assert.NotNull(c.VerifiedAt));
        Assert.Equal(3, f.Api.Revokes);
        Assert.Equal(6, f.Api.Reads);
        Assert.Equal(2, f.Directory.Calls);
        Assert.Equal(f.Clock.Now.AddHours(-24), job.DeadlineAnchor);
        Assert.All(f.Api.Hashes, h => Assert.Contains(h, new[] {
            EmployeeCredentialIndex.Hash(Contract.ContractId, Employee),
            EmployeeCredentialIndex.Hash(OtherContract.ContractId, Employee) }));
        Assert.Empty(f.Notifier.Delivered);
    }

    [Fact]
    public async Task Enabled_and_unregistered_disabled_accounts_are_not_revoked()
    {
        using var f = await Fixture.Create();
        f.Directory.Accounts.Add(new(Tenant, Guid.Parse("44444444-4444-4444-8444-444444444444"), AccountStatus.Disabled));
        await f.Worker().TickAsync();
        Assert.Null((await f.Store.ReadAsync()).Employees.Single().Job);
        Assert.Equal(0, f.Api.Searches);
    }

    [Theory]
    [InlineData(AccountStatus.Unknown, NoticeCode.UnknownAccountState)]
    [InlineData(AccountStatus.Deleted, NoticeCode.DeletedAccount)]
    public async Task Unknown_and_deleted_accounts_require_intervention(AccountStatus status, NoticeCode code)
    {
        using var f = await Fixture.Create(); f.Directory.SetStatus(status);
        await f.Worker().TickAsync();
        Assert.Equal(code, (await f.Store.ReadAsync()).Employees.Single().AccountProblem);
        Assert.Contains(f.Notifier.Delivered, n => n.Code == code && n.Severity == NoticeSeverity.Critical);
        Assert.Equal(0, f.Api.Searches);
    }

    [Fact]
    public async Task Missing_account_is_not_assumed_enabled_or_disabled()
    {
        using var f = await Fixture.Create(); f.Directory.Accounts.Clear();
        await f.Worker().TickAsync();
        Assert.Equal(NoticeCode.MissingAccount, (await f.Store.ReadAsync()).Employees.Single().AccountProblem);
        Assert.Contains(f.Notifier.Delivered, n => n.Code == NoticeCode.MissingAccount);
        Assert.Equal(0, f.Api.Revokes);
    }

    [Fact]
    public async Task Duplicate_directory_identity_blocks_processing()
    {
        using var f = await Fixture.Create(); f.Directory.SetStatus(AccountStatus.Disabled);
        f.Directory.Accounts.Add(new(Tenant, Account, AccountStatus.Enabled));
        await f.Worker().TickAsync();
        Assert.Contains(f.Notifier.Delivered, n => n.Code == NoticeCode.AmbiguousIdentity);
        Assert.Equal(0, f.Api.Searches);
    }

    [Theory]
    [InlineData("duplicate_key")]
    [InlineData("duplicate_account")]
    [InlineData("empty_key")]
    [InlineData("missing_contract")]
    public async Task Registry_rejects_unapproved_or_ambiguous_identity_shapes(string mode)
    {
        var registrations = mode switch
        {
            "duplicate_key" => new[] { Registration(), Registration() with { ObjectId = Guid.NewGuid() } },
            "duplicate_account" => new[] { Registration(), Registration() with { EmployeeKey = Guid.NewGuid() } },
            "empty_key" => new[] { Registration() with { EmployeeKey = Guid.Empty } },
            _ => new[] { Registration() with { Contracts = Array.Empty<CredentialContract>() } }
        };
        string folder = Path.Combine(Path.GetTempPath(), "aya-synthetic-" + Guid.NewGuid());
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => LocalRevocationLedger.CreateAsync(
                Path.Combine(folder, "ledger.json"), registrations, DateTimeOffset.UtcNow));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("stale")]
    [InlineData("future")]
    public async Task Incomplete_or_invalid_snapshot_does_not_advance_daily_checkpoint(string mode)
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled();
        var last = (await f.Store.ReadAsync()).LastCompleteScan;
        f.Clock.Advance(24); f.Directory.SetStatus(AccountStatus.Disabled);
        f.Directory.Complete = mode != "incomplete";
        f.Directory.ObservationOffset = mode == "stale" ? TimeSpan.FromDays(-2) : mode == "future" ? TimeSpan.FromMinutes(1) : TimeSpan.Zero;
        await f.Worker().TickAsync();
        Assert.Equal(last, (await f.Store.ReadAsync()).LastCompleteScan);
        Assert.Contains(f.Notifier.Delivered, n => n.Code == NoticeCode.IncompleteScan);
        Assert.Equal(0, f.Api.Revokes);
        f.Directory.Complete = true; f.Directory.ObservationOffset = TimeSpan.Zero;
        f.Clock.Advance(1); await f.Worker().TickAsync();
        Assert.NotNull((await f.Job()).VerifiedAt);
    }

    [Fact]
    public async Task Directory_outage_is_retried_and_stale_scan_alerts_at_26_hours()
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled();
        f.Directory.Failure = new DependencyFailureException(true);
        f.Clock.Advance(24); await f.Worker().TickAsync();
        Assert.Contains(f.Notifier.Delivered, n => n.Code == NoticeCode.DirectoryUnavailable);
        f.Clock.Advance(2); await f.Worker().TickAsync();
        Assert.Contains(f.Notifier.Delivered, n => n.Code == NoticeCode.ScanOverdue);
        Assert.Equal(3, f.Directory.Calls);
        Assert.Equal(0, f.Api.Searches);
    }

    [Fact]
    public async Task Initial_disabled_account_with_unknown_age_is_urgent_not_given_a_new_72_hours()
    {
        using var f = await Fixture.Create(); f.Directory.SetStatus(AccountStatus.Disabled);
        await f.Worker().TickAsync();
        Assert.Contains(f.Notifier.Delivered, n => n.Code == NoticeCode.UnknownDisableTime);
        Assert.Contains(f.Notifier.Delivered, n => n.Code == NoticeCode.Deadline72Hours);
        Assert.NotNull((await f.Job()).VerifiedAt);
    }

    [Fact]
    public async Task Exact_48_and_72_hour_boundaries_escalate_even_during_long_retry_after()
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled();
        var anchor = f.Clock.Now;
        f.Api.SearchFailure = new DependencyFailureException(true, TimeSpan.FromHours(100));
        await f.DisableAfter24Hours();
        f.Clock.Now = anchor.AddHours(48).AddTicks(-1); await f.Worker().TickAsync();
        Assert.DoesNotContain(f.Notifier.Delivered, n => n.Code == NoticeCode.Target48Hours);
        f.Clock.Now = anchor.AddHours(48); await f.Worker().TickAsync();
        Assert.Contains(f.Notifier.Delivered, n => n.Code == NoticeCode.Target48Hours && n.CreatedAt == f.Clock.Now);
        f.Clock.Now = anchor.AddHours(72).AddTicks(-1); await f.Worker().TickAsync();
        Assert.DoesNotContain(f.Notifier.Delivered, n => n.Code == NoticeCode.Deadline72Hours);
        f.Clock.Now = anchor.AddHours(72); await f.Worker().TickAsync();
        Assert.Contains(f.Notifier.Delivered, n => n.Code == NoticeCode.Deadline72Hours && n.CreatedAt == f.Clock.Now);
        Assert.Equal(1, (await f.Job()).Attempts);
        Assert.Null((await f.Job()).VerifiedAt);
        Assert.Equal(anchor, (await f.Job()).DeadlineAnchor);
    }

    [Fact]
    public async Task Disable_just_after_scan_and_temporary_failure_complete_before_48_hours()
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled(); var enabled = f.Clock.Now;
        f.Api.SearchFailure = new DependencyFailureException(true);
        await f.DisableAfter24Hours();
        f.Api.SearchFailure = null;
        f.Clock.Advance(1); await f.Worker().TickAsync();
        Assert.Equal(enabled.AddHours(25), (await f.Job()).VerifiedAt);
        Assert.DoesNotContain(f.Notifier.Delivered, n => n.Code == NoticeCode.Target48Hours);
    }

    [Fact]
    public async Task Retry_after_is_respected_without_waiting_for_next_daily_scan()
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled();
        f.Api.SearchFailure = new DependencyFailureException(true, TimeSpan.FromHours(3));
        await f.DisableAfter24Hours();
        f.Api.SearchFailure = null;
        f.Clock.Advance(1); await f.Worker().TickAsync(); Assert.Equal(1, f.Api.Searches);
        f.Clock.Advance(2); await f.Worker().TickAsync();
        Assert.NotNull((await f.Job()).VerifiedAt);
        Assert.Equal(2, f.Directory.Calls);
    }

    [Theory]
    [InlineData("empty", NoticeCode.EmptyCredentialSearch)]
    [InlineData("incomplete", NoticeCode.IncompleteCredentialSearch)]
    public async Task Unproven_search_coverage_cannot_complete(string mode, NoticeCode code)
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled();
        if (mode == "empty") f.Api.Ids[Contract] = [];
        else f.Api.SearchComplete = false;
        await f.DisableAfter24Hours();
        Assert.Null((await f.Job()).VerifiedAt);
        Assert.Contains(f.Notifier.Delivered, n => n.Code == code);
        Assert.Equal(0, f.Api.Revokes);
    }

    [Theory]
    [InlineData(CredentialStatus.Valid)]
    [InlineData(CredentialStatus.Unknown)]
    public async Task Acknowledgement_or_unknown_status_does_not_count_as_verified(CredentialStatus status)
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled();
        f.Api.AfterRevoke = status;
        await f.DisableAfter24Hours();
        Assert.Null((await f.Job()).VerifiedAt);
        Assert.Contains(f.Notifier.Delivered, n => n.Code == NoticeCode.VerificationPending);
        f.Api.Statuses["synthetic-1"] = CredentialStatus.Revoked;
        f.Clock.Advance(1); await f.Worker().TickAsync();
        Assert.NotNull((await f.Job()).VerifiedAt);
        Assert.Equal(1, f.Api.Revokes);
    }

    [Fact]
    public async Task Timeout_after_remote_success_recovers_after_restart_without_repeating_revoke()
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled();
        f.Api.ThrowAfterRevoke = true;
        await f.DisableAfter24Hours();
        Assert.Null((await f.Job()).VerifiedAt);
        f.Clock.Advance(1);
        var reopened = new LocalRevocationLedger(f.Path);
        await new RevocationWorker(reopened, f.Directory, f.Api, f.Notifier, f.Clock).TickAsync();
        Assert.NotNull((await reopened.ReadAsync()).Employees.Single().Job.VerifiedAt);
        Assert.Equal(1, f.Api.Revokes);
    }

    [Fact]
    public async Task Permanent_failure_is_flagged_and_deadlines_continue()
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled();
        f.Api.SearchFailure = new DependencyFailureException(false);
        await f.DisableAfter24Hours(); Assert.True((await f.Job()).NeedsIntervention);
        f.Clock.Advance(48); await f.Worker().TickAsync();
        Assert.Contains(f.Notifier.Delivered, n => n.Code == NoticeCode.Deadline72Hours);
        Assert.Equal(1, f.Api.Searches);
    }

    [Fact]
    public async Task Manual_revocation_is_reconciled_without_another_revoke()
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled();
        f.Api.Statuses["synthetic-1"] = CredentialStatus.Revoked;
        await f.DisableAfter24Hours();
        Assert.NotNull((await f.Job()).VerifiedAt); Assert.Equal(0, f.Api.Revokes);
    }

    [Fact]
    public async Task Later_credentials_are_discovered_on_daily_reconciliation()
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled(); await f.DisableAfter24Hours();
        f.Api.Ids[Contract].Add("synthetic-late-issuance");
        f.Clock.Advance(24); await f.Worker().TickAsync();
        Assert.Equal(2, (await f.Job()).Credentials.Count);
        Assert.Equal(2, f.Api.Revokes); Assert.NotNull((await f.Job()).VerifiedAt);
    }

    [Fact]
    public async Task Completed_job_does_not_generate_false_overdue_alerts_on_reconciliation()
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled(); await f.DisableAfter24Hours();
        var completion = (await f.Job()).VerifiedAt;
        f.Clock.Advance(48); await f.Worker().TickAsync();
        Assert.Equal(completion, (await f.Job()).VerifiedAt);
        Assert.DoesNotContain(f.Notifier.Delivered, n => n.Code is NoticeCode.Target48Hours or NoticeCode.Deadline72Hours);
        Assert.Equal(1, f.Api.Revokes);
    }

    [Fact]
    public async Task Reenable_does_not_cancel_recorded_offboarding_or_reset_deadline()
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled();
        f.Api.SearchFailure = new DependencyFailureException(true); await f.DisableAfter24Hours();
        var anchor = (await f.Job()).DeadlineAnchor;
        f.Directory.SetStatus(AccountStatus.Enabled); f.Api.SearchFailure = null;
        f.Clock.Advance(24); await f.Worker().TickAsync();
        Assert.Equal(anchor, (await f.Job()).DeadlineAnchor);
        Assert.NotNull((await f.Job()).VerifiedAt);
    }

    [Fact]
    public async Task Duplicate_ticks_and_duplicate_search_results_are_idempotent()
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled();
        f.Api.Ids[Contract].Add("synthetic-1"); await f.DisableAfter24Hours();
        for (int i = 0; i < 3; i++) await f.Worker().TickAsync();
        Assert.Single((await f.Job()).Credentials);
        Assert.Equal(1, f.Api.Revokes); Assert.Equal(2, f.Directory.Calls);
    }

    [Fact]
    public async Task Overlapping_local_workers_cannot_own_same_ledger()
    {
        using var f = await Fixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Directory.BeforeScan = async () => { entered.SetResult(); await release.Task; };
        var first = f.Worker().TickAsync(); await entered.Task;
        try { await Assert.ThrowsAsync<IOException>(() => f.Worker().TickAsync()); }
        finally { release.SetResult(); await first; }
        Assert.Equal(1, f.Directory.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public async Task Damaged_ledger_fails_visibly_without_recreating_success(string mode)
    {
        using var f = await Fixture.Create();
        if (mode == "missing") File.Delete(f.Path); else await File.WriteAllTextAsync(f.Path, "not-json");
        await Assert.ThrowsAnyAsync<Exception>(() => f.Worker().TickAsync());
        Assert.Equal(0, f.Directory.Calls); Assert.Equal(0, f.Api.Revokes);
    }

    [Fact]
    public async Task Notification_outbox_survives_failure_and_restart_with_stable_id()
    {
        using var f = await Fixture.Create(); f.Notifier.Fail = true; f.Directory.Accounts.Clear();
        await f.Worker().TickAsync();
        var pending = (await f.Store.ReadAsync()).Notices.Single(); Assert.False(pending.Delivered);
        f.Notifier.Fail = false;
        await new RevocationWorker(new LocalRevocationLedger(f.Path), f.Directory, f.Api, f.Notifier, f.Clock).TickAsync();
        Assert.Equal(pending.Notice.Id, Assert.Single(f.Notifier.Delivered).Id);
        Assert.True((await f.Store.ReadAsync()).Notices.Single().Delivered);
    }

    [Fact]
    public async Task Administrator_notices_do_not_contain_identity_or_upstream_exception_details()
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled();
        f.Api.SearchFailure = new InvalidOperationException("synthetic-private-body fixture@example.invalid");
        await f.DisableAfter24Hours();
        var json = System.Text.Json.JsonSerializer.Serialize(f.Notifier.Delivered);
        Assert.DoesNotContain(Employee.ToString(), json); Assert.DoesNotContain(Account.ToString(), json);
        Assert.DoesNotContain(Contract.ContractId, json); Assert.DoesNotContain("synthetic-private-body", json);
        Assert.DoesNotContain("fixture@example.invalid", await File.ReadAllTextAsync(f.Path));
    }

    [Fact]
    public void Index_uses_canonical_immutable_key_and_distinct_contract_hashes()
    {
        Assert.Equal("11111111-1111-4111-8111-111111111111", EmployeeCredentialIndex.ClaimValue(Employee));
        // Independently calculated UTF-8 SHA-256/Base64 fixture, not generated by the implementation.
        Assert.Equal("lJwNJPQzMdWSIl7tSpdt5+ArCTXqOj9P+trvAtAWFsc=", EmployeeCredentialIndex.Hash(Contract.ContractId, Employee));
        Assert.NotEqual(EmployeeCredentialIndex.Hash(Contract.ContractId, Employee), EmployeeCredentialIndex.Hash(OtherContract.ContractId, Employee));
        Assert.Throws<ArgumentException>(() => EmployeeCredentialIndex.ClaimValue(Guid.Empty));
    }

    [Fact]
    public async Task Recurring_missing_account_incident_is_not_hidden_by_previous_delivery()
    {
        using var f = await Fixture.Create(); f.Directory.Accounts.Clear();
        await f.Worker().TickAsync();
        f.Clock.Advance(24); await f.Worker().TickAsync();
        Assert.Equal(2, f.Notifier.Delivered.Count(n => n.Code == NoticeCode.MissingAccount));
    }

    [Fact]
    public async Task Notification_delivery_failure_remains_observable_in_durable_outbox()
    {
        using var f = await Fixture.Create(); f.Directory.Accounts.Clear(); f.Notifier.Fail = true;
        await f.Worker().TickAsync();
        var entry = (await f.Store.ReadAsync()).Notices.Single();
        Assert.False(entry.Delivered); Assert.Equal(1, entry.Attempts); Assert.Equal(f.Clock.Now, entry.LastAttemptAt);
    }

    [Fact]
    public async Task Revoked_synthetic_credential_is_rejected_by_subsequent_presentation_callback()
    {
        using var f = await Fixture.Create(); await f.ObserveEnabled(); await f.DisableAfter24Hours();
        using var app = new IssuanceTests.TestApp(); using var client = app.CreateClient();
        var cache = app.Services.GetRequiredService<IMemoryCache>();
        string state = Guid.NewGuid().ToString();
        cache.Set(state, "{\"status\":\"request_created\"}");
        var callback = new CallbackEvent
        {
            state = state, requestId = "synthetic-presentation", requestStatus = "presentation_verified",
            verifiedCredentialsData = [new ClaimsIssuer {
                credentialState = new CredentialState {
                    revocationStatus = f.Api.Statuses["synthetic-1"].ToString().ToUpperInvariant()
                }
            }]
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/verifier/presentationcallback");
        request.Headers.Add("api-key", IssuanceTests.CallbackKey);
        request.Content = new StringContent(JsonConvert.SerializeObject(callback), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        var saved = JObject.Parse(cache.Get<string>(state));
        Assert.Equal("presentation_error", saved.Value<string>("status"));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(double hours) => Now = Now.AddHours(hours);
    }
    private sealed class FakeDirectory(Clock clock) : IAccountDirectory
    {
        public List<AccountRecord> Accounts = [new(Tenant, Account, AccountStatus.Enabled)];
        public bool Complete = true;
        public TimeSpan ObservationOffset;
        public Exception Failure;
        public Func<Task> BeforeScan;
        public int Calls;
        public void SetStatus(AccountStatus status) => Accounts = [new(Tenant, Account, status)];
        public async Task<AccountScan> ScanAsync(CancellationToken ct)
        {
            Calls++; if (BeforeScan != null) await BeforeScan();
            ct.ThrowIfCancellationRequested(); if (Failure != null) throw Failure;
            return new(clock.Now + ObservationOffset, Complete, Accounts.ToArray());
        }
    }
    private sealed class FakeCredentials : ICredentialRevocation
    {
        public Dictionary<CredentialContract, List<string>> Ids = new() { [Contract] = ["synthetic-1"] };
        public Dictionary<string, CredentialStatus> Statuses = [];
        public List<string> Hashes = [];
        public bool SearchComplete = true, ThrowAfterRevoke;
        public Exception SearchFailure;
        public CredentialStatus AfterRevoke = CredentialStatus.Revoked;
        public int Searches, Reads, Revokes;
        public Task<CredentialSearch> SearchAsync(CredentialContract contract, string hash, CancellationToken ct)
        {
            Searches++; Hashes.Add(hash);
            if (SearchFailure != null) throw SearchFailure;
            return Task.FromResult(new CredentialSearch(SearchComplete, Ids.GetValueOrDefault(contract, []).ToArray()));
        }
        public Task<CredentialStatus> ReadStatusAsync(CredentialContract contract, string id, CancellationToken ct)
        {
            Reads++; return Task.FromResult(Statuses.GetValueOrDefault(id, CredentialStatus.Valid));
        }
        public Task RevokeAsync(CredentialContract contract, string id, CancellationToken ct)
        {
            Revokes++; Statuses[id] = AfterRevoke;
            if (ThrowAfterRevoke) throw new TimeoutException("Synthetic timeout after remote success.");
            return Task.CompletedTask;
        }
    }
    private sealed class FakeNotifications : IAdministratorNotifications
    {
        public bool Fail;
        public List<AdministratorNotice> Delivered = [];
        public Task SendAsync(AdministratorNotice notice, CancellationToken ct)
        {
            if (Fail) throw new DependencyFailureException(true);
            if (!Delivered.Any(n => n.Id == notice.Id)) Delivered.Add(notice);
            return Task.CompletedTask;
        }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly Clock Clock = new();
        public readonly FakeCredentials Api = new();
        public readonly FakeNotifications Notifier = new();
        public readonly FakeDirectory Directory;
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aya-synthetic-" + Guid.NewGuid(), "ledger.json");
        public LocalRevocationLedger Store;
        private Fixture() => Directory = new(Clock);
        public static async Task<Fixture> Create(EmployeeRegistration registration = null)
        {
            var f = new Fixture();
            f.Store = await LocalRevocationLedger.CreateAsync(f.Path, [registration ?? Registration()], f.Clock.Now);
            return f;
        }
        public RevocationWorker Worker() => new(Store, Directory, Api, Notifier, Clock);
        public Task ObserveEnabled() => Worker().TickAsync();
        public async Task DisableAfter24Hours()
        {
            Clock.Advance(24); Directory.SetStatus(AccountStatus.Disabled); await Worker().TickAsync();
        }
        public async Task<RevocationJob> Job() => (await Store.ReadAsync()).Employees.Single().Job;
        public void Dispose() => System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(Path), true);
    }
}
