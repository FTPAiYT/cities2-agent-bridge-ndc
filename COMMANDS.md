# Command reference — 0.4.3

Bridge 0.4.3 exposes 48 commands. The shared visibility changes passed live checks in the development runtime on September 16, 2026; the separately compiled community binary has not been loaded in-game. See [validation](VALIDATION.txt). The client is `bridge.ps1 COMMAND -ArgsJson 'JSON'`; `commands.json` is the machine-readable command list. Analysis and construction pause first. With controls disabled, pause manually before inspection. Status polls are exempt so a bounded simulation can reach its stop condition.

Coordinates are game-world metres. Obtain actual positions and IDs through inspection; do not reuse IDs across city sessions. Position objects use `x`, `z`, and optional `y` (terrain height is sampled when absent). Optional `index`/`version` attach a point to a node, edge, or zone block. Edge attachment also requires `curvePosition` between 0 and 1. Prefab arguments use `prefabIndex`/`prefabVersion` from `get_build_prefabs`.

| Command | Arguments and result |
|---|---|
| `ping` | Live connection and mod version. |
| `get_capabilities` | Command groups, build version, control and validation status. |
| `get_city_state` | Population, money, health, happiness, XP, time, simulation speed. |
| `get_city_management` | City state, demand by category, tax rates, households, income/expense breakdowns. Financial source values are reported in native raw units. |
| `get_build_prefabs` | Optional `filter`, `kind`, `offset`, `limit`; paginated assets with IDs, lock status and placement support. See the pagination contract below. |
| `get_prefab_details` | `index`, `version`; known native placement, capacity, and service data fields. |
| `get_services` | Service IDs, budgets, building IDs, workers and workplaces. Check `complete` and `errors` for partial results. |
| `get_buildings` | Optional `filter`, `problemsOnly`, `offset`, `limit`, and spatial `x,z,radius`; paginated building details. Default 512 per page, maximum 4096. Follow `nextOffset` to completion. |
| `diagnose_connections` | Same arguments as `get_buildings`, restricted to detected problems. Null connections and supply shortfalls are distinguished. |
| `get_water_facilities` | Water producers and sewage outlets, positions and production/processing readings. |
| `get_selected` | Inspect the selected entity. |
| `inspect_entity` | `index`, `version`; prefab, position, component names and supported utility readings. |
| `get_network` | `x`, `z`, `radius` ≤1000; nearby nodes and connected edge IDs. |
| `get_network_edges` | `x`, `z`, `radius` ≤2000; curves, endpoint IDs, lengths, prefab names, raw traffic distance/duration counters. |
| `trace_network` | `fromIndex`, `fromVersion`, `toIndex`, `toVersion`; physical graph connectivity and edge path. This does not prove vehicle routability or sufficient utility capacity. |
| `get_zone_cells` | `x`, `z`, `radius` ≤500; optional `offset`, `limit` 1–65536 (default2048). Stable entity-index pagination with `total`, `truncated`, `nextOffset`. |
| `sample_terrain` | `points:[{x,z},…]`, up to 1024; height, normal, water depth/velocity/pollution and ground, air and noise pollution. |
| `get_tiles` | Tile IDs, purchased state, boundary polygons and remaining purchase allowance. |
| `get_camera` | Camera pivot, position, angle and zoom. |
| `set_camera` | Optional `pivot:{x,y,z}`, `zoom`; returns before/after controller targets. |
| `set_simulation_speed` | `speed`: 0, 1, 2, or 4. |
| `build_road` | Prefab IDs, `start`, `end`, `maxCost`; optional `control` for a simple curve, `elevation` offset (−100 to 100). Native snapping, preview, errors, spending check, application and entity verification. |
| `build_network` | Same arguments and pipeline for pipes, power lines and other unlocked placeable network prefabs. Read prefab placement information before choosing elevation. |
| `upgrade_network` | Prefab IDs, target edge `index`/`version`, `maxCost`; native replacement preview and post-application check. |
| `zone_rectangle` | Zone prefab IDs, `start`, `end`, optional `dezone`; native marquee, diagonal ≤500, does not overwrite existing zones. Block-attached start points align the rectangle with that block; otherwise native camera orientation defines the axes. |
| `clear_zoning` | Same arguments as zoning; selects the native dezoning operation. |
| `place_building` | Building prefab IDs, `position`, `rotation` in degrees, `maxCost`. Optional `candidates:[{position,rotation},…]` (1–16), `maxSnapDistance`0–128 (default32), `previewOnly`, `allowDemolition`(defaultfalse). Native preview and bounded candidate retries; expected-prefab/position verification. |
| `relocate_building` | Placement arguments plus `moveIndex`, `moveVersion`; native move mode with a result check. |
| `demolish` | Target `index`, `version`; only buildings/network edges; checks native demolition preview and observed removal. Connected sub-entities may be removed by the game's native operation. |
| `purchase_tiles` | `tiles:[{index,version},…]`, `maxCost`; native selection validation, purchase cost and purchased-state verification. |
| `set_tax` | `area`: Residential, Commercial, Industrial or Office; `rate` −10 to 30. |
| `set_service_budget` | Service prefab IDs, `budget` 50–150. |
| `save_checkpoint` | Optional safe `label`; unique local save through the game’s save system, including preview and metadata; no existing save overwritten. |
| `get_operation` | `id`; queued/validating/applying/complete/failed/interrupted plus results. |
| `batch_execute` | `steps:[{command,args},…]`,1–64; optional `reserve`. Sequential while paused, waits for operations, stops at first failure; spending limits preserve reserve. Overall 600-second deadline. Other batch mutations are rejected while running. |
| `get_batch` | `id`; status, completed count, per-step results and failure index. |

The client also has `stop`, which writes the bridge stop latch. This disables subsequent control and stops construction/batches at their next check. An active bounded simulation step pauses at the next bridge tick. Already-applied changes remain. Remove the STOP file and re-enable the option to resume.

## Full city workflow

1. Discover unlocked prefabs and inspect placement/capacity data.
2. Read purchased land, terrain, existing roads and utility connections.
3. Start a batch with a save checkpoint, then roads/utilities, service facilities and zoning. Every construction step includes its spending limit.
4. Poll the batch result, then inspect actual created entities, connectivity, supply, demand and finances.
5. Advance simulation and reassess capacity and demand before expanding.

Batch example structure (replace IDs/coordinates with queried values before execution):

```json
{"steps":[
  {"command":"save_checkpoint","args":{"label":"before-expansion"}},
  {"command":"build_network","args":{"prefabIndex":123,"prefabVersion":1,"start":{"x":100,"z":100},"end":{"x":200,"z":100},"maxCost":5000}},
  {"command":"place_building","args":{"prefabIndex":456,"prefabVersion":1,"position":{"x":150,"z":120},"rotation":90,"maxCost":50000}}
]}
```

The batch is sequential, not transactional. A later failure leaves earlier successful steps intact. Returned IDs belong to the current city session. Save checkpoints are recoverable through CS2’s normal Load Game menu.

## Verification status

See VALIDATION.txt for current offline checks and the distinction between development-runtime evidence and community-binary verification.

## Added commands

| Command | Arguments and result |
|---|---|
| `pause_for_analysis` | Pause and return comprehensive diagnostics. Requires control. |
| `get_city_diagnostics` | Happiness/demand factors, education/workforce, finances, service capacity/occupancy, persistent shortages and efficiency penalties. |
| `get_city_map` | `x,z,radius`(16–500): footprints, roads, cells, coarse terrain/pollution grid, tiles, frame and paused state. |
| `find_building_sites` | Building prefab IDs, `x,z,radius`(16–500;default150): up to 16 geometric candidates along roads. Native validation still required. |
| `preview_building` | Same arguments as `place_building`; native preview returns cost/snapped position without applying. Requires control because native preview tools activate. |
| `plan_neighborhood` | `maxTotalCost`, optional `reserve`, arrays `roads`, `utilities`, `buildings`, `zones` containing normal command arguments. Optional `grid` below. Returns stored plan ID, steps, cost bounds, errors/warnings. |
| `execute_neighborhood` | `id`: revalidate/submit a stored ready plan with before/after saves. Returns `batch.id`. |
| `get_neighborhood_plan` | `id`: plan and execution state if submitted. |
| `cancel_batch` | Interrupt remaining work. Applied changes remain; an already-running save can finish. |
| `simulate_step` | `frames`1–262144(default4096), `wallSeconds`1–60(default30), `stallSeconds`1–wall(defaultmin(5,wall)), `speed`1/2/4(default4). Optional `cashFloor`, positive `populationChange`, `stopOnDemandChange`, `stopOnConstructionComplete`, `stopOnNewShortage`(defaulttrue), `acknowledgeNoProgress`. Returns immediately with operation ID. |
| `get_simulation_step` | `id`: status poll that does not pause. Finished results contain `reason`, `paused`, `advancedFrames`, `before`, `after`, `delta`, `elapsedSeconds`, `stagnantSteps`, `reassessmentRequired`. Snapshot failure/city change can omit `after`; check `snapshotError` and `paused`. |
| `cancel_simulation_step` | Pause/end the active step without disabling controls. |

`grid` fields: `x,z`, `columns,rows`1–4, `blockWidth,blockDepth`64–200metres, `roadPrefabIndex,roadPrefabVersion`, `zonePrefabIndex,zonePrefabVersion`, `maxCostPerRoad`. Grid coordinates are world-aligned. Explicit roads can connect to the existing city; service buildings/utilities belong in the same plan. Total generated plus explicit steps must be 1–62, leaving room for two saves. Zones retain native marquee semantics; block-attached starts give exact orientation.

`spendingUpperBound` sums supplied per-step maximums; it is not an exact native quote. It must fit `maxTotalCost` and treasury minus reserve. `knownBuildingBaseCost` uses native prefab base costs; network costs are confirmed during preview. Footprint screening does not model every sub-object, curved road, terrain constraint or infrastructure connection.

Recommended loop:

```powershell
.\bridge.ps1 pause_for_analysis
.\export-map.ps1 -X -1600 -Z 500 -Radius 400
# Analyze, plan, execute, then wait for the batch to finish while paused.
.\advance.ps1 -Frames 4096 -WallSeconds 20 -PopulationChange 50
# The final snapshot is taken after pausing. Analyze before the next action.
```

`advance.ps1` never automatically retries simulation. On a client deadline it requests cancellation once and reports if pause cannot be confirmed. A fully hung game thread cannot execute its watchdog until it resumes; the independent client deadline prevents indefinite waiting.

## Building preview identity (0.4.2 and later)

Creation and relocation validate the requested prefab and original entity before application or successful preview-only completion. Results include `expectedPrefab`, `expectedOriginal` and `previewBuildings`. A mismatch fails without application; inspect the result before retrying. `allowDemolition` permits collateral deletion only, never an unrelated relocation or modification.

## Visibility changes in 0.4.3

- `get_buildings` and `diagnose_connections`: optional `offset` (default0), `limit` (default512, max4096), and spatial `x,z,radius` together (radius >0, <=14000). Existing `filter` and `problemsOnly` remain. Results are sorted by entity index/version and include `total`, `truncated`, `nextOffset`, `citySession`. Follow `nextOffset` until null while the city stays paused; abandon collected pages when the city session changes. Road-disconnection warnings now require the prefab's RequireRoad flag.
- `get_build_prefabs`: optional `kind` (`building`, `network`, `zone`, `service`, `tree`, `prop`, `surface`, `other`, `all`), `filter`, `offset`, `limit` (default4096, max20000). Default includes known categories; `all` includes internal prefab types too. Includes pagination metadata, `prefabType` and `bridgePlacementSupported`. Discovery does not imply supported placement; `locked` still applies.
- `get_services`: service-prefab type guard; per-service exceptions appear in `errors` with `complete:false` instead of losing the whole response. Never interpret partial results as complete service coverage.
- `get_capabilities`: explicit visibility coverage and relevant loaded assembly versions. Assembly presence does not prove successful mod initialization. Traffic lane-rule, full Building Use metrics and Road Builder configuration adapters remain unsupported.
- City state's `dateMeaning` warns that raw simulation datetime differs from the displayed calendar. The existing `date` field is retained for compatibility, not corrected to a guessed calendar.
