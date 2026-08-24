# Observer v0.4 deletion and exit record

Status: `DRAFT_NOT_EXECUTED`

- Pilot ID / sample boundary ID: `<REQUIRED>`
- Data owner: `<REQUIRED>`
- Retention expiry or exit trigger: `<REQUIRED>`
- Responsible operator: `<REQUIRED>`
- Independent verifier: `<REQUIRED>`

## Deletion scope

| Data class | Location/reference | Required action | Exception basis |
| --- | --- | --- | --- |
| Authorized input | `<REQUIRED>` | `<DELETE/RETURN>` | `<NONE_OR_REQUIRED>` |
| Redacted working copy | `<REQUIRED>` | `<DELETE/RETAIN>` | `<NONE_OR_REQUIRED>` |
| Candidate results | `<REQUIRED>` | `<DELETE/RETAIN>` | `<NONE_OR_REQUIRED>` |
| Evidence and exports | `<REQUIRED>` | `<DELETE/RETAIN>` | `<NONE_OR_REQUIRED>` |
| Backups/temporary files | `<REQUIRED>` | `<DELETE>` | `<NONE_OR_REQUIRED>` |
| Immutable audit/metric records | `<REQUIRED>` | `<POLICY_DECISION>` | `<REQUIRED>` |

## Verification

- Execution timestamp UTC: `<REQUIRED>`
- Verification method: `<REQUIRED>`
- Deletion/return evidence digest: `<REQUIRED>`
- Unresolved exceptions: `<REQUIRED>`
- Operator sign-off: `<REQUIRED>`
- Independent verifier sign-off: `<REQUIRED>`
- Data-owner acknowledgement: `<REQUIRED>`
