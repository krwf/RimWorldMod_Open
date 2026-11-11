using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    // Installed only for Together. A visiting replica displays the host's RK states;
    // its unrelated home maps retain their normal simulation.
    internal static class RimKataMultiplayerTogetherAuthority
    {
        private static readonly List<(MethodBase target, MethodInfo patch)> installed = new();
        private static Func<Map, string, Pawn> originalFindPawn;

        internal static void Register(Harmony harmony)
        {
            try
            {
                foreach (Type type in typeof(RimKataMapComponent).Assembly.GetTypes())
                {
                    if (!typeof(MapComponent).IsAssignableFrom(type)) continue;
                    MethodInfo tick = AccessTools.DeclaredMethod(type, "MapComponentTick", Type.EmptyTypes);
                    if (tick != null) PatchTarget(tick, nameof(MapTick));
                }
                Patch(typeof(RimKataReactiveMotion), "Tick", nameof(ReactiveTick));
                Patch(typeof(RimKataReactiveMotion), "StaggerApplied", nameof(PawnEntry));
                Patch(typeof(RimKataKick), "StaggerApplied", nameof(ParticipantEntry));
                Patch(typeof(RimKataFlyingKick), "TryStart", nameof(ParticipantEntry));
                Patch(typeof(RimKataFlyingKick), "TryStartAfterPush", nameof(PawnEntry));
                Patch(typeof(RimKataFlyingKickApproach), "RegisterPath", nameof(ParticipantEntry));
                Patch(typeof(RimKataBreachUtility), "NotifyJobStarting", nameof(PawnEntry));
                Patch(typeof(RimKataBreachUtility), "TryCreate", nameof(PawnEntry));
                Patch(typeof(RimKataDefenseUtility), "NotifyAppliedDamageStagger", nameof(PawnEntry));
                Patch(typeof(RimKataAutoSubdue), "Request", nameof(PawnEntry));
                Patch(typeof(RimKataSecondaryHandRequirement), "NotifyMissingPartAdded", nameof(PawnEntry));
                Patch(typeof(RimKataWeaponSlotUtility), "RemoveInvalidSecondary", nameof(PawnEntry));
                PatchTarget(AccessTools.Method(typeof(RimKataDualWeaponController), "NotifyLoadoutChanged",
                    new[] { typeof(Pawn), typeof(RimKataPawnCombatState) }), nameof(PawnEntry));
                Patch(typeof(RimKataSubdueUtility), "Take", nameof(PawnEntry));
                Patch(typeof(RimKataSubdueUtility), "Remove", nameof(SubdueEntry));
                Patch(typeof(RimKataSubdueUtility), "Release", nameof(SubdueEntry));
                Patch(typeof(RimKataSubdueRegistry), "GameComponentTick", nameof(SubdueLoop), transpiler: true);
                foreach (string name in new[] { "NotifyAttackJob", "NotifyAimStarted", "NotifyMovement",
                    "NotifyAimCancelled", "NotifyJobChanged", "NotifyLoadoutChanged", "NotifyTargetMoved" })
                    Patch(typeof(RimKataGroundPoseUtility), name, nameof(PawnEntry));
                foreach (string name in new[] { "TryRangedDodge", "TryResolveDirectMeleeDefense",
                    "TryAbsorbAfterShield", "TryConsumeAvoidedProjectile", "TryAbsorbProjectileDamage",
                    "TryDodgeExplosiveProjectile" })
                    Patch(typeof(RimKataDefenseUtility), name, nameof(DefenseEntry));
                PatchTarget(AccessTools.Method(typeof(RimKataDefenseUtility), "TryResolveMeleeParry", new[] { typeof(Verb_MeleeAttack) }), nameof(MeleeDefense));
                Patch(typeof(RimKataDefenseUtility), "TryResolveCombatExtendedMeleeDefense", nameof(MeleeDefense));
                foreach (string name in new[] { "CanRollMeleeDodge", "CanUseProjectileInterception" })
                    Patch(typeof(RimKataEligibility), name, nameof(PawnEntry));
                foreach (Type type in new[] { typeof(RimKataPushMotion), typeof(RimKataThrownPawn) })
                    Patch(type, "TickInterval", nameof(ThingTick));
                Patch(typeof(JobDriver), "DriverTick", nameof(JobTick));
                Patch(typeof(JobDriver), "DriverTickInterval", nameof(JobTick));
                Patch(typeof(JobDriver), "ReadyForNextToil", nameof(JobTick));
                Patch(typeof(Pawn_MeleeVerbs), "TryMeleeAttack", nameof(MeleeAttack));
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(typeof(Verb)))
                    if (method.Name == "TryStartCastOn" || method.Name == "TryCastNextBurstShot")
                        PatchTarget(method, nameof(VerbAttack));

                // Keep Together's original health policy and echo. Only repair its
                // spawned-Map/FindPawn assumptions for actual RK-held victims.
                Type hediffs = RimKataActiveModTypes.Find("RTClient.PacketManagers.Synchronous.PM_SHediff")
                    ?? throw new TypeLoadException("Together PM_SHediff");
                Type finder = RimKataActiveModTypes.Find("RTClient.Misc.Finder")
                    ?? throw new TypeLoadException("Together Finder");
                MethodInfo find = AccessTools.Method(finder, "GetPawnFromID", new[] { typeof(Map), typeof(string) })
                    ?? throw new MissingMethodException("Together GetPawnFromID");
                originalFindPawn = (Func<Map, string, Pawn>)Delegate.CreateDelegate(typeof(Func<Map, string, Pawn>), find);
                Patch(hediffs, "Ask", nameof(HeldHediffMap), transpiler: true);
                foreach (string name in new[] { "AddHediff", "RemoveHediff", "UpdateHediff" })
                    Patch(hediffs, name, nameof(HeldHediffLookup), transpiler: true);
            }
            catch { Unregister(harmony); throw; }

            void Patch(Type type, string name, string hook, bool transpiler = false)
                => PatchTarget(AccessTools.DeclaredMethod(type, name), hook, transpiler);
            void PatchTarget(MethodBase target, string name, bool transpiler = false)
            {
                if (target == null) throw new MissingMethodException("Together authority " + name);
                MethodInfo patch = AccessTools.Method(typeof(RimKataMultiplayerTogetherAuthority), name);
                installed.Add((target, patch));
                var hook = new HarmonyMethod(patch) { priority = Priority.First };
                RimKataStartupPatches.Patch(harmony, target, prefix: transpiler ? null : hook,
                    transpiler: transpiler ? hook : null);
            }
        }

        internal static void Unregister(Harmony harmony)
        {
            foreach (var item in installed) harmony.Unpatch(item.target, item.patch);
            installed.Clear();
        }

        private static bool Allows(Pawn pawn) => !RimKataMultiplayerTogetherWire.IsReplicaClient(pawn?.MapHeld);
        private static bool MapTick(MapComponent __instance) => !RimKataMultiplayerTogetherWire.IsReplicaClient(__instance.map);
        private static bool ThingTick(Thing __instance) => !RimKataMultiplayerTogetherWire.IsReplicaClient(__instance.MapHeld);
        private static bool PawnEntry(Pawn __0) => Allows(__0);
        private static bool DefenseEntry(Pawn defender) => Allows(defender);
        private static bool ReactiveTick(RimKataReactiveMotionState state) => Allows(state?.pawn);
        private static bool ParticipantEntry(RimKataResponseVisualParticipantCache.BodyVisualEntry entry) => Allows(entry?.pawn);
        private static bool SubdueEntry(RimKataSubdueState state) => Allows(state?.pawn);
        private static bool MeleeDefense(Verb_MeleeAttack attackingVerb) => Allows(attackingVerb?.CurrentTarget.Pawn);
        private static bool MeleeAttack(Pawn ___pawn) => Allows(___pawn) || !RimKataEligibilityCache.IsCachedQualifiedPawn(___pawn);
        private static bool VerbAttack(Verb __instance)
        {
            Pawn pawn = __instance.CasterPawn;
            return Allows(pawn) || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn);
        }
        private static bool JobTick(JobDriver __instance) => __instance.GetType().Assembly != typeof(RimKataMapComponent).Assembly
            || Allows(__instance.pawn);

        // Reuse the original loop's continue branch for replica entries. Do not
        // remove them: the corresponding Remove gate keeps the received state intact.
        private static bool SubdueRelationForTick(RimKataSubdueState state)
            => Allows(state?.pawn) && RimKataSubdueUtility.IsRelationValid(state);
        private static IEnumerable<CodeInstruction> SubdueLoop(IEnumerable<CodeInstruction> instructions)
            => ReplaceCall(instructions, AccessTools.Method(typeof(RimKataSubdueUtility), "IsRelationValid"),
                AccessTools.Method(typeof(RimKataMultiplayerTogetherAuthority), nameof(SubdueRelationForTick)));

        private static IEnumerable<CodeInstruction> HeldHediffMap(IEnumerable<CodeInstruction> instructions)
            => ReplaceCall(instructions, AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Map)),
                AccessTools.Method(typeof(RimKataMultiplayerTogetherAuthority), nameof(MapForHediff)));
        private static IEnumerable<CodeInstruction> HeldHediffLookup(IEnumerable<CodeInstruction> instructions)
            => ReplaceCall(instructions, originalFindPawn.Method,
                AccessTools.Method(typeof(RimKataMultiplayerTogetherAuthority), nameof(FindHediffPawn)));

        private static IEnumerable<CodeInstruction> ReplaceCall(IEnumerable<CodeInstruction> instructions, MethodInfo from, MethodInfo to)
        {
            int count = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(from)) { instruction.opcode = OpCodes.Call; instruction.operand = to; count++; }
                yield return instruction;
            }
            if (count != 1) throw new InvalidOperationException("Together contract changed: expected one " + from.Name + " call, found " + count);
        }

        private static Map MapForHediff(Thing thing)
        {
            if (thing.Map != null || !RimKataMultiplayerTogetherWire.IsInSession) return thing.Map;
            if (thing is Pawn pawn && IsHeldParticipant(pawn)) return pawn.MapHeld;
            return thing.Map;
        }

        private static bool IsHeldParticipant(Pawn pawn)
        {
            if (pawn.ParentHolder is RimKataThrownPawn) return true;
            Pawn carrier = (pawn.ParentHolder as Pawn_CarryTracker)?.pawn;
            return carrier != null && RimKataSubdueUtility.Get(carrier)?.target == pawn;
        }

        private static Pawn FindHediffPawn(Map map, string id)
        {
            if (RimKataMultiplayerTogetherWire.IsInSession)
            {
                Pawn held = RimKataMultiplayerTogetherSnapshot.FindThing(map, id) as Pawn;
                if (held?.MapHeld == map && IsHeldParticipant(held)) return held;
            }
            return originalFindPawn(map, id);
        }
    }
}
