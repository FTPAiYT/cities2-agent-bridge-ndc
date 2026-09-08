# Rebuilding and offline tests

Using the prebuilt DLL needs no SDK. Rebuilding needs a .NET SDK and the owner's game assemblies. No game DLLs are distributed.

```powershell
pwsh -File .\build.ps1 -GamePath 'D:\SteamLibrary\steamapps\common\Cities Skylines II' -OutputDirectory .\rebuilt -CommunityRelease
pwsh -File .\tests\ClientTests.ps1
pwsh -File .\tests\JournalTests.ps1
dotnet run --project .\tests\MailboxTests.csproj -p:GamePath='D:\SteamLibrary\steamapps\common\Cities Skylines II'
```

The mailbox/geometry suite targets .NET 10. It uses Newtonsoft.Json from your own game. Its recovery tests require Windows file-sharing semantics and PowerShell 7 (`pwsh` on PATH, or set `CIAB_TEST_PWSH` to its executable). They exercise real file locks and the client against isolated synthetic mailboxes, including STOP, simulation deadlines, timeout and no-replay behavior. Client and journal tests send no commands to your running game. verify-api.ps1 accepts GamePath and checks local assembly members.

The map-viewer browser test needs Node.js, the `playwright` package and its Chromium browser. Run `node tests/map-viewer.cjs` in an environment where Node can resolve Playwright. To use an existing Chromium browser instead, set `PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH` to its executable. The test uses synthetic data and writes its screenshot to the ignored `artifacts` directory.

Rebuilt files are separate from the distributed artifacts. Installing a rebuilt binary requires reviewing its compatibility and regenerating the package manifest deliberately; do not mislabel it as the original verified package. The included manifest authenticates neither the publisher nor arbitrary rebuilt code.

## Placement regression and live acceptance

The offline suite includes ObjectPlacementTests: a native-state boundary fixture that demonstrates the retained-move failure and exercises the actual reset helper, plus the preview validator used before both preview completion and application. verify-api.ps1 checks the private native fields and their expected types. These checks do not execute the game's ECS construction systems.

Before publishing a rebuilt binary, obtain permission to use a disposable or backed-up city. Relocate building A, then preview and create a different building B. Verify A remains at its relocated position and exactly one B appears with the expected prefab and road connection. Repeat after an interrupted relocation and with consecutive different building types. Check that invalid/mismatched previews fail without changes or charges, and that explicit permitted demolition still works. Save verification and measured before/after evidence separately; do not include private city records in the public package.
