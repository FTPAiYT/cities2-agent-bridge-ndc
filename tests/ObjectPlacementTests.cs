using System;
using CitiesIIAgentBridge;

internal static class ObjectPlacementTests
{
    // Small native-boundary fixture: the native definition builder consumes the
    // retained moving entity even after its public mode changes to Create.
    private sealed class NativeToolFixture
    {
        private int m_MovingObject, m_MovingInitialized, m_UpgradingObject;
        private object m_TransformPrefab, m_Prefab;
        internal void RetainMove(int entity, object prefab)
        { m_MovingObject = m_MovingInitialized = entity; m_Prefab = prefab; }
        internal void RetainUpgrade() { m_UpgradingObject = 9; m_TransformPrefab = new object(); }
        internal (int original, object prefab) Definition() => (m_MovingObject, m_Prefab);
        internal bool OtherStateCleared => m_MovingInitialized == 0 && m_UpgradingObject == 0 && m_TransformPrefab == null;
    }

    internal static int Run()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
        var tool = new NativeToolFixture();
        var park = new object(); var fire = new object(); var police = new object();
        tool.RetainMove(7, park);
        Check(tool.Definition().original == 7 && tool.Definition().prefab == park, "fixture reproduces retained relocation state");
        tool.RetainUpgrade();
        ObjectPlacementState.Prepare(typeof(NativeToolFixture), tool, 0, 0, fire);
        Check(tool.Definition().original == 0 && tool.Definition().prefab == fire, "relocate A then create B clears old original and working prefab");
        Check(tool.OtherStateCleared, "new request clears initialized move, upgrade and transform state");
        ObjectPlacementState.Prepare(typeof(NativeToolFixture), tool, 0, 0, police);
        Check(tool.Definition().prefab == police && tool.Definition().original == 0, "consecutive creates refresh working prefab");
        ObjectPlacementState.Prepare(typeof(NativeToolFixture), tool, 0, 8, fire);
        Check(tool.Definition().original == 8 && tool.Definition().prefab == fire, "relocation retains only requested entity");
        // A failed/interrupted move still leaves the native field set; next Begin must reset it.
        ObjectPlacementState.Prepare(typeof(NativeToolFixture), tool, 0, 0, police);
        Check(tool.Definition().original == 0, "create after interrupted relocation clears native target");
        bool missing = false;
        try { ObjectPlacementState.Prepare(typeof(object), new object(), 0, 0, fire); } catch (MissingFieldException) { missing = true; }
        Check(missing, "changed native layout fails closed");

        var create = new BuildingPreviewEntry { PrefabMatches = true, Create = true };
        var move = new BuildingPreviewEntry { PrefabMatches = true, HasOriginal = true, OriginalMatches = true, Modify = true };
        string Validate(BuildingPreviewEntry[] entries, bool relocating = false, bool demolish = false) => BuildingPreviewSafety.Validate(entries, relocating, demolish);
        Check(Validate(new[] { create }) == null, "one requested new building accepted");
        Check(Validate(new[] { move }, true) == null, "one requested relocation accepted");
        var stalePark = move; stalePark.PrefabMatches = false; stalePark.OriginalMatches = false;
        Check(Validate(new[] { stalePark }) == "preview_prefab_mismatch", "stale different-building move rejected before apply");
        var staleSamePrefab = move; staleSamePrefab.OriginalMatches = false;
        Check(Validate(new[] { staleSamePrefab }) == "preview_unexpected_existing_building", "stale same-prefab move rejected for create");
        Check(Validate(new[] { staleSamePrefab }, true) == "preview_relocation_target_mismatch", "wrong relocation entity rejected");
        var wrongPrefab = create; wrongPrefab.PrefabMatches = false;
        Check(Validate(new[] { wrongPrefab }) == "preview_prefab_mismatch", "wrong new building type rejected");
        Check(Validate(Array.Empty<BuildingPreviewEntry>()) == "preview_expected_one_building", "empty preview cannot report success");
        Check(Validate(new[] { create, create }) == "preview_expected_one_building", "duplicate building preview rejected");
        Check(Validate(new[] { create }, true) == "preview_relocation_target_mismatch", "create cannot substitute for requested relocation");
        var deletion = new BuildingPreviewEntry { HasOriginal = true, Delete = true };
        Check(Validate(new[] { create, deletion }) == "unexpected_building_deletion", "unapproved collateral demolition rejected");
        Check(Validate(new[] { create, deletion }, false, true) == null, "explicit collateral demolition permitted alongside requested create");
        Check(Validate(new[] { move, deletion }, true, true) == null, "explicit collateral demolition permitted alongside relocation");
        var targetDeletion = deletion; targetDeletion.OriginalMatches = true;
        Check(Validate(new[] { move, targetDeletion }, true, true) == "unexpected_building_deletion", "relocation target cannot be deleted even with demolition enabled");
        Check(Validate(new[] { create, staleSamePrefab }, false, true) != null, "demolition permission never permits unrelated modifications");
        var cancelled = stalePark; cancelled.Cancel = true;
        Check(Validate(new[] { create, cancelled }) == null, "cancelled leftover preview does not poison valid request");
        Check(Validate(new[] { cancelled }) == "preview_expected_one_building", "cancelled target cannot satisfy request");
        var noAction = new BuildingPreviewEntry { PrefabMatches = true };
        Check(Validate(new[] { noAction }) != null, "matching prefab with no create flag rejected");
        return passed;
    }
}
