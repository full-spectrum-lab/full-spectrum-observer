# Private Python runtime

The private Python 3.11 x64 runtime is a generated release artifact and is not
committed in the source bootstrap.

WP-03/IG4 must place the exact runtime here during a controlled local build.
The build may not search `PATH` for an arbitrary Python installation.
