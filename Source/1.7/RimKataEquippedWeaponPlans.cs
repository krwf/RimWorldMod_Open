using System.Collections.Generic;
using Verse;

namespace KRWF.RimKata
{
    // Equipment and qualification events own this list; combat/render paths only consume plans.
    internal static class RimKataEquippedWeaponPlans
    {
        private static readonly Dictionary<Pawn, ThingDef[]> Owners =
            new Dictionary<Pawn, ThingDef[]>(RimKataReferenceComparer<Pawn>.Instance);
        private static bool suspended;
        private static bool changingSuspension;

        internal static void Sync(Pawn pawn)
        {
            if (pawn?.Spawned != true || pawn.Dead
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn))
            {
                Forget(pawn);
                return;
            }

            List<ThingWithComps> equipment = pawn.equipment?.AllEquipmentListForReading;
            Owners.TryGetValue(pawn, out ThingDef[] previous);
            int count = 0;
            bool changed = false;
            if (equipment != null)
            {
                for (int i = 0; i < equipment.Count; i++)
                {
                    ThingDef definition = WeaponDefinition(equipment[i]);
                    if (definition == null) continue;
                    if (previous == null || count >= previous.Length || previous[count] != definition)
                        changed = true;
                    count++;
                }
            }
            if (count == 0)
            {
                Forget(pawn);
                return;
            }
            if (!changed && previous != null && previous.Length == count) return;

            ThingDef[] current = new ThingDef[count];
            int index = 0;
            for (int i = 0; i < equipment.Count; i++)
            {
                ThingDef definition = WeaponDefinition(equipment[i]);
                if (definition != null) current[index++] = definition;
            }
            Owners[pawn] = current;
            if (suspended) return;

            // Acquire the replacement set first so an unchanged Def never loses its last owner.
            Acquire(current);
            if (previous != null) Release(previous);
        }

        internal static void Forget(Pawn pawn)
        {
            if (pawn == null || !Owners.TryGetValue(pawn, out ThingDef[] definitions)) return;
            Owners.Remove(pawn);
            if (!suspended) Release(definitions);
        }

        internal static void Suspend()
        {
            if (suspended || changingSuspension) return;
            var snapshot = new List<ThingDef[]>(Owners.Values);
            suspended = true;
            changingSuspension = true;
            try
            {
                foreach (ThingDef[] definitions in snapshot) Release(definitions);
            }
            finally { changingSuspension = false; }
        }

        internal static void Resume()
        {
            if (!suspended || changingSuspension) return;
            changingSuspension = true;
            try
            {
                List<KeyValuePair<Pawn, ThingDef[]>> restored = null;
                while (true)
                {
                    var snapshot = new List<KeyValuePair<Pawn, ThingDef[]>>(Owners);
                    foreach (var owner in snapshot) Acquire(owner.Value);
                    if (restored != null)
                        foreach (var owner in restored) Release(owner.Value);
                    restored = snapshot;
                    // Health/equipment callbacks update only Owners while restoration is in flight.
                    if (MatchesOwners(snapshot)) break;
                }
                suspended = false;
            }
            finally { changingSuspension = false; }
        }

        // Runtime reset owns unloading; do not release definitions into the next game.
        internal static void Clear()
        {
            Owners.Clear();
            suspended = false;
            changingSuspension = false;
        }

        private static bool MatchesOwners(List<KeyValuePair<Pawn, ThingDef[]>> snapshot)
        {
            if (Owners.Count != snapshot.Count) return false;
            foreach (var owner in snapshot)
                if (!Owners.TryGetValue(owner.Key, out ThingDef[] current) || current != owner.Value)
                    return false;
            return true;
        }

        private static ThingDef WeaponDefinition(ThingWithComps equipment)
        {
            return equipment != null && !equipment.Destroyed && equipment.def?.IsWeapon == true
                ? equipment.def : null;
        }

        private static void Acquire(ThingDef[] definitions)
        {
            for (int i = 0; i < definitions.Length; i++)
                RimKataWeaponExecutionRuntime.AcquireDefinition(definitions[i]);
        }

        private static void Release(ThingDef[] definitions)
        {
            for (int i = 0; i < definitions.Length; i++)
                RimKataWeaponExecutionRuntime.ReleaseDefinition(definitions[i]);
        }
    }
}

