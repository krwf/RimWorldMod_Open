using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataBreachCombat
    {
        internal struct SubdueAccess
        {
            internal bool resolved;
            internal int version;
            internal RimKataSubdueState state;
        }

        internal struct AttackScope
        {
            internal bool pushed;
            internal Verb previousVerb;
            internal Pawn previousPawn;
            internal int previousVersion;
            internal bool previousAllowed;
            internal int previousReactiveVersion;
            internal bool previousReactiveChecked;
            internal bool previousReactiveAllowed;
            internal SubdueAccess previousSubdue;
        }

        [ThreadStatic] private static Verb attackVerb;
        [ThreadStatic] private static Pawn attackPawn;
        [ThreadStatic] private static int attackVersion;
        [ThreadStatic] private static bool attackAllowed;
        [ThreadStatic] private static int attackReactiveVersion;
        [ThreadStatic] private static bool attackReactiveChecked;
        [ThreadStatic] private static bool attackReactiveAllowed;
        [ThreadStatic] private static SubdueAccess attackSubdue;

        internal static bool TryDirectMiss(ref Thing hitThing)
        {
            if (!(hitThing is Pawn victim) || (!RimKataReactiveMotion.Protected(victim)
                && !RimKataBreachUtility.IsProtected(victim)))
                return false;

            // Avoiding the entire damage scope would also suppress nested explosion damage.
            hitThing = null;
            return true;
        }

        internal static void NotifyMeleeAttempt(Verb_MeleeAttack verb)
        {
            Pawn victim = verb?.CurrentTarget.Pawn;
            RimKataBreachState state = RimKataBreachUtility.Get(victim);
            if (state == null || !state.protectedState && state.phase == BreachPhase.Released) return;
            int version = RimKataBreachUtility.AttackStateVersion;
            Pawn attacker = verb.CasterPawn;
            if (attacker?.Spawned != true || attacker == victim
                || attacker.stances.FullBodyBusy || !verb.CanHitTarget(victim)) return;

            // A failed cast can mean no attack occurred; a resolved miss still counts.
            if (version != RimKataBreachUtility.AttackStateVersion)
                state = RimKataBreachUtility.Get(victim);
            RimKataBreachUtility.NotifyMeleeAttempt(victim, state);
        }

        internal static bool AllowsAttack(Verb verb)
        {
            if (verb is Verb_BeatFire) return true;
            Pawn pawn = verb?.CasterPawn;
            return pawn == null || AllowsAttack(verb, pawn, RimKataBreachUtility.AttackStateVersion,
                out _, out _, out _, out _);
        }

        private static bool AllowsAttack(Verb verb, Pawn pawn, int version,
            out int reactiveVersion, out bool reactiveChecked, out bool reactiveAllowed,
            out SubdueAccess subdue)
        {
            subdue = default;
            bool sameAttack = attackVerb == verb && attackPawn == pawn;
            reactiveVersion = RimKataReactiveMotion.AttackStateVersion;
            reactiveChecked = RimKataReactiveMotion.Any && !RimKataReactiveAttack.OwnsVerb(verb);
            reactiveAllowed = true;
            if (reactiveChecked)
            {
                if (sameAttack && attackReactiveChecked && attackReactiveVersion == reactiveVersion)
                    reactiveAllowed = attackReactiveAllowed;
                else
                {
                    RimKataReactiveMotionState motion = RimKataReactiveMotion.Participant(pawn);
                    reactiveAllowed = motion == null || !motion.BlocksCombat && !motion.HasPendingAttack;
                }
            }
            reactiveVersion = RimKataReactiveMotion.AttackStateVersion;
            if (sameAttack)
            {
                attackReactiveVersion = reactiveVersion;
                attackReactiveChecked = reactiveChecked;
                attackReactiveAllowed = reactiveAllowed;
            }
            if (!reactiveAllowed) return false;
            int relationVersion = RimKataSubdueUtility.RelationVersion;
            subdue = sameAttack && attackSubdue.resolved && attackSubdue.version == relationVersion
                ? attackSubdue
                : new SubdueAccess
                {
                    resolved = true, version = relationVersion,
                    state = RimKataSubdueUtility.Get(pawn)
                };
            if (sameAttack) attackSubdue = subdue;
            if (!RimKataSubdueCombat.AllowsAttack(verb, pawn, subdue.state)) return false;
            if (sameAttack && attackVersion == version)
                return attackAllowed;
            bool allowed = !RimKataBreachUtility.BlocksAttacks(pawn);
            if (sameAttack)
            {
                attackVersion = version;
                attackAllowed = allowed;
            }
            return allowed;
        }

        internal static bool BeginAttack(Verb verb, out AttackScope scope)
        {
            scope = default;
            if (verb is Verb_BeatFire) return true;
            var registry = RimKataBreachUtility.Registry;
            if ((registry == null || registry.states.Count == 0)
                && !RimKataSubdueUtility.Any && !RimKataReactiveMotion.Any) return true;
            Pawn pawn = verb?.CasterPawn;
            if (pawn == null) return true;
            int version = RimKataBreachUtility.AttackStateVersion;
            bool allowed = AllowsAttack(verb, pawn, version,
                out int reactiveVersion, out bool reactiveChecked, out bool reactiveAllowed,
                out SubdueAccess subdue);
            scope = new AttackScope
            {
                pushed = true, previousVerb = attackVerb, previousPawn = attackPawn,
                previousVersion = attackVersion, previousAllowed = attackAllowed,
                previousReactiveVersion = attackReactiveVersion,
                previousReactiveChecked = attackReactiveChecked,
                previousReactiveAllowed = attackReactiveAllowed,
                previousSubdue = attackSubdue
            };
            // WarmupComplete and nested shot overrides share this synchronous cast scope.
            attackVerb = verb;
            attackPawn = pawn;
            attackVersion = version;
            attackAllowed = allowed;
            attackReactiveVersion = reactiveVersion;
            attackReactiveChecked = reactiveChecked;
            attackReactiveAllowed = reactiveAllowed;
            attackSubdue = subdue;
            return allowed;
        }

        internal static void EndAttack(AttackScope scope)
        {
            if (!scope.pushed) return;
            attackVerb = scope.previousVerb;
            attackPawn = scope.previousPawn;
            attackVersion = scope.previousVersion;
            attackAllowed = scope.previousAllowed;
            attackReactiveVersion = scope.previousReactiveVersion;
            attackReactiveChecked = scope.previousReactiveChecked;
            attackReactiveAllowed = scope.previousReactiveAllowed;
            attackSubdue = scope.previousSubdue;
        }

        // Mod overrides can bypass the base attack method.
        internal static IEnumerable<MethodBase> VerbMethods(string name, Type returnType, Type[] signature)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic) continue;
                IEnumerable<Type> types;
                try { types = AccessTools.GetTypesFromAssembly(assembly); }
                catch { continue; }
                foreach (Type type in types)
                {
                    if (type == null || !typeof(Verb).IsAssignableFrom(type)) continue;
                    MethodInfo method = type.GetMethod(name,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                        null, signature, null);
                    if (method != null && !method.IsAbstract && !method.ContainsGenericParameters
                        && method.ReturnType == returnType && method.GetMethodBody() != null)
                        yield return method;
                }
            }
        }
    }

    [HarmonyPatch]
    internal static class Patch_VerbStartCast_RimKataBreach
    {
        private static bool Prepare(MethodBase original) => RimKataExternalPatchGuard.Prepare(original);

        private static Exception Cleanup(MethodBase original, Exception exception)
            => RimKataExternalPatchGuard.Cleanup(original, exception);

        private static IEnumerable<MethodBase> TargetMethods()
            => RimKataBreachCombat.VerbMethods(nameof(Verb.TryStartCastOn), typeof(bool),
                new[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(bool),
                    typeof(bool), typeof(bool), typeof(bool) });

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Verb __instance, ref bool __result,
            out RimKataBreachCombat.AttackScope __state)
        {
            if (RimKataBreachCombat.BeginAttack(__instance, out __state)) return true;
            __result = false;
            return false;
        }

        private static void Finalizer(RimKataBreachCombat.AttackScope __state)
            => RimKataBreachCombat.EndAttack(__state);
    }

    [HarmonyPatch]
    internal static class Patch_VerbWarmup_RimKataBreach
    {
        private static bool Prepare(MethodBase original) => RimKataExternalPatchGuard.Prepare(original);

        private static Exception Cleanup(MethodBase original, Exception exception)
            => RimKataExternalPatchGuard.Cleanup(original, exception);

        private static IEnumerable<MethodBase> TargetMethods()
            => RimKataBreachCombat.VerbMethods(nameof(Verb.WarmupComplete), typeof(void), Type.EmptyTypes);

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Verb __instance, out RimKataBreachCombat.AttackScope __state)
            => RimKataBreachCombat.BeginAttack(__instance, out __state);

        private static void Finalizer(RimKataBreachCombat.AttackScope __state)
            => RimKataBreachCombat.EndAttack(__state);
    }

    [HarmonyPatch(typeof(Verb), "TryCastNextBurstShot")]
    internal static class Patch_VerbBurstShot_RimKataBreach
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Verb __instance, out RimKataBreachCombat.AttackScope __state)
            => RimKataBreachCombat.BeginAttack(__instance, out __state);

        private static void Finalizer(RimKataBreachCombat.AttackScope __state)
            => RimKataBreachCombat.EndAttack(__state);
    }

    [HarmonyPatch]
    internal static class Patch_VerbShot_RimKataBreach
    {
        private static bool Prepare(MethodBase original) => RimKataExternalPatchGuard.Prepare(original);

        private static Exception Cleanup(MethodBase original, Exception exception)
            => RimKataExternalPatchGuard.Cleanup(original, exception);

        private static IEnumerable<MethodBase> TargetMethods()
            => RimKataBreachCombat.VerbMethods("TryCastShot", typeof(bool), Type.EmptyTypes);

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Verb __instance, ref bool __result,
            out RimKataBreachCombat.AttackScope __state)
        {
            if (RimKataBreachCombat.BeginAttack(__instance, out __state)) return true;
            __result = false;
            return false;
        }

        private static void Finalizer(RimKataBreachCombat.AttackScope __state)
            => RimKataBreachCombat.EndAttack(__state);
    }
}
