using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataMultiplayerInput
    {
        private static bool installed;

        // Installed only for the one running provider. Capture precedes existing RK
        // OrderForceTarget prefixes, so no local combat state is changed before sync.
        internal static void Install(Harmony harmony)
        {
            if (installed) return;
            if (harmony == null) throw new ArgumentNullException(nameof(harmony));
            RimKataStartupPatches.Patch(harmony,
                AccessTools.DeclaredMethod(typeof(Verb), nameof(Verb.OrderForceTarget), new[] { typeof(LocalTargetInfo) }),
                prefix: new HarmonyMethod(typeof(RimKataMultiplayerInput), nameof(OrderWeaponPrefix))
                { priority = Priority.First + 100 });
            RimKataStartupPatches.Patch(harmony,
                AccessTools.DeclaredMethod(typeof(RimKataDirectionalFire), nameof(RimKataDirectionalFire.OrderSelected)),
                prefix: new HarmonyMethod(typeof(RimKataMultiplayerInput), nameof(OrderSquadGroundPrefix))
                { priority = Priority.First + 100 });
            installed = true;
        }

        private static bool OrderWeaponPrefix(Verb __instance, LocalTargetInfo target)
        {
            Pawn pawn = __instance?.CasterPawn;
            if (!RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || !RimKataMultiplayerCompat.ShouldCaptureInput(pawn)) return true;
            bool directional = !target.HasThing && target.IsValid
                && Find.Targeter?.targetingSource is RimKataDirectionalTargetingSource;
            if (!target.HasThing && !directional) return true;
            if (!RimKataMultiplayerCompat.CanExecuteCommands) return false;

            ThingWithComps weapon = __instance.EquipmentSource;
            int index = -1;
            if (weapon != null)
            {
                List<Verb> verbs = weapon.TryGetComp<CompEquippable>()?.AllVerbs;
                if (verbs == null || (index = verbs.IndexOf(__instance)) < 0) return false;
            }
            else if (!__instance.IsMeleeAttack) return true;

            int context = RimKataAttackGizmoTargetContext.Active ? RimKataMultiplayerCommands.AttackGizmoContext : 0;
            if (RimKataAttackGizmoTargetContext.SquadActive) context |= RimKataMultiplayerCommands.SquadAttackContext;
            if (directional) context |= RimKataMultiplayerCommands.DirectionalContext;
            RimKataMultiplayerCommands.OrderWeapon(pawn, weapon, index, __instance.GetUniqueLoadID(),
                target.Thing, target.Cell, context);
            return false;
        }

        private static bool OrderSquadGroundPrefix(IntVec3 cell)
        {
            var selected = Find.Selector?.SelectedPawns;
            if (selected == null) return true;
            bool capture = false;
            for (int i = 0; i < selected.Count; i++)
            {
                Pawn pawn = selected[i];
                if (RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                    && RimKataMultiplayerCompat.ShouldCaptureInput(pawn)) { capture = true; break; }
            }
            // An outer native multiplayer command may already be replaying the whole
            // squad action. Let its restored selection/context use the original loop.
            if (!capture) return true;
            if (!RimKataMultiplayerCompat.CanExecuteCommands) return false;
            for (int i = 0; i < selected.Count; i++)
            {
                Pawn pawn = selected[i];
                if (RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                    && RimKataMultiplayerCompat.ShouldCaptureInput(pawn))
                    RimKataMultiplayerCommands.OrderSquadGround(pawn, cell);
            }
            return false;
        }
    }
}
