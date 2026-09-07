# AGENTS.md

## Scope

These instructions apply to the entire repository. Kill Control is a Windows-only .NET 10 WPF application. The solution is `Kill.slnx`; the desktop project is `Kill/`; read-only safety checks live in `Kill.SelfTest/`.

The product name is **Kill Control**. Keep the existing `Kill` assembly, executable, namespaces, persisted data directories, and solution/project names unless a migration is explicitly requested. Changing those identifiers can break worker startup, existing quarantine history, backups, and user shortcuts.

## Product Contract

Kill Control must remain conservative, transparent, and recoverable wherever recovery is possible:

- Run the application's registered official uninstaller before offering residue cleanup.
- Show candidates before cleanup. Never silently broaden the selected scope.
- Prefer quarantine plus registry export over permanent deletion.
- Label irreversible operations clearly and require typed confirmation for high-risk actions.
- Treat uncertain ownership as a reason to preserve an item, not as permission to remove it.
- Do not claim perfect uninstall, zero residue, or zero impact.

## Non-Negotiable Safety Rules

### Files and registry

- Never recursively scan or delete an entire drive.
- Never clean a drive root, Windows, System32, Common Files, WindowsApps, or shared runtime locations.
- Residue matching must remain exact and attributable. Do not replace the current normalized exact-name checks with broad substring or fuzzy matching.
- Revalidate every selected path and registry key in the elevated worker. The UI result is not a security boundary.
- Cleanup request and response files must remain constrained to Kill Control's own temporary directory.
- Restore must not overwrite content that already exists at the original location.

### Windows services

- Drivers, critical services, Microsoft components, Windows-hosted services, and services with missing or uncertain hosts must remain protected.
- Never weaken `ServiceSafetyPolicy` merely to increase the number of services shown as manageable.
- All service mutations must go through `ServiceActionCoordinator` and the elevated `ServiceActionWorker`.
- The elevated worker must re-read the service registry entry and re-run protection classification immediately before mutation.
- Service deletion must export the service registry configuration before calling `sc.exe delete`.
- Prefer disabling over deleting. If a service cannot stop immediately, preserve the disabled state and report that a restart may be required.
- Blacklisting means recording the original start mode, stopping when possible, and setting the service to disabled. It is not a kernel or real-time enforcement mechanism.
- Removing a protected or missing service from Kill Control's blacklist may remove only the Kill Control record; it must not modify a newly protected service.

### Testing

- Automated tests must be read-only against the host machine.
- Never uninstall an application, delete residue, stop a real service, change a real service start mode, or delete a service during tests.
- Use synthetic paths and model objects for mutation-path unit tests.
- Resource-monitor persistence tests must inject a unique temporary database path and clean up only that test-owned directory.
- Local UI verification may enumerate applications and services and open detail views, but must not confirm a destructive dialog.

## Architecture Guide

- `ApplicationDiscoveryService`: reads desktop and current-user Store application registrations.
- `UninstallService`: resolves and launches registered uninstall commands.
- `ResidueScanner` and `SafetyPolicy`: produce conservative cleanup candidates.
- `CleanupCoordinator` and `CleanupWorker`: marshal cleanup/restore work through UAC and perform final validation.
- `ServiceDiscoveryService` and `ServiceSafetyPolicy`: enumerate and classify Windows services.
- `ServiceActionCoordinator` and `ServiceActionWorker`: marshal service actions through UAC and execute them after revalidation.
- `ResourceMetricsSampler`: reads system-wide CPU, physical memory, disk I/O, and network counters without modifying host state.
- `ProcessResourceSampler`: reads per-process CPU, working set, and Windows process I/O counters for the live Top 10 without modifying host state.
- `ResourceMonitorRepository`: owns the local SQLite schema and resource-monitor history operations.
- `ResourceTrendChart`: renders lightweight system-resource trend lines from the recent in-memory sample window.
- `KillPaths`: owns persistent and temporary path conventions. Do not duplicate these paths in feature code.
- `ConfirmationWindow`: shared risk confirmation UI. Service actions should use the internal service name as the typed confirmation value.

Keep UI code in the WPF windows, system discovery and mutation logic in `Services/`, and transport models in `Models/`. Reuse existing styles in `Kill/Themes/Styles.xaml`.

Resource monitoring must remain opt-in and scoped to the lifetime of the resource-monitor window. Do not turn it into a hidden startup task or service without an explicit product change. Its database belongs in `<program directory>\data`; never commit local database files.

Process rankings are live, read-only data and are not persisted. Label `GetProcessIoCounters` values as process I/O, not disk-only or network throughput. Do not claim per-process network monitoring unless a separate, reliable data source is implemented and verified.

## Versioning

- `VERSION` at the repository root is the only product-version source. Do not hard-code a separate product version in C# or project files.
- Versions use `x.x.xx`; the final component ranges from `10` through `99`.
- After `x.y.99`, increment the middle component and reset the final component to `10` (for example, `1.2.99` becomes `1.3.10`).
- Keep `VERSION` copied beside build and publish output so the packaged value can be inspected directly.
- Build and publish must preserve the canonical `Kill.exe` entry point and also create `Kill-v<version>.exe`. Do not rename the `Kill` assembly to implement versioned output.
- Versioned output names are derived from `VERSION`; changing the version creates a new file name and must not delete older versioned artifacts.
- Release builds write directly to the repository-root `Releases/` directory; Debug builds keep the SDK default under `bin/Debug/`.
- A normal Build apphost still depends on the DLLs beside it and is not a standalone historical archive. Use a version-specific directory and the self-contained single-file Publish output when preserving runnable older versions.

## Required Verification

Run these from the repository root after relevant changes:

```powershell
dotnet format .\Kill.slnx --verify-no-changes --verbosity minimal
dotnet build .\Kill.slnx -c Release
dotnet run --project .\Kill.SelfTest\Kill.SelfTest.csproj -c Release --no-build
```

For WPF layout changes, launch the Release build and verify the affected window at the minimum supported size. Confirm that long service names and paths wrap or trim without overlapping controls. Do not use a live destructive action as UI verification.

For release packaging, use the publish command documented in `README.md`. Build and publish outputs belong in `Releases/` and must not be committed.

## Editing and Git Hygiene

- Preserve unrelated user changes in a dirty worktree.
- Keep changes scoped to the requested behavior; do not rename the solution or persisted storage casually.
- Use nullable reference types and existing C# conventions.
- Keep comments succinct and reserve them for non-obvious safety decisions.
- Do not commit `bin/`, `obj/`, `.vs/`, `artifacts/`, generated test output, or local Kill Control data.
- Do not create a commit unless the user explicitly requests one.
