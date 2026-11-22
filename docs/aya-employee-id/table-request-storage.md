# Azure Table request persistence: design and owner checklist

This change is limited to issuance/presentation request persistence. Both activation flags remain false. No Azure resources, permissions, Microsoft identity services, or deployment are configured by this change. The local mocked revocation engine is unchanged. Free F1 remains an attended synthetic pilot host, not an availability commitment.

## Atomicity and recovery

`IAyaRequestPersistence` and its whole-dictionary read/commit API remain intact. The runtime now selects `AyaTableRequestPersistence`; the previous file adapter is retained only in the test project so every original filesystem regression can still run. `Aya:RequestStorePath` has no runtime effect. There is no file/memory fallback, account key, SAS, developer-credential chain, resource auto-creation, or automatic migration.

One private table partition (the configured ledger UUID) contains a `head` entity and immutable snapshot chunks. Readers fetch the head from the primary endpoint and load exactly its referenced chunks, validating schema, sizes, SHA-256 checksum and record structure. Old chunks are not deleted during use, so a reader remains consistent if a concurrent writer publishes a new head. The checksum detects accidental damage; authorization, not a checksum, protects against unauthorized writers.

A commit evaluates the existing state transition against a fresh snapshot, stages unique insert-only chunks, then replaces `head` with the exact previously read ETag. This conditional replacement is the atomic publication point for all records, including the active-owner guard and retention pruning. No unconditional upsert or wildcard ETag is used. A definite 412 conflict reloads and re-evaluates the pure state transformation, at most three attempts total. Time checks execute within the transformation so an expired callback cannot be accepted using the timestamp from a previous conflict attempt. Delegates must never perform external operations.

A snapshot is still limited to 2,000 records and 4 MiB. Each binary chunk is at most 48 KiB, below Table's 64 KiB binary-property limit. This avoids placing a whole snapshot in a 1 MiB entity or exceeding the 4 MiB/100-operation transaction limit (wire encoding also adds overhead). Normal small pilot snapshots need few chunks. This intentionally favors compatibility and simple correctness over high-throughput per-request queries.

The following operations cannot form one Azure transaction:

| Boundary | Recovery behavior |
| --- | --- |
| Staging several chunk entities and publishing the head | Incomplete/unreferenced chunks are invisible. The old committed head remains authoritative until its conditional replacement succeeds. |
| Table commit and Microsoft Request Service HTTP operation | Pending state is committed first. A service timeout or crash before response binding remains unconfirmed; no automatic issuance/presentation POST retry is introduced. |
| Ambiguous response after head replacement | Return sanitized storage-unavailable, never assume success or roll back. A later authoritative read recovers whichever version actually committed. Duplicate callbacks remain idempotent. |
| Removal of historical snapshots | No online deletion in this pilot adapter. Deleting a generation still being read could break recovery. Owner-only teardown is described below. |

SDK automatic retries are disabled for Table calls; a timeout/connection loss/5xx must not be converted into a misleading later 412. Only the adapter's definite publication conflicts are retried. Other storage/authentication failures return sanitized 503 through existing middleware. Operations have a 30-second cancellation budget and SDK network timeout of five seconds; these are cooperative bounds, not an F1 latency SLA. A later request may recover after availability returns. Missing tables, missing heads, incomplete snapshots and corrupt state fail closed and are never silently reinitialized. Read-only/no-change operations do not create snapshot versions.

The runtime does not grant a success response before persistence acknowledges the change. An acknowledgement lost after a pending write may block a repeat submission even though the browser saw 503; this is intentional. The owner must investigate uncertain requests, not erase state or repeat issuance automatically.

## Owner-only configuration, in order

Perform these steps only after review and separate authorization for cloud preparation/deployment. Keep issuance and verification disabled throughout. Use synthetic identities only; do not paste credentials, entity contents or identity mappings into GitHub/chat.

1. **Select a dedicated pilot store.** Use Azure Storage Table service in a standard general-purpose v2 account, preferably in the app's region. Standard LRS is sufficient for this short pilot's host-restart objective; it is not regional disaster recovery. Use the primary `https://<account>.table.core.windows.net` endpoint. Cosmos Table, secondary endpoints, sovereign-cloud endpoints, SAS URLs and custom endpoints are not implemented. Create a dedicated table, for example `AyaPilotRequests`; the application will not create it.
2. **Choose one new ledger ID.** Generate a lowercase hyphenated nonzero UUID privately. This is the partition key, not an employee identifier. Keep it stable across restarts and deployments. Never change endpoint/table/ledger to bypass an unresolved request. Reconcile any previous file-journal requests before switching; there is no automatic import.
3. **Bootstrap once, before any application writes.** In the new table, add exactly one entity using Storage Browser/Storage Explorer with the following explicit types. Let the service generate ETag and Timestamp. Do not overwrite an existing head, including during recovery.

   | Property | Type | Initial value |
   | --- | --- | --- |
   | PartitionKey | String | the chosen ledger UUID |
   | RowKey | String | `head` |
   | Schema | Int32 | `1` |
   | Generation | String | `initial` |
   | Chunks | Int32 | `0` |
   | Length | Int32 | `0` |
   | Hash | String | empty string (present, not null/omitted) |

   This marker represents an empty ledger. It is replaced on the first actual state change. Any unexpected pre-existing entities or missing state in a previously used store require investigation rather than a new bootstrap.
4. **Enable the App Service system-assigned managed identity.** The Table client uses only `ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)`, with SDK diagnostic content logging disabled. No application secret or connection string is required. This is separate from the existing Verified ID Request Service authentication configuration and does not grant Graph or Verified ID permissions.
5. **Grant only Table data access.** The exact runtime operations are entity reads, inserts and conditional replacements. For minimum permissions, define a custom role with no management `Actions` and these two `DataActions`, then assign it to the app's managed identity at the dedicated table resource scope:

   ```text
   Microsoft.Storage/storageAccounts/tableServices/tables/entities/read
   Microsoft.Storage/storageAccounts/tableServices/tables/entities/write
   ```

   `entities/write` covers insert/merge/replace. The application needs no entity delete, table create/delete, account keys, Owner, Contributor, or Microsoft Graph permissions. If using standard roles instead of a custom role, `Storage Table Data Contributor` at table scope is the narrowest suitable built-in write role, but it includes additional delete/table-management rights; that is a broader owner policy choice, not a runtime requirement. Provisioning, cleanup and any backup belong to the owner identity. Table-scoped role assignment can be performed with owner-run CLI/PowerShell/ARM; do not broaden to subscription scope for convenience. Allow role propagation before checking access.
6. **Allow authenticated network access.** Ensure the app can reach the primary Table HTTPS endpoint. Free F1 has no VNet integration; a private-endpoint-only storage network requires a different hosting/network decision. Use the storage firewall and authentication according to the approved pilot policy; do not enable anonymous access or distribute account keys. Restrict operator access because journal owner IDs and request metadata are identity-related data.
7. **Set these Linux App Service environment variables manually:**

   | Setting | Value |
   | --- | --- |
   | `Aya__RequestTable__Endpoint` | `https://<account>.table.core.windows.net` |
   | `Aya__RequestTable__TableName` | the dedicated table name |
   | `Aya__RequestTable__LedgerId` | the bootstrap partition UUID |
   | `Aya__IssuanceEnabled` | `false` |
   | `Aya__VerificationEnabled` | `false` |

   Remove the obsolete `Aya__RequestStorePath` setting when preparing the approved deployment; it cannot select the old backend. Continue the existing Easy Auth, callback, contract, synthetic eligibility and pilot-validity checklist. Storage configuration does not authorize activation.
8. **Verify storage while disabled after separately approved deployment.** Use the reviewed commit. With an authorized synthetic session, a GET to `/api/request-status?id=<fresh-nonzero-UUID>` should return 404 for an absent request after a successful store read; 503 indicates storage/configuration/bootstrap failure. Repeat after restarting the app. This read-only check cannot prove write permission. The owner must additionally verify entity insert, exact-ETag replace/conflict and read-after-restart using disposable synthetic probe entities and the application's managed identity in an approved local-to-App-Service diagnostic procedure, not a new public endpoint. Do not modify `head` for this probe. Record only pass/fail. End-to-end callback/state writes remain a gate for the separately approved activation window. No live probe was run during development.
9. **Approve retention and recovery before activation.** Current state retains the existing ten-minute pending deadline, service-expiry plus two-minute callback grace and 24-hour result visibility. Old snapshot generations and abandoned staging chunks remain in the table even after logical expiry; there is no automatic physical retention/garbage collection. Approve a short attended window and a teardown date. Stop the application, ensure no live test/recovery remains, resolve uncertain credentials, then have the owner delete the dedicated pilot table (or archive it under a separately approved restricted retention policy before deletion). Never prune chunks while the application is running. Reuse requires a deliberately new empty table/ledger, not silently resetting existing state. A restore from an old backup can omit acknowledged operations; it must remain disabled pending reconciliation, not automatic resumption.
10. **Keep availability expectations limited.** F1 CPU quotas, idle unloading and lack of SLA can interrupt callbacks even with correct storage. Check quota headroom, warm the app and supervise the window. Do not treat this change as approval for production, scale-out, automatic offboarding or automated revocation. Manual revocation must still be demonstrated first.

## Evidence and remaining gates

Synthetic tests exercise the real persistence adapter over mocked Table operations and the Azure SDK over an in-memory HTTP handler. They cover definite conflicts, bounded retries, ambiguous commits, interrupted multi-chunk writes, corrupt/missing state, two competing writers, read consistency, callback ordering/duplicates, owner isolation, expiration during retry, unavailable storage and application restart. Every original regression remains; the old file adapter's process-kill tests are historical local coverage, not evidence about Azure Table durability.

No tests use Azure, Entra, Graph or Verified ID services. Managed identity/RBAC propagation, firewall reachability, actual Table behavior and F1 latency require owner-run qualification after approval. Service-side conditional updates are relied on; operator changes, head deletion, storage account loss and stale backup restores are outside the atomicity guarantee. No public storage diagnostic endpoint is introduced.

## Microsoft references

- [Entity model and property limits](https://learn.microsoft.com/en-us/rest/api/storageservices/understanding-the-table-service-data-model)
- [Conditional ETag updates](https://learn.microsoft.com/en-us/rest/api/storageservices/update-entity2)
- [Entity group transaction limits](https://learn.microsoft.com/en-us/rest/api/storageservices/performing-entity-group-transactions)
- [Table roles and operation permissions](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/storage#storage-table-data-contributor)
- [Table-scoped role assignments](https://learn.microsoft.com/en-us/azure/storage/tables/assign-azure-role-data-access)
- [App Service limits](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits#app-service-limits)

## Local validation for this change

From `1-asp-net-core-api-idtokenhint`, using SDK 10.0.404:

```text
dotnet restore ../tests/Aya.EmployeeId.Tests/Aya.EmployeeId.Tests.csproj
dotnet build ../tests/Aya.EmployeeId.Tests/Aya.EmployeeId.Tests.csproj -c Release --no-restore --no-incremental
dotnet test ../tests/Aya.EmployeeId.Tests/Aya.EmployeeId.Tests.csproj -c Release --no-build --no-restore
node --test ../tests/Aya.EmployeeId.Ui.Tests/*.test.cjs
dotnet list ../tests/Aya.EmployeeId.Tests/Aya.EmployeeId.Tests.csproj package --vulnerable --include-transitive --no-restore
```

Result: Release build passed; 171 .NET tests (136 original plus 35 additional) and 22 browser-flow tests passed, none failed/skipped. The package advisory check reported no vulnerable packages in its current NuGet source. Restore retains NU1510 for the old Session reference; the full build retains two CS8632 warnings and the newer Azure.Identity package surfaces CS0618 on the unchanged Request Service token helper. That helper is outside this storage milestone. The test runner retains its informational artifacts-directory fallback notice.

An OS network-namespace isolation attempt was unavailable in this environment (`/proc/self/uid_map` is read-only). Tests instead use injected in-memory Table operations, a non-network HTTP handler for SDK validation, and the existing mocked profile/Request Service/token providers. No live Microsoft service tests were run. GitHub CI must also pass on the published PR head; local results do not substitute for that gate.
