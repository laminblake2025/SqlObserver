# Installer assets

## Unsigned test installer

The separate [test installer](test/TEST-INSTALL.txt) provides an interactive,
offline Windows x64 lab bootstrapper with bundled prerequisites, fresh-repository
configuration, Windows services, and data-preserving uninstall. Build it with
`tools/build-test-installer.ps1` around a verified lab package. Its source and
tests live under `installer/test/`; it does not change or consume the foundation
assessment's mutation authority. It is unsigned, fresh-install-only, and does
not satisfy production release or native installer certification gates.

## Production foundation

Production WiX packaging and PostgreSQL installation or integration assets remain reserved
for a later M12 package. The current foundation contains only closed assessment
contracts under `contracts/`; it does not create an MSI/Burn package or install,
register, start, stop, or uninstall services. It does not provision PostgreSQL,
apply migrations, write configuration/ACLs, manage credentials/certificates, or
purge data.

The application can perform a bounded, read-only migration-history assessment
using the exact embedded migration catalog. Assessment output is safe for logs
and JSON (bounded IDs, hashes, statuses, and UTC timestamps only) and always
reports `readyToMutate: false` until an accepted pinned owner policy and the
remaining lifecycle decisions/evidence exist. Local foundation checks are not
installer, runtime, support, or release evidence; ADR-0016 remains Proposed.

## Test installer prerequisite compatibility

Detection runs executable probes and checks every prerequisite before installing
missing components. Stale registry version strings alone cannot satisfy a probe.

| Product | Accepted versions and discovery |
| --- | --- |
| Windows | Server 2022+ x64 (build 20348+); server product type is required, with no upper build bound |
| .NET / ASP.NET Core | Stable x64 10.0.13+ patches; newer 10.x minors when no 10.0 is installed, matching default host selection. The x64 machine registration selects the host, with Program Files as fallback. Other majors may coexist but cannot replace .NET 10. |
| PowerShell | Stable x64 7.5+ including later majors. Probe version folders and all registered machine installations, without relying on a fixed MSI GUID or user PATH. |
| PostgreSQL | Stable x64 18.x updates. Probe server binaries found through either registry view or Program Files; reject clients, stale registrations, previews and other majors. The live connection separately verifies version and administrator rights. |

These rules follow the application's existing contracts; they do not certify
unreleased product versions. The runtime uses [.NET's default minor roll-forward
policy](https://learn.microsoft.com/en-us/dotnet/core/versions/selection), and
repository compatibility and migrations explicitly require PostgreSQL major 18.
See [PostgreSQL versioning](https://www.postgresql.org/support/versioning/) for
the distinction between compatible updates and major upgrades.

`tools/test-test-installer-prerequisites.ps1` compiles and runs the production
Inno detector functions against executable fixtures for future versions, missing
or failed probes, x86 binaries, preview versions, mixed runtime versions and
registry-free directory discovery, plus the actual local .NET and PowerShell
hosts. Windows' Framework64 C# compiler builds those test fixtures. The setup
policy tests cover PostgreSQL service command parsing with custom names and
quoted/unquoted paths. Neither test suite substitutes for a full Windows Server
install/configure/uninstall run; that lifecycle remains pending in the receipt.
