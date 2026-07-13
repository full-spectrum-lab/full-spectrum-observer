# Contributing

## Branch and commit rules

- Feature branch: `feat/IMP-xxxx-short-name`
- Fix branch: `fix/DEFECT-xxxx-short-name`
- Commit: `IMP-xxxx: imperative summary`
- Tests must reference a `TR-FK-*` identifier in the test name or metadata.

## Stop conditions

Do not commit:

- floating dependency versions;
- build output or private Python binaries;
- secrets, raw enterprise input, or private enterprise data;
- a second implementation of formal governance calculation;
- direct Python worker invocation outside `Observer.EngineFacade`;
- Console, HTTP, Copilot, Connector, Registry, microservice, MQ, or cloud-control-plane work.
