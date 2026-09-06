# Rebuilding and offline tests

Using the prebuilt DLL needs no SDK. Rebuilding needs a .NET SDK and the owner's game assemblies. No game DLLs are distributed.

```powershell
pwsh -File .\build.ps1 -GamePath 'D:\SteamLibrary\steamapps\common\Cities Skylines II' -OutputDirectory .\rebuilt -CommunityRelease
pwsh -File .\tests\ClientTests.ps1
pwsh -File .\tests\JournalTests.ps1
dotnet run --project .\tests\MailboxTests.csproj -p:GamePath='D:\SteamLibrary\steamapps\common\Cities Skylines II'
```

The mailbox/geometry suite targets .NET 10. It uses Newtonsoft.Json from your own game. Client and journal tests use synthetic data and send no commands to your running game. The map-viewer test additionally needs Playwright; see its source before running. verify-api.ps1 accepts GamePath and checks local assembly members.

Rebuilt files are separate from the distributed artifacts. Installing a rebuilt binary requires reviewing its compatibility and regenerating the package manifest deliberately; do not mislabel it as the original verified package. The included manifest authenticates neither the publisher nor arbitrary rebuilt code.
