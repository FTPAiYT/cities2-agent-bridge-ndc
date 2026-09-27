# Community preview 0.5.0 — district atlas and controls

- Add district census by home location, age/education groups, road curves, building footprints, service capacities and assignments.
- Export complete immutable snapshots to local JSON, CSV, Markdown and SVG without Carto. Node.js required for export.
- Draw and reshape districts with native preview/apply, expected-boundary checks and operation polling.
- Preview/apply guarded service-district assignments with expected-current checks and readback.
- Correct water depth/pollution sampling to the full-precision surface; add natural-resource and groundwater values with explicit units/status.

## Known issue: naming can report failure after application

Known 0.5.0 issue: named district creation/editing can apply successfully and then report an EntityCommandBuffer naming-notification error. Inspect a fresh atlas and the operation result before any retry. Never replay an uncertain mutation. Boundary edits without a name have completed normally. See RELEASE-NOTES.md.

Shared development-runtime checks verified atlas capture, district creation, boundary-only editing, service assignments and save/reload persistence. These checks do not establish effective service reach or every census edge case. Terrain/resource overlay comparison remains unverified. The neutral community DLL has not been loaded in-game or tested on another PC. This release does not install or modify the developer's game.

## Earlier releases

# Community preview 0.4.3 — service recovery and complete queries

`get_services` could throw while enumerating prefabs, and building queries stopped after the first 512 results. Version 0.4.3 ports the current development bridge's visibility changes to the community package.

- Check the actual service prefab type before reading budgets. Return per-service failures as `errors` with `complete:false` instead of losing the entire response.
- Add stable entity-index/version pagination to building and asset queries with `offset`, `limit`, `total`, `truncated`, `nextOffset` and `citySession`. Add spatial building filters. Agents must follow all pages while paused.
- Discover trees, props and surfaces with category filters, prefab types and explicit `bridgePlacementSupported` flags. Discovery alone does not make an asset placeable.
- Respect a prefab's road requirement before reporting a disconnected building, avoiding false scenery warnings.
- Report missing mod integrations and relevant loaded assemblies, and label raw simulation dates without pretending they match the displayed calendar.
- Build the community binary against Windows Steam 1.6.2f1. Add an explicit allowlisted packaging script and isolated installer tests, including a documented source-rebuild path for other game installations.

The shared 0.4.3 logic passed live development-runtime checks on September 16, 2026: complete service results, building pagination, asset category samples and a scenery warning check. The neutral community build compiles, all 102 core offline checks pass, and client, journal, native API and isolated installer checks pass. This separately compiled community DLL has not been loaded in-game or tested on another PC. See VALIDATION.txt for the precise scope. The 0.4.2 placement and mailbox repairs remain included.

Known gaps: reported invalid water values from `sample_terrain` remain unresolved. Fertility, ore, oil, fish and groundwater sampling is not included. Traffic lane rules, full Building Use metrics and Road Builder configuration remain unsupported. External Game Pass / Microsoft Store 1.6.2.0 source-build feedback does not establish compatibility of this prebuilt DLL; the installer retains its exact assembly-fingerprint check.

---

# Community preview 0.4.2 — placement state and mailbox recovery (historical)

Creating a new building after relocating another could retain the relocation target. The bridge calls native snapping and preview generation directly, bypassing the native update loop that clears that state. A new-building request could therefore preview a modification of the previously moved building.

## Placement repairs

- Reset the native moving, initialized-moving, upgrade and transform state before every candidate preview. Set the working prefab used by native snapping and definition generation to the requested building.
- Validate every root building in the preview before either reporting dry-run success or applying it. Creation requires exactly one new building of the requested prefab. Relocation requires the exact original entity and prefab. Missing, duplicate, mismatched and unintended existing-building previews are rejected.
- Explicitly permitted collateral demolition remains supported. That permission never authorizes modifying another building or deleting the relocation target.
- Check relocation prefab identity before starting and again when verifying completion. Record expected prefab/original and observed preview roots in the operation result for diagnosis.
- Add 24 offline regression checks for relocation-to-create transitions, interrupted moves, consecutive different-prefab creates, changed native layouts and preview rejection rules. Verify the reset fields and their types against the installed game assembly.

## Included mailbox recovery from 0.4.1

Temporary Windows sharing/lock violations defer publication instead of disabling controls. STOP and bounded simulation deadlines run first. Completed responses remain pending without dispatching a command again; recovery never enables controls automatically. Regression tests exercise real file locks and the PowerShell client with isolated mailboxes.

## Verification and limits

Both private-development and neutral-community adapters compile against the reference Windows Steam Cities: Skylines II 1.6.0f1 assembly. All 90 offline checks pass, including 24 new placement checks. A corresponding private 0.4.2 build passed the live move-A/create-B sequence, wrong-prefab relocation rejection, and creation after a native-rejected move preview. Observed entity identities, road connections and spending matched the requests. The test remained paused and unsaved.

This release supplies the rebuilt neutral community DLL, source, scripts and integrity manifests. The community DLL itself has not been loaded in-game or installed on another PC. Mid-operation tool interruption, consecutive creates without an intervening move attempt, explicit collateral demolition, and other mods/game versions remain untested live. Two existing obsolete-updater warnings remain. See VALIDATION.txt for evidence and DEVELOPMENT.md for the broader live acceptance checklist.

## Other limitations

Controls reset off on city load. Analysis commands pause when authorized. Entity IDs are session-specific. A failed batch leaves earlier work in place. Static planning can miss pedestrian paths or native placement restrictions. The command set does not cover every UI action. When native placement fails, inspect the recorded outcome rather than replaying it blindly.
