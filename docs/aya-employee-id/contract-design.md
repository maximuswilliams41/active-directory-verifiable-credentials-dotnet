# Aya Employee ID contract: review proposal

This is the original PR #4 proposal. PR #4 is now merged; [the employee experience design](employee-experience.md) records the subsequent approvals and PR #5 implementation. The approved type, minimum visible fields, text branding and indexed-key mapping are implemented there; validity remains unset pending a six-versus-twelve-month decision. Statements below about the application not yet emitting the key describe the PR #4 baseline, not the reconciled branch. Microsoft configuration and live testing remain unperformed.

## Compatibility confirmed, tenant configuration not inspected

Microsoft's [rules/display model](https://learn.microsoft.com/en-us/entra/verified-id/rules-and-display-definitions-model) supports an indexed claim in `idTokenHints.mapping`. Thus an immutable Aya employee key can be part of the intended credential contract. This is documentation/schema confirmation, not evidence that a tenant contract already contains that claim.

The checked-in `CredentialFiles/VerifiedCredentialExpertRulesDefinition.json` currently uses type `VerifiedCredentialExpert`, indexes `lastName`, and sets `validityInterval` to 2,592,000 seconds (30 days). It is unchanged by this milestone. The application does not yet emit `ayaEmployeeKey`; the local module's approved registry is supplied only by synthetic tests.

## Proposed identifier

Use `ayaEmployeeKey`: an opaque UUID assigned once from an approved Aya registry, rendered as lowercase hyphenated GUID text. The registry maps it to tenant ID and Entra object ID. Never use email, name or a guessed employee number. Do not assign the key to another person or automatically associate a recreated Entra account. Rehire and account replacement require an explicit owner-reviewed mapping policy.

The key is a personal identifier even when opaque. Keep registry access restricted; avoid adding it to wallet display labels. It remains a credential claim and may be disclosed during presentation, so claim minimization needs owner review.

Proposed rules mapping fragment (not a complete contract; no file is loaded by the app):

```json
{
  "outputClaim": "ayaEmployeeKey",
  "inputClaim": "$.ayaEmployeeKey",
  "required": true,
  "indexed": true
}
```

Microsoft supports one indexed claim. All other mappings, including last name, must have `indexed: false`. Searching requires the exact original value and the appropriate contract ID. Index changes are not a migration of old issued credentials. Inventory old contracts separately; unsearchable or ambiguous historical credentials require an explicit remediation/reissuance decision. See [issuer revocation guidance](https://learn.microsoft.com/en-us/entra/verified-id/how-to-issuer-revoke).

## Primary credential decisions still required

| Item | Proposal / decision |
| --- | --- |
| Credential type | `AyaEmployeeId` proposed; approve exact spelling and versioning before implementation. |
| Display title | `Aya Employee ID`. |
| Issuer display | Aya's approved organization/legal display name. |
| Card artwork | Approved logo, colors, accessibility/contrast and localization; no sample branding. |
| Visible claims | Minimum approved employee name, employee number and organization; decide whether job title is useful and how changes are handled. |
| Internal indexed claim | Required `ayaEmployeeKey`; not a replacement for a human-readable employee number. |
| Validity | Explicit owner decision in seconds. Existing 30-day sample value is a baseline for discussion, **not approved Aya policy**. Define renewal, expired-card handling and any maximum override. |
| Employment meaning | A valid card reflects issuance eligibility plus later revocation checks. The accepted 48-hour target / 72-hour escalation window means it is not an instantaneous employment-status guarantee. |

## Manual Microsoft steps for the owner, after separate authorization

1. In a nonproduction Verified ID environment, create/version the approved Aya contract and its rules/display definitions. Replace the generic type and display content and set the approved `validityInterval` explicitly.
2. Add the required indexed mapping above to the `idTokenHints` attestation, making every other claim unindexed. Confirm input and output names agree with the later application claim producer.
3. Record the authority, contract ID and manifest reference privately. Register every relevant historical contract with the future revocation adapter; do not place environment values in this repository.
4. Approve and establish the immutable-key source and account mapping. The later issuer integration must obtain it from that trusted registry, check disabled/offboarded status and durably record issuance. Do not let a client supply the key. Keep issuance disabled until those controls are complete.
5. Separately configure app-only `User.Read.All` for account reads and Verified ID Admin API `VerifiableCredential.Credential.Search` / `VerifiableCredential.Credential.Revoke` for the revocation identity, with required consent. No runtime directory-write or contract-write permission is proposed. Validate this minimum permission set using synthetic data; do not broaden it automatically on errors.
6. Configure the future hourly host/daily scan, durable shared storage, scoped workload identity, alerts, independent missed-run/outbox monitoring and retention. This PR provisions none of these.
7. With later approval, issue only a synthetic test credential under the approved contract. Confirm exact-key lookup, multiple-credential coverage, revocation status readback and rejection by a verifier with `allowRevoked=false`. Test your manual portal revocation procedure too; Microsoft's portal guidance includes the administrator's Key Vault signing permission prerequisite.

Return only pass/fail outcomes: one required indexed claim, chosen type/display/validity, synthetic lookup coverage, status-confirmed revocation, presentation rejection, emergency procedure and alert delivery. Do not return tokens, key material, employee data or credential contents.
