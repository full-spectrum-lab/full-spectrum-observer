# Observer v0.4 redaction report

Status: `DRAFT_NOT_REVIEWED`

## Identity and authorization

- Pilot ID: `<REQUIRED>`
- Sample boundary ID: `<REQUIRED>`
- Authorization manifest digest: `<REQUIRED>`
- Redaction operator: `<REQUIRED>`
- Independent checker: `<REQUIRED>`
- Completed at UTC: `<REQUIRED>`

## Source boundary

- Authorized source references: `<EXTERNAL_REFERENCES_ONLY>`
- Source item count: `<REQUIRED>`
- Source aggregate digest: `<REQUIRED>`
- Prohibited fields/classes: `<REQUIRED>`

Do not reproduce raw sensitive content in this report.

## Transform and checks

| Check | Method/version | Result | Evidence reference |
| --- | --- | --- | --- |
| Direct identifiers removed | `<REQUIRED>` | `<PASS/FAIL>` | `<REQUIRED>` |
| Indirect identifiers assessed | `<REQUIRED>` | `<PASS/FAIL>` | `<REQUIRED>` |
| Secrets/credentials scan | `<REQUIRED>` | `<PASS/FAIL>` | `<REQUIRED>` |
| Windows user paths/email scan | `<REQUIRED>` | `<PASS/FAIL>` | `<REQUIRED>` |
| Semantic utility retained | `<REQUIRED>` | `<PASS/FAIL>` | `<REQUIRED>` |
| Independent sample check | `<REQUIRED>` | `<PASS/FAIL>` | `<REQUIRED>` |

## Output

- Redacted item count: `<REQUIRED>`
- Redacted aggregate digest: `<REQUIRED>`
- Transform trace digest: `<REQUIRED>`
- Residual risk: `<REQUIRED>`
- Decision and limits: `<REQUIRED>`
- Operator sign-off: `<REQUIRED>`
- Independent checker sign-off: `<REQUIRED>`
