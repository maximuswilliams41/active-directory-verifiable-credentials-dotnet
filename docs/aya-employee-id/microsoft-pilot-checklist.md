# Owner-only Microsoft pilot checklist

Execute only after the application is reviewed and separately authorized for nonproduction deployment. These are manual owner steps, not actions performed by Codex. Never send employee information, tokens, secrets, QR payloads or credential contents back to the conversation. Return sanitized pass/fail observations instead.

## 1. Approve and isolate the test

Record the reviewed commit, nonproduction application/slot, synthetic test accounts, supported Authenticator devices and limited test window. Keep `Aya:IssuanceEnabled=false` and `Aya:VerificationEnabled=false` during preparation. Do not use real employee identities. No automatic deployment is introduced; do not run a deployment workflow until separately approved.

## 2. Qualify storage before choosing a path

Follow the [storage decision and qualification](request-reliability.md#storage-decision-before-deployment). Identify the actual hosting OS and persistent mount; do not assume `/tmp`, container writable layers or local App Service disk survive recycling. For a custom container, establish the appropriate persistent-storage configuration for that hosting model. Confirm private read/write access, atomic replacement, exclusive writer locking and restart/recycle survival using non-sensitive temporary probe data first. Use one serving instance/process. Keep journal files outside web/static roots and deployment packages. If any required behavior is unproven, stop: use a reviewed alternative adapter before deployment rather than enable issuance.

After qualification, set an absolute `Aya:RequestStorePath` to a dedicated private file. On Linux environment settings, use `Aya__RequestStorePath`; colon-delimited names denote the logical .NET configuration keys throughout this checklist. Restrict directory/file access to the application and approved operators. The `.lock` file remains after shutdown as an initialization marker; do not remove it during normal operations. Arrange protected backup and pilot teardown/retention handling.

## 3. Configure the authentication boundary

In the nonproduction App Service Authentication settings, use the intended Microsoft Entra tenant and require authentication for application pages and APIs. Restrict access to approved pilot assignments and the expected application audience. Configure the login application's appropriate HTTPS redirect URI. Define/assign the application role `Aya.EmployeeID.Request` to the authorized synthetic pilot accounts. Confirm that the platform supplies tenant/object-ID/role claims and prevents direct requests from bypassing its trusted identity headers. The code relies on this platform boundary; attributes alone are not an authentication setup.

## 4. Enable delegated profile-token support

Enable the App Service token store and configure the Microsoft provider to request a Graph-targeted delegated token with the appropriate login scopes. Begin with delegated `User.Read`, which is the documented least-privileged own-profile permission; confirm that the actual request returns `id`, `displayName`, `employeeId`, `companyName`, `userType` and explicit Boolean `accountEnabled`. Reauthenticate after changes. Missing/unknown attributes deny issuance. Do not add directory/account-write permissions to fix a read problem; return a sanitized failure for a least-privilege review if the required properties are unavailable.

References: [Graph user reads](https://learn.microsoft.com/en-us/graph/api/user-get?view=graph-rest-1.0), [App Service token store and injected headers](https://learn.microsoft.com/en-us/azure/app-service/configure-authentication-oauth-tokens). Do not display or copy the tokens when reporting results.

## 5. Prepare synthetic eligibility and immutable mappings

Use an enabled Member test account with nonempty synthetic display name/employee number and the organization value `Aya Living Inc.`. In protected application configuration, populate `Aya:EmployeeRegistry` entries with `TenantId`, `ObjectId`, `EmployeeKey` and `Active=true`. Use an owner-assigned lowercase hyphenated UUID that will never be reassigned. Registry changes require a controlled restart. No directory extension is needed: the application supplies this claim from the private registry. Retain assignment history privately; do not commit mappings to GitHub.

## 6. Create the nonproduction Aya credential contract

Review `CredentialFiles/AyaEmployeeIdRules.template.json` and `AyaEmployeeIdDisplay.json`. Create the custom credential using type `AyaEmployeeId`, required ID-token-hint mappings for `displayName`, `employeeId`, `companyName`, `ayaEmployeeKey`, and only the employee key indexed. Apply Aya Living text branding and the three visible labels; do not display the key.

Approve an exact pilot day count (supported range 1–366); this does not select the later employee six/twelve-month policy. Set `Aya:CredentialValidityDays` and contract `validityInterval = days × 86400` consistently. The template's null value is not upload-ready. Because the app sends `expirationDate`, confirm `allowOverrideValidityOnIssuance=true` on the contract. If your tenant's available contract configuration cannot support this, stop and report the limitation rather than silently change validity behavior.

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

Use the reviewed commit and the separately approved nonproduction deployment procedure. Keep both activation flags false. Verify normal pages load, issuance cannot start and unapproved callers cannot access the verifier. Validate persistence with synthetic local request records and a controlled restart before contacting Request Service. Retain pass/fail evidence, not storage contents. A failing readiness check is a stop condition.

## 10. Run the separately approved controlled test window

Only with explicit activation approval, temporarily enable the required nonproduction flags. Sign in as the authorized synthetic account and use `/Issuer`. Confirm displayed fields, desktop QR and six-digit PIN (or same-phone deep link), Authenticator consent and actual expiration, and confirmed issuance success. QR creation alone is not success.

Use `/Verifier` as an authorized pilot account. Present the test credential and confirm `presentation_verified` through the application, with no raw credential/receipt exposed. Confirm owner isolation and safe failure with missing required eligibility using benign synthetic accounts. Restart the application during a controlled pending request and confirm status recovery; a crash before response binding must remain unconfirmed, not prompt automatic reissuance.

## 11. Confirm rejection and close the pilot

Manually revoke the synthetic credential through the supported owner procedure, then request presentation again and confirm rejection. Test expired-credential rejection using an appropriately approved synthetic fixture/test window; local automated tests already exercise expiry without altering clocks or live services. A revoked card may remain displayed: use electronic verification, not visual inspection.

Turn both flags off after the window. Resolve any uncertain requests before reissuing, clean up synthetic credentials/accounts as appropriate, and apply the approved journal retention/backup policy. Return only: reviewed commit, storage qualification pass/fail, eligibility result, contract/display/expiry result, callback/restart result, valid presentation result, revoked/expired rejection result, and confirmation that both flags are disabled. No automatic offboarding, renewal automation or scale-out is part of this pilot.
