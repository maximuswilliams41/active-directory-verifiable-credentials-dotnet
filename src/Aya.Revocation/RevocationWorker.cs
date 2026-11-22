namespace Aya.Revocation;

// Tick is explicitly driven by local tests. Nothing registers this class with the web application.
// A future host would invoke it hourly; a successful account scan is scheduled every 24 hours.
public sealed class RevocationWorker(LocalRevocationLedger ledger, IAccountDirectory directory,
    ICredentialRevocation credentials, IAdministratorNotifications notifications, TimeProvider time)
{
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await ledger.WithLockAsync(async (document, save) =>
        {
            var now = time.GetUtcNow();
            if (now < document.CreatedAt || now < document.LastCompleteScan)
                throw new InvalidOperationException("Clock moved behind persisted observations.");

            if (document.NextScanAt == null || now >= document.NextScanAt)
                await ScanAsync(document, save, now, cancellationToken);

            if (now - (document.LastCompleteScan ?? document.CreatedAt) >= TimeSpan.FromHours(26))
                Notice(document, null, NoticeCode.ScanOverdue, NoticeSeverity.Critical, now);

            foreach (var employee in document.Employees.Where(e => e.Job != null))
            {
                var job = employee.Job!;
                if (job.VerifiedAt == null) Escalate(document, job, now);
                if (!job.NeedsIntervention && now >= job.NextAttemptAt && employee.AccountProblem != NoticeCode.AmbiguousIdentity)
                    await ProcessAsync(document, employee, save, now, cancellationToken);
                if (job.VerifiedAt == null) Escalate(document, job, time.GetUtcNow());
            }
            await save(); // durable outbox before any notification side effect
            foreach (var entry in document.Notices.Where(n => !n.Delivered))
            {
                entry.Attempts++;
                entry.LastAttemptAt = time.GetUtcNow();
                await save();
                try
                {
                    await notifications.SendAsync(entry.Notice, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Leave notice pending. The independent host watchdog must cover notification outages.
                    break;
                }
                entry.Delivered = true;
                await save(); // storage failures must remain visible to the caller
            }
        });
    }

    private async Task ScanAsync(LedgerDocument document, Func<Task> save, DateTimeOffset now, CancellationToken ct)
    {
        AccountScan scan;
        try { scan = await directory.ScanAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Notice(document, null, NoticeCode.DirectoryUnavailable, NoticeSeverity.Warning, now);
            document.NextScanAt = now + RetryDelay(ex);
            await save();
            return;
        }
        if (!scan.IsComplete || scan.Accounts == null || scan.ObservedAt > now || now - scan.ObservedAt > TimeSpan.FromHours(1))
        {
            Notice(document, null, NoticeCode.IncompleteScan, NoticeSeverity.Warning, now);
            document.NextScanAt = now.AddHours(1);
            await save();
            return;
        }
        foreach (var employee in document.Employees)
        {
            var r = employee.Registration;
            var matches = scan.Accounts.Where(a => a.TenantId == r.TenantId && a.ObjectId == r.ObjectId).ToArray();
            employee.AccountProblem = matches.Length switch
            {
                0 => NoticeCode.MissingAccount,
                > 1 => NoticeCode.AmbiguousIdentity,
                _ => matches[0].Status switch
                {
                    AccountStatus.Deleted => NoticeCode.DeletedAccount,
                    AccountStatus.Enabled or AccountStatus.Disabled => null,
                    _ => NoticeCode.UnknownAccountState
                }
            };
            if (employee.AccountProblem is { } problem)
            {
                Notice(document, employee.Job?.Id, problem, NoticeSeverity.Critical, now);
                continue;
            }
            if (matches[0].Status == AccountStatus.Enabled)
            {
                // A later re-enable never cancels an existing offboarding job.
                if (employee.Job == null) employee.LastEnabledAt = scan.ObservedAt;
                continue;
            }
            if (employee.Job == null)
            {
                employee.Job = new RevocationJob
                {
                    DetectedAt = now,
                    DeadlineAnchor = employee.LastEnabledAt ?? now.AddHours(-72),
                    NextAttemptAt = now
                };
                if (employee.LastEnabledAt == null)
                    Notice(document, employee.Job.Id, NoticeCode.UnknownDisableTime, NoticeSeverity.Critical, now);
            }
            else if (employee.Job.VerifiedAt != null)
            {
                // Daily reconciliation searches again, including credentials issued after an earlier scan.
                employee.Job.NextAttemptAt = now;
            }
        }
        document.LastCompleteScan = scan.ObservedAt;
        document.NextScanAt = now.AddDays(1);
        await save(); // registry observations and jobs precede any credential operations
    }

    private async Task ProcessAsync(LedgerDocument document, EmployeeEntry employee, Func<Task> save,
        DateTimeOffset now, CancellationToken ct)
    {
        var job = employee.Job!;
        job.Attempts++;
        job.NextAttemptAt = now.AddHours(1);
        await save();
        bool hadVerifiedCompletion = job.VerifiedAt != null;
        bool allSearchesComplete = true;
        foreach (var contract in employee.Registration.Contracts)
        {
            try
            {
                var found = await credentials.SearchAsync(contract,
                    EmployeeCredentialIndex.Hash(contract.ContractId, employee.Registration.EmployeeKey), ct);
                if (!found.IsComplete || found.CredentialIds == null || found.CredentialIds.Any(string.IsNullOrWhiteSpace))
                {
                    allSearchesComplete = false;
                    Notice(document, job.Id, NoticeCode.IncompleteCredentialSearch, NoticeSeverity.Warning, now);
                    continue;
                }
                if (found.CredentialIds.Count == 0)
                {
                    allSearchesComplete = false;
                    Notice(document, job.Id, NoticeCode.EmptyCredentialSearch, NoticeSeverity.Critical, now);
                    continue;
                }
                foreach (string id in found.CredentialIds.Distinct(StringComparer.Ordinal))
                    if (!job.Credentials.Any(c => c.Contract == contract && c.CredentialId == id))
                        job.Credentials.Add(new CredentialEntry { Contract = contract, CredentialId = id });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                allSearchesComplete = false;
                Failure(document, job, ex, NoticeCode.CredentialLookupFailed, now);
            }
        }
        if (!allSearchesComplete || job.Credentials.Any(c => c.VerifiedAt == null)) job.VerifiedAt = null;
        await save();
        foreach (var credential in job.Credentials.Where(c => c.VerifiedAt == null))
        {
            try
            {
                // Read first: an earlier timed-out request may already have revoked this credential.
                var status = await credentials.ReadStatusAsync(credential.Contract, credential.CredentialId, ct);
                if (status == CredentialStatus.Valid)
                {
                    await credentials.RevokeAsync(credential.Contract, credential.CredentialId, ct);
                    status = await credentials.ReadStatusAsync(credential.Contract, credential.CredentialId, ct);
                }
                if (status == CredentialStatus.Revoked) credential.VerifiedAt = time.GetUtcNow();
                else Notice(document, job.Id, NoticeCode.VerificationPending, NoticeSeverity.Warning, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Failure(document, job, ex, NoticeCode.CredentialOperationFailed, now);
            }
            await save();
        }
        if (allSearchesComplete && job.Credentials.Count > 0 && job.Credentials.All(c => c.VerifiedAt != null))
        {
            // Preserve deadline evidence even when a slow dependency finishes after a deadline.
            if (!hadVerifiedCompletion || job.VerifiedAt == null) Escalate(document, job, time.GetUtcNow());
            job.VerifiedAt ??= time.GetUtcNow();
            job.NextAttemptAt = document.NextScanAt ?? now.AddDays(1);
        }
        await save();
    }

    private static TimeSpan RetryDelay(Exception ex) => ex is DependencyFailureException { RetryAfter: { } delay }
        && delay > TimeSpan.FromHours(1) ? delay : TimeSpan.FromHours(1);

    private static void Failure(LedgerDocument document, RevocationJob job, Exception ex, NoticeCode code, DateTimeOffset now)
    {
        var next = now + RetryDelay(ex);
        if (next > job.NextAttemptAt) job.NextAttemptAt = next;
        if (ex is DependencyFailureException { Retryable: false }) job.NeedsIntervention = true;
        Notice(document, job.Id, code, job.NeedsIntervention ? NoticeSeverity.Critical : NoticeSeverity.Warning, now);
    }

    private static void Escalate(LedgerDocument document, RevocationJob job, DateTimeOffset now)
    {
        if (now >= job.DeadlineAnchor.AddHours(48))
            Notice(document, job.Id, NoticeCode.Target48Hours, NoticeSeverity.TargetMissed, now);
        if (now >= job.DeadlineAnchor.AddHours(72))
            Notice(document, job.Id, NoticeCode.Deadline72Hours, NoticeSeverity.Critical, now);
    }

    private static void Notice(LedgerDocument document, Guid? jobId, NoticeCode code, NoticeSeverity severity, DateTimeOffset now)
    {
        // Job notices are deduplicated for that job. Global/account scan incidents recur daily
        // while unresolved, so a later outage is never hidden by an old delivered notice.
        if (document.Notices.Any(n => n.Notice.JobId == jobId && n.Notice.Code == code &&
            (jobId != null || now - n.Notice.CreatedAt < TimeSpan.FromDays(1)))) return;
        document.Notices.Add(new NoticeEntry { Notice = new(Guid.NewGuid(), jobId, code, severity, now) });
    }
}
