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
    public enum RimKataDefenseOutcome
    {
        None,
        Response
    }

    public interface IRimKataResponseCooldown
    {
        bool TryApplyResponseCooldown(ThingWithComps weapon, Verb verb, LocalTargetInfo focus);
    }

    public static class RimKataDefenseUtility
    {
        public struct DamageStaggerContextState
        {
            internal Pawn previousDefender;
            internal Thing previousAttacker;
            internal bool active;
        }

        public struct CloseAttackResolutionState
        {
            internal Pawn defender;
            internal bool resolved;
            internal bool avoided;
        }

        private struct ProjectileDefenseFrame
        {
            public Pawn avoidedPawn;
            public Pawn resolvedPawn;
            public bool resolvedWasAvoided;
        }

        [ThreadStatic] private static List<ProjectileDefenseFrame> projectileDefenseFrames;
        [ThreadStatic] private static int projectileDefenseDepth;
        [ThreadStatic] private static Pawn closeResolvedDefender;
        [ThreadStatic] private static bool closeDefenseResolved;
        [ThreadStatic] private static bool closeDefenseAvoided;
        [ThreadStatic] private static Pawn currentDamageStaggerDefender;
        [ThreadStatic] private static Thing currentDamageStaggerAttacker;

        public static DamageStaggerContextState EnterDamageStaggerContext(
            Pawn defender,
            Thing attacker)
        {
            DamageStaggerContextState state = new DamageStaggerContextState
            {
                previousDefender = currentDamageStaggerDefender,
                previousAttacker = currentDamageStaggerAttacker,
                active = true
            };

            currentDamageStaggerDefender = defender;
            currentDamageStaggerAttacker = attacker;
            return state;
        }

        public static void ExitDamageStaggerContext(DamageStaggerContextState state)
        {
            if (!state.active)
            {
                return;
            }

            currentDamageStaggerDefender = state.previousDefender;
            currentDamageStaggerAttacker = state.previousAttacker;
        }

        public static void NotifyAppliedDamageStagger(Pawn defender)
        {
            if (defender != null
                && defender == currentDamageStaggerDefender)
            {
                RimKataDualWeaponController.NotifyDefensiveCombatEvent(
                    defender, currentDamageStaggerAttacker);
            }
        }

        private static void NotifyAbsorbedRangedDamageForJob(
            Pawn defender,
            DamageInfo dinfo,
            bool suppressJobNotification)
        {
            if (defender?.Spawned != true
                || !dinfo.CheckForJobOverride
                || suppressJobNotification)
            {
                return;
            }

            defender.jobs?.Notify_DamageTaken(dinfo);
        }

        public static void ResetCloseAttackResolution()
        {
            closeResolvedDefender = null;
            closeDefenseResolved = false;
            closeDefenseAvoided = false;
        }

        public static CloseAttackResolutionState PushCloseAttackResolution()
        {
            CloseAttackResolutionState state = new CloseAttackResolutionState
            {
                defender = closeResolvedDefender,
                resolved = closeDefenseResolved,
                avoided = closeDefenseAvoided
            };
            ResetCloseAttackResolution();
            return state;
        }

        public static void PopCloseAttackResolution(
            CloseAttackResolutionState state)
        {
            closeResolvedDefender = state.defender;
            closeDefenseResolved = state.resolved;
            closeDefenseAvoided = state.avoided;
        }

        public static bool TryGetCloseAttackResolution(Pawn pawn, out bool avoided)
        {
            if (RimKataProjectileImpactContext.CurrentProjectile != null)
            {
                RimKataCloseProjectileState shot = RimKataProjectileImpactContext.CurrentCloseShot;
                avoided = shot?.defenseAvoided == true;
                return shot?.target == pawn && pawn != null && shot.defenseResolved;
            }

            avoided = closeDefenseAvoided;
            return closeDefenseResolved && pawn != null && pawn == closeResolvedDefender;
        }

        internal static void RecordCloseAttackResolution(Pawn pawn, bool avoided)
        {
            if (RimKataProjectileImpactContext.CurrentProjectile != null)
            {
                RimKataCloseProjectileState shot = RimKataProjectileImpactContext.CurrentCloseShot;
                if (shot?.target == pawn && pawn != null)
                {
                    shot.defenseResolved = true;
                    shot.defenseAvoided = avoided;
                }
                return;
            }

            closeResolvedDefender = pawn;
            closeDefenseResolved = true;
            closeDefenseAvoided = avoided;
        }

        internal static bool TryGetCloseAttackData(
            Pawn pawn,
            out bool meleeResolution,
            out bool meleeHit)
        {
            if (RimKataProjectileImpactContext.CurrentProjectile != null)
            {
                RimKataCloseProjectileState shot = RimKataProjectileImpactContext.CurrentCloseShot;
                meleeResolution = shot?.meleeResolution == true;
                meleeHit = shot?.meleeHit == true;
                return pawn != null && shot?.target == pawn;
            }

            meleeResolution = RimKataFireContext.CloseMeleeResolution;
            meleeHit = RimKataFireContext.CloseMeleeHit;
            return RimKataFireContext.CloseShot && pawn != null
                && RimKataFireContext.CloseTarget == pawn;
        }

        public static void EnterProjectileImpact()
        {
            projectileDefenseFrames ??= new List<ProjectileDefenseFrame>();
            if (projectileDefenseDepth < projectileDefenseFrames.Count)
            {
                projectileDefenseFrames[projectileDefenseDepth] = default(ProjectileDefenseFrame);
            }
            else
            {
                projectileDefenseFrames.Add(default(ProjectileDefenseFrame));
            }

            projectileDefenseDepth++;
        }

        public static void ExitProjectileImpact()
        {
            if (projectileDefenseDepth > 0)
            {
                projectileDefenseDepth--;
                projectileDefenseFrames[projectileDefenseDepth] = default(ProjectileDefenseFrame);
            }
        }

        public static RimKataDefenseOutcome ResolveCloseDefense(
            Pawn defender,
            Pawn attacker,
            Verb attackingVerb = null)
        {
            return ResolveCloseDefenseCore(
                defender,
                attacker,
                attackingVerb,
                false);
        }

        private static RimKataDefenseOutcome ResolveCloseDefenseCore(
            Pawn defender,
            Pawn attacker,
            Verb attackingVerb,
            bool defenseEligibilityVerified)
        {
            if (defender?.Map == null
                || attacker == null
                || !RimKataTargeting.IsAutomaticEnemy(defender, attacker)
                || (!defenseEligibilityVerified
                    && !RimKataEligibility.CanUseDefense(defender)))
            {
                return RimKataDefenseOutcome.None;
            }

            if (defender.CanReachImmediate(attacker, PathEndMode.Touch))
            {
                defender.Map.GetComponent<RimKataMapComponent>()?.EnterCloseCombat(defender, attacker);
            }

            if (TryResolveMeleeParry(
                defender,
                attacker,
                attackingVerb,
                true))
            {
                return RimKataDefenseOutcome.Response;
            }

            return RimKataDefenseOutcome.None;
        }

        public static RimKataCloseDefensePrecheck PrecheckCloseGunfire(
            Pawn attacker,
            Thing target,
            Verb attackingVerb,
            bool meleeHit)
        {
            if (!meleeHit
                || !(target is Pawn defender)
                || attacker == null
                || attackingVerb == null
                || !RimKataTargeting.IsAutomaticEnemy(defender, attacker)
                || !RimKataEligibility.CanUseDefense(defender)
                || HasActiveApparelShield(defender))
            {
                return RimKataCloseDefensePrecheck.None;
            }

            float dodgeChance =
                RimKataCombatMath.CloseMeleeDodgeChanceVerified(defender);
            if (Rand.Chance(dodgeChance))
            {
                return RimKataCloseDefensePrecheck.FirstDodgeSucceeded;
            }

            if (ResolveCloseDefenseCore(
                    defender,
                    attacker,
                    attackingVerb,
                    true)
                == RimKataDefenseOutcome.Response)
            {
                float accidentalFireChance = RimKataTargetAccess.SettingsFor(defender)?.ResponseAccidentalFireChance ?? 0.2f;
                if (Rand.Chance(accidentalFireChance))
                {
                    int dodgeDurationTicks = RimKataTargetAccess.SettingsFor(defender)?.GetRangedDodgeDurationTicks(defender)
                        ?? RimKataSettings.DefaultRangedDodgeDurationTicks;
                    defender.Map?.GetComponent<RimKataMapComponent>()
                        ?.BeginCloseCombatDodge(defender, dodgeDurationTicks);
                    return RimKataCloseDefensePrecheck.ResponseSucceededWithAccidentalShot;
                }

                return RimKataCloseDefensePrecheck.ResponseSucceeded;
            }

            return RimKataCloseDefensePrecheck.FirstDodgeAndResponseFailed;
        }

        private static bool HasActiveApparelShield(Pawn pawn)
        {
            List<Apparel> worn = pawn?.apparel?.WornApparel;
            if (worn == null)
            {
                return false;
            }

            for (int i = 0; i < worn.Count; i++)
            {
                CompShield shield = worn[i]?.GetComp<CompShield>();
                if (shield != null && shield.ShieldState == ShieldState.Active)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TryResolveMeleeParry(
            Verb_MeleeAttack attackingVerb)
        {
            if (attackingVerb == null)
            {
                return false;
            }

            Pawn defender = attackingVerb.CurrentTarget.Pawn;
            Pawn attacker = attackingVerb.CasterPawn;
            if (defender == null
                || attacker == null
                || !RimKataEligibility.CanUseDefense(defender))
            {
                return false;
            }

            if (RimKataTargeting.IsAutomaticEnemy(defender, attacker)
                && defender.CanReachImmediate(attacker, PathEndMode.Touch))
            {
                defender.Map?.GetComponent<RimKataMapComponent>()?
                    .EnterCloseCombat(defender, attacker);
            }

            return TryResolveMeleeParry(
                defender,
                attacker,
                attackingVerb,
                true);
        }

        private static bool TryResolveMeleeParry(
            Pawn defender,
            Pawn attacker,
            Verb attackingVerb,
            bool defenseEligibilityVerified = false)
        {
            if (defender?.Map == null
                || attacker == null
                || attacker.Map != defender.Map
                || RimKataTargetAccess.SettingsFor(defender)?.responseEnabled == false
                || (defenseEligibilityVerified
                    ? defender.kindDef?.canMeleeAttack != true
                    : !RimKataEligibility.CanUseMeleeResponse(defender))
                || IsExcludedPlayerFactionMeleeAttack(defender, attacker)
                || !Rand.Chance(
                    RimKataCombatMath.MeleeParryChance(
                        defender,
                        attacker)))
            {
                return false;
            }

            NotifySuccessfulResponse(defender, attacker, attackingVerb);
            return true;
        }

        private static bool IsExcludedPlayerFactionMeleeAttack(
            Pawn defender,
            Pawn attacker)
        {
            return attacker.Faction == defender.Faction
                && attacker.Faction?.IsPlayer == true
                && attacker.IsFreeNonSlaveColonist
                && !attacker.InMentalState
                && !attacker.IsQuestLodger();
        }

        internal static bool IsDirectHitBullet(Projectile projectile)
        {
            // Mod bullets can use an explosive DamageDef without detonating.
            // Their actual victim must still receive the ordinary impact roll.
            return projectile is Bullet
                && projectile.def?.projectile != null
                && projectile.def.projectile.explosionRadius <= 0f;
        }

        internal static Pawn ExplosiveDodgeTarget(Projectile projectile)
        {
            if (IsDirectHitBullet(projectile))
            {
                return null;
            }

            Pawn defender = projectile?.intendedTarget.Pawn;
            if (defender == null
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(defender)
                || !RimKataEligibility.CanRollRangedDodgeVerified(defender, false)
                || projectile.Destroyed || !projectile.Spawned
                || defender.Map != projectile.Map
                || (projectile.usedTarget.Pawn != defender
                    && (projectile.usedTarget.HasThing || projectile.usedTarget.Cell != defender.Position)))
            {
                return null;
            }

            ProjectileProperties properties = projectile.def?.projectile;
            DamageDef damage = projectile.damageDefOverride ?? properties?.damageDef;
            if (properties == null || (properties.explosionRadius <= 0f && damage?.isExplosive != true))
            {
                return null;
            }

            Thing attacker = projectile.Launcher;
            if (attacker == defender
                || (attacker?.Faction != null && attacker.Faction == defender.Faction))
            {
                return null;
            }
            return defender;
        }

        internal static bool TryDodgeExplosiveProjectile(
            Projectile projectile, Pawn defender, bool alreadyAvoided)
        {
            if (defender == null || ExplosiveDodgeTarget(projectile) != defender
                || !RimKataEligibility.CanRollRangedDodge(defender, false)
                || !RimKataProjectileMissUtility.TryPrepareMiss(projectile, defender, out var flight))
            {
                return false;
            }

            if (!alreadyAvoided && !TryRangedDodge(defender, projectile.Launcher, projectile))
            {
                return false;
            }

            // The same projectile still detonates normally at its missed destination.
            RimKataProjectileMissUtility.ApplyMiss(projectile, flight);
            if (projectile.def.projectile.alwaysFreeIntercept)
            {
                // This flag makes native HitFlags ignore the removed IntendedTarget bit.
                defender.Map?.GetComponent<RimKataMapComponent>()
                    ?.RegisterLaunchedRangedProjectile(projectile, defender, preventDirectHit: true);
            }
            return true;
        }

        public static bool TryRangedDodge(Pawn defender, Thing attacker, Projectile projectile)
        {
            RimKataMapComponent component = defender.Map?.GetComponent<RimKataMapComponent>();
            RimKataSettings settings = RimKataTargetAccess.SettingsFor(defender);

            // This gate is independent of the normal dodge roll, delay and
            // additional-dodge allowance, including while a tumble is playing.
            float immediateTumbleChance = settings?.ImmediateTumbleChance ?? 0f;
            if (component != null
                && settings?.tumbleEnabled != false
                && !component.IsCloseCombatActive(defender)
                && immediateTumbleChance > 0f
                && Rand.Chance(immediateTumbleChance))
            {
                component.BeginImmediateTumble(defender);
                component.MarkCurrentRangedProjectilesAvoided(defender);
                return true;
            }

            int dodgeDurationTicks = settings?.GetRangedDodgeDurationTicks(defender) ?? RimKataSettings.DefaultRangedDodgeDurationTicks;

            if (component?.IsRangedDodgeDelayActive(defender) == true)
            {
                if (settings?.tumbleEnabled == false
                    || !component.CanTryAdditionalDodge(defender)
                    || !RimKataCombatMath.RollConfiguredChance(
                        defender,
                        RimKataChanceKind.RangedDodge))
                {
                    return false;
                }

                bool closeCombatDodge = component.IsCloseCombatActive(defender);
                bool playTumble = !closeCombatDodge;
                if (!component.TryBeginAdditionalDodge(defender, playTumble))
                {
                    return false;
                }

                if (closeCombatDodge)
                {
                    component.BeginCloseCombatDodge(defender, dodgeDurationTicks);
                }

                component.MarkCurrentRangedProjectilesAvoided(defender);
                return true;
            }

            if (!RimKataCombatMath.RollConfiguredChance(defender, RimKataChanceKind.RangedDodge))
            {
                return false;
            }

            RimKataDodgeMovementUtility.ApplySuccessfulRangedDodge(
                defender,
                attacker,
                projectile,
                component,
                dodgeDurationTicks);

            return true;
        }

        public static bool ShouldSuppressBulletStagger(Pawn pawn)
        {
            if (projectileDefenseDepth <= 0 || projectileDefenseFrames == null)
            {
                return false;
            }

            ProjectileDefenseFrame frame = projectileDefenseFrames[projectileDefenseDepth - 1];
            return pawn != null
                && pawn == frame.avoidedPawn
                && RimKataProjectileImpactContext.CurrentProjectile != null;
        }

        public static void MarkProjectileAvoided(Pawn pawn)
        {
            if (RimKataProjectileImpactContext.CurrentProjectile != null
                && projectileDefenseDepth > 0
                && projectileDefenseFrames != null)
            {
                int index = projectileDefenseDepth - 1;
                ProjectileDefenseFrame frame = projectileDefenseFrames[index];
                frame.avoidedPawn = pawn;
                projectileDefenseFrames[index] = frame;
            }
        }

        public static bool TryGetResolvedProjectileDefense(Pawn pawn, out bool avoided)
        {
            if (projectileDefenseDepth <= 0 || projectileDefenseFrames == null)
            {
                avoided = false;
                return false;
            }

            ProjectileDefenseFrame frame = projectileDefenseFrames[projectileDefenseDepth - 1];
            avoided = frame.resolvedWasAvoided;
            return RimKataProjectileImpactContext.CurrentProjectile != null
                && pawn != null
                && pawn == frame.resolvedPawn;
        }

        public static void RecordProjectileDefense(Pawn pawn, bool avoided)
        {
            if (RimKataProjectileImpactContext.CurrentProjectile != null
                && projectileDefenseDepth > 0
                && projectileDefenseFrames != null)
            {
                int index = projectileDefenseDepth - 1;
                ProjectileDefenseFrame frame = projectileDefenseFrames[index];
                frame.resolvedPawn = pawn;
                frame.resolvedWasAvoided = avoided;
                projectileDefenseFrames[index] = frame;
            }
        }

        public static bool TryAbsorbAfterShield(Pawn defender, DamageInfo dinfo)
        {
            Projectile projectile = RimKataProjectileImpactContext.CurrentProjectile;
            RimKataCloseProjectileState closeShot = RimKataProjectileImpactContext.CurrentCloseShot;
            if (closeShot?.target != defender)
            {
                closeShot = null;
            }
            bool closeAttack = closeShot != null
                || (projectile == null && RimKataFireContext.CloseShot
                    && RimKataFireContext.CloseTarget == defender);
            if (TryGetResolvedProjectileDefense(defender, out bool previouslyAvoided))
            {
                return previouslyAvoided;
            }

            if (dinfo.Def == null
                || (!IsDirectHitBullet(projectile)
                    && (!dinfo.Def.isRanged
                        || dinfo.Def.isExplosive
                        || projectile?.def?.projectile?.explosionRadius > 0f)))
            {
                return false;
            }

            Thing attacker = projectile?.Launcher ?? dinfo.Instigator;

            if (!closeAttack
                && defender.Map?.GetComponent<RimKataMapComponent>()
                    ?.TryConsumeAvoidedRangedProjectile(
                    projectile,
                    defender,
                    out bool suppressJobNotification) == true)
            {
                RecordProjectileDefense(defender, true);
                MarkProjectileAvoided(defender);
                NotifyAbsorbedRangedDamageForJob(
                    defender,
                    dinfo,
                    suppressJobNotification);
                return true;
            }

            if (closeAttack
                && (attacker == null
                    || attacker == defender
                    || !RimKataTargeting.IsAutomaticEnemy(
                        defender,
                        attacker)))
            {
                return false;
            }

            Faction defenderFaction = defender.Faction;
            Faction attackerFaction = attacker?.Faction;
            if (!closeAttack
                && (attacker == defender
                    || (defenderFaction != null
                        && attackerFaction != null
                        && defenderFaction == attackerFaction)))
            {
                return false;
            }

            if (closeAttack)
            {
                if (TryGetCloseAttackResolution(defender, out bool closePreviouslyAvoided))
                {
                    RecordProjectileDefense(defender, closePreviouslyAvoided);
                    if (closePreviouslyAvoided)
                    {
                        MarkProjectileAvoided(defender);
                    }

                    return closePreviouslyAvoided;
                }

                RimKataCloseDefensePrecheck precheck = closeShot?.precheck
                    ?? RimKataFireContext.CloseDefensePrecheck;
                bool meleeResolution = closeShot?.meleeResolution
                    ?? RimKataFireContext.CloseMeleeResolution;
                Pawn closeAttacker = closeShot != null ? attacker as Pawn : RimKataFireContext.Shooter;
                Verb attackingVerb = closeShot != null ? closeShot.attackingVerb : RimKataFireContext.ActiveVerb;
                if (precheck == RimKataCloseDefensePrecheck.FirstDodgeSucceeded
                    || precheck == RimKataCloseDefensePrecheck.ResponseSucceeded
                    || precheck == RimKataCloseDefensePrecheck.ResponseSucceededWithAccidentalShot)
                {
                    RecordCloseAttackResolution(defender, true);
                    RecordProjectileDefense(defender, true);
                    MarkProjectileAvoided(defender);
                    if (precheck == RimKataCloseDefensePrecheck.FirstDodgeSucceeded)
                    {
                        NotifyAbsorbedRangedDamageForJob(
                            defender,
                            dinfo,
                            RimKataEligibility
                                .IsWorkMovementDefenseException(defender));
                    }

                    return true;
                }

                if (!RimKataEligibility.TryGetDefenseEligibility(
                        defender,
                        out bool workMovementDefenseException))
                {
                    RecordCloseAttackResolution(defender, false);
                    RecordProjectileDefense(defender, false);
                    return false;
                }

                bool firstDodgeAndResponseAlreadyFailed = precheck == RimKataCloseDefensePrecheck.FirstDodgeAndResponseFailed;
                float closeDodgeChance = meleeResolution
                    ? RimKataCombatMath.CloseMeleeDodgeChanceVerified(
                        defender)
                    : 0f;
                if (!firstDodgeAndResponseAlreadyFailed
                    && meleeResolution
                    && Rand.Chance(closeDodgeChance))
                {
                    RecordCloseAttackResolution(defender, true);
                    RecordProjectileDefense(defender, true);
                    MarkProjectileAvoided(defender);
                    NotifyAbsorbedRangedDamageForJob(
                        defender,
                        dinfo,
                        workMovementDefenseException);
                    return true;
                }

                RimKataDefenseOutcome outcome = firstDodgeAndResponseAlreadyFailed
                    ? RimKataDefenseOutcome.None
                    : ResolveCloseDefenseCore(
                        defender,
                        closeAttacker,
                        attackingVerb,
                        true);
                if (outcome != RimKataDefenseOutcome.None)
                {
                    RecordCloseAttackResolution(defender, true);
                    RecordProjectileDefense(defender, true);
                    MarkProjectileAvoided(defender);
                    float accidentalFireChance = RimKataTargetAccess.SettingsFor(defender)?
                        .ResponseAccidentalFireChance ?? 0.2f;
                    if (Rand.Chance(accidentalFireChance))
                    {
                        int dodgeDurationTicks = RimKataTargetAccess.SettingsFor(defender)?
                            .GetRangedDodgeDurationTicks(defender)
                            ?? RimKataSettings.DefaultRangedDodgeDurationTicks;
                        defender.Map?.GetComponent<RimKataMapComponent>()?
                            .BeginCloseCombatDodge(defender, dodgeDurationTicks);
                        RimKataProjectileUtility.SpawnDeflectedMiss(
                            projectile, closeAttacker, defender, attackingVerb);
                    }
                    return true;
                }

                if (meleeResolution
                    && Rand.Chance(closeDodgeChance))
                {
                    RecordCloseAttackResolution(defender, true);
                    RecordProjectileDefense(defender, true);
                    MarkProjectileAvoided(defender);
                    RimKataProjectileUtility.SpawnDeflectedMiss(
                        RimKataProjectileImpactContext.CurrentProjectile,
                        closeAttacker,
                        defender,
                        attackingVerb);
                    NotifyAbsorbedRangedDamageForJob(
                        defender,
                        dinfo,
                        workMovementDefenseException);
                    return true;
                }

                RecordCloseAttackResolution(defender, false);
                RecordProjectileDefense(defender, false);
                return false;
            }

            if (!RimKataEligibility.TryGetDefenseEligibility(
                    defender,
                    out bool rangedWorkMovementDefenseException))
            {
                return false;
            }

            if (RimKataEligibility.CanRollRangedDodgeVerified(
                    defender,
                    false)
                && TryRangedDodge(
                    defender,
                    attacker,
                    projectile))
            {
                RecordProjectileDefense(defender, true);
                MarkProjectileAvoided(defender);
                NotifyAbsorbedRangedDamageForJob(
                    defender,
                    dinfo,
                    rangedWorkMovementDefenseException);
                return true;
            }

            RecordProjectileDefense(defender, false);
            return false;
        }

        private static void NotifySuccessfulResponse(Pawn defender, Pawn attacker, Verb attackingVerb)
        {
            defender.Map?.GetComponent<RimKataMapComponent>()?
                .CancelDeflectionSpin(defender);
            if (Find.BattleLog != null
                && RimKataDefOf.RimKata_ParryBattleLog != null)
            {
                Find.BattleLog.Add(
                    new BattleLogEntry_Event(
                        defender,
                        RimKataDefOf.RimKata_ParryBattleLog,
                        attacker));
            }

            int deflectionSign = Rand.Bool ? 1 : -1;
            if (attacker?.Map != null)
            {
                int duration = ResponseDeflectionTicks(attacker, attackingVerb);
                float spinChance = RimKataTargetAccess.SettingsFor(defender)
                    ?.ResponseAttackerSpinChance ?? 0f;
                bool spin = spinChance > 0f && Rand.Chance(spinChance);
                RimKataMapComponent component = attacker.Map.GetComponent<RimKataMapComponent>();
                int spinTicks = spin || component?.GetState(attacker, false)?.DeflectionSpinActive == true
                    ? ResponseAttackerSpinTicks(attacker, attackingVerb)
                    : 8;
                component?.BeginDeflection(
                    attacker,
                    duration,
                    deflectionSign,
                    attackingVerb?.EquipmentSource as ThingWithComps,
                    spin,
                    spinTicks);
            }

            LocalTargetInfo responseFocus = attacker != null
                ? new LocalTargetInfo(attacker)
                : new LocalTargetInfo(defender.Position);

            if (!RimKataWeaponStealMapComponent.TrySchedule(
                    defender, attacker, attackingVerb, out bool allowDisarm)
                && allowDisarm)
            {
                TryDisarmAttacker(defender, attacker, attackingVerb);
            }

            ThingWithComps weapon = SelectResponseWeapon(defender);
            if (weapon == null)
            {
                // Facing is a response of the pawn, even when there is no weapon
                // to animate. Keep its current attack job and cooldown untouched.
                Verb unarmedVerb = (defender.stances?.curStance as Stance_Busy)?.verb;
                if (unarmedVerb?.IsMeleeAttack != true || unarmedVerb.EquipmentSource != null)
                {
                    unarmedVerb = defender.meleeVerbs?.TryGetMeleeVerb(attacker);
                }

                int unarmedPoseTicks = RimKataCombatMath.CooldownTicksForSingleShot(
                    unarmedVerb, defender, true);
                if (unarmedPoseTicks <= 0)
                {
                    unarmedPoseTicks = ResponseDeflectionTicks(attacker, attackingVerb);
                }

                defender.Map?.GetComponent<RimKataMapComponent>()?.BeginResponsePose(
                    defender,
                    Mathf.Max(1, unarmedPoseTicks),
                    0f,
                    deflectionSign,
                    responseFocus,
                    null,
                    true);
                return;
            }

            Verb verb = RimKataWeaponSlotUtility.CombatVerb(defender, weapon);
            if (verb == null)
            {
                return;
            }

            SpawnWeaponResponseEffect(defender, weapon, responseFocus);
            ApplyResponseWeaponDurabilityLoss(defender, weapon);
            if (weapon.Destroyed || defender.equipment?.AllEquipmentListForReading?.Contains(weapon) != true)
            {
                if (defender.Spawned)
                {
                    if (defender.stances?.curStance is Stance_RimKataAim)
                    {
                        defender.stances.SetStance(new Stance_Mobile());
                    }
                    else
                    {
                        defender.stances?.CancelBusyStanceSoft();
                    }
                }

                return;
            }

            verb = RimKataWeaponSlotUtility.CombatVerb(defender, weapon);
            if (verb == null)
            {
                return;
            }

            int ticks = RimKataCombatMath.CooldownTicksForSingleShot(verb, defender, true);
            bool cooldownApplied = defender.jobs?.curDriver is IRimKataResponseCooldown customDriver
                && customDriver.TryApplyResponseCooldown(weapon, verb, responseFocus);
            cooldownApplied = cooldownApplied
                || RimKataDraftedFireController.TryApplyResponseCooldown(defender, weapon, verb, responseFocus)
                || RimKataDualWeaponController.TryApplyResponseCooldown(
                    defender,
                    weapon,
                    verb,
                    responseFocus);
            if (!cooldownApplied)
            {
                verb.Reset();
                defender.stances.SetStance(new Stance_Cooldown(ticks, responseFocus, verb));
            }

            bool responseTargetQueued = cooldownApplied
                && RimKataDualWeaponController.IsResponseTargetQueued(
                    defender,
                    weapon,
                    responseFocus);
            int poseTicks = Mathf.Max(1, ticks);
            defender.Map?.GetComponent<RimKataMapComponent>()?.BeginResponsePose(
                defender,
                poseTicks,
                5f,
                deflectionSign,
                responseFocus,
                weapon,
                !responseTargetQueued);
        }

        private static ThingWithComps SelectResponseWeapon(Pawn defender)
        {
            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(defender);
            ThingWithComps secondary = RimKataWeaponSlotUtility.CanUseSecondarySlot(defender)
                ? RimKataWeaponSlotUtility.SecondaryWeapon(defender)
                : null;
            bool primaryUsable = primary != null && !primary.Destroyed;
            bool secondaryUsable = secondary != null && !secondary.Destroyed;
            if (primaryUsable && secondaryUsable)
            {
                return Rand.Bool ? primary : secondary;
            }

            return primaryUsable ? primary : secondaryUsable ? secondary : null;
        }

        private static void TryDisarmAttacker(Pawn defender, Pawn attacker, Verb attackingVerb)
        {
            if (attacker?.RaceProps?.Humanlike != true)
            {
                return;
            }

            RimKataSettings settings = RimKataTargetAccess.SettingsFor(defender);
            ThingWithComps weapon = attackingVerb?.EquipmentSource as ThingWithComps;
            if (attackingVerb != null
                && attacker?.equipment?.AllEquipmentListForReading?.Contains(weapon) != true)
            {
                return;
            }

            if (attackingVerb == null)
            {
                weapon = attacker?.equipment?.Primary;
            }
            if (settings == null
                || defender?.Map == null
                || attacker?.Map != defender.Map
                || weapon == null
                || weapon.Destroyed)
            {
                return;
            }

            float chance = settings.GetResponseDisarmChance(defender);
            if (chance <= 0f || (chance < 1f && !Rand.Chance(chance)))
            {
                return;
            }

            IntVec3 awayFromAttacker = defender.Position - attacker.Position;
            IntVec3 direction = new IntVec3(Math.Sign(awayFromAttacker.x), 0, Math.Sign(awayFromAttacker.z));
            if (direction == IntVec3.Zero)
            {
                direction = FacingCell(defender.Rotation);
            }

            IntVec3 dropCell = defender.Position + direction;
            if (!dropCell.InBounds(defender.Map) || !dropCell.Walkable(defender.Map))
            {
                dropCell = defender.Position;
            }

            attacker.equipment.TryDropEquipment(weapon, out _, dropCell, false);
        }

        private static IntVec3 FacingCell(Rot4 rotation)
        {
            switch (rotation.AsInt)
            {
                case 0: return IntVec3.North;
                case 1: return IntVec3.East;
                case 2: return IntVec3.South;
                case 3: return IntVec3.West;
                default: return IntVec3.North;
            }
        }

        private static void ApplyResponseWeaponDurabilityLoss(Pawn defender, ThingWithComps weapon)
        {
            RimKataSettings settings = RimKataTargetAccess.SettingsFor(defender);
            if (settings == null
                || settings.responseWeaponDurabilityLossChancePercent <= 0f
                || settings.responseWeaponDurabilityLossAmount < 1
                || weapon == null
                || weapon.Destroyed
                || weapon.def?.useHitPoints != true)
            {
                return;
            }

            float chance = settings.ResponseWeaponDurabilityLossChance;
            if (chance < 1f && !Rand.Chance(chance))
            {
                return;
            }

            weapon.TakeDamage(new DamageInfo(DamageDefOf.Deterioration, settings.responseWeaponDurabilityLossAmount));
        }

        private static int ResponseDeflectionTicks(Pawn attacker, Verb attackingVerb)
        {
            Verb verb = attackingVerb ?? (attacker?.stances?.curStance as Stance_Busy)?.verb ?? RimKataWeaponSlotUtility.CombatVerb(attacker, attacker?.equipment?.Primary);
            if (verb?.verbProps == null || attacker == null)
            {
                return 0;
            }

            int ticks = verb.IsMeleeAttack
                ? verb.verbProps.AdjustedCooldownTicks(verb, attacker)
                : RimKataCombatMath.CooldownTicksForSingleShot(verb, attacker, false);
            return Mathf.Max(2, ticks);
        }

        private static int ResponseAttackerSpinTicks(Pawn attacker, Verb attackingVerb)
        {
            ThingWithComps weapon = attackingVerb?.EquipmentSource as ThingWithComps;
            RimKataPawnCombatState state = attacker?.Map
                ?.GetComponent<RimKataMapComponent>()?.GetState(attacker, false);
            RimKataWeaponCycleState cycle = weapon == null ? null
                : state?.primaryWeaponCycle?.weapon == weapon ? state.primaryWeaponCycle
                : state?.secondaryWeaponCycle?.weapon == weapon ? state.secondaryWeaponCycle
                : null;
            int remaining = Mathf.Max(0, cycle?.cooldownTicksRemaining ?? 0);
            if (attacker?.stances?.curStance is Stance_Cooldown cooldown
                && cooldown.verb == attackingVerb)
            {
                remaining = Mathf.Max(remaining, cooldown.ticksLeft);
            }

            // A synchronous hit is resolved before its caller installs the new
            // cooldown. Projectiles already in flight use only the time left.
            if (remaining == 0 && attackingVerb != null
                && (attackingVerb.IsMeleeAttack
                    || RimKataFireContext.ActiveVerb == attackingVerb))
            {
                remaining = ResponseDeflectionTicks(attacker, attackingVerb);
            }
            return Mathf.Clamp(remaining, 8, 24);
        }

        private static void SpawnWeaponResponseEffect(
            Pawn defender,
            ThingWithComps weapon,
            LocalTargetInfo focus)
        {
            if (defender?.Map == null || weapon == null || !focus.IsValid)
            {
                return;
            }

            Vector3 drawLoc = defender.DrawPos + RimKataVisualUtility.DrawOffset(RimKataVisualUtility.SnapshotFor(defender));
            Vector3 focusLoc = focus.HasThing
                ? focus.Thing.DrawPos
                : focus.Cell.ToVector3Shifted();
            Vector3 aimVector = focusLoc - drawLoc;
            float aimAngle = aimVector.sqrMagnitude > 0.001f ? aimVector.AngleFlat() : 0f;
            float distanceFactor = defender.ageTracker?.CurLifeStage?.equipmentDrawDistanceFactor ?? 1f;
            float drawDistance = (0.4f + weapon.def.equippedDistanceOffset) * distanceFactor;
            Vector3 equipmentCenter = drawLoc + new Vector3(0f, 0f, drawDistance).RotatedBy(aimAngle);
            Vector3 effectOffset = equipmentCenter - defender.Position.ToVector3Shifted();
            Effecter effecter = EffecterDefOf.Deflect_General.Spawn(defender.Position, defender.Map, effectOffset, 1f);
            effecter?.Cleanup();
        }

    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PreApplyDamage))]
    public static class Patch_Pawn_PreApplyDamage_RimKata
    {
        public static bool Prefix(Pawn __instance, ref bool absorbed)
        {
            if (!RimKataDefenseUtility.TryGetCloseAttackData(
                    __instance, out bool meleeResolution, out bool meleeHit))
            {
                return true;
            }

            if (RimKataDefenseUtility.TryGetCloseAttackResolution(__instance, out bool previouslyAvoided)
                && previouslyAvoided)
            {
                RimKataDefenseUtility.RecordProjectileDefense(__instance, true);
                RimKataDefenseUtility.MarkProjectileAvoided(__instance);
                absorbed = true;
                return false;
            }

            if (meleeResolution && !meleeHit)
            {
                RimKataDefenseUtility.RecordCloseAttackResolution(__instance, true);
                RimKataDefenseUtility.RecordProjectileDefense(__instance, true);
                RimKataDefenseUtility.MarkProjectileAvoided(__instance);
                absorbed = true;
                return false;
            }

            return true;
        }

        public static void Postfix(
            Pawn __instance,
            ref DamageInfo dinfo,
            ref bool absorbed)
        {
            bool closeMeleeTarget = RimKataDefenseUtility.TryGetCloseAttackData(
                __instance, out bool meleeResolution, out _)
                && meleeResolution;
            if (absorbed
                && closeMeleeTarget
                && !RimKataDefenseUtility.TryGetCloseAttackResolution(__instance, out _))
            {
                RimKataDefenseUtility.RecordCloseAttackResolution(__instance, false);
            }

            if (absorbed
                && !RimKataDefenseUtility.TryGetResolvedProjectileDefense(__instance, out _)
                && (closeMeleeTarget
                    || RimKataEligibility.HasRimKataAccess(__instance)))
            {
                RimKataDefenseUtility.RecordProjectileDefense(__instance, false);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.PreApplyDamage))]
    public static class Patch_PawnHealth_PreApplyDamage_RimKata
    {
        private static readonly FieldInfo PawnField = AccessTools.Field(typeof(Pawn_HealthTracker), "pawn");
        private static readonly FieldInfo StancesField = AccessTools.Field(typeof(Pawn), nameof(Pawn.stances));
        private static readonly MethodInfo NotifyDamageTakenMethod = AccessTools.Method(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.Notify_DamageTaken));
        private static readonly MethodInfo TryAbsorbMethod = AccessTools.Method(typeof(RimKataDefenseUtility), nameof(RimKataDefenseUtility.TryAbsorbAfterShield));

        public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            int notifyIndex = codes.FindIndex(code => code.Calls(NotifyDamageTakenMethod));
            int stancesIndex = -1;
            for (int i = notifyIndex - 1; i >= 0 && i >= notifyIndex - 8; i--)
            {
                if (codes[i].opcode == OpCodes.Ldfld && Equals(codes[i].operand, StancesField))
                {
                    stancesIndex = i;
                    break;
                }
            }

            int insertIndex = -1;
            for (int i = stancesIndex - 1; i >= 0 && i >= stancesIndex - 4; i--)
            {
                if (codes[i].opcode == OpCodes.Ldarg_0)
                {
                    insertIndex = i;
                    break;
                }
            }

            if (notifyIndex < 0 || stancesIndex < 0 || insertIndex < 0)
            {
                Log.Error("[RimKata] Could not place the post-shield defense hook.");
                return codes;
            }

            List<Label> originalLabels = new List<Label>(codes[insertIndex].labels);
            codes[insertIndex].labels.Clear();
            Label continueLabel = generator.DefineLabel();
            codes[insertIndex].labels.Add(continueLabel);
            CodeInstruction first = new CodeInstruction(OpCodes.Ldarg_0);
            first.labels.AddRange(originalLabels);
            first.blocks.AddRange(codes[insertIndex].blocks);
            codes[insertIndex].blocks.Clear();

            List<CodeInstruction> injected = new List<CodeInstruction>
            {
                first,
                new CodeInstruction(OpCodes.Ldfld, PawnField),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, TryAbsorbMethod),
                new CodeInstruction(OpCodes.Brfalse_S, continueLabel),
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldc_I4_1),
                new CodeInstruction(OpCodes.Stind_I1),
                new CodeInstruction(OpCodes.Ret)
            };

            codes.InsertRange(insertIndex, injected);
            return codes;
        }
    }

    [HarmonyPatch(typeof(StaggerHandler), nameof(StaggerHandler.Notify_BulletImpact))]
    public static class Patch_StaggerHandler_RimKataDodge
    {
        public static bool Prefix(
            StaggerHandler __instance,
            Bullet bullet,
            out RimKataDefenseUtility.DamageStaggerContextState __state)
        {
            __state = default(RimKataDefenseUtility.DamageStaggerContextState);
            if (RimKataDefenseUtility.ShouldSuppressBulletStagger(__instance.parent))
            {
                return false;
            }

            __state = RimKataDefenseUtility.EnterDamageStaggerContext(
                __instance.parent,
                bullet?.Launcher);
            return true;
        }

        public static Exception Finalizer(
            Exception __exception,
            RimKataDefenseUtility.DamageStaggerContextState __state)
        {
            RimKataDefenseUtility.ExitDamageStaggerContext(__state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Verb_MeleeAttack), "TryCastShot")]
    public static class Patch_Verb_MeleeAttack_Context
    {
        public struct MeleeAttackContextState
        {
            internal RimKataDefenseUtility.DamageStaggerContextState damageStaggerContext;
            internal bool active;
        }

        private static readonly MethodInfo GetDodgeChanceMethod = AccessTools.Method(typeof(Verb_MeleeAttack), "GetDodgeChance");
        private static readonly MethodInfo RandChanceMethod = AccessTools.Method(typeof(Rand), nameof(Rand.Chance), new[] { typeof(float) });
        private static readonly MethodInfo TryResolveMeleeParryMethod =
            AccessTools.Method(
                typeof(RimKataDefenseUtility),
                nameof(RimKataDefenseUtility.TryResolveMeleeParry),
                new[] { typeof(Verb_MeleeAttack) });
        private static readonly MethodInfo PlayOneShotMethod =
            AccessTools.Method(
                typeof(Verse.Sound.SoundStarter),
                "PlayOneShot",
                new[]
                {
                    typeof(SoundDef),
                    typeof(Verse.Sound.SoundInfo)
                });
        private static readonly MethodInfo NotifyMeleeAttackOnMethod =
            AccessTools.Method(
                typeof(Pawn_DrawTracker),
                nameof(Pawn_DrawTracker.Notify_MeleeAttackOn));
        private static readonly MethodInfo StaggerForMethod =
            AccessTools.Method(
                typeof(StaggerHandler),
                nameof(StaggerHandler.StaggerFor),
                new[] { typeof(int), typeof(float) });

        public static bool Prefix(
            Verb_MeleeAttack __instance,
            ref bool __result,
            out MeleeAttackContextState __state)
        {
            __state = new MeleeAttackContextState
            {
                damageStaggerContext = RimKataDefenseUtility.EnterDamageStaggerContext(
                    __instance?.CurrentTarget.Pawn,
                    __instance?.CasterPawn),
                active = true
            };
            bool rimKataCycleAttack = RimKataFireContext.ActiveVerb == __instance;
            bool physicalRangedWeaponAttack = !rimKataCycleAttack && RimKataDodgeMovementUtility.ShouldBlockPhysicalMeleeVerb(__instance);
            if (physicalRangedWeaponAttack
                && RimKataDraftedFireController.ShouldReplacePhysicalMeleeAttack(
                    __instance?.CasterPawn,
                    __instance?.CurrentTarget.Thing))
            {
                __result = false;
                return false;
            }

            return true;
        }

        public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            int dodgeChanceIndex = -1;
            int dodgeBranchIndex = -1;
            for (int i = 0; i < codes.Count - 2; i++)
            {
                if (!codes[i].Calls(GetDodgeChanceMethod)
                    || !codes[i + 1].Calls(RandChanceMethod))
                {
                    continue;
                }

                dodgeChanceIndex = i;
                dodgeBranchIndex = i + 2;
                break;
            }

            int playSoundIndex = codes.FindIndex(
                code => code.Calls(PlayOneShotMethod));
            int notifyAttackIndex = codes.FindIndex(
                code => code.Calls(NotifyMeleeAttackOnMethod));
            int staggerIndex = codes.FindIndex(
                notifyAttackIndex + 1,
                code => code.Calls(StaggerForMethod));

            if (dodgeChanceIndex < 0
                || dodgeBranchIndex < 0
                || (codes[dodgeBranchIndex].opcode != OpCodes.Brtrue
                    && codes[dodgeBranchIndex].opcode != OpCodes.Brtrue_S)
                || playSoundIndex < 0
                || playSoundIndex + 1 >= codes.Count
                || notifyAttackIndex < 0
                || notifyAttackIndex + 1 >= codes.Count
                || staggerIndex < 0
                || staggerIndex + 1 >= codes.Count)
            {
                Log.Error("[RimKata] Could not place the dedicated melee parry branch.");
                return codes;
            }

            int staggerSkipIndex = staggerIndex + 1;
            if (codes[staggerSkipIndex].opcode == OpCodes.Pop)
            {
                staggerSkipIndex++;
            }

            if (staggerSkipIndex >= codes.Count)
            {
                Log.Error("[RimKata] Could not locate the post-stagger melee continuation.");
                return codes;
            }

            LocalBuilder parrySucceeded = generator.DeclareLocal(typeof(bool));
            Label continueHitLabel = generator.DefineLabel();
            Label attackerFollowupLabel = generator.DefineLabel();
            Label skipDefenderStaggerLabel = generator.DefineLabel();

            CodeInstruction hitStart = codes[dodgeBranchIndex + 1];
            hitStart.labels.Add(continueHitLabel);
            codes[playSoundIndex + 1].labels.Add(attackerFollowupLabel);
            codes[staggerSkipIndex].labels.Add(skipDefenderStaggerLabel);

            int staggerGuardIndex = notifyAttackIndex + 1;
            CodeInstruction staggerGuard = new CodeInstruction(
                OpCodes.Ldloc,
                parrySucceeded);
            staggerGuard.labels.AddRange(codes[staggerGuardIndex].labels);
            codes[staggerGuardIndex].labels.Clear();
            staggerGuard.blocks.AddRange(codes[staggerGuardIndex].blocks);
            codes[staggerGuardIndex].blocks.Clear();
            codes.InsertRange(
                staggerGuardIndex,
                new[]
                {
                    staggerGuard,
                    new CodeInstruction(
                        OpCodes.Brtrue,
                        skipDefenderStaggerLabel)
                });

            int parryInsertIndex = dodgeBranchIndex + 1;
            codes.InsertRange(
                parryInsertIndex,
                new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(
                        OpCodes.Call,
                        TryResolveMeleeParryMethod),
                    new CodeInstruction(
                        OpCodes.Brfalse,
                        continueHitLabel),
                    new CodeInstruction(OpCodes.Ldc_I4_0),
                    new CodeInstruction(OpCodes.Stloc_0),
                    new CodeInstruction(OpCodes.Ldc_I4_1),
                    new CodeInstruction(OpCodes.Stloc, parrySucceeded),
                    new CodeInstruction(
                        OpCodes.Br,
                        attackerFollowupLabel)
                });

            return codes;
        }

        public static void Postfix(Verb_MeleeAttack __instance)
        {
            Pawn defender = __instance?.CurrentTarget.Pawn;
            // This attack may already have stunned or disarmed the defender.
            if (defender == null
                || defender.Drafted != true
                || !RimKataEligibility.HasRimKataAccess(defender)
                || defender.mindState?.meleeThreat == null)
            {
                return;
            }

            defender.Map?.GetComponent<RimKataMapComponent>()
                ?.ScheduleDraftedMeleeThreatClear(defender);
        }

        public static Exception Finalizer(
            Exception __exception,
            MeleeAttackContextState __state)
        {
            if (__state.active)
            {
                RimKataDefenseUtility.ExitDamageStaggerContext(__state.damageStaggerContext);
            }

            return __exception;
        }
    }

    [HarmonyPatch(typeof(Verb_MeleeAttack), "GetDodgeChance")]
    public static class Patch_Verb_MeleeAttack_RimKataDefense
    {
        private static readonly MethodInfo IsMeleeAttackGetter = AccessTools.PropertyGetter(
            typeof(VerbProperties), nameof(VerbProperties.IsMeleeAttack));
        private static readonly MethodInfo StanceAllowsMeleeDodgeMethod = AccessTools.Method(
            typeof(Patch_Verb_MeleeAttack_RimKataDefense), nameof(StanceAllowsMeleeDodge));
        private static readonly MethodInfo AddMeleeDodgeBonusMethod = AccessTools.Method(
            typeof(RimKataCombatMath),
            nameof(RimKataCombatMath.AddConfiguredMeleeDodgeBonusToVanilla));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            for (int i = 0; i < codes.Count; i++)
            {
                if (!codes[i].Calls(IsMeleeAttackGetter))
                {
                    continue;
                }

                // Preserve every other native early return (surprise attacks,
                // immobile targets, etc.) and change only the ranged-stance gate.
                CodeInstruction loadDodgeTarget = new CodeInstruction(OpCodes.Ldarg_1);
                loadDodgeTarget.labels.AddRange(codes[i].labels);
                loadDodgeTarget.blocks.AddRange(codes[i].blocks);
                codes[i].labels.Clear();
                codes[i].blocks.Clear();
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = StanceAllowsMeleeDodgeMethod;
                codes.Insert(i, loadDodgeTarget);
                break;
            }
            int finalReturn = -1;
            for (int i = codes.Count - 1; i >= 0; i--)
            {
                if (codes[i].opcode == OpCodes.Ret)
                {
                    finalReturn = i;
                    break;
                }
            }

            if (finalReturn < 0)
            {
                Log.Error("[RimKata] Could not place the melee dodge bonus hook.");
                return codes;
            }

            CodeInstruction finalRet = codes[finalReturn];
            CodeInstruction loadTarget = new CodeInstruction(OpCodes.Ldarg_1);
            loadTarget.labels.AddRange(finalRet.labels);
            finalRet.labels.Clear();
            loadTarget.blocks.AddRange(finalRet.blocks);
            finalRet.blocks.Clear();
            codes.Insert(finalReturn, loadTarget);
            codes.Insert(finalReturn + 1, new CodeInstruction(OpCodes.Call, AddMeleeDodgeBonusMethod));
            return codes;
        }

        private static bool StanceAllowsMeleeDodge(VerbProperties props, LocalTargetInfo target)
        {
            if (props.IsMeleeAttack)
            {
                return true;
            }

            Pawn pawn = target.Pawn;
            if (pawn == null || !RimKataEligibility.CanRollMeleeDodge(pawn))
            {
                return false;
            }

            // Native weapon warmup/cooldown can run without a RimKata weapon cycle.
            ThingWithComps aimingWeapon = (pawn.stances?.curStance as Stance_Busy)
                ?.verb?.EquipmentSource as ThingWithComps;
            return (aimingWeapon?.def?.IsRangedWeapon == true
                    && !aimingWeapon.Destroyed
                    && pawn.equipment?.AllEquipmentListForReading?.Contains(aimingWeapon) == true)
                || pawn.stances?.curStance is Stance_RimKataAim
                || RimKataDualWeaponController.IsCombatActiveForPortrait(pawn, pawn.CurJobDef);
        }
    }

    [HarmonyPatch(typeof(StaggerHandler), nameof(StaggerHandler.StaggerFor))]
    public static class Patch_StaggerHandler_RimKataMeleeDefense
    {
        public static void Postfix(
            StaggerHandler __instance,
            bool __result)
        {
            if (__result)
            {
                RimKataDefenseUtility.NotifyAppliedDamageStagger(__instance.parent);
            }
        }
    }
}
