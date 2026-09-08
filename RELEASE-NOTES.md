# Community preview 0.4.2 — placement state and mailbox recovery

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

Both private-development and neutral-community adapters compile against the reference Windows Steam Cities: Skylines II 1.6.0f1 assembly. All 90 offline checks pass, including 24 new placement checks. The native-state tests use a small boundary fixture; they are not an in-game construction run. This new DLL has not been loaded in-game, installed on another PC, or tested against other mods/game versions. Two existing obsolete-updater warnings remain.

The public source update does not replace the latest published release ZIP. See VALIDATION.txt for current evidence and DEVELOPMENT.md for the required live reproduction before a binary release.

## Other limitations

Controls reset off on city load. Analysis commands pause when authorized. Entity IDs are session-specific. A failed batch leaves earlier work in place. Static planning can miss pedestrian paths or native placement restrictions. The command set does not cover every UI action. When native placement fails, inspect the recorded outcome rather than replaying it blindly.
