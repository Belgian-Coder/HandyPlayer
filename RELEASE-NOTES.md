# HandyPlayer 1.1.2

This patch updates the local build and release process:

- Validation, tests, builds and packaging run locally on PC-MARC, WINDOWS11-VM
  or the ARM64 Mac. GitHub Actions entry points are removed; hosted,
  self-hosted and manually dispatched Actions jobs are prohibited.
- Local builds default to normalized source paths in PDBs without depending on
  GitHub environment variables.
- Verified dependency caches share the owning primary checkout's OS-specific
  cache, with ownership checks and shared coordination locks.
- App and macOS bundle version metadata are synchronized at 1.1.2.

This version preparation retains the reviewed SDK/native dependency pins,
packaging profiles, required Windows MSI gate and macOS ad-hoc signature checks.
Windows artifacts make no code-signing claim; Developer ID signing and
notarization are not implemented.

Build, validation and native acceptance status must come from receipts for this
exact version and source. Historical 1.1.1 candidate results remain attached to
their original commits and do not qualify 1.1.2. Publication and installation
follow the selected local release request and its target-specific checks.
