# MSIX symbol policy

Preview.20 intentionally publishes the unsigned/signed MSIX package without a
separate `.appxsym` symbol artifact. The hosted Windows build image does not
provide `mspdbcmf.exe`, and the release must not claim crash-symbol coverage it
did not produce.

`scripts/Build-UnsignedPackage.ps1` sets both
`AppxPackageIncludePrivateSymbols=false` and
`AppxPackageIncludePublicSymbols=false`. CI and release validation fail if an
`.appxsym` file nevertheless appears.

Before a future stable release promises MSIX crash-symbol diagnostics, the
release owner must either provision a supported `mspdbcmf.exe` toolchain and
publish the resulting symbol artifact, or explicitly revise this policy and its
release evidence.
