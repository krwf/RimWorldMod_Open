using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    // Breach protection and attack suspension have their own lifetime. They do
    // not borrow prone probabilities, fall conditions, or projectile tracking.
    internal static class RimKataBreachCombat
    {
        internal struct AttackScope
        {
            internal bool pushed;
            internal Verb previousVerb;
            internal Pawn previousPawn;
            internal int previousVersion;
            internal bool previousAllowed;
        }

        [ThreadStatic] private static Verb attackVerb;
        [ThreadStatic] private static Pawn attackPawn;
        [ThreadStatic] private static int attackVersion;
        [ThreadStatic] private static bool attackAllowed;

        internal static bool TryDirectMiss(ref Thing hitThing)
        {
            if (!(hitThing is Pawn victim) || !RimKataBreachUtility.IsProtected(victim))
                return false;

            // Keep the original Impact call, including any explosion it creates.
            // Do not mark its whole damage scope avoided: that would also absorb
            // an explosion hitting this pawn in the same nested impact scope.
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

            // This runs before the hit, dodge and parry rolls. A failed cast from
            // an invalid position is not an attack, but a valid miss still is.
            if (version != RimKataBreachUtility.AttackStateVersion)
                state = RimKataBreachUtility.Get(victim);
            RimKataBreachUtility.NotifyMeleeAttempt(victim, state);
        }

        internal static bool AllowsAttack(Verb verb)
        {
            if (verb is Verb_BeatFire) return true;
            Pawn pawn = verb?.CasterPawn;
            return pawn == null || AllowsAttack(verb, pawn, RimKataBreachUtility.AttackStateVersion);
        }

        private static bool AllowsAttack(Verb verb, Pawn pawn, int version)
        {
            if (!RimKataSubdueCombat.AllowsAttack(verb, pawn)) return false;
            if (attackVerb == verb && attackPawn == pawn && attackVersion == version)
                return attackAllowed;
            bool allowed = !RimKataBreachUtility.BlocksAttacks(pawn);
            if (attackVerb == verb && attackPawn == pawn)
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
            if ((registry == null || registry.states.Count == 0) && !RimKataSubdueUtility.Any) return true;
            Pawn pawn = verb?.CasterPawn;
            if (pawn == null) return true;
            int version = RimKataBreachUtility.AttackStateVersion;
            bool allowed = AllowsAttack(verb, pawn, version);
            scope = new AttackScope
            {
                pushed = true, previousVerb = attackVerb, previousPawn = attackPawn,
                previousVersion = attackVersion, previousAllowed = attackAllowed
            };
            // A synchronous cast may visit WarmupComplete, burst and override/base
            // shot methods. Share its decision, including a nonparticipant miss.
            // A later tick starts a new scope; participant events invalidate even
            // an in-flight decision before the next nested attack boundary.
            attackVerb = verb;
            attackPawn = pawn;
            attackVersion = version;
            attackAllowed = allowed;
            return allowed;
        }

        internal static void EndAttack(AttackScope scope)
        {
            if (!scope.pushed) return;
            attackVerb = scope.previousVerb;
            attackPawn = scope.previousPawn;
            attackVersion = scope.previousVersion;
            attackAllowed = scope.previousAllowed;
        }

        // Discover overrides once at patch installation. CE and other mods may
        // bypass the base method; ordinary play performs no type/assembly scan.
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
