# Community preview — release notes

Runtime source: Cities II Agent Bridge 0.4.0. Reference game: Windows Steam Cities: Skylines II 1.6.0f1. Exact Game.dll fingerprint is in artifacts/build-manifest.json.

## Gameplay evidence from the development build

Road and industrial access construction, residential/office/industrial zoning, native service-building placement, underground electricity connection, budget adjustment, bounded simulation, city diagnostics and verified checkpoint saves were exercised in Cities II Agent. Mailboxes and bus routes were placed through native computer use, not claimed as dedicated bridge commands. City-specific records are intentionally not distributed.

## Community release changes

- Same runtime source rebuilt without debug symbols, removing the developer PDB path from the binary. No game behavior changed for packaging.
- Portable installer verifies package hashes and local game fingerprint, supports read-only CheckOnly and WhatIf, refuses installation while Cities2 is running, and backs up existing bridge files.
- Agent guide, removal and stop instructions, portable test GamePath, and optional journaling scripts included.
- Strict file allowlist excludes all personal transcripts, gameplay journals, city snapshots/saves, debug symbols, build response files and third-party game DLLs.

## Verification boundaries

Compilation succeeded with two existing obsolete-updater API warnings. This community binary has not been loaded in-game, and installation has not been verified on a separate PC. No game was restarted or controlled to prepare the release. Offline package/client/journal/mailbox validation is recorded in VALIDATION.txt inside the release ZIP. The existing map-viewer browser tests were not rerun for this packaging-only change.

## Known limitations

`get_services` produced a NullReferenceException during the development session; use the available city diagnostics and game UI when it fails. Static neighborhood geometry checks can miss conflicts with existing pedestrian paths. A native placement rejection requires inspection, not blind retries.

Controls reset off on city load. Analysis commands pause when authorized. Native placement can fail; a queued operation is not completion. Entity IDs are session-specific. Batch work is not transactional. Geometry suggestions do not guarantee a valid native placement. Simulation and traffic need observation after construction. The exposed command set does not cover every game UI function. No compatibility guarantee exists for other game versions or mod combinations.


## Standalone community build

Assembly, namespaces, mailbox paths, tool IDs, save prefixes and documentation use neutral community names. The build is isolated from the development mod. Offline validation does not establish in-game compatibility; this binary has not been loaded in-game.
