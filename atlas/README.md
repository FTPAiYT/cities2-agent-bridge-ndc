# District atlas — bridge 0.5.0

Local district data and map export with no Carto dependency. Community build0.5.0 includes terrain/resource sampling. Shared development-runtime district boundaries and assignments survived save/reload; the neutral community DLL and a fresh-machine installation remain untested.

Known 0.5.0 issue: named district creation/editing can apply successfully and then report an EntityCommandBuffer naming-notification error. Inspect a fresh atlas and the operation result before any retry. Never replay an uncertain mutation. Boundary edits without a name have completed normally. See RELEASE-NOTES.md.

## What this version contains

- District names, entity identities, boundary vertices and area.
- Census by home district: children, teens, adults, seniors, education levels, enrolled residents by school level, homelessness, sickness and injury. Tourists, commuters and dead citizens are excluded from living resident counts. Dead counts are retained separately. A homeless household's temporary home is used when available. Missing home or district membership goes into an explicit unlocated bucket; a native null district goes into outside-districts.
- Citywide road edges with exact cubic control points, node identities and lengths; building positions and oriented lot rectangles.
- School enrollment and nominal capacity including active upgrades. Healthcare, fire, police, deathcare and garbage expose their combined native capacity fields, service state, efficiency and available patient/occupant buffer counts. Upgrades are not counted again as independent service facilities. These are observations, not a claim that all beds/vehicles/seats are effective or reachable.
- Existing service-district assignments and a guarded command to preview or replace them. Empty assignments mean citywide; null means that building does not support assignments.
- Create named districts and reshape existing districts through the game's native polygon preview/apply pipeline, with exact boundary readback and checks against unintended neighboring changes.
- Local JSON, district CSV, Markdown report and SVG map. The report separates school levels and lists each service's assigned districts. Shared capacity is never allocated in full to every district.

## Export

After installation and during a separately authorized game session, from the bridge directory:

```powershell
./export-atlas.ps1 -Capture -OutputDirectory artifacts/atlas-YYYYMMDD-HHMM
```

Requires Node.js on PATH and the bridge to be ready with the city paused. Existing dispatcher behavior pauses analysis when bridge control is enabled; with control disabled the game must already be paused. Capture refuses an in-progress construction operation or batch. No installation or game launch is performed by this script.

The first `get_district_atlas` request constructs one immutable snapshot. Subsequent pages reference its `snapshotId`; every layer has `total`, `offset`, `limit`, `nextOffset` and `truncated`. The exporter validates all pages, city/session/frame identity, completeness and unique row IDs before writing. It never silently accepts a partial map. A new capture, city load or successful assignment replaces/invalidates the previous cache. Simulation advancing does not rewrite a cached snapshot: exported data retain their original timestamp and frame. Entity IDs from a saved atlas are historical and must be refreshed before a later mutation.

For an already collected array of raw pages or successful bridge response envelopes (no game contact):

```powershell
./export-atlas.ps1 -PagesPath saved-pages.json -OutputDirectory artifacts/atlas-offline-v1
```

Use a new output directory; overwriting is rejected. Output:

- `atlas.json`: complete three-layer snapshot, census, identities, service details and limitations.
- `districts.csv`: summary for each district plus outside/unlocated rows.
- `report.md`: district comparison, enrollment by level and service assignments.
- `map.svg`: district polygons, curved roads and building footprints; service buildings highlighted.

Coordinates are **local game-world metres**, with X/Z as the horizontal plane. These are not geographic longitude/latitude and are deliberately not advertised as GIS-ready GeoJSON. The map shows +Z upward; it does not claim a compass orientation.

Direct queries:

```powershell
./bridge.ps1 get_district_atlas -ArgsJson '{"layer":"districts","limit":256}'
./bridge.ps1 get_district_atlas -ArgsJson '{"snapshotId":"REPLACE","layer":"buildings","offset":0,"limit":512}'
```

## Service assignments

`set_service_districts` accepts a live building `index`/`version`, `districts`, and `expectedDistricts`. Both arrays contain live `{index,version}` district identities; lists must be unique and contain at most 64 entries. `expectedDistricts` must match the current set, preventing an old atlas from silently replacing newer choices. Wrong generations, deleted districts, unsupported buildings and active construction/batches are rejected. Normal control/STOP and city-session checks apply.

`apply` defaults to false. Preview returns `before`, `requested`, `after`, and `applied:false`. Only an explicit `apply:true` changes the native `ServiceDistrict` buffer. The adapter emits `Updated`, matching the game's selection-tool implementation, refreshes the info panel, invalidates the atlas, and verifies the buffer by reading it back. Readback verifies assignment storage only; simulation dispatch and service reach still require observation.

Example argument shape (identities are placeholders; refresh them first):

```json
{
  "index": 100,
  "version": 1,
  "expectedDistricts": [],
  "districts": [{"index": 200, "version": 1}],
  "apply": false
}
```

On an uncertain mutation response, inspect the same request receipt and fresh assignments. Never resend blindly. Save/reload persistence has not been tested.

## District drawing

`create_district` takes a `polygon` array of 3–64 `{x,z}` corners in game-world metres and an optional `name` (1–80 printable characters). An optional repeated closing point is accepted. Terrain heights are sampled automatically. Clockwise, counterclockwise and concave polygons are supported. Self-intersections, repeated internal corners, backtracking, edges shorter than the native prefab minimum and areas below 64 square metres are rejected before opening the tool. The native tool still checks placement validity.

`edit_district` takes the same new `polygon`, a live district `index`/`version`, and the complete `expectedPolygon` from a fresh observation. The expected shape must match the current one before editing. The native recreate path updates the original district entity, preserving its identity. An optional `name` renames it only after the boundary is verified.

Both commands default to a **native preview only**. `apply:true` permits application after validation. Both return an operation ID: poll `get_operation` using that same ID until terminal. A queued/applying reply is not completion. This uses the native area definition/preview/apply systems, not direct mutation of live district node buffers.

```powershell
# Coordinates below are an argument example, not a selected site in this city.
./bridge.ps1 create_district -ArgsJson '{"name":"Example district","polygon":[{"x":0,"z":0},{"x":200,"z":0},{"x":200,"z":200},{"x":0,"z":200}],"apply":false}'
./bridge.ps1 get_operation -ArgsJson '{"id":"RETURNED_OPERATION_ID"}'
```

The preview must contain exactly one complete matching district, with the intended polygon. Deletion, another district's modification, an unexpected prefab, altered geometry or physical infrastructure changes are rejected. This version deliberately refuses a native preview that would reshape neighboring districts; it is a one-district operation. Existing boundaries are checked again before apply, and result geometry and neighboring boundaries are checked afterward. Readback tolerates reversed winding, a different starting vertex and a closing vertex, with a 5 cm coordinate tolerance. It does not accept a different vertex sequence or clipped polygon.

The tool has a 30-second wall deadline and respects control revocation, STOP and tool switching. `applicationMayHaveOccurred:true` distinguishes failures/interruption after submitting native apply. Inspect the original receipt and current district state after such a failure; do not resend blindly or assume rollback. Optional names are set and read back only after geometry succeeds. A failed naming check can therefore leave a successfully drawn district, whose identity is included in the operation result.

Capture a new atlas after drawing. Census membership updates and service effects require native systems to settle and must be observed; the drawing receipt only verifies district entities and boundaries.

## Live acceptance

District creation/boundary editing is implemented (`districtBoundaryEditing:true`); see the naming caveat above. There is no automatic calculation of exactly how many new schools or service buildings are needed: eligible applicants, effective capacity, access and shared coverage must be established first. Census totals intentionally disclose a different definition from the native HUD and need comparison in game, especially for moving and homeless households.

Under a future authorized live test: preview and create one agreed district; reshape it and verify its identity, name and neighbors; check STOP during preview and a subsequent create after an edit; compare district polygons and age totals with the UI; inspect all four school levels and an upgraded facility; check outside/unlocated buckets; capture a complete export and inspect its map; preview an assignment, apply it to one agreed building, verify the panel and observed simulation behavior, restore the original assignments and verify save/reload persistence. Test large-city capture latency as the census runs synchronously while paused. No publication is part of this local feature.

## Offline verification

```powershell
./build.ps1 -OutputDirectory artifacts/district-atlas-v1/build
dotnet run --project tests/MailboxTests.csproj
node --test tests/atlas.test.mjs
./verify-api.ps1
./tests/DistrictApiTests.ps1 -BridgeAssemblyPath artifacts/district-atlas-v1/build/CitiesIIAgentBridge.dll
./tests/TerrainApiTests.ps1 -GamePath 'C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II' -BridgeAssemblyPath artifacts/district-atlas-v1/build/CitiesIIAgentBridge.dll
```

Building/testing does not install the mod. Follow INSTALL.md and obtain installation permission.
