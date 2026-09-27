# Cities II Agent Bridge 0.5.0 - community preview

**You must purchase and install Cities: Skylines II for this to work.** This download does not include the game, make it free, or work with the original Cities: Skylines. You supply the game; the bridge lets your agent interact with it.

## Give this URL to your agent

Share this repository: **https://github.com/FTPAiYT/cities2-agent-bridge-ndc**

Copy this prompt into an agent that can access your Windows computer:

> Help me try Cities II Agent Bridge: https://github.com/FTPAiYT/cities2-agent-bridge-ndc. First explain that I need to own and install Cities: Skylines II. Read README.md, INSTALL.md and AGENTS.md. Check my prerequisites and game compatibility, then guide me through installing the latest community release. Ask before installing or controlling my game. Do not close or restart the game without my permission.

**Agents: start with [INSTALL.md](INSTALL.md), then [AGENTS.md](AGENTS.md).** On GitHub, download the ZIP and matching `.sha256` file from [Releases](https://github.com/FTPAiYT/cities2-agent-bridge-ndc/releases/latest). The release ZIP is the installation package; GitHub's automatic source-code ZIP is for development. Never assume this URL alone grants permission to install or play.

## Your city, your name

Pick your own city name and theme with your agent. Agents should preserve existing city names and ask about the vision for a new city before building.

The mod uses `CitiesIIAgentBridge.dll` and a dedicated `CitiesIIAgentBridge` mailbox folder. Your city name is independent. This release appears in the game as **Cities II Agent Bridge**.

## What you need

- Your own installed **Cities: Skylines II**, on **Windows**. This preview targets the Steam build **1.6.2f1** and checks the actual game assembly fingerprint.
- **PowerShell 7** (`pwsh`). No administrator access or .NET SDK is needed for the supplied DLL.
- An agent with **local file and command access**. A browser-only chat cannot operate this package. Separate computer-use access is needed for game UI actions outside the bridge's commands.

This is a community experiment: try it, show what your agent builds, and report what breaks. It is not an official mod or an autonomous city-playing bot.

## Turn the bridge on inside your city

Installing the mod does **not** enable game control. After installation:

1. Launch Cities: Skylines II and **load a city**.
2. Pause the game.
3. Open **Options → Cities II Agent Bridge**.
4. Turn on **Allow local bridge controls** when you are ready to let your agent act.

**This switch resets off when a city loads.** Turning it on in the main menu is not enough; enable it after loading the city you want to play. The agent can inspect the connection with `ping`, but construction and other control commands need this switch enabled. See [INSTALL.md](INSTALL.md) for the complete setup and stop instructions.

## What it does

An unofficial, local agent interface for Cities: Skylines II on Windows. The DLL runs in the game; an agent uses the included PowerShell client to read city data and request supported actions. It does not play independently and does not require publishing through a mod marketplace.

**Start with INSTALL.md.** You need your own installed copy of Cities: Skylines II, PowerShell 7 (`pwsh`), and an agent that can run local commands. Ordinary browser-only chat cannot install or operate it. Computer-use access is optional for bridge commands but necessary for UI tasks the bridge does not expose, such as the bus-route and mailbox workflows used in our sessions.

Includes compiled mod, source, installation and control scripts, command reference, optional local journal and map viewer, and offline tests. No game assemblies, saved cities, conversation logs, account information, or transcripts are included.

## Compatibility and evidence

Version 0.5.0 retains the current development bridge's service-read fix, complete building and asset pagination, tree/prop/surface discovery, scenery road-warning correction, and explicit capability limits. It also retains the placement-state and mailbox recovery repairs from 0.4.2. Follow every `nextOffset` in one paused city session and check service `complete`/`errors`; a successful request may still contain partial results.

The shared 0.4.3 logic passed development-runtime checks on Windows / Steam / Cities: Skylines II **1.6.2f1** on September 16, 2026. This separately compiled community DLL has not been loaded in-game or installed on another PC. See [VALIDATION.txt](VALIDATION.txt) for the exact test scope. The release is compiled without debug symbols and uses neutral assembly, namespace, mailbox, tool and save-prefix names.

The installer checks the actual `Game.dll` fingerprint and refuses a mismatch. A contributor reports sustained use of an earlier source build on Game Pass / Microsoft Store 1.6.2.0 after rebuilding; that is external evidence, not verification of this binary. See [DEVELOPMENT.md](DEVELOPMENT.md) for rebuilding against another installation.

Known gaps: reported invalid `sample_terrain` water values remain unresolved; fertility, ore, oil, fish and groundwater sampling is not included. Traffic lane rules, full Building Use metrics and Road Builder configuration remain unsupported. Broader asset discovery does not imply placement support. The raw simulation date can differ from the displayed game calendar.

The bridge uses files under `%LOCALAPPDATA%\CitiesIIAgentBridge`; it opens no network listener. Other programs running under the same Windows account can access that mailbox. Enabling bridge controls permits construction, demolition, zoning, taxes, spending and saves within the game's supported operations. This is not a sandbox for an untrusted agent. Use an agent you trust and agree on scope before gameplay.

The game option resets off when a city loads. Enable it inside the loaded city, not just in the main menu. To stop bridge control, run `pwsh -File .\bridge.ps1 stop`. Applied changes remain. Native computer control, if separately enabled, must be stopped through that tool too.

## Files

- INSTALL.md: agent-readable installation and first-connection workflow.
- AGENTS.md: gameplay and consent rules for agents reading this directory.
- COMMANDS.md / commands.json: commands, arguments and limitations.
- install.ps1 / verify-package.ps1: compatibility, file integrity and installation.
- bridge.ps1 / advance.ps1: mailbox client and bounded simulation helper.
- journal.ps1: optional local visible-progress journal; transcript import is optional and Codex-format-specific.
- export-map.ps1 / view-map.html: local city map export.
- src / build.ps1 / tests: source and offline development checks.

Keep the ZIP intact when sharing. Compare its SHA-256 against the checksum supplied alongside it by the distributor. Internal hashes detect corruption but are not a publisher signature. Nothing is uploaded automatically.

## New in 0.5.0

District census and map exports, district drawing/reshaping, service-district assignments, and improved water/resource/groundwater sampling. See [atlas usage](atlas/README.md) and [release notes](RELEASE-NOTES.md). Node.js is required for the atlas exporter.

Known 0.5.0 issue: named district creation/editing can apply successfully and then report an EntityCommandBuffer naming-notification error. Inspect a fresh atlas and the operation result before any retry. Never replay an uncertain mutation. Boundary edits without a name have completed normally. See RELEASE-NOTES.md.
