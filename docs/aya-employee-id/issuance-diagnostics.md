# Issuance diagnostics for an owner-approved pilot

`POST /api/issuer/issuance-request` emits application event **7200**, named
`AyaIssuance`, in logger category `AspNetCoreVerifiableCredentials.IssuerController`.
It generates a new random `IssuanceCorrelationId` for each controller invocation
and returns it in **`X-Aya-Issuance-Correlation-ID`**. Incoming correlation headers
are not used. The ID is independent of the persisted request ID, employee identity,
Microsoft request ID and callback state. Response JSON and HTTP status behavior
are unchanged; the response header is the only new client-visible diagnostic.

The event has exactly these structured fields: `IssuanceCorrelationId`,
`ReasonCode`, `Stage`, `HttpStatus`, `ServiceHttpStatus`, `ValidationReason`, `PrefixCategory` (plus the logger's fixed
message template). `Started` is Information level, with no HTTP statuses yet.
The terminal event is Warning for unsuccessful responses and Information for 200.
Reasons and stages are closed enums. No exception object/message, payload, header,
credential, claim, PIN, QR, issuance URL, persisted ID or employee data is logged.

## Interpretation

| Reason | Meaning | Application HTTP |
| --- | --- | --- |
| `TokenUnavailable` | Application-token provider returned no token | 502 |
| `TokenAcquisitionFailed` | Application-token provider threw | 502 |
| `ServiceRequestPreparationFailed` | Local outbound-message preparation failed | 502 |
| `ServiceTransportFailed` | Sending/buffering the service request failed or timed out | 502 |
| `ServiceRejected` | Received status other than the required 201; numeric status recorded | 400 |
| `ServiceResponseFailed` | Could not read/parse the 201 response | 502 |
| `ResponseValidationFailed` | 201 response failed link/expiry validation or timestamp conversion | 502 |
| `StateBindingFailed` | 201 response could not be bound to local state | 502 |
| `RequestAlreadyActive` | Existing nonterminal issuance for the same owner is still active | 409 |
| `StateCapacityReached`, `StateIdConflict`, `StateArgumentsInvalid` | Other state-creation refusal; existing `request_already_active` body retained | 409 |
| `IncompleteEmployeeProfile` | Existing pre-submission profile/configuration conflict | 409 |
| `PreSubmissionConflict` | Other pre-submission `InvalidOperationException` | 409 |
| `EmployeeClaimsFailed` | Fresh employee-profile retrieval/processing failed | 502 |
| `StorageUnavailable` | Storage read/commit failed; `Stage` identifies where | 503 |
| `RequestCreated` | Response accepted and state bound; **not credential issuance completion** | 200 |

Guard reasons are `IssuanceDisabled`, `ValidityUnconfigured`,
`RequestNotConfigured` (503), `IdentityDenied` (401), and `EmployeeNotEligible`
(403). `UnexpectedFailure` is the fixed fallback for an unclassified failure.
`ServiceHttpStatus` is present only if `SendAsync` returned a response. A missing
status does not prove Microsoft never received the request. In particular,
default HTTP response buffering can fail before that response reaches the caller.

If recording an uncertain outcome itself fails, the terminal event reports
`StorageUnavailable` / `OutcomeRecording` / 503. It does not claim the uncertain
state was persisted. The adapter, ETag handling, transaction ordering, retention,
ownership, callbacks and duplicate protection are unchanged.

## Correlate one future test

These instructions do not authorize deployment, enabling issuance, or another
issuance attempt. Use only after the owner separately approves that pilot test.

1. Confirm the deployed revision contains this change. Keep both activation flags
   disabled until the owner approves the test. Do not delete or reset existing
   uncertain requests to obtain a fresh attempt.
2. Open the existing Azure App Service Log stream and browser Developer Tools
   Network panel. Use **Preserve log**. This PR does not change logging settings;
   if the application category is filtered or the configured log provider does
   not reach Log stream, stop and review observability separately with the owner.
   Do not enable body/header/identity logging.
3. During the separately approved synthetic test, submit once through the normal
   portal. Select its `POST /api/issuer/issuance-request` Network entry. Read only
   the response status and response header `X-Aya-Issuance-Correlation-ID`.
   Do not replay the request or export a HAR, cookies, bodies, tokens, PIN or QR.
4. Find that exact correlation ID in Log stream. Record the UTC time, fixed reason,
   stage, application HTTP status, service HTTP status (if any), `ValidationReason`
   and `PrefixCategory`. These values
   are sufficient to identify the failing boundary without sharing employee data.
5. Stop after an unsuccessful result. A subsequent 409 has its own correlation
   ID and cannot explain the preceding failure. Do not retry automatically or
   clear state. Existing owner-bound status polling remains the read-only option.

A framework rejection before the action (for example antiforgery validation),
proxy-generated response, or abrupt process termination may have no terminal
event/header. `Started` without a terminal event is incomplete evidence, not
success. The request ledger does not persist these diagnostic IDs, and log
retention/delivery remains dependent on the host's existing logging provider.
This change cannot recover a historical exception or diagnose Microsoft's full
error details without further separately approved investigation.

## Synthetic validation

Run from `1-asp-net-core-api-idtokenhint` with the selected .NET 10 SDK:

```sh
dotnet restore ../tests/Aya.EmployeeId.Tests/Aya.EmployeeId.Tests.csproj
dotnet build ../tests/Aya.EmployeeId.Tests/Aya.EmployeeId.Tests.csproj --configuration Release --no-restore
dotnet test ../tests/Aya.EmployeeId.Tests/Aya.EmployeeId.Tests.csproj --configuration Release --no-build --no-restore
node --test ../tests/Aya.EmployeeId.Ui.Tests/*.test.cjs
```

The diagnostic tests use synthetic identities, token-provider/HTTP mocks and local
persistence. They assert response compatibility, retained uncertain state across
host restart, duplicate suppression, callback-binding rejection, storage failures,
closed structured fields, no logged exception object and exclusion of synthetic
sensitive values. Existing Table SDK/ETag and authorization regressions remain in
the complete suite. These tests validate application logging; they do not certify
third-party middleware, platform logs or independently configured telemetry.

## Response-validation detail

Event 7200 also includes `ValidationReason`, a closed enum. Existing reason codes,
stages, HTTP behavior and correlation remain unchanged. It identifies the first
failed check; no response value or length is recorded. `None` means no failure was
attributed to these checks, not that credential issuance completed.

| ValidationReason | Check |
| --- | --- |
| `UrlValueInvalid` | JSON URL value could not be read using the existing conversion |
| `UrlMissing`, `UrlTooLong`, `UrlPrefixRejected` | Missing URL, existing 8192-character bound, or exact protocol prefix |
| `UrlQueryInvalid` | Query parsing threw |
| `RequestUriMissing`, `RequestUriAmbiguous`, `RequestUriInvalid` | Required single absolute request URI |
| `RequestUriSchemeRejected`, `RequestUriHostRejected` | Existing HTTPS and approved-host checks |
| `RequestUriPortRejected`, `RequestUriUserInfoRejected`, `RequestUriFragmentRejected` | Existing default-port, no-userinfo and no-fragment checks |
| `ExpiryMissing`, `ExpiryNotInteger`, `ExpiryOutOfRange` | Missing expiry, existing Int64 parsing, or Unix-seconds conversion range |

For a separately approved future test, capture the same correlation header and
fixed event fields described above, including `ValidationReason` and `PrefixCategory`. Do not capture
or share the URL, request ID, body, QR, PIN, token, exception or employee fields.
A failure during unknown-state recording can instead produce `StorageUnavailable`
at `OutcomeRecording`; `ValidationReason` then retains the preceding check detail.

### Compatibility review

Reviewed against the public [Microsoft issuance response specification](https://learn.microsoft.com/en-us/entra/verified-id/issuance-request-api#successful-response).
It documents HTTP 201, string `requestId` and `url`, integer `expiry`, and QR output.
Its URL example uses the issuance prefix and request-URI host accepted here; its
expiry example is consistent with Unix seconds. A synthetic version of that shape
with current expiry passes. This documents compatibility with the example, not a
guarantee that every live response matches it. The reference does not enumerate
all permitted deep-link variations or promise Aya's ten-minute expiry bound.

Aya still checks exact prefix/host and URI structure before returning a wallet
link. Missing or nonnumeric expiry fails at response validation; an integer outside
the Unix-seconds conversion range does too. A numeric millisecond timestamp would
also fail conversion at current dates; this is a tested hypothesis, not evidence
of Microsoft's actual response. The request expiry is separate from credential
validity; changing the credential's lifespan is not a correction for this check.

`requestId` reading and binding occur only after URL and expiry conversion pass.
Binding rejects missing/oversized service IDs, an expiry that is past or more than
ten minutes ahead, absent/already-bound/expired local state, and inconsistent early
callbacks. The same specification's [callback section](https://learn.microsoft.com/en-us/entra/verified-id/issuance-request-api#callback-events)
documents returning the original state and the service request ID. Aya's additional
ordering, retention and duplicate controls remain local policies.

`ResponseValidationFailed` at `ResponseValidation` with service HTTP 201 establishes
that a response was received and parsed as an object, then rejected before binding.
It does not establish which URL/expiry predicate failed, nor demonstrate callback
reachability or successful credential issuance. Request-ID binding and callback
correlation cannot be the direct cause of that specific event. The confirmed issuance prefix `openid-vc://?` is now accepted alongside
`openid://vc?`; the destination, expiration and binding checks remain in force.

### Rejected issuance prefix category

`PrefixCategory` is one closed enum on the existing event, never a URL fragment.
It is populated only after the issuance validator returns `UrlPrefixRejected`.
Exact, case-sensitive ordinal matching includes the query delimiter:

| PrefixCategory | Fixed structural match |
| --- | --- |
| `NotApplicable` | Started event or any path without an issuance prefix rejection |
| `OpenIdVcSlashQuery` | Starts exactly with `openid://vc/?` |
| `OpenIdVcSchemeQuery` | Historical diagnostic value for `openid-vc://?`; now accepted at the prefix check |
| `Other` | Any other rejected prefix; no input characters are recorded |

Only the confirmed `openid-vc://?` variant is now accepted, in both server and
employee-page issuance validation. `openid://vc/?` and unrecognized formats remain
rejected. Accepted links still require a single HTTPS `request_uri` at the approved
Microsoft hostname, default port, and no destination credentials or fragment.
The employee page rejects raw outer fragments and parses the actual URL query;
empty destination fragment markers are also rejected.
Classification of rejected prefixes does not parse the
query, validate the destination, or imply that a link is safe to accept. Length
and missing-value checks still precede prefix validation. If recording the
uncertain outcome fails, the category retains the preceding prefix-rejection
detail, just like `ValidationReason`. Response bodies, retention, duplicate
protection, presentation validation and all other server URL acceptance rules are unchanged.
Accepted prefixes use `NotApplicable`, including when a later destination check fails.

Owner gate: review and approve merge/deployment separately; keep both activation
flags disabled until a separately approved synthetic test. Confirm the deployed
revision and the existing flags-off populated-ledger result before one normal
portal attempt with the already approved synthetic identity and unchanged role,
tenant, object-ID and registry restrictions. Record only the generated correlation
ID, UTC time and fixed event fields above. Do not capture a URL, body or prefix
sample, including when the category is `Other`. Stop on failure, preserve existing
requests, and return issuance to disabled; verification stays disabled. This
category is evidence for a later review, not approval to relax URL validation.
