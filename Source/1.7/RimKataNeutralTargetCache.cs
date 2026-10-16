using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataNeutralTargetInvalidation
    {
        internal sealed class Version
        {
            internal int revision;

            public Version() { }
        }

        private static ConditionalWeakTable<Map, Version> mapVersions =
            new ConditionalWeakTable<Map, Version>();
        private static int globalRevision;

        internal static int GlobalRevision => globalRevision;

        internal static Version ForMap(Map map)
        {
            return map != null ? mapVersions.GetOrCreateValue(map) : null;
        }

        internal static void Invalidate(Map map)
        {
            if (map != null && mapVersions.TryGetValue(map, out Version version))
            {
                unchecked { version.revision++; }
            }
        }

        internal static void InvalidateAll()
        {
            unchecked { globalRevision++; }
        }

        internal static void ResetGame()
        {
            mapVersions = new ConditionalWeakTable<Map, Version>();
            InvalidateAll();
        }
    }

    [HarmonyPatch(typeof(AttackTargetsCache), nameof(AttackTargetsCache.UpdateTarget))]
    internal static class Patch_AttackTargetsCacheUpdateTarget_RimKataNeutralTargets
    {
        private static void Postfix(IAttackTarget __0, Map ___map)
        {
            if (__0 is Pawn) RimKataNeutralTargetInvalidation.Invalidate(___map);
        }
    }
}
