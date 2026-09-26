using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    // Installed only with CE. Its extra-aim timer accepts our mobile warmup
    // without changing CE's native firing, aim mode or ordinary stance checks.
    internal static class RimKataCombatExtendedCrawlFire
    {
        internal static void Apply(Harmony harmony, Type shooter)
        {
            RimKataCrawlFireUtility.ResolveOriginalWarmup = RimKataCombatExtendedFire.OriginalWarmup;
            MethodInfo tick = AccessTools.DeclaredMethod(shooter, "VerbTickCE", Type.EmptyTypes);
            Type ammo = AccessTools.TypeByName("CombatExtended.CompAmmoUser");
            MethodInfo reload = ammo == null ? null : AccessTools.DeclaredMethod(
                ammo, "TryStartReload", Type.EmptyTypes);
            MethodInfo outOfAmmo = ammo == null ? null : AccessTools.DeclaredMethod(
                ammo, "DoOutOfAmmoAction", Type.EmptyTypes);
            if (tick?.ReturnType != typeof(void) || reload?.ReturnType != typeof(void)
                || outOfAmmo?.ReturnType != typeof(void))
                throw new InvalidOperationException("CE crawl warmup API does not match.");

            harmony.Patch(tick, transpiler: new HarmonyMethod(
                typeof(RimKataCombatExtendedCrawlFire), nameof(WarmupTypeTranspiler)));
            var preserveCrawlJob = new HarmonyMethod(
                typeof(RimKataCombatExtendedCrawlFire), nameof(AmmoActionPrefix));
            harmony.Patch(reload, prefix: preserveCrawlJob);
            harmony.Patch(outOfAmmo, prefix: preserveCrawlJob);
            RimKataCrawlFireHits.ApplyCombatExtended(harmony);
        }

        private static IEnumerable<CodeInstruction> WarmupTypeTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            MethodInfo fromHandle = AccessTools.Method(typeof(Type), nameof(Type.GetTypeFromHandle));
            MethodInfo inequality = AccessTools.Method(typeof(Type), "op_Inequality",
                new[] { typeof(Type), typeof(Type) });
            int match = -1;
            for (int i = 2; i < codes.Count; i++)
            {
                if (codes[i - 2].opcode != OpCodes.Ldtoken
                    || !Equals(codes[i - 2].operand, typeof(Stance_Warmup))
                    || !codes[i - 1].Calls(fromHandle)
                    || !codes[i].Calls(inequality))
                    continue;
                if (match >= 0 || codes[i].blocks.Count != 0)
                    throw new InvalidOperationException("CE warmup type check does not match the supported shape.");
                match = i;
            }
            if (match < 0)
                throw new InvalidOperationException("CE warmup type check was not found.");

            CodeInstruction comparison = codes[match];
            var loadVerb = new CodeInstruction(OpCodes.Ldarg_0);
            loadVerb.labels.AddRange(comparison.labels);
            comparison.labels.Clear();
            comparison.opcode = OpCodes.Call;
            comparison.operand = AccessTools.Method(typeof(RimKataCombatExtendedCrawlFire),
                nameof(IsDifferentWarmupType));
            codes.Insert(match, loadVerb);
            return codes;
        }

        private static bool IsDifferentWarmupType(Type actual, Type expected, Verb verb)
        {
            // Other mods' stance subclasses retain CE's original exact-type rule.
            return actual != expected
                && (actual != typeof(Stance_RimKataCrawlWarmup)
                    || !RimKataCrawlFireUtility.IsCrawlVerb(verb));
        }

        private static bool AmmoActionPrefix(ThingComp __instance)
        {
            // Running out of rounds ends crawling fire. CE must not replace the
            // pawn's crawl job with reloading, collecting ammo or switching guns.
            return !RimKataCrawlFireUtility.IsCrawlWeapon(__instance.parent);
        }
    }
}
