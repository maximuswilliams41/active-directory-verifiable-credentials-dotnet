# Owner-only Microsoft pilot checklist

Execute only after the application is reviewed and separately authorized for nonproduction deployment. These are manual owner steps, not actions performed by Codex. Never send employee information, tokens, secrets, QR payloads or credential contents back to the conversation. Return sanitized pass/fail observations instead.

## 1. Approve and isolate the test

Record the reviewed commit, nonproduction application/slot, synthetic test accounts, supported Authenticator devices and limited test window. Keep `Aya:IssuanceEnabled=false` and `Aya:VerificationEnabled=false` during preparation. Do not use real employee identities. No automatic deployment is introduced; do not run a deployment workflow until separately approved.

## 2. Prepare managed-identity Table request storage

Complete the [Table resources, bootstrap, permissions and recovery checklist](table-request-storage.md#owner-only-configuration-in-order). The current runtime uses Table Storage; `Aya:RequestStorePath` no longer selects a filesystem adapter. Provision a dedicated pilot table, initialize its head exactly once, and grant the app's system-assigned managed identity narrowly scoped entity read/write access. Set the three `Aya__RequestTable__*` environment variables. Do not initialize missing state in a previously used ledger. Approve a pilot teardown/retention plan for historical snapshots before activation.

Colon-delimited names denote logical .NET keys; Linux environment variables use double underscores. Keep one App Service instance for this attended F1 pilot. Table persistence does not remove F1 quota, cold-start or callback-availability limits. No deployment or activation is authorized by setting these values.

## 3. Configure the authentication boundary

In the nonproduction App Service Authentication settings, use the intended Microsoft Entra tenant and require authentication for application pages and APIs. Restrict access to approved pilot assignments and the expected application audience. Configure the login application's appropriate HTTPS redirect URI. Define/assign the application role `Aya.EmployeeID.Request` to the authorized synthetic pilot accounts. Confirm that the platform supplies tenant/object-ID/role claims and prevents direct requests from bypassing its trusted identity headers. The code relies on this platform boundary; attributes alone are not an authentication setup.

Identity compatibility: `X-MS-CLIENT-PRINCIPAL-ID` is an opaque provider identifier and need not equal the Entra Object ID. The app requires one nonblank provider identifier, but derives ownership from validated tenant/Object ID claims and the exact `Aya.EmployeeID.Request` role in the trusted Easy Auth principal. Registry matching and Graph `/me` ID/account checks remain mandatory. Do not replace registry Object IDs with `/.auth/me.user_id`, weaken role/registry restrictions, or expose identity headers or tokens in diagnostics. The platform must continue to validate authentication and prevent direct header injection. See [Easy Auth header definitions](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-user-identities#access-user-claims-in-app-code) and [Entra oid/sub semantics](https://learn.microsoft.com/en-us/entra/identity-platform/id-token-claims-reference#payload-claims). A later owner-authorized pilot must verify this behavior at the actual Easy Auth boundary; synthetic TestServer headers do not qualify that boundary.

## 4. Enable delegated profile-token support

Enable the App Service token store and configure the Microsoft provider to request a Graph-targeted delegated token with the appropriate login scopes. Begin with delegated `User.Read`, which is the documented least-privileged own-profile permission; confirm that the actual request returns `id`, `displayName`, `employeeId`, `companyName`, `userType` and explicit Boolean `accountEnabled`. Reauthenticate after changes. Missing/unknown attributes deny issuance. Do not add directory/account-write permissions to fix a read problem; return a sanitized failure for a least-privilege review if the required properties are unavailable.

References: [Graph user reads](https://learn.microsoft.com/en-us/graph/api/user-get?view=graph-rest-1.0), [App Service token store and injected headers](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-oauth-tokens). Do not display or copy the tokens when reporting results.

## 5. Prepare synthetic eligibility and immutable mappings

Use an enabled Member test account with nonempty synthetic display name/employee number and the organization value `Aya Living Inc.`. In protected application configuration, populate `Aya:EmployeeRegistry` entries with `TenantId`, `ObjectId`, `EmployeeKey` and `Active=true`. Use an owner-assigned lowercase hyphenated UUID that will never be reassigned. Registry changes require a controlled restart. No directory extension is needed: the application supplies this claim from the private registry. Retain assignment history privately; do not commit mappings to GitHub.

## 6. Create the nonproduction Aya credential contract

Review `CredentialFiles/AyaEmployeeIdRules.template.json` and `AyaEmployeeIdDisplay.json`. Create the custom credential using type `AyaEmployeeId`, required ID-token-hint mappings for `displayName`, `employeeId`, `companyName`, `ayaEmployeeKey`, and only the employee key indexed. Apply Aya Living text branding and the three visible labels; do not display the key.

The controlled pilot uses the existing contract's seven-day `validityInterval = 604800`. The app omits `expirationDate`; leave `allowOverrideValidityIntervalOnIssuance=false`. No Admin API permission or expiration-override change is needed for issuance. The template's null validity remains intentionally undecided for future employee contracts; do not upload it unchanged or replace the existing pilot contract with it.

Before separately authorized activation, confirm `VerifiedID:CredentialManifest` points to that pilot contract and `Aya:CredentialValidityDays=7` matches it. The application setting still gates readiness and supplies portal wording; it no longer controls issued expiration or validates the remote contract. An owner-observed mismatch is a stop condition. Verify actual seven-day expiry in the controlled Authenticator test. The final employee six/twelve-month policy remains undecided; see [future contract validity management](employee-experience.md#contract-controlled-validity-and-future-versions).

References: [rules/display schema](https://learn.microsoft.com/en-us/entra/verified-id/rules-and-display-definitions-model), [issuance expiration override](https://learn.microsoft.com/en-us/entra/verified-id/issuance-request-api). Keep signing-key verification material available for the chosen lifetime. Existing older contracts are not migrated by this change.

## 7. Configure the Request Service identity and issuer

Confirm the existing application identity has the Request Service application permission `VerifiableCredential.Create.All` and required admin consent. Use the application's supported credential method (managed identity, certificate or protected secret) and matching configuration; never place a credential in source. Keep the Graph delegated profile token separate from the app-only Request Service token.

Privately set `VerifiedID:TenantId`, `VerifiedID:DidAuthority`, `VerifiedID:CredentialManifest`, the appropriate application identity/credential settings and `VerifiedID:ApiEndpoint`. The implemented wallet-link/manifest validation targets Microsoft's global Verified ID service; do not silently substitute a sovereign-cloud endpoint. The verifier pins `AyaEmployeeId` and the configured Aya DID; confirm that DID's linked domain verifies, because linked-domain validation stays enabled. The pilot does not need Graph write or automated-revocation Admin API permissions.

Reference: [Request Service authorization](https://learn.microsoft.com/en-us/entra/verified-id/get-started-request-api).

## 8. Configure callbacks without opening the application

Set `Aya:PublicOrigin` to the exact HTTPS scheme and host, without a path, query, fragment or user information. Privately configure a strong random `Aya:CallbackApiKey`. Both request paths use this setting; the legacy `API-KEY` setting is no longer used.

Make these exact endpoints reachable by Request Service without an interactive sign-in redirect:

- `POST /api/issuer/issuecallback`
- `POST /api/verifier/presentationcallback`

If using Easy Auth excluded paths, exclude only those exact callback paths, not `/api/*`. Application-level callback key checks remain mandatory. Confirm HTTPS/proxy routing preserves their method, body and `api-key` header. Keep profile, creation and polling routes authenticated. Verify deployment/access diagnostics do not collect authorization headers, callback bodies, PINs, receipts or employee claims. Reference: [presentation callback requirements](https://learn.microsoft.com/en-us/entra/verified-id/presentation-request-api).

## 9. Deploy disabled, only after explicit approval

Use the reviewed commit and the separately approved nonproduction deployment procedure. Keep both activation flags false. Verify normal pages load, issuance cannot start and unapproved callers cannot access the verifier. Complete the disabled Table read check and owner-run synthetic write/conflict/restart qualification described in the Table checklist before contacting Request Service. Retain pass/fail evidence, not storage contents. A failing readiness check is a stop condition.

## 10. Run the separately approved controlled test window

Only with explicit activation approval, temporarily enable the required nonproduction flags. Sign in as the authorized synthetic account and use `/Issuer`. Confirm displayed fields, desktop QR and six-digit PIN (or same-phone deep link), Authenticator consent and actual expiration, and confirmed issuance success. QR creation alone is not success.

Use `/Verifier` as an authorized pilot account. Present the test credential and confirm `presentation_verified` through the application, with no raw credential/receipt exposed. Confirm owner isolation and safe failure with missing required eligibility using benign synthetic accounts. Restart the application during a controlled pending request and confirm status recovery; a crash before response binding must remain unconfirmed, not prompt automatic reissuance.

## 11. Confirm rejection and close the pilot

Manually revoke the synthetic credential through the supported owner procedure, then request presentation again and confirm rejection. Test expired-credential rejection using an appropriately approved synthetic fixture/test window; local automated tests already exercise expiry without altering clocks or live services. A revoked card may remain displayed: use electronic verification, not visual inspection.

Turn both flags off after the window. Resolve any uncertain requests before reissuing, clean up synthetic credentials/accounts as appropriate, and apply the approved journal retention/backup policy. Return only: reviewed commit, storage qualification pass/fail, eligibility result, contract/display/expiry result, callback/restart result, valid presentation result, revoked/expired rejection result, and confirmation that both flags are disabled. No automatic offboarding, renewal automation or scale-out is part of this pilot.

## Read-only eligibility diagnostics

The eligibility endpoint emits Information-level event `7100` (`AyaEligibility`) in category `AspNetCoreVerifiableCredentials.AyaIssuanceEligibilityController`. Each handled invocation logs `CheckStarted` and one terminal fixed `ReasonCode`, with the same fresh server-generated `EligibilityRequestId` (32 hexadecimal characters). It is not an employee ID, stored credential/request-state ID, incoming trace ID or client-supplied correlation header. No new response field/header or diagnostic endpoint is added. Use event timestamps and that ID to pair the two records in existing application logs; do not turn on HTTP-body/header or identity-token logging. Existing logging sinks/filters determine whether events are retained; this change does not configure Azure log collection.

| Fixed terminal code(s) | Server observation; existing HTTP behavior |
| --- | --- |
| `IssuanceDisabled`, `ValidityUnconfigured` | Readiness gate stopped the request; 503 with the existing `issuance_disabled` error. No identity, registry eligibility or Graph checks run. |
| `PrincipalMissing`, `PrincipalTooLarge`, `PrincipalMalformed` | The server's Easy Auth principal is absent or cannot be accepted; 403. |
| `ProviderIdMissing`, `ProviderIdAmbiguous` | No single nonblank provider identifier reached the server; 403. |
| `ObjectIdMissing`, `ObjectIdInvalid`, `ObjectIdConflicting`, `RequiredRoleMissing` | Required server-side Object ID/role checks failed; 403. |
| `ConfiguredTenantInvalid`, `TenantClaimMissing`, `TenantClaimInvalid`, `TenantMismatch`, `TenantPrincipalMalformed` | Configured tenant or server tenant-claim validation failed; 403. |
| `RegistryEmpty`, `RegistryNoMatch`, `RegistryInactive` | The loaded registry has no rows, no matching tenant/Object ID pair, or an inactive match; 403. |
| `RegistryInvalidEntry`, `RegistryDuplicateKey`, `RegistryDuplicateAccount` | Invalid or ambiguous registry, including inactive rows; existing 409 `incomplete_employee_profile` response. |
| `GraphTokenMissing` | The server did not receive a nonblank delegated access token; 403. |
| `GraphObjectIdMismatch`, `GraphUserTypeNotMember`, `GraphAccountStateUnconfirmed`, `GraphAccountDisabled`, `CompanyMismatch` | A successful server Graph response failed the indicated check; 403. |
| `CompanyNameMissing`, `DisplayNameMissing`, `EmployeeIdMissing` | Required profile field absent/blank; existing 409 response. |
| `GraphLookupFailed` | Graph non-success response or HTTP request failure; existing 502 response. |
| `Eligible` | All checks passed; existing 200 response. No credential is issued by this GET. |
| `AuthorizationDeniedUnclassified`, `ProfileInvalidUnclassified` | Defensive fallback for an unexpected existing authorization/profile exception; existing 403/409 retained, exception details never logged by this diagnostic. |

The first failed check wins; subsequent checks are not executed for diagnostics. Unhandled exceptions retain their existing error handling; a lone `CheckStarted` is not a completed eligibility decision. Codes are selected by code, never copied from exception messages, claims or upstream response bodies. Ordinary 403 responses remain exactly `{"eligible":false}`. Correlation IDs and reasons are server-log-only, including for anonymous/invalid requests.

Owner investigation, without enabling issuance or changing configuration:

1. Verify the existing deployment's source commit, target application/slot, exact request origin and request timestamp. Current source with effective `Aya:IssuanceEnabled=false` returns 503 before authorization; a contemporaneous application 403 cannot come from that execution path. Compare evidence for an older request, wrong target/artifact, stale process configuration or a higher-priority override rather than assuming another identity defect. No such live cause has been established from repository inspection.
2. The app uses `Host.CreateDefaultBuilder` configuration. Environment variables override JSON; command-line arguments can override environment variables. Inspect only the relevant settings privately, not an environment/configuration dump. `Aya__EmployeeRegistry__0__TenantId`, `ObjectId`, `EmployeeKey` and `Active` are separate leaf settings under the common prefix. A JSON value at `Aya__EmployeeRegistry__0` alone is not expanded into these leaves. `EmployeeKey` is the registry field name; `ayaEmployeeKey` is the output claim name. Match the registry tenant privately to `VerifiedID__TenantId`.
3. `AyaEmployeeRegistry` snapshots all merged configuration children when its singleton is constructed. It does not read the registry from Graph, a browser session or Table Storage, and it does not reload rows per request. Review process/deployment timing without restarting or changing settings during this read-only investigation. One active entry does not exclude invalid inactive/leftover entries. No row values, hashes or employee counts are added to logs.
4. After a separately approved deployment of these diagnostics, one read-only eligibility GET with issuance still disabled should log `IssuanceDisabled` and return 503. It intentionally cannot reveal downstream eligibility while disabled. Do not bypass the gate or enable issuance for troubleshooting without separate approval. If a request does not reach this version of the controller, these events cannot diagnose it.
5. Browser `/.auth/me` data and browser-side Graph calls do not prove the exact claims, token or Graph response received by the application. Once a future authorized test reaches eligibility checks, the fixed code identifies the server-side failure without exposing header contents. Do not change accepted claim aliases, role assignments, registry restrictions or permissions merely because browser and server observations differ.

Return only the deployed commit, HTTP status, terminal reason code, generated diagnostic ID and timestamp. Do not return raw logs, authentication headers, tokens, claims, registry rows or Graph profile data. Reference: [configuration precedence and environment keys](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-10.0), [Easy Auth identity headers](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-user-identities).
