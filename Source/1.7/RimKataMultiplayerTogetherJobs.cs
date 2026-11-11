using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    // Reuses Together's real PlayerJob builder and packet format. A host command has
    // already started this job, so publishing it must neither install Wait(1000) nor
    // restart/rewind that same host job when the server echoes the packet.
    internal static class RimKataMultiplayerTogetherJobs
    {
        private const int MaximumAcknowledgements = 128;
        private static Action<Job, Pawn> ask;
        private static Action<Action> originalBypass;
        private static Func<IList> pending;
        private static Func<object, string> pawnId, jobXml;
        private static Func<object, byte[]> packetData;
        private static Action<object, byte[]> setPacketData;
        private static Func<Array, byte[]> encode;
        private static Func<byte[], Array> decode;
        private static Type recordType;
        private static readonly HashSet<string> acknowledgements = new(StringComparer.Ordinal);
        [ThreadStatic] private static Pawn publishingPawn;
        [ThreadStatic] private static Job publishingJob;
        [ThreadStatic] private static string publication;

        internal static void Register(Harmony harmony)
        {
            Type manager = RimKataActiveModTypes.Find("RTClient.PacketManagers.Synchronous.PM_SJob");
            Type serializer = RimKataActiveModTypes.Find("RTShared.Misc.Serializer");
            Type packet = RimKataActiveModTypes.Find("RTNetwork.Packets.PKT_Synchronous");
            recordType = RimKataActiveModTypes.Find("RTClient.Hooks.Synchronous.PlayerJob")
                ?? throw new TypeLoadException("Together PlayerJob");
            ask = (Action<Job, Pawn>)Delegate.CreateDelegate(typeof(Action<Job, Pawn>),
                AccessTools.Method(manager, "Ask", new[] { typeof(Job), typeof(Pawn) }));
            originalBypass = (Action<Action>)Delegate.CreateDelegate(typeof(Action<Action>), AccessTools.Method(
                RimKataActiveModTypes.Find("RTClient.Misc.PatchHandler"), "ExecuteInBypass", new[] { typeof(Action) }));
            pending = Expression.Lambda<Func<IList>>(Expression.Convert(Expression.Call(
                AccessTools.PropertyGetter(manager, "PlayerJobs")), typeof(IList))).Compile();
            pawnId = Getter<string>(recordType, "PawnID");
            jobXml = Getter<string>(recordType, "Job");
            packetData = Getter<byte[]>(packet, "Data");
            var instance = Expression.Parameter(typeof(object));
            var bytes = Expression.Parameter(typeof(byte[]));
            setPacketData = Expression.Lambda<Action<object, byte[]>>(Expression.Call(Expression.Convert(instance, packet),
                AccessTools.PropertySetter(packet, "Data"), bytes), instance, bytes).Compile();
            Type arrayType = recordType.MakeArrayType();
            var array = Expression.Parameter(typeof(Array));
            encode = Expression.Lambda<Func<Array, byte[]>>(Expression.Call(
                AccessTools.Method(serializer, "ConvertObjectToBytes").MakeGenericMethod(arrayType),
                Expression.Convert(array, arrayType), Expression.Constant(false)), array).Compile();
            decode = Expression.Lambda<Func<byte[], Array>>(Expression.Convert(Expression.Call(
                AccessTools.Method(serializer, "ConvertBytesToObject").MakeGenericMethod(arrayType),
                bytes, Expression.Constant(false)), typeof(Array)), bytes).Compile();
            RimKataStartupPatches.Patch(harmony, AccessTools.Method(manager, "Handle"),
                prefix: new HarmonyMethod(typeof(RimKataMultiplayerTogetherJobs), nameof(ReceivePrefix)) { priority = Priority.First });
            RimKataStartupPatches.Patch(harmony, AccessTools.Method(typeof(Job), nameof(Job.ExposeData)),
                postfix: new HarmonyMethod(typeof(RimKataMultiplayerTogetherJobs), nameof(ExposePublication)));
            RimKataStartupPatches.Patch(harmony, ask.Method,
                transpiler: new HarmonyMethod(typeof(RimKataMultiplayerTogetherJobs), nameof(PreserveCurrentJob)));
        }

        private static IEnumerable<CodeInstruction> PreserveCurrentJob(IEnumerable<CodeInstruction> instructions)
        {
            int count = 0;
            foreach (CodeInstruction code in instructions)
            {
                if (code.Calls(originalBypass.Method))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(RimKataMultiplayerTogetherJobs), nameof(InstallNativeWait));
                    count++;
                }
                yield return code;
            }
            if (count != 1) throw new InvalidOperationException("Together Ask's Wait boundary changed.");
        }
        private static void InstallNativeWait(Action action)
        {
            if (publishingJob == null) originalBypass(action);
        }

        internal static void PublishCurrent(Pawn pawn)
        {
            Job job = pawn?.CurJob;
            if (job == null || !RimKataMultiplayerTogetherWire.IsHostSession) return;
            if (acknowledgements.Count >= MaximumAcknowledgements)
                throw new InvalidOperationException("Together did not acknowledge the published RK jobs.");
            IList queue = pending();
            // The command supersedes an older queued order for this actor. Preserve
            // all other actors and let Together retain its normal batching for them.
            for (int i = queue.Count - 1; i >= 0; i--)
                if (pawnId(queue[i]) == pawn.ThingID) queue.RemoveAt(i);
            Pawn previousPawn = publishingPawn;
            Job previousJob = publishingJob;
            string previousPublication = publication;
            object record = null;
            publishingPawn = pawn;
            publishingJob = job;
            publication = Guid.NewGuid().ToString("N");
            try
            {
                ask(job, pawn);
                for (int i = queue.Count - 1; i >= 0; i--)
                    if (pawnId(queue[i]) == pawn.ThingID) { record = queue[i]; queue.RemoveAt(i); break; }
                if (record == null || pawn.CurJob != job)
                    throw new InvalidOperationException("Together could not publish the current RK job without changing it.");
            }
            finally
            {
                publishingPawn = previousPawn;
                publishingJob = previousJob;
                publication = previousPublication;
            }
            Array records = Array.CreateInstance(recordType, 1);
            records.SetValue(record, 0);
            string key = Identity(record);
            acknowledgements.Add(key);
            try { RimKataMultiplayerTogetherWire.SendNativeJobs(encode(records)); }
            catch { acknowledgements.Remove(key); throw; }
        }

        // This marker belongs to an actual job document. It makes one publication's
        // acknowledgement distinct from a later request of the same native job.
        private static void ExposePublication(Job __instance)
        {
            if (Scribe.mode != LoadSaveMode.Saving || __instance != publishingJob) return;
            string token = publication;
            Scribe_Values.Look(ref token, "rimKataTogetherPublication");
        }

        private static bool ReceivePrefix(object __1)
        {
            if (!RimKataMultiplayerTogetherWire.IsHostSession || acknowledgements.Count == 0) return true;
            try
            {
                Array records = decode(packetData(__1));
                var remaining = new List<object>(records.Length);
                var acknowledged = new List<string>();
                foreach (object record in records)
                {
                    string key = Identity(record);
                    if (acknowledgements.Contains(key)) acknowledged.Add(key);
                    else remaining.Add(record);
                }
                if (acknowledged.Count == 0) return true;
                if (remaining.Count != 0)
                {
                    Array filtered = Array.CreateInstance(recordType, remaining.Count);
                    for (int i = 0; i < remaining.Count; i++) filtered.SetValue(remaining[i], i);
                    setPacketData(__1, encode(filtered));
                }
                // Commit acknowledgements only after a mixed packet was rebuilt successfully.
                foreach (string key in acknowledged) acknowledgements.Remove(key);
                return remaining.Count != 0;
            }
            catch (Exception exception)
            {
                RimKataMultiplayerTogetherWire.FailExtension("native job acknowledgement failed: " + exception.Message);
                return false;
            }
        }

        internal static void Clear() => acknowledgements.Clear();

        private static string Identity(object record)
        {
            using (SHA256 hash = SHA256.Create())
                return pawnId(record) + ":" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(jobXml(record))));
        }
        private static Func<object, T> Getter<T>(Type type, string name)
        {
            var value = Expression.Parameter(typeof(object));
            return Expression.Lambda<Func<object, T>>(Expression.Call(Expression.Convert(value, type),
                AccessTools.PropertyGetter(type, name)), value).Compile();
        }
    }
}
