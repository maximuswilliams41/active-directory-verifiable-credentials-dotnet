# Local mocked revocation milestone

## Scope and execution boundary

`src/Aya.Revocation` is a dependency-free .NET 10 class library. It is referenced by the test project only, not by the web app. `IAccountDirectory`, `ICredentialRevocation` and `IAdministratorNotifications` have only synthetic test implementations. There are no Microsoft HTTP adapters, token providers, cloud resources, live scheduler, new endpoints or deployment changes.

The only web behavior changes enforce `allowRevoked=false` for configured/template presentation requests and reject a successful presentation callback unless every credential has explicit `VALID` status. Missing/unknown/revoked statuses become a generic presentation error. Existing callback authentication still applies. These checks do not constitute a full production verifier audit: trusted issuer/type policy, callback correlation and the existing verifier callback-key configuration still need separate owner-reviewed work.

## State and timing

- An explicitly initialized local ledger contains approved employee UUID/account/contract mappings. Duplicate/empty identities are rejected. The worker never assigns identities from a scan or accepts employee input.
- An explicit `TickAsync` performs a complete account scan when due (24 hours after a successful scan). Failed/incomplete scans retry after one hour or later `Retry-After`. The supplied snapshot must be complete and recent. A future Graph adapter must follow all pages, preserve tenant/object identity and map omitted/deleted accounts accurately.
- Missing, deleted, unknown and duplicate account observations remain unresolved and create critical notices. Incomplete scans cannot advance the successful-scan checkpoint or create new revocation decisions. Already recorded offboarding jobs survive subsequent missing/deleted accounts and re-enablement; ambiguous account results pause affected processing.
- Disabled registered employees create durable jobs before external work. Their deadline starts at the **last known enabled observation**, a conservative bound on disablement. Detection does not reset the clock. Initial disabled accounts without history are treated as already urgent, with unknown timing and immediate escalation.
- Search covers every registered contract using `Base64(SHA256(UTF8(contractId + canonicalEmployeeKey)))`. Empty/incomplete results are unresolved, not success. IDs returned by search are distinct from issuance-request IDs. Known credentials remain tracked across later searches.
- Each credential is read before revocation and again afterward. Only explicit `Revoked` readback completes it. A timeout after remote success recovers by reading status on the next attempt. Already confirmed credentials need no second revoke. Daily reconciliation discovers credentials added after earlier completion.
- A future host should invoke ticks hourly, with processing started immediately on daily detection. Transient work retries hourly (or later server guidance). Permanent adapter failures set `NeedsIntervention`; repair/resume tooling is not implemented. Escalation is evaluated even while waiting for a retry or intervention.
- At **48 hours** from the conservative anchor, unresolved jobs generate a target-missed notice. At **72 hours**, they generate a critical deadline-breach notice. Recovery continues; the deadline is never represented as a guaranteed Microsoft availability SLA. Jobs already completed do not generate false deadline notices during ordinary reconciliation.
- A scan older than 26 hours creates an overdue-scan notice. A stopped host cannot notify anyone itself: deployment requires an independent watchdog for missed ticks/scans and undelivered notices.

Daily snapshots do not reveal every intermediate disable/re-enable event or an exact disable timestamp. The owner procedure must keep offboarded accounts disabled until confirmation, or use emergency manual revocation. Backdating from the last enabled observation intentionally escalates early after a long gap.

## Local durability, retries and privacy

`LocalRevocationLedger` uses an exclusive file lock across a tick, flushes state and atomically replaces its JSON file. Separate store instances/processes cannot work the same local file concurrently; a competing tick fails visibly and can be retried. Missing/corrupt storage fails closed rather than recreating empty success. Tests reopen the persisted file to check restart behavior.

This is **single-machine synthetic infrastructure**, not Azure Table Storage, an encrypted production registry, or a power-loss/multi-instance durability claim. Production requires a separately reviewed shared store, atomic leases/version checks, migration/backup/retention and access controls. The local JSON file must never receive real employee data.

Administrator notices use a durable outbox with attempt timestamps and stable IDs. They contain codes, severity, time and an opaque job reference, not employee identity, credential contents or upstream exception messages. Future delivery must deduplicate IDs (at-least-once semantics). Failed notifications stay pending and retry on subsequent ticks. Global scan/account issues repeat daily while unresolved; per-job failure/deadline alerts are deduplicated. Inspect the restricted registry for affected account records.

The ledger records attempts, confirmed credential timestamps and job outcome. Future application logging must use sanitized categories; do not log raw HTTP bodies, employee keys, index hashes or tokens. A notification outage needs independent monitoring rather than recursive alerts through the same failed channel.

## Emergency operation

The owner-only Microsoft portal procedure remains the emergency path; no new admin web interface or live command is added. A subsequent mocked status read recognizes manual revocation without issuing another revoke. There is no automatic un-revoke or automatic cancellation of offboarding after re-enablement.

## Local validation

Run from `1-asp-net-core-api-idtokenhint` so its SDK policy selects .NET 10:

```sh
dotnet restore ../tests/Aya.EmployeeId.Tests/Aya.EmployeeId.Tests.csproj
dotnet build ../tests/Aya.EmployeeId.Tests/Aya.EmployeeId.Tests.csproj --configuration Release --no-restore
dotnet test ../tests/Aya.EmployeeId.Tests/Aya.EmployeeId.Tests.csproj --configuration Release --no-build --no-restore
```

The suite uses synthetic identifiers, mocked ports/HTTP handlers, temporary local storage and a controllable clock; it does not wait real hours or contact Microsoft. The PR workflow builds/tests with read-only repository permission and no secrets/cloud login/deployment. Network-isolated local execution provides additional evidence beyond the workflow.

Acceptance covers: disabled/enabled/unregistered accounts; approved immutable mapping; all credential/contract matches; missing/deleted/ambiguous/incomplete scans; stale snapshots; retry-after; empty search; acknowledgement without confirmation; timeout/restart; duplicate and overlapping workers; failed notification recovery; manual revocation reconciliation; later issuance discovery; status rejection through the real presentation callback endpoint; successful recovery before 48 hours; exact 48/72-hour boundaries while retry is deferred; and privacy of notices/errors.

## Gates

Before merge: review this deliberately local scope, automated results, fail-closed presentation behavior and issuance-disabled default. Missing live integration is an intentional limitation, not a reason to activate anything.

Before deployment/issuance: complete [the owner contract/configuration checklist](contract-design.md), approve and implement cloud adapters/hosting separately, close issuance/registry lifecycle races, validate actual presentation status propagation and alert delivery with synthetic credentials, and obtain explicit owner authorization. A deployment workflow still exists but remains manual-only and unchanged.
