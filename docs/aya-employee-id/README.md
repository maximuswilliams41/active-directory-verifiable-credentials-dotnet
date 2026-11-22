# Aya Employee ID milestones

These are parallel workstreams. Local revocation development does not replace the primary credential-design work or authorize Microsoft configuration.

| Workstream | Current deliverable | Exit criteria / next decision |
| --- | --- | --- |
| **Primary: actual Aya Employee ID** | [Employee experience and approved contract fields](employee-experience.md), implemented in PR #5 | Final review of the reconciled branch and complete regression suite; owner still chooses validity and authorizes any later configuration or deployment. |
| **Parallel: revocation** | [Local state machine and synthetic tests](revocation-local.md), approved and merged in PR #4 | Preserve mocked behavior, recovery, status rejection and deadline evidence. Cloud adapters, scheduler, storage and notifications remain a later separately approved milestone. |
| **Owner-only Microsoft validation** | Manual checklist in both documents | Owner configures and tests a nonproduction synthetic credential, then returns non-sensitive outcomes. No deployment or live issuance is authorized by this PR. |

Issuance remains disabled by default. Neither workstream is complete merely because this local simulation passes. The next primary product checkpoint remains replacing the generic sample with an approved Aya Employee ID contract, display and validity period.
