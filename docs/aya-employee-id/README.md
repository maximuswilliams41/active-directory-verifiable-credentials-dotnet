# Aya Employee ID milestones

These are parallel workstreams. Local revocation development does not replace the primary credential-design work or authorize Microsoft configuration.

| Workstream | Current deliverable | Exit criteria / next decision |
| --- | --- | --- |
| **Primary: actual Aya Employee ID** | [Contract design](contract-design.md), including indexed-key proposal | Owner approves credential type, display information, claims, validity and renewal policy; implementation/configuration gets separate review. |
| **Parallel: revocation** | [Local state machine and synthetic tests](revocation-local.md) | Review mocked behavior, recovery, status rejection and deadline evidence. Cloud adapters, scheduler, storage and notifications are a later separately approved milestone. |
| **Owner-only Microsoft validation** | Manual checklist in both documents | Owner configures and tests a nonproduction synthetic credential, then returns non-sensitive outcomes. No deployment or live issuance is authorized by this PR. |

Issuance remains disabled by default. Neither workstream is complete merely because this local simulation passes. The next primary product checkpoint remains replacing the generic sample with an approved Aya Employee ID contract, display and validity period.
