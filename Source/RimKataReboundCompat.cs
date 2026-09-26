using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    // Keep Rebound's own chance, trait and projectile rules; only select its weapon.
    internal static class RimKataReboundCompat
    {
        private struct WeaponScope
        {
            internal Pawn_EquipmentTracker tracker;
            internal ThingWithComps weapon;
            internal bool resolved, overridden;
        }

        [ThreadStatic] private static WeaponScope current;
        private static readonly MethodInfo PrimaryGetter = AccessTools.PropertyGetter(
            typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Primary));

        internal static void Apply()
        {
            Type api = AccessTools.TypeByName("ProjectileInversion.API");
            if (api == null) return;
            var harmony = new Harmony("krwf.rimkata.rebound");
            try
            {
                MethodInfo impact = AccessTools.DeclaredMethod(
                    AccessTools.TypeByName("ProjectileInversion.Projectile_ImpactSomething"), "Prefix");
                MethodInfo hasWeapon = AccessTools.DeclaredMethod(api, "hasWeapon", new[] { typeof(Pawn) });
                MethodInfo chance = AccessTools.DeclaredMethod(api, "CheckPawnInverseChance", new[] { typeof(Pawn) });
                MethodInfo damage = AccessTools.DeclaredMethod(api, "DamageWeapon", new[] { typeof(Pawn) });
                Validate(impact, typeof(bool), typeof(Projectile), typeof(LocalTargetInfo), typeof(Thing));
                Validate(hasWeapon, typeof(bool), typeof(Pawn));
                Validate(chance, typeof(bool), typeof(Pawn));
                Validate(damage, typeof(void), typeof(Pawn));
                Type ceImpactType = AccessTools.TypeByName("ProjectileInversion.BulletCE_Impact");
                MethodInfo ceImpact = ceImpactType == null ? null : AccessTools.DeclaredMethod(ceImpactType, "Prefix");
                if (ceImpactType != null && (ceImpact?.ReturnType != typeof(bool)
                    || !ceImpact.IsStatic || ceImpact.GetParameters().Length == 0
                    || ceImpact.GetParameters()[0].ParameterType != typeof(Thing)))
                    throw new InvalidOperationException("Rebound CE impact API did not match.");

                var transpiler = new HarmonyMethod(typeof(RimKataReboundCompat), nameof(WeaponTranspiler));
                harmony.Patch(hasWeapon, transpiler: transpiler);
                harmony.Patch(chance, transpiler: transpiler);
                harmony.Patch(damage, transpiler: transpiler);
                harmony.Patch(impact,
                    prefix: new HarmonyMethod(typeof(RimKataReboundCompat), nameof(ImpactPrefix)) { priority = Priority.First },
                    transpiler: transpiler,
                    finalizer: new HarmonyMethod(typeof(RimKataReboundCompat), nameof(ImpactFinalizer)) { priority = Priority.Last });
                if (ceImpact != null)
                    harmony.Patch(ceImpact,
                        prefix: new HarmonyMethod(typeof(RimKataReboundCompat), nameof(CeImpactPrefix)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(RimKataReboundCompat), nameof(ImpactFinalizer)) { priority = Priority.Last });
            }
            catch (Exception exception)
            {
                harmony.UnpatchAll(harmony.Id);
                Log.Warning("[RimKata] Rebound secondary weapon integration was not applied.\n" + exception);
            }
        }

        private static void Validate(MethodInfo method, Type returnType, params Type[] parameters)
        {
            if (method == null || !method.IsStatic || method.ReturnType != returnType)
                throw new InvalidOperationException("Rebound weapon API did not match.");
            ParameterInfo[] actual = method.GetParameters();
            if (actual.Length != parameters.Length)
                throw new InvalidOperationException("Rebound weapon parameters did not match.");
            for (int i = 0; i < actual.Length; i++)
                if (actual[i].ParameterType != parameters[i])
                    throw new InvalidOperationException("Rebound weapon parameters did not match.");
            bool found = false;
            foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method))
                if (instruction.Calls(PrimaryGetter)) found = true;
            if (!found) throw new InvalidOperationException("Rebound primary weapon lookup was not found.");
        }

        private static void ImpactPrefix(LocalTargetInfo __1, out WeaponScope __state)
        {
            __state = current;
            current = new WeaponScope { tracker = (__1.Thing as Pawn)?.equipment };
        }

        private static void CeImpactPrefix(Thing __0, out WeaponScope __state)
        {
            __state = current;
            current = new WeaponScope { tracker = (__0 as Pawn)?.equipment };
        }

        private static Exception ImpactFinalizer(Exception __exception, WeaponScope __state)
        {
            current = __state;
            return __exception;
        }

        private static IEnumerable<CodeInstruction> WeaponTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo replacement = AccessTools.Method(typeof(RimKataReboundCompat), nameof(SelectedWeapon));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(PrimaryGetter))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
                yield return instruction;
            }
        }

        private static ThingWithComps SelectedWeapon(Pawn_EquipmentTracker tracker)
        {
            if (tracker == null || tracker != current.tracker) return tracker?.Primary;
            if (!current.resolved)
            {
                current.resolved = true;
                Pawn pawn = tracker.pawn;
                ThingWithComps secondary = RimKataWeaponSlotUtility.SecondaryWeapon(pawn);
                if (UsableMeleeWeapon(secondary) && tracker.AllEquipmentListForReading.Contains(secondary)
                    && RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn))
                {
                    ThingWithComps primary = tracker.Primary;
                    current.weapon = UsableMeleeWeapon(primary) && Rand.Bool ? primary : secondary;
                    current.overridden = true;
                }
            }
            if (!current.overridden) return tracker.Primary;
            // A dropped weapon ends this selection; never switch hands within one rebound.
            return current.weapon != null && !current.weapon.Destroyed
                && tracker.AllEquipmentListForReading.Contains(current.weapon) ? current.weapon : null;
        }

        private static bool UsableMeleeWeapon(ThingWithComps weapon)
        {
            return weapon != null && !weapon.Destroyed && weapon.def.IsMeleeWeapon
                && !BreakdownableUtility.IsBrokenDown(weapon);
        }
    }
}
