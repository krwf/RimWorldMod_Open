using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataDirectionalFire
    {
        internal struct ExecutionScope
        {
            internal Verb verb;
            internal IntVec3 endpoint;
        }

        [ThreadStatic] internal static Verb activeVerb;
        [ThreadStatic] internal static IntVec3 activeEndpoint;

        internal static bool HasRegisteredTarget(RimKataWeaponCycleState cycle)
            => cycle?.directionalFireCell.IsValid == true;

        internal static bool HasRegisteredTarget(RimKataPawnCombatState state)
            => HasRegisteredTarget(state?.primaryWeaponCycle)
                || HasRegisteredTarget(state?.secondaryWeaponCycle);

        internal static bool Register(Pawn pawn, RimKataPawnCombatState state,
            IntVec3 cell, ThingWithComps weapon = null)
        {
            if (pawn?.Map == null || state == null || !cell.InBounds(pawn.Map)
                || cell == pawn.Position
                || RimKataTargetAccess.SettingsFor(pawn)?.directionalFireEnabled == false) return false;
            bool registered = RegisterSlot(pawn, state.primaryWeaponCycle, cell, weapon);
            return RegisterSlot(pawn, state.secondaryWeaponCycle, cell, weapon) || registered;
        }

        private static bool RegisterSlot(Pawn pawn, RimKataWeaponCycleState cycle,
            IntVec3 cell, ThingWithComps requestedWeapon)
        {
            if (cycle?.weapon == null || requestedWeapon != null && cycle.weapon != requestedWeapon
                || !cycle.ordinaryWeaponEnabled || cycle.boundVerb?.IsMeleeAttack != false
                || cycle.weapon.def.IsRangedWeapon != true) return false;
            float range = RimKataRangeUtility.ResolveEffectiveRange(pawn, cycle.weapon, cycle.boundVerb);
            if (range <= 0f || pawn.Position.DistanceToSquared(cell) > range * range) return false;
            if (cycle.directionalFireCell != cell)
            {
                cycle.nativeAttack?.Cancel();
                cycle.ClearPlan();
                cycle.cachedCandidateTarget = null;
                cycle.cachedCandidateInterception = false;
                cycle.cachedDirectionalFireCell = IntVec3.Invalid;
                cycle.focusedTarget = null;
                cycle.focusedTargetFromAttackGizmo = false;
            }
            cycle.directionalFireCell = cell;
            return true;
        }

        internal static void Clear(RimKataWeaponCycleState cycle)
        {
            if (cycle == null) return;
            cycle.directionalFireCell = IntVec3.Invalid;
            cycle.cachedDirectionalFireCell = IntVec3.Invalid;
        }

        internal static void Clear(Pawn pawn)
        {
            if (!RimKataCombatStatePresenceCache.TryGetOwner(pawn, out RimKataMapComponent owner)) return;
            Clear(owner.GetState(pawn, false));
        }

        internal static void Clear(RimKataPawnCombatState state)
        {
            ClearOrder(state?.primaryWeaponCycle);
            ClearOrder(state?.secondaryWeaponCycle);
        }

        private static void ClearOrder(RimKataWeaponCycleState cycle)
        {
            if (cycle == null) return;
            Clear(cycle);
            if (cycle.plannedDirectionalFireCell.IsValid)
            {
                cycle.nativeAttack?.Cancel();
                cycle.ClearPlan();
            }
            cycle.visualDirectionalFireCell = IntVec3.Invalid;
            cycle.cooldownTurnCell = IntVec3.Invalid;
        }

        internal static bool TryGetShotTarget(Pawn pawn, RimKataWeaponCycleState cycle,
            out LocalTargetInfo target)
            => TryGetShotTarget(pawn, cycle, cycle?.boundVerb, out target);

        internal static bool TryGetShotTarget(Pawn pawn, RimKataWeaponCycleState cycle,
            Verb verb, out LocalTargetInfo target)
        {
            target = LocalTargetInfo.Invalid;
            if (!HasRegisteredTarget(cycle) || pawn?.Map == null || cycle.weapon == null
                || verb?.IsMeleeAttack != false) return false;
            float range = RimKataRangeUtility.ResolveEffectiveRange(pawn, cycle.weapon, verb);
            IntVec3 anchor = cycle.directionalFireCell;
            if (!anchor.InBounds(pawn.Map) || anchor == pawn.Position || range <= 0f
                || pawn.Position.DistanceToSquared(anchor) > range * range)
            {
                Clear(cycle);
                return false;
            }
            if (!TryGetEndpoint(pawn.Position, anchor, range, pawn.Map, out IntVec3 end)) return false;
            LocalTargetInfo shot = new LocalTargetInfo(end);
            ExecutionScope previous = Begin(verb, end);
            try
            {
                if (!verb.CanHitTarget(shot)) return false;
            }
            finally { EndNativeExecution(previous); }
            target = shot;
            return true;
        }

        internal static ExecutionScope BeginNativeExecution(RimKataNativeAttack request)
            => request?.cycle?.HasDirectionalFire == true && !request.target.HasThing
                && request.target.Cell == request.cycle.plannedDirectionalFireCell
                    ? Begin(request.verb, request.target.Cell)
                    : Begin(null, IntVec3.Invalid);

        private static ExecutionScope Begin(Verb verb, IntVec3 endpoint)
        {
            var previous = new ExecutionScope
            {
                verb = activeVerb,
                endpoint = activeEndpoint
            };
            activeVerb = verb;
            activeEndpoint = endpoint;
            return previous;
        }

        internal static void EndNativeExecution(ExecutionScope previous)
        {
            activeVerb = previous.verb;
            activeEndpoint = previous.endpoint;
        }

        internal static bool IsOpenGround(Verb verb, IntVec3 cell)
        {
            Map map = verb.Caster.Map;
            return cell.Standable(map) && !map.thingGrid.CellContains(cell, ThingCategory.Pawn);
        }

        internal static bool TryGetEndpoint(IntVec3 origin, IntVec3 anchor, float range,
            Map map, out IntVec3 endpoint)
        {
            endpoint = IntVec3.Invalid;
            if (map == null || range <= 0f || !origin.InBounds(map) || !anchor.InBounds(map)
                || origin == anchor) return false;
            Vector3 direction = (anchor - origin).ToVector3().normalized;
            Vector3 start = origin.ToVector3Shifted();
            float distance = range;
            if (direction.x > 0f) distance = Mathf.Min(distance, (map.Size.x - start.x - 0.001f) / direction.x);
            else if (direction.x < 0f) distance = Mathf.Min(distance, (0.001f - start.x) / direction.x);
            if (direction.z > 0f) distance = Mathf.Min(distance, (map.Size.z - start.z - 0.001f) / direction.z);
            else if (direction.z < 0f) distance = Mathf.Min(distance, (0.001f - start.z) / direction.z);
            float rangeSquared = range * range;
            for (int i = 0; i < 8 && distance > 0f; i++, distance -= 0.25f)
            {
                IntVec3 cell = (start + direction * distance).ToIntVec3();
                if (cell != origin && cell.InBounds(map) && origin.DistanceToSquared(cell) <= rangeSquared)
                {
                    endpoint = cell;
                    return true;
                }
            }
            return false;
        }

        internal static bool CanUseCommand(Verb verb)
        {
            Pawn pawn = verb?.CasterPawn;
            return verb?.IsMeleeAttack == false && verb.EquipmentSource?.def.IsRangedWeapon == true
                && pawn?.Spawned == true && pawn.IsPlayerControlled
                && RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                && RimKataTargetAccess.SettingsFor(pawn)?.directionalFireEnabled != false
                && RimKataDualWeaponController.CanUsePlayerWeaponCommand(pawn, verb)
                && !RimKataSubdueUtility.IsHolding(pawn);
        }

        internal static bool CanOrderCell(Pawn pawn, Verb verb, IntVec3 cell)
        {
            if (pawn?.Map == null || !cell.InBounds(pawn.Map) || cell == pawn.Position
                || verb?.IsMeleeAttack != false) return false;
            float range = RimKataRangeUtility.ResolveEffectiveRange(pawn, verb.EquipmentSource, verb);
            return range > 0f && pawn.Position.DistanceToSquared(cell) <= range * range;
        }

        internal static bool Order(Pawn pawn, Verb verb, IntVec3 cell)
        {
            if (!CanUseCommand(verb) || !CanOrderCell(pawn, verb, cell)) return false;
            if (pawn.jobs.curDriver is JobDriver_RimKataAttack driver && driver.IsDirectionalFire)
                return driver.TryUpdateDirectionalFire(verb, cell);
            Job order = JobMaker.MakeJob(RimKataDefOf.RimKata_Attack, cell);
            order.playerForced = true;
            order.verbToUse = verb;
            pawn.jobs.TryTakeOrderedJob(order, JobTag.Misc);
            return true;
        }

        internal static void OrderSelected(IntVec3 cell)
        {
            var selected = Find.Selector?.SelectedPawns;
            if (selected == null) return;
            for (int i = 0; i < selected.Count; i++)
            {
                Pawn pawn = selected[i];
                if (pawn?.Drafted != true || pawn.Spawned != true || !pawn.IsPlayerControlled
                    || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                    || RimKataTargetAccess.SettingsFor(pawn)?.directionalFireEnabled == false
                    || !RimKataEligibility.CanBeginGunKataAttack(pawn)
                    || RimKataSubdueUtility.IsHolding(pawn)) continue;
                ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
                Verb primaryVerb = RimKataWeaponSlotUtility.CombatVerb(pawn, primary);
                bool canPrimary = primary?.def.IsRangedWeapon == true
                    && CanOrderCell(pawn, primaryVerb, cell);
                ThingWithComps secondary = RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn, primary, true)
                    ? RimKataWeaponSlotUtility.SecondaryWeaponWithVerifiedAccess(pawn) : null;
                Verb secondaryVerb = secondary?.def.IsRangedWeapon == true
                    ? RimKataWeaponSlotUtility.CombatVerb(pawn, secondary) : null;
                if (!canPrimary && !CanOrderCell(pawn, secondaryVerb, cell)) continue;
                Job order = JobMaker.MakeJob(RimKataDefOf.RimKata_Attack, cell);
                order.playerForced = true;
                pawn.jobs.TryTakeOrderedJob(order, JobTag.Misc);
            }
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.TryFindShootLineFromTo))]
    internal static class Patch_Verb_RimKataDirectionalShootLine
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            FieldInfo requiresSight = AccessTools.Field(typeof(VerbProperties), nameof(VerbProperties.requireLineOfSight));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
                if (!instruction.LoadsField(requiresSight)) continue;
                foreach (var code in IgnoreDirectionalSight(generator)) yield return code;
                replaced++;
            }
            if (replaced != 1)
                throw new InvalidOperationException("RimKata directional sight branch changed: " + replaced);
        }

        internal static IEnumerable<CodeInstruction> IgnoreDirectionalSight(ILGenerator generator)
        {
            Label unchanged = generator.DefineLabel();
            Label ignoreSight = generator.DefineLabel();
            yield return new CodeInstruction(OpCodes.Ldsfld,
                AccessTools.Field(typeof(RimKataDirectionalFire), nameof(RimKataDirectionalFire.activeVerb)));
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Bne_Un, unchanged);
            yield return new CodeInstruction(OpCodes.Ldarga_S, (byte)2);
            yield return new CodeInstruction(OpCodes.Call,
                AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.HasThing)));
            yield return new CodeInstruction(OpCodes.Brtrue, unchanged);
            yield return new CodeInstruction(OpCodes.Ldarga_S, (byte)2);
            yield return new CodeInstruction(OpCodes.Call,
                AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.Cell)));
            yield return new CodeInstruction(OpCodes.Ldsfld,
                AccessTools.Field(typeof(RimKataDirectionalFire), nameof(RimKataDirectionalFire.activeEndpoint)));
            yield return new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(IntVec3), "op_Inequality", new[] { typeof(IntVec3), typeof(IntVec3) }));
            yield return new CodeInstruction(OpCodes.Brtrue, unchanged);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Verb), nameof(Verb.verbProps)));
            yield return new CodeInstruction(OpCodes.Ldfld,
                AccessTools.Field(typeof(VerbProperties), nameof(VerbProperties.mustCastOnOpenGround)));
            yield return new CodeInstruction(OpCodes.Brfalse, ignoreSight);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldarga_S, (byte)2);
            yield return new CodeInstruction(OpCodes.Call,
                AccessTools.PropertyGetter(typeof(LocalTargetInfo), nameof(LocalTargetInfo.Cell)));
            yield return new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(RimKataDirectionalFire), nameof(RimKataDirectionalFire.IsOpenGround)));
            yield return new CodeInstruction(OpCodes.Brfalse, unchanged);
            yield return new CodeInstruction(OpCodes.Pop).WithLabels(ignoreSight);
            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
            yield return new CodeInstruction(OpCodes.Nop).WithLabels(unchanged);
        }
    }

    internal sealed class RimKataDirectionalTargetingSource : ITargetingSource
    {
        private static readonly MethodInfo CloneParameters = AccessTools.Method(typeof(object), "MemberwiseClone");
        private readonly Verb verb;
        private readonly TargetingParameters parameters;

        internal RimKataDirectionalTargetingSource(Verb verb)
        {
            this.verb = verb;
            parameters = (TargetingParameters)CloneParameters.Invoke(verb.targetParams, null);
            parameters.canTargetLocations = true;
        }

        public bool CasterIsPawn => verb.CasterIsPawn;
        public bool IsMeleeAttack => verb.IsMeleeAttack;
        public bool Targetable => verb.Targetable;
        public bool MultiSelect => verb.MultiSelect;
        public bool HidePawnTooltips => verb.HidePawnTooltips;
        public Thing Caster => verb.Caster;
        public Pawn CasterPawn => verb.CasterPawn;
        public Verb GetVerb => verb;
        public Texture2D UIIcon => verb.UIIcon;
        public TargetingParameters targetParams => parameters;
        public ITargetingSource DestinationSelector => verb.DestinationSelector;

        public bool CanHitTarget(LocalTargetInfo target)
            => target.HasThing ? verb.CanHitTarget(target)
                : target.IsValid && RimKataDirectionalFire.CanOrderCell(verb.CasterPawn, verb, target.Cell);

        public bool ValidateTarget(LocalTargetInfo target, bool showMessages = true)
            => target.HasThing ? verb.ValidateTarget(target, showMessages)
                : target.IsValid && CasterPawn?.Map != null && target.Cell.InBounds(CasterPawn.Map);

        public void DrawHighlight(LocalTargetInfo target)
        {
            verb.DrawHighlight(target);
            if (target.HasThing || !target.IsValid || CasterPawn?.Map == null) return;
            float range = RimKataRangeUtility.ResolveEffectiveRange(CasterPawn, verb.EquipmentSource, verb);
            if (RimKataDirectionalFire.TryGetEndpoint(CasterPawn.Position, target.Cell, range,
                    CasterPawn.Map, out IntVec3 end))
                GenDraw.DrawLineBetween(CasterPawn.DrawPos, end.ToVector3Shifted());
        }

        public void OrderForceTarget(LocalTargetInfo target) => verb.OrderForceTarget(target);
        public void OnGUI(LocalTargetInfo target) => verb.OnGUI(target);
    }

    [HarmonyPatch(typeof(Targeter), nameof(Targeter.BeginTargeting), new[]
    {
        typeof(ITargetingSource), typeof(ITargetingSource), typeof(bool),
        typeof(Func<LocalTargetInfo, ITargetingSource>), typeof(Action), typeof(bool)
    })]
    internal static class Patch_Targeter_RimKataDirectionalFire
    {
        private static void Prefix(ref ITargetingSource source)
        {
            if (source is Verb verb && RimKataDirectionalFire.CanUseCommand(verb))
                source = new RimKataDirectionalTargetingSource(verb);
        }
    }

    [HarmonyPatch(typeof(Command_VerbTarget), nameof(Command_VerbTarget.ProcessInput))]
    internal static class Patch_CommandVerbTarget_RimKataDirectionalFire
    {
        private static void Postfix(Command_VerbTarget __instance)
        {
            Targeter targeter = Find.Targeter;
            if (targeter?.targetingSource is Verb source
                && source.verbProps == __instance.verb?.verbProps
                && RimKataDirectionalFire.CanUseCommand(__instance.verb))
                targeter.targetingSource = new RimKataDirectionalTargetingSource(source);
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.OrderForceTarget))]
    internal static class Patch_Verb_RimKataDirectionalFireOrder
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Verb __instance, LocalTargetInfo target)
        {
            if (target.HasThing || !target.IsValid) return true;
            if (Find.Targeter?.targetingSource is RimKataDirectionalTargetingSource)
            {
                RimKataDirectionalFire.Order(__instance.CasterPawn, __instance, target.Cell);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PawnAttackGizmoUtility), "GetSquadAttackGizmo")]
    internal static class Patch_PawnAttackGizmoUtility_RimKataDirectionalFire
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ref Gizmo __result)
        {
            if (__result is not Command_Target command
                || !RimKataMultiSelectAttackGizmoUtility.GetSelectedAttackGizmoFacts().HasDirectionalFireUser)
                return;
            command.targetingParams.canTargetLocations = true;
            Action<LocalTargetInfo> original = command.action;
            command.action = target =>
            {
                if (!target.HasThing && target.IsValid) RimKataDirectionalFire.OrderSelected(target.Cell);
                else original?.Invoke(target);
            };
        }
    }
}
