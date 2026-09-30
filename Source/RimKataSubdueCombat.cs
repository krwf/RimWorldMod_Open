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
        {
            var state = RimKataSubdueUtility.Get(pawn);
            return state == null || RimKataSubdueAutomaticFire.IsAcquiring(verb, pawn)
                || executing == state && state.attackVerb == verb
                && state.attackAllowed && (state.HasExternalTarget || state.attackEnabled)
                && RimKataSubdueUtility.IsRelationValid(state);
        }

        internal static void Begin(RimKataSubdueState state, bool restoring = false)
        {
            int ticks = state.attackTicks;
            bool warming = state.warming;
            if (state.HasExternalTarget) state.attackEnabled = false;
            // End outstanding native requests before choosing the held-target verb.
            RimKataDualWeaponController.Reset(state.pawn, true);
            state.pawn.Map?.GetComponent<RimKataMapComponent>()?.GetState(state.pawn, false)?.CancelVisual();
            state.pawn.stances?.CancelBusyStanceHard();
            RefreshWeapon(state);
            if (restoring) { state.attackTicks = ticks; state.warming = warming; }
        }

        internal static void RefreshWeapon(RimKataSubdueState state)
        {
            if (executing == state) return;
            int cooldown = state.warming ? 0 : state.attackTicks;
            state.attackVerb?.Reset();
            ThingWithComps primary = state.pawn.equipment?.Primary;
            if (state.HasExternalTarget && !CanUseExternalWeapon(state, state.externalWeapon))
            {
                state.externalTarget = LocalTargetInfo.Invalid;
                state.externalWeapon = null;
                state.externalOrderJobId = -1;
            }
            state.weapon = state.HasExternalTarget ? state.externalWeapon
                : RimKataWeaponSlotUtility.CanUseOneHandWeapon(state.pawn, primary, true)
                ? primary : null;
            state.attackVerb = state.weapon != null
                ? RimKataWeaponSlotUtility.CombatVerb(state.pawn, state.weapon)
                : NaturalMelee(state.pawn);
            state.weaponRevision = RimKataEquipmentUtility.WeaponConfigurationRevision;
            state.warming = false;
            state.attackTicks = cooldown;
            if (state.attackVerb != null) RimKataPreparedWeaponData.Bind(state.attackVerb);
        }

        private static Verb NaturalMelee(Pawn pawn)
        {
            Verb best = null;
            float score = -1f;
            // Only owned body/hediff attacks, never a two-handed gun's bash verb.
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
            if (executing != state) state.attackVerb?.Reset();
        }

        private static bool CanUseExternalWeapon(RimKataSubdueState state, ThingWithComps weapon)
        {
            if (weapon == null || weapon.Destroyed
                || state.pawn.equipment?.AllEquipmentListForReading.Contains(weapon) != true
                || !RimKataWeaponSlotUtility.CanUseOneHandWeapon(state.pawn, weapon, true)) return false;
            Verb verb = RimKataWeaponSlotUtility.CombatVerb(state.pawn, weapon);
            return verb is Verb_LaunchProjectile || verb is Verb_MeleeAttackDamage;
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
                && RimKataSubdueDefense.ReleaseForDefense(defender, attacker)) return;
            if (state.attackAllowed && !state.attackEnabled && !state.HasExternalTarget)
                TryQueueCloseTarget(state, attacker);
        }

        internal static bool TryHandleMeleeAttempt(Pawn pawn, Thing target)
        {
            var state = RimKataSubdueUtility.Get(pawn);
            if (state == null || OwnsAttack(pawn)) return false;
            if (state.attackAllowed && target != null
                && RimKataSubdueDefense.ReleaseForDefense(pawn, target)) return false;
            if (state.attackAllowed && !state.attackEnabled && !state.HasExternalTarget)
                TryQueueCloseTarget(state, target);
            // The held pair owns all attacks. Never let an ordinary bash or
            // the main dual cycle run alongside its selected attack mode.
            return true;
        }

        private static void TryQueueCloseTarget(RimKataSubdueState state, Thing target)
        {
            if (target == null || !IsLiveExternalTarget(state, target)
                || !state.pawn.CanReachImmediate(target, PathEndMode.Touch)) return;
            if (TrySetExternalTarget(state, state.pawn.equipment?.Primary, target, true))
                state.externalOrderJobId = -1;
        }

        // Weapon orders stay inside the existing hold. Starting AttackStatic
        // here would let native job cleanup drop the carried pawn.
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
        {
            if (!state.attackAllowed || !RimKataSubdueUtility.IsRelationValid(state)
                || !CanUseExternalWeapon(state, weapon) || target.Thing == state.target
                || target.Thing == state.pawn || !IsLiveExternalTarget(state, target)
                || (requireReachNow && !RimKataWeaponSlotUtility.CanWeaponAttackTargetWithoutRushing(
                    state.pawn, weapon, target.Thing))) return false;
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
            // Toggling cannot bypass a cooldown already earned by an attack.
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
                if (executing != state) state.attackVerb?.Reset();
                state.visualAttackTick = -1;
                RimKataSubdueJobs.StopAttackJob(state.pawn);
            }
            state.SuspendAutomaticFire();
            RimKataSubdueRender.Publish(state);
        }

        internal static void Tick(RimKataSubdueState state)
        {
            if (state.HasExternalTarget && !IsLiveExternalTarget(state, state.externalTarget))
                StopExternalAttack(state);
            if (state.attackTicks > 0) { --state.attackTicks; if (state.attackTicks > 0) return; }
            if (!state.attackAllowed || RimKataSubdueJobs.IsApproaching(state)) return;
            if (!state.HasExternalTarget && (!state.attackEnabled || state.target.Dead)) return;
            if (state.weaponRevision != RimKataEquipmentUtility.WeaponConfigurationRevision)
                RefreshWeapon(state);
            Verb verb = state.attackVerb;
            if (verb == null) return;
            if (state.HasExternalTarget && (!IsLiveExternalTarget(state, state.externalTarget)
                || !RimKataWeaponSlotUtility.CanWeaponAttackTargetWithoutRushing(
                    state.pawn, state.weapon, state.externalTarget.Thing)))
            {
                if (RimKataSubdueJobs.IsMeleeOrder(state)) return;
                StopExternalAttack(state);
                return;
            }
            if (!state.HasExternalTarget && !state.attackEnabled) return;
            Thing closeTarget = state.externalTarget.Thing;
            if (state.HasExternalTarget
                && RimKataSubdueDefense.ReleaseForDefense(state.pawn, closeTarget))
            {
                // Rejoin the established close-combat entry after putting the
                // held pawn down; never fire this now-obsolete held cycle too.
                RimKataDraftedFireController.TryQueuePhysicalMeleeAttack(state.pawn, closeTarget);
                return;
            }
            if (!state.warming)
            {
                state.warming = true;
                state.attackTicks = verb.IsMeleeAttack ? 0 : RimKataCombatMath.WarmupTicksForSingleShot(verb);
                if (state.attackTicks > 0) return;
            }
            state.warming = false;
            // Set this before damage, which may kill/release the held pawn.
            state.attackTicks = Math.Max(1, RimKataCombatMath.CooldownTicksForSingleShot(verb, state.pawn, false));
            if (verb is Verb_MeleeAttackDamage melee)
            {
                if (state.HasExternalTarget) StrikeExternal(state, melee);
                else Strike(state, melee);
            }
            else if (verb is Verb_LaunchProjectile) Shoot(state, verb);
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
            // Native damage generation preserves weapon/tool, body-part, quality,
            // armor penetration and extra damage. Only the held location differs.
            if (landed) foreach (DamageInfo generated in MeleeDamage(verb, target))
            {
                if (!RimKataSubdueUtility.IsRelationValid(state) || target.Dead) break;
                DamageInfo damage = generated;
                damage.SetAngle(state.facing.FacingCell.ToVector3());
                // Armor deflection uses Position with MapHeld. The unspawned
                // target still has its pickup cell until this hit synchronizes it.
                target.Position = pawn.Position;
                target.TakeDamage(damage).AssociateWithLog(log);
            }
            (landed ? MeleeHitSound(verb) : MeleeMissSound(verb))
                ?.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
            pawn.skills?.Learn(SkillDefOf.Melee, 20f);
        }

        private static void StrikeExternal(RimKataSubdueState state, Verb_MeleeAttackDamage verb)
        {
            Pawn pawn = state.pawn;
            LocalTargetInfo target = state.externalTarget;
            var oldState = executing;
            executing = state;
            var previous = RimKataFireContext.Begin(verb, pawn, target.Thing,
                false, false, false, null, false, false, default);
            Rot4 facing = pawn.Rotation;
            Stance stance = pawn.stances.curStance;
            bool nonInterrupting = NonInterrupting(verb);
            try
            {
                verb.Reset();
                CurrentTarget(verb) = target;
                NonInterrupting(verb) = true;
                // Native melee hit/dodge/parry and equipment effects remain
                // intact. The hold owns its timer and weapon-only animation.
                verb.WarmupComplete();
                if (RimKataFireContext.ShotFired && RimKataSubdueUtility.Get(pawn) == state)
                    RimKataSubdueRender.NotifyAttack(state, false, target);
            }
            finally
            {
                verb.Reset();
                NonInterrupting(verb) = nonInterrupting;
                if (pawn.stances.curStance is Stance_Busy busy && busy.verb == verb)
                    pawn.stances.curStance = stance;
                pawn.Rotation = facing;
                RimKataFireContext.End(verb, previous);
                executing = oldState;
            }
        }

        private static void Shoot(RimKataSubdueState state, Verb verb)
        {
            Pawn pawn = state.pawn;
            bool outside = state.HasExternalTarget;
            LocalTargetInfo destination = outside ? state.externalTarget : new LocalTargetInfo(state.target);
            bool close = !outside || pawn.CanReachImmediate(destination, PathEndMode.Touch);
            bool landed = false;
            var precheck = RimKataCloseDefensePrecheck.None;
            if (!outside)
            {
                // Held pawns have no map cell. Resolve only this close shot at
                // the carrier's valid cell without spoofing the target's map.
                ShotReport report = ShotReport.HitReportFor(pawn, verb, pawn.Position);
                TargetSize(ref report) = Mathf.Clamp(state.target.BodySize, 0.5f, 2f);
                landed = Rand.Chance(Mathf.Clamp01(report.TotalEstimatedHitChance));
            }
            else if (close)
            {
                landed = RimKataCombatMath.RollCloseRangedNonMiss(pawn, verb, destination);
                precheck = RimKataDefenseUtility.PrecheckCloseGunfire(pawn, destination.Thing, verb, landed);
                if (precheck == RimKataCloseDefensePrecheck.ResponseSucceeded) return;
                if (precheck == RimKataCloseDefensePrecheck.ResponseSucceededWithAccidentalShot) landed = false;
                if (!RimKataSubdueUtility.IsRelationValid(state)) return;
            }
            var oldState = executing;
            bool oldHit = hit;
            bool oldHeldShot = heldShot;
            executing = state;
            hit = landed;
            heldShot = !outside;
            var previous = RimKataFireContext.Begin(verb, pawn, close ? destination.Thing : null,
                outside && !close && pawn.pather?.MovingNow == true, close, false, null,
                close, landed, precheck);
            RimKataFireContext.SuppressCloseLaunch = !outside;
            Rot4 facing = pawn.Rotation;
            Stance stance = pawn.stances.curStance;
            int? count = BurstCount(verb);
            bool nonInterrupting = NonInterrupting(verb);
            try
            {
                verb.Reset();
                CurrentTarget(verb) = outside ? destination : new LocalTargetInfo(pawn.Position);
                NonInterrupting(verb) = true;
                BurstCount(verb) = 1;
                // Retain native ammo use, shot callbacks, muzzle/sound effects
                // and projectile subclass behavior for this one close shot.
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
                // Moving the projectile alone does not move pawn armor effects.
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
