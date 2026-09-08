# Community preview 0.4.1 — mailbox recovery

Temporary Windows sharing or lock violations while publishing the heartbeat previously reached the fatal-error handler and disabled bridge controls. Version 0.4.1 treats those transport errors as retryable. Reference game: Windows Steam Cities: Skylines II 1.6.0f1; the build manifest records the exact Game.dll fingerprint.

## Changes

- Shared mailbox readers permit atomic replacement. Publication retries briefly and defers to a later tick if the file remains locked.
- STOP and bounded simulation deadlines are checked before mailbox publication. New requests and workflow steps wait when transport is blocked.
- Completed responses remain pending until publication succeeds, without dispatching the command again. The PowerShell client waits for its original response ID under its existing timeout.
- Recovery never enables controls automatically. Non-sharing errors still reach the fatal-error handler; game faults still disable controls.
- Added Windows file-lock and real PowerShell client regression tests. Removed a machine-specific dependency path from the map-viewer test.

## Verification

The community DLL builds without debug symbols, with two existing obsolete-updater API warnings. All 66 mailbox, simulation/geometry, recovery and PowerShell lock checks passed, along with the standalone client, map export, journal, map-viewer browser and API contract checks. See VALIDATION.txt for the validation scope.

This rebuilt community DLL has not been loaded in-game or installed on another PC. Offline tests do not establish in-game compatibility. No game was launched, controlled or modified to prepare this update.

## Known limitations

`get_services` previously produced a NullReferenceException; use the available city diagnostics and game UI when it fails. Static neighborhood geometry checks can miss conflicts with existing pedestrian paths. A native placement rejection requires inspection, not blind retries.

Controls reset off on city load. Analysis commands pause when authorized. Native placement can fail; a queued operation is not completion. Entity IDs are session-specific. Batch work is not transactional. Geometry suggestions do not guarantee valid native placement. The command set does not cover every game UI function. Other game versions and mod combinations remain unverified.
