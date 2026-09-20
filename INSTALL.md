# Install Cities II Agent Bridge

**First: you must own and install Cities: Skylines II.** The bridge does not include the game and does not work with Cities: Skylines I. If the game is not installed, stop setup and ask the user to install their own copy first.

If reading this on GitHub, download the community ZIP and its `.sha256` file from https://github.com/FTPAiYT/cities2-agent-bridge-ndc/releases/latest. Compare the ZIP's SHA-256 to that checksum before extracting. Use the packaged release, not GitHub's automatically generated source-code archive. The repository's `artifacts` directory is intentionally absent; the release contains the compiled DLL and build manifest.

## Prompt to give your agent

> Read INSTALL.md and AGENTS.md in this extracted package. Verify the files and my game compatibility, explain the installation, then install the bridge if I have authorized installation. Do not close or restart my game, change a save, or start playing without my permission.

## 1. Inspect and verify

Extract the ZIP into a writable folder (not inside the ZIP viewer). PowerShell 7 is required. No administrator access or .NET SDK is needed to use the supplied DLL. Do not change system execution policy or disable security software as part of installation. If scripts are blocked, explain the exact restriction and let the owner review/unblock trusted downloaded files.

From the extracted folder:

```powershell
pwsh -NoProfile -File .\verify-package.ps1
```

Find the actual game directory. Ask the owner or use their Steam Library > Cities: Skylines II > Manage > Browse local files. Do not assume the game is on C:. It must contain `Cities2_Data\Managed\Game.dll`.

```powershell
pwsh -NoProfile -File .\install.ps1 -GamePath 'D:\SteamLibrary\steamapps\common\Cities Skylines II' -CheckOnly
```

Replace the example path. CheckOnly reads files and prints the destination; it neither installs nor communicates with the game. A fingerprint mismatch means this release is unverified against that game build: stop and report it; do not silently bypass the check.

The 0.4.3 prebuilt package targets the Windows Steam 1.6.2f1 assembly. Game Pass / Microsoft Store and other builds may have a different fingerprint. If the owner wants a source rebuild, follow [DEVELOPMENT.md](DEVELOPMENT.md) to generate a separate package against their installed assemblies. Rebuilding needs a .NET SDK and does not establish runtime compatibility. Do not change the downloaded release's hashes to force installation.

## 2. Install

If the game is open, ask the owner to save and close it. Never terminate it yourself. With installation authorized and the game closed:

```powershell
pwsh -NoProfile -File .\install.ps1 -GamePath 'D:\SteamLibrary\steamapps\common\Cities Skylines II'
```

The installer copies only `CitiesIIAgentBridge.dll` into the current user's `AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\CitiesIIAgentBridge`. Existing bridge DLL/PDB files are backed up under that folder before replacement; other mods and saves are untouched. Custom destinations are supported with `-Destination`, but only use one intentionally selected by the owner. Copy hashes are checked. Installation does not launch the game.

## 3. First connection

Have the owner launch the game and load a disposable test city or a backed-up save. Confirm the Cities II Agent Bridge section appears in Options. If it does not, inspect the game's log and installed path; do not claim a successful runtime install merely because file copying worked.

Pause the city manually. Read-only connection check:

```powershell
pwsh -NoProfile -File .\bridge.ps1 ping
```

Before gameplay, agree on duration, construction/spending scope, save behavior, and whether native computer control is allowed. Then have the owner enable **Options > Cities II Agent Bridge > Allow local bridge controls** in the loaded city. It resets off on city changes by design.

```powershell
pwsh -NoProfile -File .\bridge.ps1 pause_for_analysis
pwsh -NoProfile -File .\bridge.ps1 get_capabilities
pwsh -NoProfile -File .\bridge.ps1 save_checkpoint -ArgsJson '{"label":"before-agent-session"}'
```

Poll `get_operation` using the returned ID until `complete`; a queued save is not a verified save. Never reuse IDs from documentation or another city. Use COMMANDS.md to discover current entities, positions and unlocked prefabs.

## Stop, resume and remove

`pwsh -NoProfile -File .\bridge.ps1 stop` writes a STOP latch. It disables further bridge controls, interrupts supported pending work at the next check and pauses a bounded simulation. It does not undo construction or stop a separate computer-use tool. If the bridge is unresponsive, pause manually in the game.

To resume after an intentional stop, obtain fresh permission, remove only `%LOCALAPPDATA%\CitiesIIAgentBridge\STOP`, and re-enable the in-game checkbox. Never clear the latch automatically.

To uninstall, have the owner save and close the game. Remove only the installed `CitiesIIAgentBridge.dll` (and any bridge PDB from an older installation). Do not delete the Mods folder or game saves. Existing bridge-created game objects are ordinary game objects, but loading a save after removal has not been independently tested for this community release; keep a backup. Optional journals/mailbox files can be retained for troubleshooting or removed separately at the owner's request.

## Troubleshooting

- Temporary mailbox file lock: version 0.4.1 retries publication and resumes when the lock clears. STOP and bounded simulation deadlines are still checked. It never turns disabled controls back on.
- Response timeout: a command may already have run. Inspect the response for the original request ID and the city state before deciding what to do; do not resubmit a mutation blindly.
- Stale heartbeat: verify the game and mod are running; never replay a timed-out construction request blindly.
- Controls disabled: enable the option after loading the city; check for an intentional STOP latch.
- Locked prefab/native placement error: inspect current unlocks and native preview results; don't override game state.
- Installation blocked because Cities2 is running: ask the owner to save and close it.
- Sharing diagnostics: review logs first. Do not upload city data or chat transcripts automatically.
