# HandyPlayer 1.1.3

This patch refreshes third-party dependencies to their latest stable releases:

- Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter,
  Avalonia.Headless and Avalonia.Skia 12.1.6 -> 12.2.0.
- Serilog.Extensions.Logging 10.0.0 -> 11.0.0.
- System.IO.Abstractions and System.IO.Abstractions.TestingHelpers
  23.0.0 -> 23.1.0.
- coverlet.MTP (test coverage) 10.1.0 -> 10.1.1.
- App and macOS bundle version metadata are synchronized at 1.1.3.

Unchanged because they are already current: .NET SDK 11.0.100 and the .NET 11
runtime packages, Avalonia.Controls.DataGrid 12.1.2, CommunityToolkit.Mvvm 8.4.2,
Serilog.Sinks.File 7.0.0, Buttplug 5.0.1, NSubstitute 6.2.0, xunit.v3 4.0.3, the
BenchmarkDotNet 0.16.0-preview.2 line and the pinned libmpv
`20261108-git-55d3feb45c` (mpv v0.42.0) native baseline.

The packaging tool stays on WiX 6.0.2. WiX 7 enforces explicit acceptance of
the Open Source Maintenance Fee EULA, which requires the owner's own decision.

This version retains the reviewed SDK/native dependency pins, packaging
profiles, required Windows MSI gate and macOS ad-hoc signature checks. Windows
artifacts make no code-signing claim; Developer ID signing and notarization are
not implemented.

Build, validation and native acceptance status must come from receipts for this
exact version and source. Historical 1.1.2 candidate results remain attached to
their original commits and do not qualify 1.1.3. Publication and installation
follow the selected local release request and its target-specific checks.
