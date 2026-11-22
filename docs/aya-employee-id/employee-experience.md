# Aya Employee ID and Authenticator milestone

## Review scope

PR #4 was approved and merged into main at `7dfbc1d4bbcab2450ef1325dafac7e356fcaf993`. This branch incorporates that main commit and preserves its revocation simulation, presentation checks and regression tests without extending revocation infrastructure. This milestone implements the approved `AyaEmployeeId` type, minimum visible fields, immutable-key mapping, text branding, employee portal flow and synthetic tests. Issuance stays disabled; no Microsoft service was configured or contacted. PR #5 still requires director review and explicit merge approval.

## Credential and employee experience

- Title: **Aya Employee ID**; issuer display: **Aya Living**. The owner approved text branding for now. Colors are provisional neutral tones, not a claim that final brand assets are approved. The employee pages no longer load Microsoft's sample logo or a remote credential manifest.
- Display claims: `displayName` (Employee name), `employeeId` (Employee number), `companyName` (Organization). These are the only visible employee fields.
- Required indexed claim: `ayaEmployeeKey`, a lowercase hyphenated UUID from the owner-managed registry. It is not displayed on the card or profile preview. It is still credential data and may be disclosed during presentation; it is not a secret.
- `CredentialFiles/AyaEmployeeIdRules.template.json` contains the complete proposed rules mapping with exactly one indexed claim. Its `validityInterval` is intentionally **null**: this template is not ready to upload until the owner chooses a lifespan. `AyaEmployeeIdDisplay.json` contains the Aya display definition. Historical generic sample definitions remain reference material and are not the proposed Aya contract.
- The portal explains eligibility, previews approved fields, presents an Authenticator deep link and desktop QR/PIN, and reports confirmed success, expiry, failure or uncertainty. Stopping polling does not falsely claim to cancel an already opened wallet request. No photo upload, Face Check, email-notification subsystem or renewal automation is added.
- Card/portal wording requires electronic employment verification. A displayed card or screenshot is not proof of current employment.

## Eligibility and claim mapping

Both profile preview and issuance use the same server-side eligibility checks: trusted App Service identity, explicit `Aya.EmployeeID.Request` role, matching configured tenant, matching Graph `/me` object ID, `Member` user type, `accountEnabled` explicitly true, Aya organization, complete minimum fields and an active unambiguous registry mapping. The existing Graph profile request now selects only the needed attributes; there is no new Microsoft API client or credential acquisition mechanism.

`Aya:EmployeeRegistry` is an owner-managed configuration array with `TenantId`, `ObjectId`, `EmployeeKey`, `Active`. Checked-in entries are empty. The server snapshots this configuration; it does not assign keys from user input or generate a new key on each request. Duplicate keys/accounts, invalid UUIDs, inactive/unmapped employees and wrong tenants fail closed. No registry identities are returned to the browser. Real entries must stay in access-controlled environment configuration and never enter GitHub or test fixtures.

**Governance limit:** configuration cannot prove historical immutability across administrator edits/restarts. Before live use, the owner must establish the authoritative assignment/history process, retain prior mappings, and prohibit key reuse/reassignment. This milestone introduces neither a cloud registry nor an HR synchronization service. Account replacement and rehire require explicit owner review. Configuration changes require a controlled restart; Graph account state is checked on each eligible request.

Issuance uses type `AyaEmployeeId`, the approved four-claim payload and explicit expiration derived from the configured day count. Desktop requests use a random six-digit PIN hashed with a fresh salt; the PIN appears only in the authenticated response/portal, not the QR payload. Profile and issuance responses are marked no-store. The existing App Service authentication boundary must be configured to prevent untrusted direct identity-header injection; this code does not establish that boundary itself.

## Six versus twelve months: decision pending

| Consideration | Six months | Twelve months |
| --- | --- | --- |
| Approximate annual renewal events for 160 continuing employees | 320 | 160 |
| Employee interruptions | Twice yearly | Once yearly |
| Profile/eligibility refresh through renewal | More frequent | Less frequent |
| Remaining cryptographic lifetime if offboarding revocation fails | Up to about six months | Up to about twelve months |
| Visual-card problem | Still exists | Still exists, with a longer potentially unexpired-looking period |

These are steady-state estimates, excluding initial issuance, turnover, lost devices and replacements. If each renewal requires five minutes of combined employee/support time, the difference is approximately 13 additional hours per year; that is an assumption for planning, not a measured result.

**Recommendation for review:** begin with six months until live revocation, electronic verification and support operations have been demonstrated. Twelve months becomes more attractive if renewals are burdensome and those controls are reliable. Neither choice is adopted in this branch. Automated revocation is currently mocked development, not an operating production control; a long lifespan must not rely on it as though deployed.

Revocation changes verification status, not possession of the wallet credential. Expiry also should not be assumed to remove its visual representation. Screenshots can persist regardless of either policy. Staff must use electronic verification that rejects revoked/expired credentials, not inspect the card image. Actual Authenticator rendering/labels require owner-run synthetic device tests. Microsoft's [revocation guidance](https://learn.microsoft.com/en-us/entra/verified-id/how-to-issuer-revoke) documents status checks and retained credential possession.

`Aya:CredentialValidityDays` remains **null**, with no fallback to 30 days. The supported configurable range is 1–366 days. Tests use 180 and 365 days only as synthetic examples approximating six/twelve months; they are not exactly six/twelve calendar months and are not approved policy. After the owner decides, set the exact day count and `validityInterval = days × 86400`. If calendar-month precision is required, agree that policy before implementing a calendar-based expiry calculation. Application expiry uses UTC; device display may use local time.

Longer lifetimes also require retaining signing-key verification material throughout the credential lifetime; owner key rotation/retention must be compatible. See [Microsoft key rotation guidance](https://learn.microsoft.com/en-us/entra/verified-id/how-to-rotate-keys).

## Owner-only Microsoft checklist, for later authorization

1. Approve the exact lifespan/day count. Keep `Aya:IssuanceEnabled=false` throughout configuration review.
2. Review the Aya rules/display files. Replace the template's null validity with the approved seconds, retaining only `ayaEmployeeKey` as indexed. Upload/create a nonproduction custom `AyaEmployeeId` contract. Confirm the approved branding and three visible labels in Authenticator; add an approved logo later if desired.
3. Store the correct issuer authority, contract/manifest reference and tenant privately in the application configuration. Confirm validity settings and the service's expiration override policy agree with the request's explicit `expirationDate`; do not permit a client-supplied override.
4. Establish the authoritative immutable key register and eligibility assignments. Configure only approved mappings in protected configuration. Confirm delegated Graph profile permissions return all selected fields, particularly `accountEnabled`; omitted or inaccessible values intentionally deny issuance. Resolve least-privilege permissions yourself rather than broadening them automatically.
5. Validate App Service authentication, tenant restrictions, employee role assignment, callback authentication and externally reachable callback URL. Existing process-local issuance correlation and early callback handling still need review before live rollout; this PR does not claim durable multi-instance issuance state.
6. Confirm the existing Request Service application permission (`VerifiableCredential.Create.All`) and credential handling are correct. No permission, credential or Microsoft configuration is changed by this code review.
7. Only after separate authorization, run a synthetic pilot on the intended Authenticator devices. Check fields, text branding, QR/deep link/PIN, actual expiry, successful callback, indexed lookup, expired/revoked presentation rejection and how revoked cards remain displayed. A supported manual emergency-revocation process is required until automated revocation is implemented and validated.

Return only non-sensitive pass/fail observations and policy choices. Do not send employee data, keys, access tokens or credential contents. Reference: [rules/display schema](https://learn.microsoft.com/en-us/entra/verified-id/rules-and-display-definitions-model), [issuance API](https://learn.microsoft.com/en-us/entra/verified-id/issuance-request-api).

## Local validation and release boundaries

From `1-asp-net-core-api-idtokenhint`:

```sh
dotnet restore ../tests/Aya.EmployeeId.Tests/Aya.EmployeeId.Tests.csproj
dotnet build ../tests/Aya.EmployeeId.Tests/Aya.EmployeeId.Tests.csproj --configuration Release --no-restore
dotnet test ../tests/Aya.EmployeeId.Tests/Aya.EmployeeId.Tests.csproj --configuration Release --no-build --no-restore
node --test ../tests/Aya.EmployeeId.Ui.Tests/*.test.cjs
```

Server tests mock Graph, token acquisition and issuance. Browser-flow tests use synthetic fetch responses and a controllable clock. Headless desktop/mobile checks can render the actual disabled `/Issuer` page in an outbound-network-isolated environment. Neither a browser preview nor mocked success demonstrates live Authenticator acceptance.

Reconciliation validation: SDK 10.0.404; restore and a full non-incremental Release build passed. The combined suite passed 100 .NET tests and 11 employee-flow tests, with zero failures or skips. The .NET suite ran with networking disabled. Restore reports the existing NU1510 redundant Session reference warning; compilation reports the two existing CS8632 nullable-annotation warnings. No regression test was removed.

PR validation has read-only repository permission, no secrets or cloud login, and builds/tests only. Deployment remains manual-only and unchanged. The reconciliation retains both revocation and employee-flow CI path filters, the shared test host's service accessor, and all regression tests from main and PR #5. The complete combined suite must pass locally and in GitHub CI before final review. Microsoft device acceptance remains an owner-only future check, not a result of these synthetic tests.
