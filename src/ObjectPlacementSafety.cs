using System;
using System.Collections.Generic;
using System.Reflection;

namespace CitiesIIAgentBridge
{
    // The adapter calls native snapping/definition methods without native OnUpdate.
    // Reproduce its state transition explicitly, including the working prefab.
    internal static class ObjectPlacementState
    {
        internal static void Prepare(Type nativeType, object tool, object emptyEntity, object movingEntity, object selectedPrefab)
        {
            Set(nativeType, tool, "m_MovingObject", movingEntity);
            Set(nativeType, tool, "m_MovingInitialized", emptyEntity);
            Set(nativeType, tool, "m_UpgradingObject", emptyEntity);
            Set(nativeType, tool, "m_TransformPrefab", null);
            Set(nativeType, tool, "m_Prefab", selectedPrefab);
        }

        private static void Set(Type type, object target, string name, object value)
        {
            var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null) throw new MissingFieldException(type.FullName, name);
            field.SetValue(target, value);
        }
    }

    internal struct BuildingPreviewEntry
    {
        internal bool PrefabMatches, HasOriginal, OriginalMatches, Create, Modify, Delete, Cancel;
    }

    internal static class BuildingPreviewSafety
    {
        // Entries represent root buildings only, not generated lanes, props or sub-buildings.
        // This same gate is required for dry-run success and for application.
        internal static string Validate(IEnumerable<BuildingPreviewEntry> entries, bool relocating, bool allowDemolition)
        {
            int targets = 0;
            foreach (var entry in entries)
            {
                if (entry.Cancel) continue;
                if (entry.Delete)
                {
                    if (!allowDemolition || !entry.HasOriginal || entry.OriginalMatches || entry.Create || entry.Modify)
                        return "unexpected_building_deletion";
                    continue;
                }
                if (!entry.PrefabMatches) return "preview_prefab_mismatch";
                if (relocating)
                {
                    if (!entry.HasOriginal || !entry.OriginalMatches || !entry.Modify || entry.Create)
                        return "preview_relocation_target_mismatch";
                }
                else if (entry.HasOriginal || !entry.Create || entry.Modify)
                    return "preview_unexpected_existing_building";
                targets++;
            }
            return targets == 1 ? null : "preview_expected_one_building";
        }
    }
}
