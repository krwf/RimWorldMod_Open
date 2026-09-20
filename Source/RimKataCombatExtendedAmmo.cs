using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataCombatExtendedAmmo
    {
        private delegate bool FindWeaponInvoker(
            ThingComp inventory, out ThingWithComps weapon, bool useAOE, object predicate);

        private sealed class WeaponAmmo
        {
            internal readonly ThingComp comp;
            internal int lastReloadAttemptTick = -1;

            internal WeaponAmmo(ThingWithComps weapon)
            {
                comp = RimKataCombatExtendedCompat.FindComp(weapon, ammoType);
            }
        }

        private static readonly ConditionalWeakTable<ThingWithComps, WeaponAmmo> Weapons =
            new ConditionalWeakTable<ThingWithComps, WeaponAmmo>();
        private static Type ammoType;
        private static Type rangedVerbType;
        private static Func<ThingComp, bool> canFire;
        private static Action<ThingComp> startReload;
        private static MethodInfo findWeapon;
        private static MethodInfo switchWeapon;
        private static FindWeaponInvoker findWeaponInvoker;
        private static Func<ThingComp, bool, bool, bool, object, bool> switchWeaponInvoker;

        internal static void Apply(Harmony harmony, Type compType)
        {
            MethodInfo ready = AccessTools.PropertyGetter(compType, "CanBeFiredNow");
            MethodInfo reload = AccessTools.Method(compType, "TryStartReload", Type.EmptyTypes);
            MethodInfo exhausted = AccessTools.Method(compType, "DoOutOfAmmoAction", Type.EmptyTypes);
            Type inventoryType = AccessTools.TypeByName("CombatExtended.CompInventory");
            Type predicateType = typeof(Func<,,>).MakeGenericType(typeof(ThingWithComps), compType, typeof(bool));
            findWeapon = AccessTools.Method(inventoryType, "TryFindViableWeapon",
                new[] { typeof(ThingWithComps).MakeByRefType(), typeof(bool), predicateType });
            switchWeapon = AccessTools.Method(inventoryType, "SwitchToNextViableWeapon",
                new[] { typeof(bool), typeof(bool), typeof(bool), predicateType });
            if (ready?.ReturnType != typeof(bool) || reload?.ReturnType != typeof(void)
                || exhausted?.ReturnType != typeof(void) || findWeapon?.ReturnType != typeof(bool)
                || switchWeapon?.ReturnType != typeof(bool))
            {
                Log.Warning("[RimKata] CE ammo API did not match; automatic reload integration was not applied.");
                return;
            }

            try
            {
                var comp = Expression.Parameter(typeof(ThingComp), "comp");
                var instance = Expression.Convert(comp, compType);
                canFire = Expression.Lambda<Func<ThingComp, bool>>(Expression.Call(instance, ready), comp).Compile();
                startReload = Expression.Lambda<Action<ThingComp>>(Expression.Call(instance, reload), comp).Compile();
                var inventory = Expression.Parameter(typeof(ThingComp), "inventory");
                var weapon = Expression.Parameter(typeof(ThingWithComps).MakeByRefType(), "weapon");
                var aoe = Expression.Parameter(typeof(bool), "useAOE");
                var predicate = Expression.Parameter(typeof(object), "predicate");
                var fists = Expression.Parameter(typeof(bool), "useFists");
                var stop = Expression.Parameter(typeof(bool), "stopJob");
                findWeaponInvoker = Expression.Lambda<FindWeaponInvoker>(
                    Expression.Call(Expression.Convert(inventory, inventoryType), findWeapon,
                        weapon, aoe, Expression.Convert(predicate, predicateType)), inventory, weapon, aoe, predicate).Compile();
                switchWeaponInvoker = Expression.Lambda<Func<ThingComp, bool, bool, bool, object, bool>>(
                    Expression.Call(Expression.Convert(inventory, inventoryType), switchWeapon,
                        fists, aoe, stop, Expression.Convert(predicate, predicateType)), inventory, fists, aoe, stop, predicate).Compile();
                ValidateOutOfAmmoCalls(PatchProcessor.GetOriginalInstructions(exhausted));
                harmony.Patch(exhausted, transpiler: new HarmonyMethod(
                    typeof(RimKataCombatExtendedAmmo), nameof(OutOfAmmoTranspiler)));
                rangedVerbType = AccessTools.TypeByName("CombatExtended.Verb_LaunchProjectileCE");
                ammoType = compType;
            }
            catch (Exception exception)
            {
                ammoType = null;
                harmony.Unpatch(exhausted, AccessTools.Method(
                    typeof(RimKataCombatExtendedAmmo), nameof(OutOfAmmoTranspiler)));
                Log.Warning("[RimKata] CE automatic reload integration could not be applied: " + exception.Message);
            }
        }

        // Called only before a qualified pawn begins a native weapon attack. CE
        // can replace the current job here, so the caller must stop on false.
        internal static bool EnsureReady(Pawn pawn, Verb verb)
        {
            if (ammoType == null || verb == null || verb.IsMeleeAttack
                || rangedVerbType?.IsInstanceOfType(verb) != true)
                return true;
            ThingWithComps weapon = verb?.EquipmentSource;
            if (pawn?.equipment == null || weapon == null
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || weapon.holdingOwner != pawn.equipment.GetDirectlyHeldThings())
                return true;

            WeaponAmmo ammo = Weapons.GetValue(weapon, CreateAmmo);
            if (ammo.comp == null || canFire(ammo.comp))
                return true;

            int tick = Find.TickManager?.TicksGame ?? -1;
            if (tick < 0 || ammo.lastReloadAttemptTick != tick)
            {
                ammo.lastReloadAttemptTick = tick;
                startReload(ammo.comp);
            }
            return false;
        }

        private static WeaponAmmo CreateAmmo(ThingWithComps weapon) => new WeaponAmmo(weapon);

        private static void ValidateOutOfAmmoCalls(List<CodeInstruction> codes)
        {
            int findCalls = 0;
            int switchCalls = 0;
            foreach (CodeInstruction code in codes)
            {
                if (!Equals(code.operand, findWeapon) && !Equals(code.operand, switchWeapon))
                    continue;
                if ((code.opcode != OpCodes.Callvirt && code.opcode != OpCodes.Call) || code.blocks.Count != 0)
                    throw new InvalidOperationException("CE out-of-ammo weapon calls do not match the supported shape.");
                if (Equals(code.operand, findWeapon)) findCalls++;
                else switchCalls++;
            }
            if (findCalls != 1 || switchCalls != 1)
                throw new InvalidOperationException("CE out-of-ammo weapon calls do not match the supported shape.");
        }

        private static IEnumerable<CodeInstruction> OutOfAmmoTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            ValidateOutOfAmmoCalls(codes);
            foreach (CodeInstruction code in codes)
            {
                string replacement = Equals(code.operand, findWeapon) ? nameof(FindReplacementWeapon)
                    : Equals(code.operand, switchWeapon) ? nameof(SwitchReplacementWeapon) : null;
                if (replacement != null)
                {
                    // Pass this ammunition component in addition to CE's existing
                    // call arguments. Its mote and TryPickupAmmo path stay intact.
                    var loadAmmo = new CodeInstruction(OpCodes.Ldarg_0);
                    loadAmmo.labels.AddRange(code.labels);
                    code.labels.Clear();
                    yield return loadAmmo;
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(RimKataCombatExtendedAmmo), replacement);
                }
                yield return code;
            }
        }

        private static bool FindReplacementWeapon(
            ThingComp inventory, out ThingWithComps weapon, bool useAOE, object predicate, ThingComp ammo)
        {
            if (!IsSecondaryAmmo(ammo))
                return findWeaponInvoker(inventory, out weapon, useAOE, predicate);
            weapon = null;
            return false;
        }

        private static bool SwitchReplacementWeapon(
            ThingComp inventory, bool useFists, bool useAOE, bool stopJob, object predicate, ThingComp ammo)
        {
            return !IsSecondaryAmmo(ammo)
                && switchWeaponInvoker(inventory, useFists, useAOE, stopJob, predicate);
        }

        private static bool IsSecondaryAmmo(ThingComp ammo)
        {
            ThingWithComps weapon = ammo.parent;
            return weapon?.ParentHolder is Pawn_EquipmentTracker equipment
                && RimKataCombatExtendedCompat.IsHeldSecondary(equipment.pawn, weapon);
        }
    }
}
