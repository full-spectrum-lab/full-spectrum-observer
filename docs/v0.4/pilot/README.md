# Observer v0.4 narrow-pilot evidence package

This directory defines the minimum evidence package for the authorized, redacted knowledge-conflict pilot. Templates are not evidence of approval or completion.

Required completed artifacts:

1. `AUTHORIZED_SAMPLE_MANIFEST`: identifies the owner, lawful/contractual authorization, approved purpose, sample boundary, retention mode, and Pack digest.
2. `REDACTION_REPORT`: records the redaction method, independent check, residual-risk decision, and source/output digests without copying sensitive source content.
3. `PILOT_ACCEPTANCE_RECORD`: records frozen dependencies, golden/boundary/adversarial coverage, two-reviewer agreement, all five metric numerators and denominators, audit verification, limitations, and target-user sign-off.
4. `DELETION_EXIT_RECORD`: records the expiry trigger, deletion scope, verification method, exceptions, owner, and completion evidence.

Release rules:

- Do not place raw confidential or personal data in this repository.
- Do not replace placeholders with invented approval, signatures, sample counts, dates, or metric values.
- A zero denominator is reported as `UNKNOWN`, never as zero or one.
- Every result remains a candidate requiring human review; no pilot artifact authorizes automatic employee, customer, or business action.
- Claims are limited to the recorded sample boundary and exact `pack_id@version@digest`, Pack Profile digests, Observer version, and Engine version.
