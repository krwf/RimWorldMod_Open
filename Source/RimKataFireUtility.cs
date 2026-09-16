using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    public struct RimKataAutomaticCastSuppressionState
    {
        internal bool pushed;
        internal Pawn previousPawn;
        internal int previousDepth;
    }

    public static class RimKataAutomaticCastSuppression
    {
        [ThreadStatic] private static Pawn activePawn;
        [ThreadStatic] private static int depth;

        public static bool ActiveFor(Pawn pawn)
        {
            return pawn != null && depth > 0 && activePawn == pawn;
        }

        public static RimKataAutomaticCastSuppressionState Push(Pawn pawn)
        {
            RimKataAutomaticCastSuppressionState state =
                new RimKataAutomaticCastSuppressionState
                {
                    pushed = pawn != null,
                    previousPawn = activePawn,
                    previousDepth = depth
                };
            if (pawn != null)
            {
                activePawn = pawn;
                depth++;
            }

            return state;
        }

        public static void Pop(RimKataAutomaticCastSuppressionState state)
        {
            if (!state.pushed)
            {
                return;
            }

            activePawn = state.previousPawn;
            depth = state.previousDepth;
        }
    }

    public enum RimKataCloseDefensePrecheck
    {
        None,
        FirstDodgeSucceeded,
        FirstDodgeAndResponseFailed,
        ResponseSucceeded,
        ResponseSucceededWithAccidentalShot
    }

    public static class RimKataInterceptionShotRegistry
    {
        public static void Register(
            Projectile shot,
            Projectile targetProjectile)
        {
            if (shot == null
                || targetProjectile == null
                || shot.Map == null)
            {
                return;
            }

            shot.Map.GetComponent<RimKataMapComponent>()?
                .RegisterInterceptionShot(shot, targetProjectile);
        }

        public static bool TryResolve(
            Projectile shot,
            ref Thing hitThing,
            bool blockedByShield)
        {
            RimKataMapComponent component =
                shot?.Map?.GetComponent<RimKataMapComponent>();
            if (component == null
                || !component.TryTakeInterceptionTarget(
                    shot,
                    out Projectile target))
            {
                return false;
            }

            if (blockedByShield || hitThing != target)
            {
                return false;
            }

            Pawn shooter = shot.Launcher as Pawn;
            if (shooter?.Map == null
                || target?.Map != shooter.Map
                || !RimKataTargeting.IsInterceptionTargetActive(target)
                || !RimKataInterceptionTrajectory.TryGetContact(shot, target, out Vector3 contact))
            {
                // Vanilla accepts a usedTarget Thing at any distance. Keep the
                // shot's normal ground impact, not damage to that remote Thing.
                hitThing = null;
                return false;
            }

            RimKataInterceptionTrajectory.PlaceAtContact(shot, contact);
            return RimKataInterceptionUtility.Resolve(shooter, target, contact);
        }
    }

    public static class RimKataFireContext
    {
        public struct ScopeState
        {
            private Verb activeVerb;
            private Pawn shooter;
            private Thing closeTarget;
            private bool closeShot;
            private bool closeMeleeResolution;
            private bool closeMeleeHit;
            private bool interceptionShot;
            private Projectile interceptionTarget;
            private float movingAccuracyMultiplier;
            private float interceptionAccuracyBonusMultiplier;
            private float serumInterceptionMultiplier;
            private bool shotFired;
            private bool suppressCloseLaunch;
            private RimKataCloseDefensePrecheck closeDefensePrecheck;
            private RimKataDefenseUtility.CloseAttackResolutionState defenseState;

            internal static ScopeState Capture()
            {
                return new ScopeState
                {
                    activeVerb = ActiveVerb,
                    shooter = Shooter,
                    closeTarget = CloseTarget,
                    closeShot = CloseShot,
                    closeMeleeResolution = CloseMeleeResolution,
                    closeMeleeHit = CloseMeleeHit,
                    interceptionShot = InterceptionShot,
                    interceptionTarget = InterceptionTarget,
                    movingAccuracyMultiplier = MovingAccuracyMultiplier,
                    interceptionAccuracyBonusMultiplier = InterceptionAccuracyBonusMultiplier,
                    serumInterceptionMultiplier = SerumInterceptionMultiplier,
                    shotFired = ShotFired,
                    suppressCloseLaunch = SuppressCloseLaunch,
                    closeDefensePrecheck = CloseDefensePrecheck,
                    defenseState = RimKataDefenseUtility.PushCloseAttackResolution()
                };
            }

            internal void Restore()
            {
                ActiveVerb = activeVerb;
                Shooter = shooter;
                CloseTarget = closeTarget;
                CloseShot = closeShot;
                CloseMeleeResolution = closeMeleeResolution;
                CloseMeleeHit = closeMeleeHit;
                InterceptionShot = interceptionShot;
                InterceptionTarget = interceptionTarget;
                MovingAccuracyMultiplier = movingAccuracyMultiplier;
                InterceptionAccuracyBonusMultiplier = interceptionAccuracyBonusMultiplier;
                SerumInterceptionMultiplier = serumInterceptionMultiplier;
                ShotFired = shotFired;
                SuppressCloseLaunch = suppressCloseLaunch;
                CloseDefensePrecheck = closeDefensePrecheck;
                RimKataDefenseUtility.PopCloseAttackResolution(defenseState);
            }
        }

        [ThreadStatic] public static Verb ActiveVerb;
        [ThreadStatic] public static Pawn Shooter;
        [ThreadStatic] public static Thing CloseTarget;
        [ThreadStatic] public static bool CloseShot;
        [ThreadStatic] public static bool CloseMeleeResolution;
        [ThreadStatic] public static bool CloseMeleeHit;
        [ThreadStatic] public static bool InterceptionShot;
        [ThreadStatic] public static Projectile InterceptionTarget;
        [ThreadStatic] public static float MovingAccuracyMultiplier;
        [ThreadStatic] public static float InterceptionAccuracyBonusMultiplier;
        [ThreadStatic] public static float SerumInterceptionMultiplier;
        [ThreadStatic] public static bool ShotFired;
        [ThreadStatic] public static bool SuppressCloseLaunch;
        [ThreadStatic] public static RimKataCloseDefensePrecheck CloseDefensePrecheck;

        public static ScopeState Begin(
            Verb verb,
            Pawn shooter,
            Thing closeTarget,
            bool movingShot,
            bool closeShot,
            bool interceptionShot,
            Projectile interceptionTarget,
            bool closeMeleeResolution,
            bool closeMeleeHit,
            RimKataCloseDefensePrecheck closeDefensePrecheck)
        {
            RimKataSettings settings = RimKataTargetAccess.SettingsFor(shooter);
            float nextMovingAccuracyMultiplier = movingShot
                ? settings?.GetMovingAccuracyMultiplier(shooter) ?? 1f
                : 1f;
            float nextInterceptionAccuracyBonusMultiplier = interceptionShot
                ? settings?.GetInterceptionAccuracyBonusMultiplier(shooter) ?? 1f
                : 1f;
            float nextSerumInterceptionMultiplier = interceptionShot
                && settings != null
                && RimKataSerumUtility.IsMindNumbed(shooter)
                    ? settings.GetSerumInterceptionMultiplier(shooter)
                    : 1f;
            ScopeState previous = ScopeState.Capture();
            ActiveVerb = verb;
            Shooter = shooter;
            CloseTarget = closeTarget;
            CloseShot = closeShot;
            CloseMeleeResolution = closeMeleeResolution;
            CloseMeleeHit = closeMeleeHit;
            InterceptionShot = interceptionShot;
            InterceptionTarget = interceptionTarget;
            MovingAccuracyMultiplier = nextMovingAccuracyMultiplier;
            InterceptionAccuracyBonusMultiplier = nextInterceptionAccuracyBonusMultiplier;
            SerumInterceptionMultiplier = nextSerumInterceptionMultiplier;
            ShotFired = false;
            SuppressCloseLaunch = false;
            CloseDefensePrecheck = closeDefensePrecheck;
            return previous;
        }

        public static void End(Verb ownerVerb, ScopeState previous)
        {
            if (ownerVerb == null || ActiveVerb != ownerVerb)
            {
                return;
            }

            previous.Restore();
        }
    }

    public static class RimKataProjectileImpactContext
    {
        private struct ImpactScope
        {
            public Projectile previousProjectile;
            public RimKataCloseProjectileState previousCloseShot;
            public RimKataMapComponent component;
            public bool ownsDefenseScope;
        }

        [ThreadStatic] private static Stack<ImpactScope> projectileStack;
        [ThreadStatic] public static Projectile CurrentProjectile;
        [ThreadStatic] internal static RimKataCloseProjectileState CurrentCloseShot;

        public static void Enter(Projectile projectile)
        {
            projectileStack ??= new Stack<ImpactScope>();
            bool ownsDefenseScope = projectile != CurrentProjectile;
            RimKataMapComponent component = ownsDefenseScope
                ? projectile?.Map?.GetComponent<RimKataMapComponent>()
                : null;
            projectileStack.Push(new ImpactScope
            {
                previousProjectile = CurrentProjectile,
                previousCloseShot = CurrentCloseShot,
                component = component,
                ownsDefenseScope = ownsDefenseScope
            });
            CurrentProjectile = projectile;
            if (ownsDefenseScope)
            {
                CurrentCloseShot = component?.CloseShotFor(projectile);
                RimKataDefenseUtility.EnterProjectileImpact();
            }
        }

        public static void Exit()
        {
            Projectile exitingProjectile = CurrentProjectile;
            ImpactScope scope = projectileStack != null
                && projectileStack.Count > 0
                    ? projectileStack.Pop()
                    : default(ImpactScope);
            CurrentProjectile = scope.previousProjectile;
            CurrentCloseShot = scope.previousCloseShot;
            if (scope.ownsDefenseScope)
            {
                RimKataDefenseUtility.ExitProjectileImpact();
            }

            // Projectile.Impact can be entered once by an override and again
            // by its base implementation.  Keep the tracked result until the
            // outermost scope for that projectile has completed; the
            // projectile may already be despawned before damage is applied.
            if (exitingProjectile != null
                && exitingProjectile != CurrentProjectile)
            {
                scope.component?.NotifyRangedProjectileImpactFinished(exitingProjectile);
            }
        }
    }

    public static class RimKataVerbUtility
    {
        private delegate bool CausesTimeSlowdownDelegate(
            Verb verb,
            LocalTargetInfo target);

        private static readonly CausesTimeSlowdownDelegate CausesTimeSlowdown =
            ResolveCausesTimeSlowdown();
        private static TickManager lastNormalSpeedSignalManager;
        private static int lastNormalSpeedSignalTick = -1;

        private static CausesTimeSlowdownDelegate ResolveCausesTimeSlowdown()
        {
            MethodInfo method = AccessTools.Method(
                typeof(Verb),
                "CausesTimeSlowdown",
                new[] { typeof(LocalTargetInfo) });
            if (method == null)
            {
                Log.Warning(
                    "[RimKata] Could not resolve Verb.CausesTimeSlowdown; RimKata attacks will not request vanilla combat speed.");
                return null;
            }

            try
            {
                return AccessTools.MethodDelegate<CausesTimeSlowdownDelegate>(
                    method,
                    null,
                    false,
                    null);
            }
            catch (Exception exception)
            {
                Log.Warning(
                    "[RimKata] Could not bind Verb.CausesTimeSlowdown; RimKata attacks will not request vanilla combat speed. "
                    + exception.Message);
                return null;
            }
        }

        internal static void RequestNormalSpeedForCombat(
            Verb verb,
            LocalTargetInfo target)
        {
            TickManager tickManager = Find.TickManager;
            if (verb == null
                || tickManager == null
                || CausesTimeSlowdown == null
                || (ReferenceEquals(
                        lastNormalSpeedSignalManager,
                        tickManager)
                    && lastNormalSpeedSignalTick == tickManager.TicksGame))
            {
                return;
            }

            if (!CausesTimeSlowdown(verb, target))
            {
                return;
            }

            TimeSlower slower = tickManager.slower;
            if (slower == null)
            {
                return;
            }

            slower.SignalForceNormalSpeed();
            lastNormalSpeedSignalManager = tickManager;
            lastNormalSpeedSignalTick = tickManager.TicksGame;
        }

    }

    public static class RimKataProjectileUtility
    {
        private static readonly FieldInfo DestinationField = AccessTools.Field(typeof(Projectile), "destination");
        private static readonly FieldInfo TicksToImpactField = AccessTools.Field(typeof(Projectile), "ticksToImpact");
        private static readonly FieldInfo LifetimeField = AccessTools.Field(typeof(Projectile), "lifetime");
        private static readonly FieldInfo LandedField = AccessTools.Field(typeof(Projectile), "landed");
        private static readonly Dictionary<Type, MethodInfo> ImpactMethods = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> ExplodeMethods = new Dictionary<Type, MethodInfo>();

        public static void Impact(Projectile projectile, Thing hitThing, bool blockedByShield = false)
        {
            if (projectile == null || projectile.Destroyed)
            {
                return;
            }

            MethodInfo method = MethodFor(ImpactMethods, projectile.GetType(), "Impact", typeof(Thing), typeof(bool));
            method?.Invoke(projectile, new object[] { hitThing, blockedByShield });
        }

        public static void DetonateNow(Projectile projectile)
        {
            if (projectile == null || projectile.Destroyed)
            {
                return;
            }

            MethodInfo method = MethodFor(ExplodeMethods, projectile.GetType(), "Explode");
            if (method != null)
            {
                method.Invoke(projectile, null);
            }
            else
            {
                Impact(projectile, null);
            }
        }

        public static bool PrepareImmediateImpact(Projectile projectile, IntVec3 cell)
        {
            if (projectile == null
                || projectile.Destroyed
                || projectile.Map == null
                || !cell.InBounds(projectile.Map))
            {
                return false;
            }

            projectile.Position = cell;
            DestinationField.SetValue(projectile, cell.ToVector3Shifted());
            TicksToImpactField.SetValue(projectile, 0);
            LifetimeField.SetValue(projectile, 0);
            LandedField.SetValue(projectile, false);
            return true;
        }

        public static IntVec3 FindCloseMissCell(Pawn shooter, Thing target, Map map)
        {
            if (target == null || map == null)
            {
                return IntVec3.Invalid;
            }

            int start = Rand.Range(0, GenAdj.AdjacentCells.Length);
            for (int i = 0; i < GenAdj.AdjacentCells.Length; i++)
            {
                IntVec3 cell = target.Position + GenAdj.AdjacentCells[(start + i) % GenAdj.AdjacentCells.Length];
                if (cell.InBounds(map)
                    && cell != target.Position
                    && (shooter == null || cell != shooter.Position))
                {
                    return cell;
                }
            }

            return target.Position;
        }

        public static void SpawnDeflectedMiss(Projectile source, Pawn attacker, Pawn defender, Verb sourceVerb)
        {
            ThingDef sourceDef = source?.def;
            if (sourceDef == null || attacker?.Map == null || defender?.Map != attacker.Map)
            {
                return;
            }

            Map map = attacker.Map;
            IntVec3 missCell = FindMissCell(attacker.Position, defender.Position, map);
            if (!missCell.IsValid || missCell == defender.Position)
            {
                return;
            }

            Projectile redirected = ThingMaker.MakeThing(sourceDef) as Projectile;
            if (redirected == null)
            {
                return;
            }

            GenSpawn.Spawn(redirected, attacker.Position, map);
            redirected.damageDefOverride = source.damageDefOverride;
            if (source.extraDamages != null)
            {
                redirected.extraDamages = new List<ExtraDamage>(source.extraDamages);
            }

            ProjectileHitFlags flags = ProjectileHitFlags.NonTargetPawns | ProjectileHitFlags.NonTargetWorld;
            bool previousSuppression = RimKataFireContext.SuppressCloseLaunch;
            RimKataFireContext.SuppressCloseLaunch = true;
            try
            {
                redirected.Launch(attacker, attacker.DrawPos, missCell, defender, flags, false, sourceVerb?.EquipmentSource);
                redirected.stoppingPower = source.stoppingPower;
            }
            finally
            {
                RimKataFireContext.SuppressCloseLaunch = previousSuppression;
            }
        }

        private static IntVec3 FindMissCell(IntVec3 attacker, IntVec3 defender, Map map)
        {
            Vector3 forward = (defender - attacker).ToVector3();
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
            {
                forward = Vector3.forward;
            }

            forward.Normalize();
            Vector3 side = new Vector3(-forward.z, 0f, forward.x) * (Rand.Bool ? 2f : -2f);
            for (int distance = 8; distance >= 2; distance--)
            {
                IntVec3 cell = (defender.ToVector3Shifted() + forward * distance + side).ToIntVec3();
                if (cell.InBounds(map))
                {
                    return cell;
                }
            }

            return IntVec3.Invalid;
        }

        private static MethodInfo MethodFor(Dictionary<Type, MethodInfo> cache, Type type, string name, params Type[] parameters)
        {
            if (!cache.TryGetValue(type, out MethodInfo method))
            {
                Type current = type;
                while (current != null && method == null)
                {
                    method = current.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, parameters, null);
                    current = current.BaseType;
                }

                cache[type] = method;
            }

            return method;
        }
    }

    [HarmonyPatch]
    public static class Patch_Verb_TryCastShot_RimKata
    {
        [ThreadStatic] internal static Verb CurrentVerb;

        public static IEnumerable<MethodBase> TargetMethods()
        {
            HashSet<MethodBase> methods = new HashSet<MethodBase>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                {
                    continue;
                }

                IEnumerable<Type> types;
                try
                {
                    types = AccessTools.GetTypesFromAssembly(assembly);
                }
                catch
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (typeof(Verb).IsAssignableFrom(type))
                    {
                        MethodInfo method = type.GetMethod(
                            "TryCastShot",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null,
                            Type.EmptyTypes,
                            null);
                        if (method != null
                            && !method.IsAbstract
                            && method.ReturnType == typeof(bool)
                            && methods.Add(method))
                        {
                            yield return method;
                        }
                    }
                }
            }
        }

        public static void Prefix(Verb __instance, out Verb __state)
        {
            __state = CurrentVerb;
            CurrentVerb = __instance;
        }

        public static void Finalizer(Verb __state)
        {
            CurrentVerb = __state;
        }

        public static void Postfix(Verb __instance, bool __result)
        {
            if (RimKataFireContext.ActiveVerb == __instance
                && (__result || __instance.IsMeleeAttack))
            {
                RimKataFireContext.ShotFired = true;
            }
        }
    }

    internal struct RimKataNativeCastScope
    {
        internal RimKataNativeAttack request;
        internal bool convertedOpening;
        internal bool ownsWarmup;
        internal Verb previousWarmup;
    }

    [HarmonyPatch]
    public static class Patch_Verb_WarmupComplete_RimKataOpeningSingleShot
    {
        [ThreadStatic] private static Verb activeWarmup;
        public static IEnumerable<MethodBase> TargetMethods()
        {
            HashSet<MethodBase> methods = new HashSet<MethodBase>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                {
                    continue;
                }

                IEnumerable<Type> types;
                try
                {
                    types = AccessTools.GetTypesFromAssembly(assembly);
                }
                catch
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type == null
                        || !typeof(Verb).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    MethodInfo method = type.GetMethod(
                        nameof(Verb.WarmupComplete),
                        BindingFlags.Instance
                            | BindingFlags.Public
                            | BindingFlags.NonPublic
                            | BindingFlags.DeclaredOnly,
                        null,
                        Type.EmptyTypes,
                        null);
                    if (method != null
                        && !method.IsAbstract
                        && !method.ContainsGenericParameters
                        && method.ReturnType == typeof(void)
                        && methods.Add(method))
                    {
                        yield return method;
                    }
                }
            }
        }

        internal static void Prefix(
            Verb __instance,
            out RimKataNativeCastScope __state)
        {
            __state = default;
            if (activeWarmup == __instance) return;
            __state.ownsWarmup = true;
            __state.previousWarmup = activeWarmup;
            activeWarmup = __instance;
            __state.request = RimKataNativeAttack.BeginNativeCast(__instance);
            if (__state.request != null || RimKataFireContext.ActiveVerb == __instance)
                return;
            if (RimKataDualWeaponController.ShouldConvertVanillaOpeningToSingleShot(__instance))
            {
                if (!RimKataPreparedWeaponData.IsCurrent(__instance))
                {
                    RimKataPreparedWeaponData.Bind(__instance);
                }
                __state.convertedOpening = true;
            }
        }

        internal static void Postfix(Verb __instance, RimKataNativeCastScope __state)
        {
            if (__state.convertedOpening
                && __instance.CasterPawn?.stances?.curStance is Stance_Cooldown cooldown
                && cooldown.verb == __instance)
            {
                cooldown.ticksLeft = RimKataCombatMath.CooldownTicksForSingleShot(
                    __instance, __instance.CasterPawn, false);
            }
        }

        internal static Exception Finalizer(
            Verb __instance, Exception __exception, RimKataNativeCastScope __state)
        {
            try
            {
                if (__state.request != null)
                    __state.request.FinishNativeCast(__exception);
            }
            finally
            {
                if (__state.ownsWarmup) activeWarmup = __state.previousWarmup;
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(VerbProperties), nameof(VerbProperties.AdjustedFullCycleTime))]
    public static class Patch_VerbProperties_RimKataShootingExperience
    {
        public static void Postfix(Verb ownerVerb, ref float __result)
        {
            RimKataPreparedWeaponData.AdjustShootingExperienceCycleTime(ownerVerb, ref __result);
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.TryFindShootLineFromTo))]
    public static class Patch_Verb_CloseRimKataShootLine
    {
        public static bool Prefix(
            Verb __instance,
            IntVec3 root,
            LocalTargetInfo targ,
            ref ShootLine resultingLine,
            ref bool __result)
        {
            Thing target = RimKataFireContext.CloseTarget;
            if (!RimKataFireContext.CloseShot
                || RimKataFireContext.ActiveVerb != __instance
                || target == null
                || !target.Spawned)
            {
                return true;
            }

            resultingLine = new ShootLine(root, targ.Cell);
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.AimOnTargetChance_IgnoringPosture), MethodType.Getter)]
    public static class Patch_ShotReport_MovingAccuracy_RimKata
    {
        public static void Postfix(ref float __result)
        {
            if (RimKataFireContext.ActiveVerb != null && RimKataFireContext.CloseMeleeResolution)
            {
                __result = RimKataFireContext.CloseMeleeHit ? 1f : 0f;
                return;
            }

            if (RimKataFireContext.ActiveVerb != null)
            {
                __result = Mathf.Clamp01(
                    __result
                    * RimKataFireContext.InterceptionAccuracyBonusMultiplier
                    * RimKataFireContext.MovingAccuracyMultiplier
                    * RimKataFireContext.SerumInterceptionMultiplier);
            }
        }
    }

    [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.PassCoverChance), MethodType.Getter)]
    public static class Patch_ShotReport_MovingCoverRoll_RimKata
    {
        public static void Postfix(ref float __result)
        {
            if (RimKataFireContext.ActiveVerb != null && RimKataFireContext.CloseMeleeResolution)
            {
                __result = 1f;
            }
        }
    }

    [HarmonyPatch(typeof(VerbProperties), nameof(VerbProperties.ForcedMissRadius), MethodType.Getter)]
    public static class Patch_VerbProperties_CloseMeleeForcedMiss_RimKata
    {
        public static void Postfix(VerbProperties __instance, ref float __result)
        {
            if (RimKataFireContext.ActiveVerb?.verbProps == __instance && RimKataFireContext.CloseMeleeResolution)
            {
                __result = 0f;
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_Projectile_Launch_CloseRimKata
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(Projectile),
                nameof(Projectile.Launch),
                new[]
                {
                    typeof(Thing), 
                    typeof(Vector3), 
                    typeof(LocalTargetInfo), 
                    typeof(LocalTargetInfo),
                    typeof(ProjectileHitFlags), 
                    typeof(bool), typeof(Thing), 
                    typeof(ThingDef)
                });
        }

        public static void Prefix(
            Thing launcher,
            Thing equipment,
            ref LocalTargetInfo usedTarget,
            LocalTargetInfo intendedTarget,
            ref ProjectileHitFlags hitFlags,
            ref ThingDef targetCoverDef)
        {
            if (launcher != RimKataFireContext.Shooter
                || equipment != RimKataFireContext.ActiveVerb?.EquipmentSource
                || !RimKataFireContext.CloseShot
                || !RimKataFireContext.CloseMeleeResolution
                || RimKataFireContext.InterceptionShot
                || RimKataFireContext.SuppressCloseLaunch
                || !IntendedForCloseTarget(intendedTarget))
            {
                return;
            }

            Thing target = RimKataFireContext.CloseTarget;
            targetCoverDef = null;
            if (RimKataFireContext.CloseMeleeHit)
            {
                usedTarget = target;
                hitFlags |= ProjectileHitFlags.IntendedTarget;
            }
            else
            {
                usedTarget = new LocalTargetInfo(RimKataProjectileUtility.FindCloseMissCell(
                    RimKataFireContext.Shooter, target, launcher.Map));
                hitFlags &= ~ProjectileHitFlags.IntendedTarget;
            }
        }

        public static void Postfix(
            Projectile __instance,
            Thing launcher,
            Thing equipment,
            LocalTargetInfo usedTarget,
            LocalTargetInfo intendedTarget)
        {
            Map launchMap = __instance?.Map ?? launcher?.Map;
            RimKataMapComponent component =
                launchMap?.GetComponent<RimKataMapComponent>();
            component?.RegisterLaunchedExplosiveProjectile(__instance);
            component?.RegisterExplosiveDodge(__instance);

            bool ownedShot = launcher == RimKataFireContext.Shooter
                && RimKataFireContext.ActiveVerb != null
                && equipment == RimKataFireContext.ActiveVerb.EquipmentSource;
            if (ownedShot && RimKataFireContext.InterceptionShot)
            {
                Projectile interceptionTarget = RimKataFireContext.InterceptionTarget;
                if (!__instance.Destroyed
                    && interceptionTarget != null
                    && usedTarget.HasThing
                    && usedTarget.Thing == interceptionTarget
                    && intendedTarget.HasThing
                    && intendedTarget.Thing == interceptionTarget)
                {
                    // Only the vanilla hit branch reaches here. Use the actual
                    // spawned ammo/speed, and never reroll a miss or home in flight.
                    if (RimKataInterceptionTrajectory.TryRedirectHit(
                        __instance, interceptionTarget,
                        RimKataFireContext.Shooter, RimKataFireContext.ActiveVerb))
                    {
                        RimKataInterceptionShotRegistry.Register(
                            __instance,
                            interceptionTarget);
                    }
                    else
                    {
                        RimKataInterceptionTrajectory.ReleaseUnreachableHit(__instance);
                    }
                }

                return;
            }

            if (ownedShot
                && RimKataFireContext.CloseShot
                && !RimKataFireContext.SuppressCloseLaunch
                && IntendedForCloseTarget(intendedTarget)
                && !__instance.Destroyed)
            {
                component?.RegisterCloseProjectile(new RimKataCloseProjectileState
                {
                    projectile = __instance,
                    target = RimKataFireContext.CloseTarget,
                    attackingVerb = RimKataFireContext.ActiveVerb,
                    meleeResolution = RimKataFireContext.CloseMeleeResolution,
                    meleeHit = RimKataFireContext.CloseMeleeHit,
                    precheck = RimKataFireContext.CloseDefensePrecheck
                });
                return;
            }

            if (RegisterIncomingCloseShot(
                    component, __instance, launcher, usedTarget, intendedTarget))
            {
                return;
            }

            Pawn usedPawn = usedTarget.Pawn;
            Pawn intendedPawn = intendedTarget.Pawn;
            if (usedPawn != null
                && usedPawn == intendedPawn
                && IsTrackableOrdinaryRangedProjectile(__instance)
                && RimKataEligibility.HasRimKataAccess(usedPawn)
                && launcher != usedPawn
                && (launcher?.Faction == null
                    || usedPawn.Faction == null
                    || launcher.Faction != usedPawn.Faction))
            {
                component?.RegisterLaunchedRangedProjectile(__instance, usedPawn);
            }
        }

        private static bool RegisterIncomingCloseShot(
            RimKataMapComponent component,
            Projectile projectile,
            Thing launcher,
            LocalTargetInfo usedTarget,
            LocalTargetInfo intendedTarget)
        {
            Pawn defender = intendedTarget.Pawn;
            Pawn attacker = launcher as Pawn;
            // Classify Touch once, before flight. Ordinary distant shots never
            // enter the close-defense eligibility or reachability checks.
            if (component == null
                || RimKataFireContext.SuppressCloseLaunch
                || defender == null
                || attacker == null
                || attacker == defender
                || usedTarget.Pawn != defender
                || attacker.Map != defender.Map
                || !attacker.Position.AdjacentTo8WayOrInside(defender.Position)
                || !IsTrackableOrdinaryRangedProjectile(projectile)
                || !RimKataTargeting.IsAutomaticEnemy(defender, attacker)
                || !defender.CanReachImmediate(attacker, PathEndMode.Touch)
                || !RimKataEligibility.CanUseDefense(defender))
            {
                return false;
            }

            Verb verb = Patch_Verb_TryCastShot_RimKata.CurrentVerb;
            component.RegisterCloseProjectile(new RimKataCloseProjectileState
            {
                projectile = projectile,
                target = defender,
                attackingVerb = verb?.CasterPawn == attacker ? verb : null,
                meleeResolution = true,
                meleeHit = true
            });
            return true;
        }

        private static bool IsTrackableOrdinaryRangedProjectile(
            Projectile projectile)
        {
            if (projectile == null
                || projectile.Destroyed
                || projectile.def?.projectile == null
                || projectile.def.projectile.explosionRadius > 0f)
            {
                return false;
            }

            DamageDef damageDef = projectile.damageDefOverride
                ?? projectile.def.projectile.damageDef;
            return RimKataDefenseUtility.IsDirectHitBullet(projectile)
                || (damageDef?.isRanged == true && !damageDef.isExplosive);
        }

        private static bool IntendedForCloseTarget(LocalTargetInfo intendedTarget)
        {
            Thing closeTarget = RimKataFireContext.CloseTarget;
            if (closeTarget == null)
            {
                return false;
            }

            return intendedTarget.HasThing
                ? intendedTarget.Thing == closeTarget
                : intendedTarget.IsValid && intendedTarget.Cell == closeTarget.Position;
        }
    }

    [HarmonyPatch(typeof(Projectile), "CanHit")]
    public static class Patch_Projectile_CanHit_RimKataExplosiveMiss
    {
        public static bool Prefix(Projectile __instance, Thing __0, ref bool __result)
        {
            if (__0 is Pawn target
                && RimKataEligibilityCache.IsCachedQualifiedPawn(target)
                && __instance.def?.projectile?.alwaysFreeIntercept == true
                && __instance.Map?.GetComponent<RimKataMapComponent>()
                    ?.PreventsDirectProjectileHit(__instance, target) == true)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    public static class Patch_Projectile_Impact_Context
    {
        public static void Apply(Harmony harmony)
        {
            if (harmony == null)
            {
                throw new ArgumentNullException(nameof(harmony));
            }

            // Prefix rewrites the argument by ref; vanilla Impact stays by value.
            MethodInfo prefixMethod = AccessTools.Method(
                typeof(Patch_Projectile_Impact_Context),
                nameof(Prefix),
                new[] { typeof(Projectile), typeof(Thing).MakeByRefType(), typeof(bool) });
            MethodInfo finalizerMethod = AccessTools.Method(
                typeof(Patch_Projectile_Impact_Context),
                nameof(Finalizer),
                new[] { typeof(Exception) });
            if (prefixMethod == null || finalizerMethod == null)
            {
                throw new InvalidOperationException("Could not resolve Projectile.Impact context patch methods.");
            }

            foreach (MethodBase target in FindTargetMethods())
            {
                try
                {
                    Patches existing = Harmony.GetPatchInfo(target);
                    bool hasPrefix = HasPatch(existing?.Prefixes, harmony.Id, prefixMethod);
                    bool hasFinalizer = HasPatch(existing?.Finalizers, harmony.Id, finalizerMethod);
                    if (hasPrefix && hasFinalizer)
                    {
                        continue;
                    }

                    harmony.Patch(target, prefix: hasPrefix ? null : new HarmonyMethod(prefixMethod), finalizer: hasFinalizer ? null : new HarmonyMethod(finalizerMethod));
                }
                catch (Exception exception)
                {
                    Exception root = exception.GetBaseException();
                    string message = root.Message.NullOrEmpty()
                        ? "<no message>"
                        : root.Message.Replace('\r', ' ').Replace('\n', ' ');
                    Log.Warning("[RimKata] Skipped Projectile.Impact patch for " + (target.DeclaringType?.FullName ?? "<unknown>") + "::" + target.Name + ": " + root.GetType().Name + ": " + message);
                }
            }
        }

        private static IEnumerable<MethodBase> FindTargetMethods()
        {
            HashSet<MethodBase> methods = new HashSet<MethodBase>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                {
                    continue;
                }

                IEnumerable<Type> types;
                try
                {
                    types = AccessTools.GetTypesFromAssembly(assembly);
                }
                catch
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type == null || !typeof(Projectile).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    MethodInfo method;
                    try
                    {
                        method = type.GetMethod("Impact", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null,
                            new[] 
                            { 
                                typeof(Thing), 
                                typeof(bool) 
                            },
                            null);
                    }
                    catch
                    {
                        continue;
                    }

                    if (method != null && !method.IsAbstract && methods.Add(method))
                    {
                        yield return method;
                    }
                }
            }
        }

        private static bool HasPatch(
            IEnumerable<Patch> patches,
            string owner,
            MethodInfo patchMethod)
        {
            if (patches == null)
            {
                return false;
            }

            foreach (Patch patch in patches)
            {
                if (patch.owner == owner && patch.PatchMethod == patchMethod)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool Prefix(
            Projectile __instance,
            ref Thing __0,
            bool __1)
        {
            RimKataProjectileImpactContext.Enter(__instance);

            if (RimKataDefenseUtility.ExplosiveDodgeTarget(__instance) != null
                && __instance.Map?.GetComponent<RimKataMapComponent>()
                    ?.DeferImpactForExplosiveDodge(__instance, __0, __1) == true)
            {
                return false;
            }

            if (RimKataInterceptionShotRegistry.TryResolve(
                    __instance,
                    ref __0,
                    __1))
            {
                if (!__instance.Destroyed)
                {
                    // Keep the interceptor's own impact/fuse independent of
                    // the target projectile's interception result.
                    if (__instance.def?.projectile?.explosionRadius > 0f)
                    {
                        return __instance.Spawned && __instance.Map != null;
                    }

                    __instance.Destroy(DestroyMode.Vanish);
                }

                return false;
            }

            return true;
        }

        public static Exception Finalizer(Exception __exception)
        {
            RimKataProjectileImpactContext.Exit();
            return __exception;
        }
    }
}
