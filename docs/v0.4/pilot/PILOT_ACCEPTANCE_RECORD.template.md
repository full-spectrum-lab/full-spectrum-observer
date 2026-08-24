# Observer v0.4 narrow-pilot acceptance record

Status: `DRAFT_NOT_ACCEPTED`

## Frozen boundary

- Pilot ID / sample boundary ID: `<REQUIRED>`
- Observer version and commit: `<REQUIRED>`
- Engine version and commit: `<REQUIRED>`
- `pack_id@version@digest`: `<REQUIRED>`
- Detached signature key ID and verification evidence: `<REQUIRED>`
- Pack Profile ref/id/version/digest: `<REQUIRED>`
- Input aggregate digest: `<REQUIRED>`
- Authorization / redaction / deletion-plan refs: `<REQUIRED>`

## Required evidence

| Evidence | Required result | Actual result | Reference |
| --- | --- | --- | --- |
| Golden sample | candidate boundary preserved | `<REQUIRED>` | `<REQUIRED>` |
| Boundary sample | no automatic action | `<REQUIRED>` | `<REQUIRED>` |
| Adversarial sample | untrusted instruction does not authorize action | `<REQUIRED>` | `<REQUIRED>` |
| Interrupted batch recovery | no duplicate terminal item | `<REQUIRED>` | `<REQUIRED>` |
| Exact Profile replay | identical frozen bindings | `<REQUIRED>` | `<REQUIRED>` |
| Two-reviewer agreement | numerator/denominator plus disagreements | `<REQUIRED>` | `<REQUIRED>` |
| Audit chain | complete and verified | `<REQUIRED>` | `<REQUIRED>` |
| Sensitive export scan | clean | `<REQUIRED>` | `<REQUIRED>` |

## Five value metrics

| Metric | Numerator | Denominator | Value | Unit |
| --- | ---: | ---: | ---: | --- |
| Discovery rate | `<REQUIRED>` | `<REQUIRED>` | `<VALUE_OR_UNKNOWN>` | ratio |
| False-positive rate | `<REQUIRED>` | `<REQUIRED>` | `<VALUE_OR_UNKNOWN>` | ratio |
| Human-review duration | `<TOTAL_MS>` | `<REVIEW_COUNT>` | `<MEAN_OR_UNKNOWN>` | milliseconds |
| Evidence completeness rate | `<REQUIRED>` | `<REQUIRED>` | `<VALUE_OR_UNKNOWN>` | ratio |
| Repeat-result consistency rate | `<REQUIRED>` | `<REQUIRED>` | `<VALUE_OR_UNKNOWN>` | ratio |

## Conclusion

- Sample-limited finding: `<REQUIRED>`
- Known limitations and `UNKNOWN`: `<REQUIRED>`
- Incidents/deviations: `<REQUIRED>`
- Exit/deletion decision: `<REQUIRED>`
- Target-user acceptance decision: `<ACCEPT/REJECT/CONDITIONAL>`
- Target-user name/role/signature/date: `<REQUIRED>`
- Operator name/role/signature/date: `<REQUIRED>`

This record cannot establish validity outside the stated sample and frozen version boundary.
