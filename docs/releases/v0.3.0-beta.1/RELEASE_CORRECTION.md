# v0.3.0-beta.1 release correction

`v0.3.0-beta.1` preserves the verified v0.3 product logic and corrects the
public release identity chain.

The superseded `v0.3.0-beta` RC5 package was functionally and structurally
verified, but its internal identity still said `RELEASE_CANDIDATE /
NOT_RELEASED`.  Its tag source archive also contained pre-release README
wording.  This correction release makes the tag, README, build script,
package identity, manifests and public Release state agree.

The old `v0.3.0-beta` tag and artifacts remain immutable historical evidence.

This release is still a Beta pre-release:

```text
PRODUCTION_READY = NO
WINDOWS_X64_VERIFIED = YES
LINUX_MACOS_EXECUTED = NO
FINAL_BUSINESS_OR_PRODUCTION_ACTIONS = NOT_AUTHORIZED
```
