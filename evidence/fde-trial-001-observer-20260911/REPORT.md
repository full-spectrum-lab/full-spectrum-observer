# FDE-TRIAL-001 Observer local implementation evidence

## Scope

This evidence covers the committed Instance 5 implementation of the Observer FDE fixture validation and lossless projection boundary.

```ini
QPP_TASK_COMMIT = 580a9e08e2c9fd524972b0b07bee149932f91fd0
PROTOCOL_CONTRACT_BASELINE_COMMIT = e08822cce605f14958c30f736dd0b0be3719de32
OBSERVER_BASE_COMMIT = 98da9559b8076770d9b5471ba0fde2cac4a6fe3c
OBSERVER_IMPLEMENTATION_COMMIT = LOCAL_COMMIT_SHA_REPORTED_BY_GIT
ENGINE_RUNTIME_COMMIT = 11c6f83593a327457940c6b5832aa03aa50713ee
FIXTURE_VERSION = 0.1.0-rc
```

## Implemented boundary

- Requires an external Protocol checkout at the exact contract baseline commit.
- Validates the frozen manifest metadata, Schema byte digest and exact six-document file set.
- Rejects duplicate JSON keys and duplicate document types.
- Validates every document against the frozen JSON Schema with format checking.
- Recomputes complete-document RFC 8785 SHA-256 values and both policy source-content digests.
- Resolves every protected JSON Pointer and validates frozen projection paths and assembly rules.
- Rejects cross-document snapshot, candidate digest, authority and validity-window conflicts.
- Projects each complete parsed document under the frozen `document_keys`, preserving arrays and scalar types.
- Returns the exact five-field `SnapshotBinding` and canonical `protocol_object_digest`.
- Contains no refund decision, policy approval, human-decision persistence or ActionSink call.

## Verification

```ini
FDE_TARGETED_TESTS = 12/12 PASS
FDE_AND_ENGINE2_TARGETED_TESTS = 31/31 PASS
PYTHON_FULL_REGRESSION_PINNED = 112/112 PASS
PINNED_ENGINE_RUNTIME_ACTUALLY_INVOKED = YES
DOTNET_SDK = 10.0.301
DOTNET_LOCKED_RESTORE = PASS
DOTNET_RELEASE_BUILD = PASS
BUILD_WARNINGS = 0
BUILD_ERRORS = 0
```

## Limits

```ini
FULL_FDE_EXERCISE = NOT_EXECUTED
FOUR_REPOSITORY_COMPOSITE_CI = NOT_EXECUTED
GENERAL_COMPATIBILITY = NOT_CONFIRMED
REAL_NETWORK = NOT_AUTHORIZED
REAL_REFUND_ACTION = FORBIDDEN
PRODUCTION_READY = NO
COMMIT = NOT_CREATED
PUSH = NOT_PERFORMED
```
