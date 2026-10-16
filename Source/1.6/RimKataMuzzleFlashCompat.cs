using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataMuzzleFlashCompat
    {
        private struct FlashScope
        {
            internal Verb verb;
            internal bool centerResolved;
            internal bool hasCenter;
            internal Vector3 center;
        }

        [ThreadStatic] private static FlashScope current;

        internal static void Apply(Harmony harmony)
        {
            Type utility = RimKataActiveModTypes.Find("MuzzleFlash.MuzzleFlashUtility");
            if (utility == null)
                return;

            Type flashDef = RimKataActiveModTypes.Find("MuzzleFlash.MuzzleFlashDef");
            Type burstPatch = RimKataActiveModTypes.Find("MuzzleFlash.Patch.HarmonyPatch_Verb");
            MethodInfo burst = burstPatch == null ? null : AccessTools.Method(
                burstPatch, "Postfix", new[] { typeof(Verb), typeof(int) });
            MethodInfo indexed = AccessTools.Method(utility, "SpawnMuzzleFlashByVerbIndex",
                new[] { typeof(Verb), typeof(int), typeof(bool) });
            MethodInfo primary = AccessTools.Method(utility, "IsPrimaryVerb", new[] { typeof(Verb) });
            MethodInfo available = AccessTools.Method(utility, "MuzzleFlashAvailable", new[] { typeof(Verb) });
            MethodInfo spawn = flashDef == null ? null : AccessTools.Method(utility, "SpawnMuzzleFlash",
                new[] { typeof(Map), flashDef, typeof(Vector3), typeof(Vector3),
                    typeof(Vector3), typeof(Vector2), typeof(bool) });
            if (burst?.ReturnType != typeof(void) || indexed?.ReturnType != typeof(void)
                || spawn?.ReturnType != typeof(void) || primary?.ReturnType != typeof(bool)
                || available?.ReturnType != typeof(bool))
            {
                Log.Warning("[RimKata] Muzzle Flash API did not match; ground-pose flash integration was not applied.");
                return;
            }

            var adapter = new Harmony(harmony.Id + ".groundPoseMuzzleFlash");
            try
            {
                RimKataStartupPatches.Patch(adapter, burst, prefix: Patch(nameof(BeginFlash)), finalizer: Patch(nameof(EndFlash)));
                RimKataStartupPatches.Patch(adapter, indexed, prefix: Patch(nameof(BeginFlash)), finalizer: Patch(nameof(EndFlash)));
                RimKataStartupPatches.Patch(adapter, spawn, prefix: Patch(nameof(SpawnFlashPrefix)));
                RimKataStartupPatches.Patch(adapter, primary, postfix: Patch(nameof(IsPrimaryVerbPostfix)));
                RimKataStartupPatches.Patch(adapter, available, postfix: Patch(nameof(FlashAvailablePostfix)));
            }
            catch
            {
                adapter.UnpatchAll(adapter.Id);
                throw;
            }
        }

        private static HarmonyMethod Patch(string name) =>
            new HarmonyMethod(typeof(RimKataMuzzleFlashCompat), name);

        private static void BeginFlash(Verb __0, out FlashScope __state)
        {
            __state = current;
            current = new FlashScope { verb = __0 };
        }

        private static void EndFlash(FlashScope __state) => current = __state;

        private static void SpawnFlashPrefix(ref Vector3 __2)
        {
            if (current.verb == null)
                return;
            if (!current.centerResolved)
            {
                current.centerResolved = true;
                current.hasCenter = RimKataGroundPoseUtility.TryGetShotCenter(current.verb, out current.center, headCentered: true);
            }
            if (current.hasCenter)
            {
                __2.x = current.center.x;
                __2.z = current.center.z;
            }
        }

        private static void IsPrimaryVerbPostfix(Verb __0, ref bool __result)
        {
            if (__result || __0 == null || current.verb != __0
                || !RimKataPreparedWeaponData.TryGetPrepared(__0, out RimKataPreparedWeaponValues prepared))
                return;

            // Muzzle Flash compares VerbProperties by identity; RimKata binds a single-shot copy.
            var definitions = __0.EquipmentSource?.def?.Verbs;
            if (definitions != null && definitions.Count != 0
                && ReferenceEquals(definitions[0], prepared.OriginalProperties))
                __result = true;
        }

        private static void FlashAvailablePostfix(Verb __0, ref bool __result)
        {
            // Muzzle Flash's burst postfix also runs for cancelled shots; Reset clears their target.
            if (__result && RimKataCrawlFireUtility.IsCrawlVerb(__0)
                && !__0.CurrentTarget.IsValid)
                __result = false;
        }
    }
}
