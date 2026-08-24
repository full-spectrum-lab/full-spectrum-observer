# Observer v0.4 Scenario Pack fixtures

This directory contains synthetic fixtures for the v0.4 Scenario Pack loading boundary.

- `knowledge-conflict/1.0.0`: the default synthetic knowledge-conflict Pack.
- `synthetic-policy-conflict/1.0.0`: a different synthetic Pack used only to prove that the same loader and contract accept a second scenario without scenario-specific Core code.

Neither Pack is a production dataset, a validated business rule, or an authorization to take action. Both require human review and preserve `UNKNOWN`.

Each Pack is frozen as `pack_id@version@digest` and has a detached RSA-SHA256 signature. The repository trust root is for these synthetic fixtures only; it is not a production signing authority. The digest and signature rules follow [SCENARIO_PACK_CONTRACT.md](../../docs/v0.4/SCENARIO_PACK_CONTRACT.md).
