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
- Local UI verification may enumerate applications and services and open detail views, but must not confirm a destructive dialog.

## Architecture Guide

- `ApplicationDiscoveryService`: reads desktop and current-user Store application registrations.
- `UninstallService`: resolves and launches registered uninstall commands.
- `ResidueScanner` and `SafetyPolicy`: produce conservative cleanup candidates.
- `CleanupCoordinator` and `CleanupWorker`: marshal cleanup/restore work through UAC and perform final validation.
- `ServiceDiscoveryService` and `ServiceSafetyPolicy`: enumerate and classify Windows services.
- `ServiceActionCoordinator` and `ServiceActionWorker`: marshal service actions through UAC and execute them after revalidation.
- `KillPaths`: owns persistent and temporary path conventions. Do not duplicate these paths in feature code.
- `ConfirmationWindow`: shared risk confirmation UI. Service actions should use the internal service name as the typed confirmation value.

Keep UI code in the WPF windows, system discovery and mutation logic in `Services/`, and transport models in `Models/`. Reuse existing styles in `Kill/Themes/Styles.xaml`.

## Required Verification

Run these from the repository root after relevant changes:

```powershell
dotnet format .\Kill.slnx --verify-no-changes --verbosity minimal
dotnet build .\Kill.slnx -c Release
dotnet run --project .\Kill.SelfTest\Kill.SelfTest.csproj -c Release --no-build
```

For WPF layout changes, launch the Release build and verify the affected window at the minimum supported size. Confirm that long service names and paths wrap or trim without overlapping controls. Do not use a live destructive action as UI verification.

For release packaging, use the publish command documented in `README.md`. Published output belongs in `artifacts/` and must not be committed.

## Editing and Git Hygiene

- Preserve unrelated user changes in a dirty worktree.
- Keep changes scoped to the requested behavior; do not rename the solution or persisted storage casually.
- Use nullable reference types and existing C# conventions.
- Keep comments succinct and reserve them for non-obvious safety decisions.
- Do not commit `bin/`, `obj/`, `.vs/`, `artifacts/`, generated test output, or local Kill Control data.
- Do not create a commit unless the user explicitly requests one.
