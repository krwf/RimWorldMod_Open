using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using Verse.AI;

namespace KRWF.RimKata
{
    public sealed partial class RimKataSubdueState
    {
        internal int attackTicks;
        internal bool warming;
        internal int weaponRevision = -1;
        internal LocalTargetInfo externalTarget = LocalTargetInfo.Invalid;
        internal ThingWithComps externalWeapon;
        internal bool HasExternalTarget => externalTarget.HasThing;
        internal bool attackAllowed = true;
        internal int externalOrderJobId = -1;
        internal RimKataNativeAttack nativeAttack;
        internal Verb weaponVerb;
        internal bool weaponDirty;
        internal int lastAttackTick = -1;

        internal void ExposeCombat()
        {
            Scribe_Values.Look(ref attackTicks, "attackTicks");
            Scribe_Values.Look(ref warming, "warming");
            Scribe_TargetInfo.Look(ref externalTarget, "externalTarget");
            Scribe_References.Look(ref externalWeapon, "externalWeapon");
            Scribe_Values.Look(ref attackAllowed, "attackAllowed", true);
            Scribe_Values.Look(ref externalOrderJobId, "externalOrderJobId", -1);
        }
    }

    internal static class RimKataSubdueCombat
    {
        internal struct ExecutionScope
        {
            internal RimKataSubdueState previous;
            internal bool hit, heldShot;
        }

        private static readonly AccessTools.FieldRef<Verb, LocalTargetInfo> CurrentTarget =
            AccessTools.FieldRefAccess<Verb, LocalTargetInfo>("currentTarget");
        private static readonly AccessTools.FieldRef<Verb, bool> NonInterrupting =
            AccessTools.FieldRefAccess<Verb, bool>("nonInterruptingSelfCast");
        private static readonly AccessTools.FieldRef<Verb, int?> BurstCount =
            AccessTools.FieldRefAccess<Verb, int?>("cachedBurstShotCount");
        private static readonly AccessTools.FieldRef<Projectile, LocalTargetInfo> IntendedTarget =
            AccessTools.FieldRefAccess<Projectile, LocalTargetInfo>("intendedTarget");
        private static readonly AccessTools.StructFieldRef<ShotReport, float> TargetSize =
            AccessTools.StructFieldRefAccess<ShotReport, float>("factorFromTargetSize");
        private static readonly Func<Verb_MeleeAttackDamage, LocalTargetInfo, IEnumerable<DamageInfo>> MeleeDamage =
            AccessTools.MethodDelegate<Func<Verb_MeleeAttackDamage, LocalTargetInfo, IEnumerable<DamageInfo>>>(
                AccessTools.Method(typeof(Verb_MeleeAttackDamage), "DamageInfosToApply"));
        private static readonly Func<Verb_MeleeAttack, SoundDef> MeleeHitSound =
            AccessTools.MethodDelegate<Func<Verb_MeleeAttack, SoundDef>>(
                AccessTools.Method(typeof(Verb_MeleeAttack), "SoundHitPawn"));
        private static readonly Func<Verb_MeleeAttack, SoundDef> MeleeMissSound =
            AccessTools.MethodDelegate<Func<Verb_MeleeAttack, SoundDef>>(
                AccessTools.Method(typeof(Verb_MeleeAttack), "SoundMiss"));
        private static readonly Func<Verb_MeleeAttack, Func<ManeuverDef, RulePackDef>, bool, BattleLogEntry_MeleeCombat> MeleeLog =
            AccessTools.MethodDelegate<Func<Verb_MeleeAttack, Func<ManeuverDef, RulePackDef>, bool, BattleLogEntry_MeleeCombat>>(
                AccessTools.Method(typeof(Verb_MeleeAttack), "CreateCombatLog"));
        private static readonly Func<ManeuverDef, RulePackDef> HitRules = maneuver => maneuver.combatLogRulesHit;
        private static readonly Func<ManeuverDef, RulePackDef> MissRules = maneuver => maneuver.combatLogRulesMiss;
        private static readonly Func<ManeuverDef, RulePackDef> DodgeRules = maneuver => maneuver.combatLogRulesDodge;

        [ThreadStatic] private static RimKataSubdueState executing;
        [ThreadStatic] private static bool hit;
        [ThreadStatic] private static bool heldShot;

        internal static bool OwnsAttack(Pawn pawn, Verb verb = null)
            => executing != null && executing.pawn == pawn
                && (verb == null || executing.attackVerb == verb);

        internal static bool AllowsAttack(Verb verb, Pawn pawn)
            => AllowsAttack(verb, pawn, RimKataSubdueUtility.Get(pawn));

        internal static bool AllowsAttack(Verb verb, Pawn pawn, RimKataSubdueState state)
        {
            bool pending = state?.nativeAttack?.Pending == true;
            return state == null || RimKataSubdueAutomaticFire.IsAcquiring(verb, pawn)
                || (executing == state && !pending || pending
                    && !state.nativeAttack.Cancelled && state.nativeAttack.verb == verb
                    && state.nativeAttack.target == state.externalTarget
                    && state.nativeAttack.weapon == state.externalWeapon)
                && state.attackVerb == verb
                && !state.weaponDirty
                && state.attackAllowed && (state.HasExternalTarget || state.attackEnabled)
                && RimKataSubdueUtility.IsRelationValid(state);
        }

        internal static ExecutionScope BeginNativeExecution(RimKataSubdueState state)
        {
            var scope = new ExecutionScope { previous = executing, hit = hit, heldShot = heldShot };
            executing = state;
            hit = heldShot = false;
            return scope;
        }

        internal static void EndNativeExecution(ExecutionScope scope)
        {
            executing = scope.previous;
            hit = scope.hit;
            heldShot = scope.heldShot;
        }

        internal static void Begin(RimKataSubdueState state, bool restoring = false)
        {
            int ticks = state.attackTicks;
            bool warming = state.warming;
            if (state.HasExternalTarget) state.attackEnabled = false;
            RimKataDualWeaponController.Reset(state.pawn, true);
            state.pawn.Map?.GetComponent<RimKataMapComponent>()?.GetState(state.pawn, false)?.CancelVisual();
            state.pawn.stances?.CancelBusyStanceHard();
            RefreshWeapon(state);
            if (restoring) { state.attackTicks = ticks; state.warming = warming; }
        }

        internal static void RefreshWeapon(RimKataSubdueState state)
        {
            state.weaponDirty = true;
            state.nativeAttack?.Cancel();
            if (executing == state || state.nativeAttack?.Executing == true) return;
            int cooldown = state.warming ? 0 : state.attackTicks;
            state.attackVerb?.Reset();
            ThingWithComps primary = state.pawn.equipment?.Primary;
            Verb externalVerb = null;
            if (state.HasExternalTarget && !TryGetExternalWeaponVerb(state, state.externalWeapon, out externalVerb))
            {
                state.externalTarget = LocalTargetInfo.Invalid;
                state.externalWeapon = null;
                state.externalOrderJobId = -1;
            }
            state.weapon = state.HasExternalTarget ? state.externalWeapon
                : RimKataWeaponSlotUtility.CanUseOneHandWeapon(state.pawn, primary, true)
                ? primary : null;
            state.weaponVerb = state.weapon != null
                ? externalVerb ?? RimKataWeaponSlotUtility.CombatVerb(state.pawn, state.weapon)
                : NaturalMelee(state.pawn);
            state.attackVerb = state.weaponVerb;
            state.weaponRevision = RimKataEquipmentUtility.WeaponConfigurationRevision;
            state.weaponDirty = false;
            state.warming = false;
            state.attackTicks = cooldown;
            if (state.attackVerb != null) RimKataPreparedWeaponData.Bind(state.attackVerb);
        }

        private static Verb NaturalMelee(Pawn pawn)
        {
            Verb best = null;
            float score = -1f;
            foreach (VerbEntry entry in pawn.meleeVerbs.GetUpdatedAvailableVerbsList(false))
            {
                Verb verb = entry.verb;
                if (verb.EquipmentSource != null || !(verb is Verb_MeleeAttackDamage)
                    || !verb.IsStillUsableBy(pawn)) continue;
                float damage = verb.verbProps.AdjustedMeleeDamageAmount(verb, pawn);
                if (damage > score) { best = verb; score = damage; }
            }
            return best;
        }

        internal static void End(RimKataSubdueState state)
        {
            state.attackEnabled = false;
            state.externalTarget = LocalTargetInfo.Invalid;
            state.externalWeapon = null;
            state.externalOrderJobId = -1;
            state.nativeAttack?.Cancel();
            if (executing != state && state.nativeAttack?.Executing != true) state.attackVerb?.Reset();
        }

        private static bool CanUseExternalWeapon(RimKataSubdueState state, ThingWithComps weapon)
            => TryGetExternalWeaponVerb(state, weapon, out _);

        private static bool TryGetExternalWeaponVerb(RimKataSubdueState state, ThingWithComps weapon,
            out Verb verb)
        {
            verb = null;
            if (weapon == null || weapon.Destroyed
                || state.pawn.equipment?.AllEquipmentListForReading.Contains(weapon) != true
                || !RimKataWeaponSlotUtility.CanUseOneHandWeapon(state.pawn, weapon, true)) return false;
            verb = RimKataWeaponSlotUtility.CombatVerb(state.pawn, weapon);
            return verb is Verb_LaunchProjectile || verb is Verb_MeleeAttackDamage;
        }

        private static bool CanAttackExternal(RimKataSubdueState state, Verb verb, Thing target,
            bool? knownAdjacent, out bool adjacent)
        {
            if (verb == null) { adjacent = false; return false; }
            adjacent = knownAdjacent ?? state.pawn.CanReachImmediate(target, PathEndMode.Touch);
            if (verb.IsMeleeAttack) return adjacent && verb.Available();
            bool available = adjacent ? RimKataDualWeaponController.VerbUsable(state.pawn, verb, true)
                : verb.Available();
            return available && (adjacent || !verb.ApparelPreventsShooting() && verb.CanHitTarget(target));
        }

        internal static void NotifyIncomingMeleeAttempt(Verb verb)
        {
            Pawn defender = verb.CurrentTarget.Pawn;
            var state = RimKataSubdueUtility.Get(defender);
            if (state == null) return;
            Pawn attacker = verb.CasterPawn;
            if (attacker == null || !RimKataTargeting.IsAutomaticEnemy(defender, attacker)) return;
            if (!RimKataSubdueDefense.IsDamageTransferEnabled(state)
                && attacker.stances?.FullBodyBusy != true && verb.CanHitTarget(defender)
                && RimKataSubdueDefense.ReleaseKnownForDefense(state, attacker)) return;
            if (state.attackAllowed && !state.attackEnabled && !state.HasExternalTarget)
                TryQueueCloseTarget(state, attacker);
        }

        internal static bool TryHandleMeleeAttempt(Pawn pawn, Thing target)
        {
            var state = RimKataSubdueUtility.Get(pawn);
            if (state == null || OwnsAttack(pawn)) return false;
            if (state.attackAllowed && target != null
                && RimKataSubdueDefense.ReleaseKnownForDefense(state, target)) return false;
            if (state.attackAllowed && !state.attackEnabled && !state.HasExternalTarget)
                TryQueueCloseTarget(state, target);
            return true;
        }

        private static void TryQueueCloseTarget(RimKataSubdueState state, Thing target)
        {
            if (target == null || !IsLiveExternalTarget(state, target)
                || !state.pawn.CanReachImmediate(target, PathEndMode.Touch)) return;
            if (TrySetKnownExternalTarget(state, state.pawn.equipment?.Primary, target, true, true))
                state.externalOrderJobId = -1;
        }

        // Starting AttackStatic would run native job cleanup and drop the held pawn.
        internal static bool TryHandleWeaponOrder(Verb verb, LocalTargetInfo target)
        {
            if (verb == null) return false;
            var state = RimKataSubdueUtility.Get(verb.CasterPawn);
            if (state == null) return false;
            ThingWithComps weapon = verb.EquipmentSource;
            if (verb.IsMeleeAttack && state.attackAllowed
                && CanUseExternalWeapon(state, weapon) && IsLiveExternalTarget(state, target)
                && target.Thing != state.target && target.Thing != state.pawn
                && !state.pawn.CanReachImmediate(target, PathEndMode.Touch))
            {
                Job job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
                job.verbToUse = verb;
                job.playerForced = true;
                state.pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                return true;
            }
            if (!TrySetExternalTarget(state, weapon, target, true))
            {
                Messages.Message("CannotFire".Translate(), MessageTypeDefOf.RejectInput, false);
                return true;
            }
            state.externalOrderJobId = -1;
            RimKataSubdueJobs.StopAttackJob(state.pawn);
            return true;
        }

        internal static bool TrySetExternalTarget(RimKataSubdueState state, ThingWithComps weapon,
            LocalTargetInfo target, bool requireReachNow)
            => TrySetKnownExternalTarget(state, weapon, target, requireReachNow, null);

        private static bool TrySetKnownExternalTarget(RimKataSubdueState state, ThingWithComps weapon,
            LocalTargetInfo target, bool requireReachNow, bool? knownAdjacent)
        {
            if (!state.attackAllowed || !RimKataSubdueUtility.IsRelationValid(state)
                || !TryGetExternalWeaponVerb(state, weapon, out Verb verb) || target.Thing == state.target
                || target.Thing == state.pawn || !IsLiveExternalTarget(state, target)
                || requireReachNow && !CanAttackExternal(state, verb, target.Thing, knownAdjacent, out _)) return false;
            state.attackEnabled = false;
            if (state.externalTarget == target && state.externalWeapon == weapon) return true;
            state.externalTarget = target;
            state.externalWeapon = weapon;
            RefreshWeapon(state);
            state.SuspendAutomaticFire();
            RimKataSubdueRender.Publish(state);
            return true;
        }

        private static bool IsLiveExternalTarget(RimKataSubdueState state, LocalTargetInfo target)
            => target.HasThing && target.Thing.Spawned && !target.Thing.Destroyed
                && target.Thing.Map == state.pawn.Map
                && (target.Pawn == null || !target.Pawn.Dead && !target.Pawn.Downed);

        internal static void StopExternalAttack(RimKataSubdueState state)
        {
            if (!state.HasExternalTarget || RimKataSubdueUtility.Get(state.pawn) != state) return;
            state.externalTarget = LocalTargetInfo.Invalid;
            state.externalWeapon = null;
            state.externalOrderJobId = -1;
            RefreshWeapon(state);
            state.SuspendAutomaticFire();
            RimKataSubdueRender.Publish(state);
        }

        internal static void SetAttackEnabled(RimKataSubdueState state, bool enabled)
        {
            if (RimKataSubdueUtility.Get(state.pawn) != state || !state.attackAllowed) return;
            if (enabled && state.HasExternalTarget)
            {
                StopExternalAttack(state);
                RimKataSubdueJobs.StopAttackJob(state.pawn);
            }
            state.attackEnabled = enabled;
            if (!enabled && state.warming) { state.warming = false; state.attackTicks = 0; }
            state.SuspendAutomaticFire();
            RimKataSubdueRender.Publish(state);
        }

        internal static void SetAttackAllowed(RimKataSubdueState state, bool allowed)
        {
            if (RimKataSubdueUtility.Get(state.pawn) != state) return;
            state.attackAllowed = allowed;
            if (!allowed)
            {
                state.attackEnabled = false;
                StopExternalAttack(state);
                if (state.warming) { state.warming = false; state.attackTicks = 0; }
                state.nativeAttack?.Cancel();
                if (executing != state && state.nativeAttack?.Executing != true) state.attackVerb?.Reset();
                state.visualAttackTick = -1;
                RimKataSubdueJobs.StopAttackJob(state.pawn);
            }
            state.SuspendAutomaticFire();
            RimKataSubdueRender.Publish(state);
        }

        internal static void Tick(RimKataSubdueState state)
        {
            if (state.weaponDirty || state.nativeAttack?.Pending == true
                && state.weaponRevision != RimKataEquipmentUtility.WeaponConfigurationRevision)
                RefreshWeapon(state);
            if (state.HasExternalTarget && !IsLiveExternalTarget(state, state.externalTarget))
                StopExternalAttack(state);
            if (state.nativeAttack?.Pending == true || state.lastAttackTick == Find.TickManager.TicksGame) return;
            if (state.attackTicks > 0) { --state.attackTicks; if (state.attackTicks > 0) return; }
            if (!state.attackAllowed || RimKataSubdueJobs.IsApproaching(state)) return;
            if (!state.HasExternalTarget && (!state.attackEnabled || state.target.Dead)) return;
            if (state.weaponRevision != RimKataEquipmentUtility.WeaponConfigurationRevision)
                RefreshWeapon(state);
            Verb verb = state.attackVerb;
            if (verb == null) return;
            bool adjacent = false;
            if (state.HasExternalTarget
                && !CanAttackExternal(state, state.weaponVerb, state.externalTarget.Thing, null, out adjacent))
            {
                if (RimKataSubdueJobs.IsMeleeOrder(state)) return;
                StopExternalAttack(state);
                return;
            }
            if (!state.HasExternalTarget && !state.attackEnabled) return;
            Thing closeTarget = state.externalTarget.Thing;
            if (state.HasExternalTarget
                && RimKataSubdueDefense.ReleaseKnownForDefense(state, closeTarget, adjacent))
            {
                RimKataDraftedFireController.TryQueuePhysicalMeleeAttack(state.pawn, closeTarget);
                return;
            }
            if (!state.warming)
            {
                if (state.weapon != null && verb.IsMeleeAttack)
                {
                    Verb selected = RimKataDualWeaponController.ResolveWeaponMeleeVerb(
                        state.pawn, state.weapon,
                        state.HasExternalTarget ? state.externalTarget.Thing : state.target,
                        damageOnly: true);
                    if (selected == null) return;
                    state.attackVerb = verb = selected;
                }
                state.warming = true;
                state.attackTicks = verb.IsMeleeAttack ? 0 : RimKataCombatMath.WarmupTicksForSingleShot(verb);
                if (state.attackTicks > 0) return;
            }
            if (state.HasExternalTarget)
            {
                QueueExternalAttack(state, verb, adjacent);
                return;
            }
            state.warming = false;
            // Damage callbacks can kill or release the held pawn.
            state.attackTicks = Math.Max(1, RimKataCombatMath.CooldownTicksForSingleShot(verb, state.pawn, false));
            RimKataSubdueState previous = executing;
            executing = state;
            try
            {
                if (verb is Verb_MeleeAttackDamage melee) Strike(state, melee);
                else if (verb is Verb_LaunchProjectile) ShootHeld(state, verb);
            }
            finally { executing = previous; }
        }

        private static void QueueExternalAttack(RimKataSubdueState state, Verb verb, bool adjacent)
        {
            RimKataNativeAttack request = state.nativeAttack ?? (state.nativeAttack = new RimKataNativeAttack());
            if (request.Pending || verb.state != VerbState.Idle || state.weaponDirty
                || RimKataSubdueUtility.Get(state.pawn) != state || !state.attackAllowed
                || !state.HasExternalTarget || state.attackEnabled || state.attackVerb != verb) return;
            request.pawn = state.pawn;
            request.subdueState = state;
            request.weapon = state.weapon;
            request.verb = verb;
            request.cycleVerb = verb;
            request.job = state.pawn.CurJob;
            request.firedTarget = state.externalTarget.Thing;
            request.target = state.externalTarget;
            request.closeCombatContext = adjacent;
            request.closeShot = !verb.IsMeleeAttack && adjacent;
            request.closeMeleeResolution = request.closeShot;
            request.closeMeleeHit = false;
            request.closeDefensePrecheck = RimKataCloseDefensePrecheck.None;
            try
            {
                if (!request.Queue()) return;
                if (RimKataSubdueUtility.Get(state.pawn) == state && state.pawn.CurJob == request.job
                    && !state.weaponDirty && state.weapon == request.weapon
                    && state.externalTarget == request.target) return;
                request.Cancel();
            }
            finally
            {
                if (!request.Pending) request.ClearCompletedReferences();
            }
        }

        internal static bool CanContinueAttack(RimKataNativeAttack request)
        {
            RimKataSubdueState state = request.subdueState;
            Pawn pawn = request.pawn;
            if (state == null || RimKataSubdueUtility.Get(pawn) != state
                || !RimKataSubdueUtility.IsRelationValid(state) || !state.attackAllowed
                || state.attackEnabled || state.weaponDirty || state.nativeAttack != request
                || state.weaponRevision != RimKataEquipmentUtility.WeaponConfigurationRevision
                || state.externalTarget != request.target || state.weapon != request.weapon
                || state.attackVerb != request.verb || pawn.CurJob != request.job
                || pawn.Dead || pawn.Downed || pawn.InMentalState || pawn.stances.stunner.Stunned
                || request.verb.CasterPawn != pawn || request.weapon?.Destroyed != false
                || request.weapon.holdingOwner != pawn.equipment?.GetDirectlyHeldThings()
                || RimKataSubdueJobs.IsApproaching(state) || !IsLiveExternalTarget(state, request.target))
                return false;
            Verb actionVerb = request.verb;
            Thing target = request.target.Thing;
            bool available = CanAttackExternal(state, actionVerb, target, null, out bool adjacent);
            if (!request.Pending || request.Cancelled || state.weaponDirty
                || RimKataSubdueUtility.Get(pawn) != state || state.externalTarget != request.target)
                return false;
            if (!available)
            {
                if (adjacent && actionVerb.IsMeleeAttack) state.warming = false;
                return false;
            }
            if (RimKataSubdueDefense.ReleaseKnownForDefense(state, target, adjacent))
            {
                RimKataDraftedFireController.TryQueuePhysicalMeleeAttack(pawn, target);
                return false;
            }
            if (RimKataSubdueUtility.Get(pawn) != state || !request.Pending || request.Cancelled) return false;
            request.closeCombatContext = adjacent;
            request.closeShot = !request.verb.IsMeleeAttack && adjacent;
            request.closeMeleeResolution = request.closeShot;
            return true;
        }

        internal static bool AttackStarting(RimKataNativeAttack request)
        {
            RimKataSubdueState state = request.subdueState;
            if (!state.warming) return true;
            int cooldown = Math.Max(1, RimKataCombatMath.CooldownTicksForSingleShot(request.verb, state.pawn, false));
            if (!request.Pending || request.Cancelled || RimKataSubdueUtility.Get(state.pawn) != state
                || state.weaponDirty || !state.attackAllowed || state.externalTarget != request.target)
                return false;
            state.warming = false;
            state.attackTicks = cooldown;
            state.lastAttackTick = Find.TickManager.TicksGame;
            return true;
        }

        internal static void AttackCompleted(RimKataNativeAttack request)
        {
            RimKataSubdueState state = request.subdueState;
            if (request.HasFired && !request.Cancelled && RimKataSubdueUtility.Get(state.pawn) == state)
            {
                RimKataSubdueRender.NotifyAttack(state, !request.verb.IsMeleeAttack, request.target);
                RimKataSubdueRender.Publish(state);
            }
        }

        private static float DarknessOffset(Pawn source, Pawn carrier, StatDef outdoorLight,
            StatDef outdoorDark, StatDef indoorDark, StatDef indoorLight)
        {
            if (!ModsConfig.IdeologyActive) return 0f;
            StatDef stat = DarknessCombatUtility.IsOutdoorsAndLit(carrier) ? outdoorLight
                : DarknessCombatUtility.IsOutdoorsAndDark(carrier) ? outdoorDark
                : DarknessCombatUtility.IsIndoorsAndDark(carrier) ? indoorDark : indoorLight;
            return source.GetStatValue(stat);
        }

        private static void Strike(RimKataSubdueState state, Verb_MeleeAttackDamage verb)
        {
            Pawn pawn = state.pawn, target = state.target;
            RimKataSubdueRender.NotifyAttack(state, false, target);
            bool immobile = target.Downed || target.GetPosture() != PawnPosture.Standing;
            float chance = immobile ? 1f : pawn.GetStatValue(StatDefOf.MeleeHitChance);
            chance += immobile ? 0f : DarknessOffset(pawn, pawn, StatDefOf.MeleeHitChanceOutdoorsLitOffset,
                StatDefOf.MeleeHitChanceOutdoorsDarkOffset, StatDefOf.MeleeHitChanceIndoorsDarkOffset,
                StatDefOf.MeleeHitChanceIndoorsLitOffset);
            float dodge = immobile ? 0f : target.GetStatValue(StatDefOf.MeleeDodgeChance);
            dodge += immobile ? 0f : DarknessOffset(target, pawn, StatDefOf.MeleeDodgeChanceOutdoorsLitOffset,
                StatDefOf.MeleeDodgeChanceOutdoorsDarkOffset, StatDefOf.MeleeDodgeChanceIndoorsDarkOffset,
                StatDefOf.MeleeDodgeChanceIndoorsLitOffset);
            bool aimed = Rand.Chance(Mathf.Clamp01(chance));
            bool dodged = aimed && Rand.Chance(Mathf.Clamp01(dodge));
            bool landed = aimed && !dodged;
            LocalTargetInfo oldTarget = CurrentTarget(verb);
            CurrentTarget(verb) = target;
            BattleLogEntry_MeleeCombat log;
            try { log = MeleeLog(verb, landed ? HitRules : dodged ? DodgeRules : MissRules, landed); }
            finally { CurrentTarget(verb) = oldTarget; }
            if (landed) foreach (DamageInfo generated in MeleeDamage(verb, target))
            {
                if (!RimKataSubdueUtility.IsRelationValid(state) || target.Dead) break;
                DamageInfo damage = generated;
                damage.SetAngle(state.facing.FacingCell.ToVector3());
                // Armor deflection uses Position with MapHeld; an unspawned pawn retains its pickup cell.
                target.Position = pawn.Position;
                target.TakeDamage(damage).AssociateWithLog(log);
            }
            (landed ? MeleeHitSound(verb) : MeleeMissSound(verb))
                ?.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
            pawn.skills?.Learn(SkillDefOf.Melee, 20f);
        }

        private static void ShootHeld(RimKataSubdueState state, Verb verb)
        {
            Pawn pawn = state.pawn;
            LocalTargetInfo destination = state.target;
            // Held pawns have no map cell, so the close-shot report uses the carrier's cell.
            ShotReport report = ShotReport.HitReportFor(pawn, verb, pawn.Position);
            TargetSize(ref report) = Mathf.Clamp(state.target.BodySize, 0.5f, 2f);
            bool landed = Rand.Chance(Mathf.Clamp01(report.TotalEstimatedHitChance));
            var oldState = executing;
            bool oldHit = hit;
            bool oldHeldShot = heldShot;
            executing = state;
            hit = landed;
            heldShot = true;
            var previous = RimKataFireContext.Begin(verb, pawn, destination.Thing,
                false, true, false, null, true, landed, RimKataCloseDefensePrecheck.None);
            RimKataFireContext.SuppressCloseLaunch = true;
            Rot4 facing = pawn.Rotation;
            Stance stance = pawn.stances.curStance;
            int? count = BurstCount(verb);
            bool nonInterrupting = NonInterrupting(verb);
            try
            {
                verb.Reset();
                CurrentTarget(verb) = new LocalTargetInfo(pawn.Position);
                NonInterrupting(verb) = true;
                BurstCount(verb) = 1;
                verb.WarmupComplete();
                if (RimKataFireContext.ShotFired && RimKataSubdueUtility.Get(pawn) == state)
                    RimKataSubdueRender.NotifyAttack(state, true, destination);
            }
            finally
            {
                verb.Reset();
                BurstCount(verb) = count;
                NonInterrupting(verb) = nonInterrupting;
                if (pawn.stances.curStance is Stance_Busy busy && busy.verb == verb)
                    pawn.stances.curStance = stance;
                pawn.Rotation = facing;
                RimKataFireContext.End(verb, previous);
                executing = oldState;
                hit = oldHit;
                heldShot = oldHeldShot;
            }
        }

        internal static void ResolveProjectile(Projectile projectile, Thing launcher, Thing equipment)
        {
            var state = executing;
            if (state == null || !heldShot || launcher != state.pawn || equipment != state.weapon
                || !RimKataSubdueUtility.IsRelationValid(state) || projectile.Destroyed) return;
            IntendedTarget(projectile) = state.target;
            if (RimKataProjectileUtility.PrepareImmediateImpact(projectile, state.pawn.Position))
            {
                if (hit) state.target.Position = state.pawn.Position;
                RimKataProjectileUtility.Impact(projectile, hit ? state.target : null);
            }
        }
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch), new[]
    {
        typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo),
        typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef)
    })]
    internal static class Patch_ProjectileLaunch_RimKataSubdue
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Projectile __instance, Thing launcher, Thing equipment)
            => RimKataSubdueCombat.ResolveProjectile(__instance, launcher, equipment);
    }
}
