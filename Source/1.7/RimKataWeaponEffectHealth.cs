using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    // Copy the original health entry points, including Harmony callbacks. Only the
    // selected mutation list and the incoming manager's merge decision are redirected.
    internal static class RimKataWeaponEffectHealth
    {
        private delegate void AddAction(Pawn_HealthTracker tracker, Hediff hediff, BodyPartRecord part,
            DamageInfo? dinfo, DamageWorker.DamageResult result);
        private delegate Hediff CreateAction(Pawn_HealthTracker tracker, HediffDef def, BodyPartRecord part,
            DamageInfo? dinfo, DamageWorker.DamageResult result);
        private static readonly object BuildLock = new object();
        private static readonly FieldInfo Hediffs = AccessTools.Field(typeof(HediffSet), nameof(HediffSet.hediffs));
        private static readonly MethodInfo Merge = AccessTools.Method(typeof(Hediff), nameof(Hediff.TryMergeWith), new[] { typeof(Hediff) });
        private static readonly MethodInfo ReadList = AccessTools.Method(typeof(RimKataWeaponEffectHealth), nameof(MutationList));
        private static readonly MethodInfo MergeCall = AccessTools.Method(typeof(RimKataWeaponEffectHealth), nameof(TryMerge));
        private static readonly MethodInfo Rewriter = AccessTools.Method(typeof(RimKataWeaponEffectHealth), nameof(Rewrite));
        private static AddAction add;
        private static CreateAction create;
        private static Action<Pawn_HealthTracker, Hediff> remove;
        [ThreadStatic] private static Context current;
        [ThreadStatic] private static Shape building;

        private readonly struct Context
        {
            internal readonly HediffSet set;
            internal readonly List<Hediff> effects;
            internal readonly Hediff noMerge;
            internal Context(Pawn_HealthTracker tracker, List<Hediff> effects, Hediff noMerge)
            { set = tracker.hediffSet; this.effects = effects; this.noMerge = noMerge; }
        }

        private sealed class Shape
        {
            internal int listReads;
            internal bool merge;
            internal MethodInfo source, replacement;
            internal MethodInfo[] callbacks;
        }

        internal static bool Initialize(out string reason)
        {
            lock (BuildLock)
            {
                reason = null;
                if (add != null) return true;
                try
                {
                    Type[] arguments = { typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) };
                    MethodInfo originalAdd = AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff), arguments);
                    MethodInfo direct = AccessTools.Method(typeof(HediffSet), nameof(HediffSet.AddDirect),
                        new[] { typeof(Hediff), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) });
                    MethodInfo originalRemove = AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.RemoveHediff), new[] { typeof(Hediff) });
                    MethodInfo originalCreate = AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff),
                        new[] { typeof(HediffDef), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) });
                    MethodInfo scopedAdd = AccessTools.Method(typeof(RimKataWeaponEffects), "AddHediff",
                        new[] { typeof(Pawn_HealthTracker), typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) });
                    if (Hediffs == null || Merge == null || scopedAdd == null)
                        throw new NotSupportedException("The original health storage or scoped addition entry is unavailable.");
                    MethodInfo directCopy = Copy(direct, new Shape
                    {
                        listReads = 3, merge = true,
                        callbacks = new[] { AccessTools.Method(typeof(Hediff), nameof(Hediff.PostAdd), new[] { typeof(DamageInfo?) }) }
                    });
                    MethodInfo addCopy = Copy(originalAdd, new Shape { source = direct, replacement = directCopy });
                    MethodInfo removeCopy = Copy(originalRemove, new Shape
                    {
                        listReads = 1,
                        callbacks = new[] { AccessTools.Method(typeof(Hediff), nameof(Hediff.PreRemoved)),
                            AccessTools.Method(typeof(Hediff), nameof(Hediff.PostRemoved)) }
                    });
                    MethodInfo createCopy = Copy(originalCreate, new Shape { source = originalAdd, replacement = scopedAdd });
                    var readyAdd = (AddAction)addCopy.CreateDelegate(typeof(AddAction));
                    var readyRemove = (Action<Pawn_HealthTracker, Hediff>)removeCopy.CreateDelegate(typeof(Action<Pawn_HealthTracker, Hediff>));
                    var readyCreate = (CreateAction)createCopy.CreateDelegate(typeof(CreateAction));
                    // Nothing becomes callable until every snapshot and signature has succeeded.
                    create = readyCreate;
                    remove = readyRemove;
                    add = readyAdd;
                    return true;
                }
                catch (Exception exception)
                {
                    while (exception is TargetInvocationException && exception.InnerException != null) exception = exception.InnerException;
                    reason = exception.GetType().Name + ": " + exception.Message;
                    return false;
                }
            }
        }

        internal static void Add(Pawn_HealthTracker tracker, Hediff hediff, BodyPartRecord part,
            DamageInfo? dinfo, DamageWorker.DamageResult result, List<Hediff> localEffects, bool preventMerge)
        {
            Context previous = current;
            current = new Context(tracker, localEffects, preventMerge ? hediff : null);
            try { add(tracker, hediff, part, dinfo, result); }
            finally { current = previous; }
        }

        internal static void Remove(Pawn_HealthTracker tracker, Hediff hediff, List<Hediff> localEffects)
        {
            Context previous = current;
            current = new Context(tracker, localEffects, null);
            try { remove(tracker, hediff); }
            finally { current = previous; }
        }

        // The nested Add decides ownership after MakeHediff and before original PostAdd callbacks.
        internal static Hediff CreateAndAdd(Pawn_HealthTracker tracker, HediffDef def, BodyPartRecord part,
            DamageInfo? dinfo, DamageWorker.DamageResult result)
            => create(tracker, def, part, dinfo, result);

        private static List<Hediff> MutationList(HediffSet set)
            => current.effects != null && ReferenceEquals(current.set, set) ? current.effects : set.hediffs;

        private static bool TryMerge(Hediff existing, Hediff incoming)
            => (current.noMerge == null || !ReferenceEquals(current.noMerge, incoming)) && existing.TryMergeWith(incoming);

        private static MethodInfo Copy(MethodInfo original, Shape shape)
        {
            if (original == null || (shape.callbacks != null && Array.IndexOf(shape.callbacks, null) >= 0))
                throw new NotSupportedException("An original health callback is unavailable.");
            Shape previous = building;
            building = shape;
            try
            {
                if (!RimKataWeaponExecutionBundle.TryCreateSnapshot(original, Rewriter, out MethodInfo copy, out string reason))
                    throw new NotSupportedException(original.Name + ": " + reason);
                return copy;
            }
            finally { building = previous; }
        }

        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            Shape shape = building ?? throw new InvalidOperationException("No health snapshot is being prepared.");
            int lists = 0, merges = 0, replacements = 0;
            int[] callbacks = new int[shape.callbacks?.Length ?? 0];
            foreach (CodeInstruction instruction in instructions)
            {
                var code = new CodeInstruction(instruction);
                if (Equals(code.operand, Hediffs))
                {
                    if (code.opcode != OpCodes.Ldfld || shape.listReads == 0)
                        throw new NotSupportedException("The original health list access has changed.");
                    code.opcode = OpCodes.Call;
                    code.operand = ReadList;
                    lists++;
                }
                else if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) && code.operand is MethodInfo method)
                {
                    for (int i = 0; i < callbacks.Length; i++) if (method == shape.callbacks[i]) callbacks[i]++;
                    if (shape.merge && method == Merge)
                    {
                        code.opcode = OpCodes.Call;
                        code.operand = MergeCall;
                        merges++;
                    }
                    else if (method == shape.source)
                    {
                        code.opcode = OpCodes.Call;
                        code.operand = shape.replacement;
                        replacements++;
                    }
                }
                yield return code;
            }
            if (lists != shape.listReads || merges != (shape.merge ? 1 : 0)
                || replacements != (shape.source != null ? 1 : 0))
                throw new NotSupportedException("The original health mutation shape has changed.");
            foreach (int count in callbacks)
                if (count != 1) throw new NotSupportedException("The original health callback shape has changed.");
        }
    }
}
