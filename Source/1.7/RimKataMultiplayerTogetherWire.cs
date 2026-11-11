using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    // A distinct extension action, not an existing Job/Hediff/Chat action. Together's
    // server relays Action envelopes without decoding their Data or ActionType.
    // Both endpoints must acknowledge this exact protocol and command contract.
    internal static class RimKataMultiplayerTogetherWire
    {
        private const int ActionCode = 0x524B0001;
        private const int Magic = 0x524B5457;
        private const int Protocol = 1;
        private const int MaximumPayload = 2 * 1024 * 1024;
        private enum Kind : byte { Hello, Acknowledge, Command, Replica, Failure }
        private sealed class StaleCommandException : Exception { }
        private static Func<Map> mapGetter;
        private static Func<bool> hostGetter, bypassGetter;
        private static Func<Faction> allyGetter, enemyGetter;
        private static Action<Action> inBypass;
        private static Action<byte[]> send, sendJobs;
        private static Func<object, int> actionOf;
        private static Func<object, byte[]> dataOf;
        private static readonly Dictionary<MethodBase, int> commandIds = new();
        private static MethodInfo[] commandMethods;
        private static string contract, settingsContract;
        private static Map map;
        private static bool active, host, gotHello, gotAcknowledgement, warned, faulted;
        private static string nonce, peer;
        private static long sequence, receivedSequence, nextHello, nextReplica;
        private static int helloAttempts;
        private static readonly HashSet<Pawn> remotePawns = new();
        private static readonly HashSet<Pawn> arrivingPawns = new();
        [ThreadStatic] private static int executionDepth;
        [ThreadStatic] private static int tickDepth;
        [ThreadStatic] private static int importingParty;
        [ThreadStatic] private static Pawn remoteCommandPawn;

        internal static Func<Map, byte[]> CaptureReplica;
        internal static Action<Map, byte[]> ApplyReplica;
        internal static Action<Map> ClearReplica;
        internal static bool IsInSession => active && map != null && mapGetter?.Invoke() == map;
        internal static bool IsHostSession => IsInSession && host;
        internal static bool CanExecuteCommands => IsInSession && gotHello && gotAcknowledgement
            && !faulted && CaptureReplica != null && ApplyReplica != null;
        internal static bool IsReplicaClient(Map candidate) => IsInSession && !host && candidate == map;
        internal static bool ShouldCaptureInput(Pawn pawn) => IsInSession && pawn?.Map == map
            && tickDepth == 0 && executionDepth == 0 && bypassGetter?.Invoke() != true;
        internal static bool IsAuthorizedRemoteCommand(Pawn pawn) => executionDepth != 0
            && pawn != null && pawn == remoteCommandPawn;
        internal static bool IsRemoteParticipant(Pawn pawn) => CanExecuteCommands && host
            && pawn?.Map == map && remotePawns.Contains(pawn);
        internal static void RecordImportedPawn(Pawn pawn)
        {
            if (importingParty != 0 && pawn != null) arrivingPawns.Add(pawn);
        }

        internal static void Register(Harmony harmony, IReadOnlyList<MethodInfo> commands)
        {
            Type session = RequiredType("RTClient.Managers.SessionManager");
            Type patches = RequiredType("RTClient.Misc.PatchHandler");
            Type network = RequiredType("RTNetwork.Components.Network");
            Type packet = RequiredType("RTNetwork.Packets.PKT_Synchronous");
            Type dispatcher = RequiredType("RTClient.Managers.MainThreadManager");
            Type manager = RequiredType("RTClient.PacketManagers.Synchronous.PM_Synchronous");
            mapGetter = Getter<Map>(session, "SynchronousMap");
            hostGetter = Getter<bool>(session, "IsSynchronousHost");
            bypassGetter = Getter<bool>(patches, "BypassFlag");
            allyGetter = Getter<Faction>(session, "AllyFaction");
            enemyGetter = Getter<Faction>(session, "EnemyFaction");
            inBypass = (Action<Action>)Delegate.CreateDelegate(typeof(Action<Action>),
                AccessTools.Method(patches, "ExecuteInBypass", new[] { typeof(Action) }));
            BuildTransport(network, packet);
            commandMethods = commands.OrderBy(Identity, StringComparer.Ordinal).ToArray();
            if (commandMethods.Length == 0) throw new InvalidOperationException("Together command contract is empty.");
            foreach (MethodInfo method in commandMethods)
                if (!method.IsStatic || method.ReturnType != typeof(void)
                    || method.GetParameters().Any(p => !Supported(p.ParameterType)))
                    throw new InvalidOperationException("Unsupported Together command: " + Identity(method));
            using (SHA256 hash = SHA256.Create())
                contract = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    "RimKata/Together/" + Protocol + "/snapshot/" + RimKataMultiplayerTogetherSnapshot.SchemaVersion
                    + "/build/" + typeof(RimKataMultiplayerTogetherWire).Assembly.ManifestModule.ModuleVersionId
                    + "\n" + string.Join("\n", commandMethods.Select(Identity))))).Replace("-", "");

            var installed = new List<(MethodBase target, MethodInfo patch)>();
            try
            {
                Patch(AccessTools.Method(dispatcher, "DoOnSynchronousStartMethods"), nameof(StartPostfix), 1);
                Patch(AccessTools.Method(dispatcher, "DoOnSynchronousEndMethods"), nameof(EndPostfix), 1);
                Patch(AccessTools.Method(session, "CleanValues"), nameof(EndPostfix), 1);
                Patch(AccessTools.Method(dispatcher, "Update"), nameof(UpdatePostfix), 1);
                Patch(AccessTools.Method(manager, "RouteToManager"), nameof(RoutePrefix), 0);
                Patch(AccessTools.Method(manager, "RouteToManager"), nameof(RoutePostfix), 1);
                Patch(AccessTools.Method(manager, "SpawnOtherPawns"), nameof(ImportPartyPrefix), 0);
                Patch(AccessTools.Method(manager, "SpawnOtherPawns"), nameof(ImportPartyFinalizer), 2);
                Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)), nameof(TickPrefix), 0);
                Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)), nameof(TickFinalizer), 2);
                for (int i = 0; i < commandMethods.Length; i++)
                {
                    commandIds.Add(commandMethods[i], i);
                    Patch(commandMethods[i], nameof(CommandPrefix), 0);
                }
            }
            catch
            {
                foreach (var item in installed) harmony.Unpatch(item.target, item.patch);
                commandIds.Clear();
                throw;
            }

            void Patch(MethodBase target, string name, int kind)
            {
                if (target == null) throw new MissingMethodException(name);
                MethodInfo patch = AccessTools.Method(typeof(RimKataMultiplayerTogetherWire), name);
                installed.Add((target, patch));
                var hook = new HarmonyMethod(patch);
                if (name == nameof(RoutePrefix)) hook.priority = Priority.First;
                RimKataStartupPatches.Patch(harmony, target, prefix: kind == 0 ? hook : null,
                    postfix: kind == 1 ? hook : null, finalizer: kind == 2 ? hook : null);
            }
        }

        private static void BuildTransport(Type network, Type packet)
        {
            MethodInfo endpoint = AccessTools.PropertyGetter(network, "ServerEndpoint");
            MethodInfo enqueue = AccessTools.Method(endpoint.ReturnType, "EnqueuePacket");
            ParameterInfo[] parameters = enqueue.GetParameters();
            if (parameters.Length != 2 || !parameters[0].ParameterType.IsEnum
                || parameters[1].ParameterType != typeof(object)) throw new MissingMethodException("Together EnqueuePacket");
            PropertyInfo step = AccessTools.Property(packet, "CurrentStepMode");
            PropertyInfo action = AccessTools.Property(packet, "CurrentActionType");
            PropertyInfo data = AccessTools.Property(packet, "Data");
            if (!step.PropertyType.IsEnum || !action.PropertyType.IsEnum || data.PropertyType != typeof(byte[]))
                throw new MissingMethodException("Together synchronous envelope");
            var bytes = Expression.Parameter(typeof(byte[]));
            var envelope = Expression.Variable(packet);
            send = Expression.Lambda<Action<byte[]>>(Expression.Block(new[] { envelope },
                Expression.Assign(envelope, Expression.New(packet)),
                Expression.Assign(Expression.Property(envelope, step), Expression.Constant(Enum.Parse(step.PropertyType, "Action"))),
                Expression.Assign(Expression.Property(envelope, action), Expression.Constant(Enum.ToObject(action.PropertyType, ActionCode))),
                Expression.Assign(Expression.Property(envelope, data), bytes),
                Expression.Call(Expression.Call(endpoint), enqueue,
                    Expression.Constant(Enum.Parse(parameters[0].ParameterType, "Synchronous")), Expression.Convert(envelope, typeof(object)))), bytes).Compile();
            sendJobs = Expression.Lambda<Action<byte[]>>(Expression.Block(new[] { envelope },
                Expression.Assign(envelope, Expression.New(packet)),
                Expression.Assign(Expression.Property(envelope, step), Expression.Constant(Enum.Parse(step.PropertyType, "Action"))),
                Expression.Assign(Expression.Property(envelope, action), Expression.Constant(Enum.Parse(action.PropertyType, "SPlayerJob"))),
                Expression.Assign(Expression.Property(envelope, data), bytes),
                Expression.Call(Expression.Call(endpoint), enqueue,
                    Expression.Constant(Enum.Parse(parameters[0].ParameterType, "Synchronous")), Expression.Convert(envelope, typeof(object)))), bytes).Compile();
            var obj = Expression.Parameter(typeof(object));
            actionOf = Expression.Lambda<Func<object, int>>(Expression.Convert(
                Expression.Property(Expression.Convert(obj, packet), action), typeof(int)), obj).Compile();
            dataOf = Expression.Lambda<Func<object, byte[]>>(
                Expression.Property(Expression.Convert(obj, packet), data), obj).Compile();
        }

        private static void StartPostfix()
        {
            var arrived = new List<Pawn>(arrivingPawns);
            EndPostfix();
            map = mapGetter();
            if (map == null) return;
            active = true;
            host = hostGetter();
            if (host) foreach (Pawn pawn in arrived)
                if (pawn?.Map == map) remotePawns.Add(pawn);
            nonce = Guid.NewGuid().ToString("N");
            nextHello = Stopwatch.GetTimestamp();
            try { settingsContract = RimKataMultiplayerSettings.Fingerprint(); }
            catch (Exception exception) { Fail("settings could not be compared: " + exception.Message); }
        }

        private static void EndPostfix()
        {
            RimKataMultiplayerTogetherJobs.Clear();
            try
            {
                if (map != null && ClearReplica != null)
                    ExecuteExternal(() => ClearReplica(map), markDirty: false);
            }
            catch (Exception exception) { Warn("replica cleanup failed: " + exception.Message); }
            finally
            {
                active = gotHello = gotAcknowledgement = warned = faulted = false;
                map = null;
                nonce = peer = null;
                settingsContract = null;
                sequence = receivedSequence = nextHello = nextReplica = 0;
                helloAttempts = 0;
                remotePawns.Clear();
                arrivingPawns.Clear();
            }
        }

        private static void UpdatePostfix()
        {
            if (!active || faulted) return;
            if (mapGetter() != map) { EndPostfix(); return; }
            long now = Stopwatch.GetTimestamp();
            if (!CanExecuteCommands && now >= nextHello && helloAttempts < 10)
            {
                nextHello = now + Stopwatch.Frequency;
                helloAttempts++;
                try { Send(Kind.Hello, null, null); }
                catch (Exception exception) { Fail("handshake send failed: " + exception.Message); }
            }
            if (!CanExecuteCommands && helloAttempts >= 10 && now >= nextHello)
            { Fail("the other endpoint did not acknowledge RK Preview support; RK commands are disabled for this session."); return; }
            if (!CanExecuteCommands || !host || CaptureReplica == null || now < nextReplica) return;
            nextReplica = now + Stopwatch.Frequency / 10;
            try
            {
                byte[] state = CaptureReplica(map);
                if (state != null && state.Length != 0)
                    Send(Kind.Replica, peer, writer => { writer.Write(state.Length); writer.Write(state); });
            }
            catch (Exception exception) { Fail("replica publication failed: " + exception.Message); }
        }

        private static bool RoutePrefix(object __1)
        {
            if (actionOf(__1) != ActionCode) return true;
            byte[] payload = dataOf(__1);
            if (!IsInSession || payload == null || payload.Length > MaximumPayload) return false;
            try { Receive(payload); }
            catch (StaleCommandException) { /* A target can disappear while a valid command is in flight. */ }
            catch (Exception exception) { Fail("extension message was rejected: " + exception.Message); }
            return false;
        }

        private static void RoutePostfix(object __1)
        {
            if (IsInSession && host && actionOf(__1) != ActionCode)
                RimKataMultiplayerTogetherSnapshot.MarkDirty(map);
        }

        private static void Receive(byte[] payload)
        {
            using (var stream = new MemoryStream(payload, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Protocol) return;
                Kind kind = (Kind)reader.ReadByte();
                string sender = reader.ReadString();
                string recipient = reader.ReadString();
                bool senderHost = reader.ReadBoolean();
                long serial = reader.ReadInt64();
                string signature = reader.ReadString();
                if (sender == nonce || sender.Length != 32 || senderHost == host) return;
                if (signature != contract) { Fail("the peer's RimKata build or Preview protocol differs; RK commands are disabled for this session."); return; }
                string peerSettings = reader.ReadString();
                if (peerSettings != settingsContract)
                { Fail("RimKata settings differ between the two players; RK commands are disabled for this session."); return; }
                if (kind == Kind.Hello)
                {
                    if (peer != null && peer != sender) return;
                    peer = sender;
                    gotHello = true;
                    Send(Kind.Acknowledge, peer, null);
                    return;
                }
                if (sender != peer || recipient != nonce) return;
                if (kind == Kind.Failure) { Fail("the other endpoint stopped RK Preview synchronization.", false); return; }
                if (kind == Kind.Acknowledge) { gotAcknowledgement = true; return; }
                if (!CanExecuteCommands || serial <= receivedSequence) return;
                receivedSequence = serial;
                if (kind == Kind.Command && host)
                {
                    int index = reader.ReadInt32();
                    if (index < 0 || index >= commandMethods.Length) return;
                    MethodInfo method = commandMethods[index];
                    ParameterInfo[] parameters = method.GetParameters();
                    var args = new object[parameters.Length];
                    for (int i = 0; i < args.Length; i++) args[i] = ReadArgument(reader, parameters[i].ParameterType);
                    if (stream.Position != stream.Length || !(args.FirstOrDefault() is Pawn pawn)
                        || pawn.Map != map || !remotePawns.Contains(pawn)
                        || (pawn.Faction != allyGetter() && pawn.Faction != enemyGetter())) return;
                    Pawn previous = remoteCommandPawn;
                    remoteCommandPawn = pawn;
                    try { Execute(method, args); }
                    finally { remoteCommandPawn = previous; }
                }
                else if (kind == Kind.Replica && !host && ApplyReplica != null)
                {
                    int length = reader.ReadInt32();
                    if (length < 0 || length > MaximumPayload || length != stream.Length - stream.Position) return;
                    byte[] snapshot = reader.ReadBytes(length);
                    ExecuteExternal(() => ApplyReplica(map, snapshot));
                }
            }
        }

        private static bool CommandPrefix(MethodBase __originalMethod, object[] __args)
        {
            if (!IsInSession || executionDepth != 0) return true;
            if (!(__args.FirstOrDefault() is Pawn pawn) || pawn.Map != map) return true;
            if (!CanExecuteCommands) return false;
            if (host)
            {
                try { Execute((MethodInfo)__originalMethod, __args); }
                catch (Exception exception) { Fail("host command failed: " + exception.Message); }
                return false;
            }
            if (pawn.Faction != Faction.OfPlayer || !commandIds.TryGetValue(__originalMethod, out int index)) return false;
            try
            {
                ParameterInfo[] parameters = __originalMethod.GetParameters();
                Send(Kind.Command, peer, writer =>
                {
                    writer.Write(index);
                    for (int i = 0; i < __args.Length; i++) WriteArgument(writer, parameters[i].ParameterType, __args[i]);
                });
            }
            catch (StaleCommandException) { }
            catch (Exception exception) { Fail("command could not be sent: " + exception.Message); }
            return false;
        }

        private static void Execute(MethodInfo method, object[] args)
        {
            Pawn pawn = args.Length != 0 ? args[0] as Pawn : null;
            Job previousJob = pawn?.CurJob;
            LocalTargetInfo previousA = previousJob?.targetA ?? LocalTargetInfo.Invalid;
            LocalTargetInfo previousB = previousJob?.targetB ?? LocalTargetInfo.Invalid;
            LocalTargetInfo previousC = previousJob?.targetC ?? LocalTargetInfo.Invalid;
            Verb previousVerb = previousJob?.verbToUse;
            bool previousForced = previousJob?.playerForced == true;
            bool previousDirectional = pawn?.jobs?.curDriver is JobDriver_RimKataAttack oldDriver && oldDriver.IsDirectionalFire;
            ExecuteExternal(() => method.Invoke(null, args));
            Job currentJob = pawn?.CurJob;
            bool currentDirectional = pawn?.jobs?.curDriver is JobDriver_RimKataAttack newDriver && newDriver.IsDirectionalFire;
            if (!IsHostSession || pawn?.Map != map || currentJob == null
                || currentJob == previousJob && currentJob.targetA == previousA && currentJob.targetB == previousB
                    && currentJob.targetC == previousC && currentJob.verbToUse == previousVerb
                    && currentJob.playerForced == previousForced && currentDirectional == previousDirectional) return;
            // Ownership/Things must exist on the receiving replica before its native
            // Job target decoder runs. The two packets use the same ordered transport.
            byte[] state = CaptureReplica(map);
            if (state != null && state.Length != 0)
                Send(Kind.Replica, peer, writer => { writer.Write(state.Length); writer.Write(state); });
            RimKataMultiplayerTogetherJobs.PublishCurrent(pawn);
        }

        internal static void SendNativeJobs(byte[] payload)
        {
            if (!CanExecuteCommands || !host || payload == null || payload.Length > MaximumPayload)
                throw new InvalidDataException("Together RK job publication is not available.");
            sendJobs(payload);
        }

        internal static void FailExtension(string reason) => Fail(reason);

        internal static void ExecuteExternal(Action invoke, bool markDirty = true)
        {
            executionDepth++;
            try
            {
                if (bypassGetter()) invoke();
                else
                {
                    // Together catches exceptions inside ExecuteInBypass. Preserve
                    // the error so a failed apply cannot leave this wire marked ready.
                    Exception failure = null;
                    inBypass(() => { try { invoke(); } catch (Exception exception) { failure = exception; } });
                    if (failure != null) throw failure;
                }
                if (markDirty)
                {
                    RimKataMultiplayerTogetherSnapshot.MarkDirty(map);
                    nextReplica = 0;
                }
            }
            finally { executionDepth--; }
        }

        private static void Send(Kind kind, string recipient, Action<BinaryWriter> body)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic); writer.Write(Protocol); writer.Write((byte)kind);
                writer.Write(nonce); writer.Write(recipient ?? string.Empty); writer.Write(host);
                writer.Write(++sequence); writer.Write(contract);
                writer.Write(settingsContract ?? string.Empty);
                body?.Invoke(writer);
                if (stream.Length > MaximumPayload) throw new InvalidDataException("Together RK message exceeds its size limit.");
                send(stream.ToArray());
            }
        }

        private static void WriteArgument(BinaryWriter writer, Type type, object value)
        {
            if (typeof(Thing).IsAssignableFrom(type))
            {
                Thing thing = value as Thing;
                writer.Write(thing != null);
                if (thing == null) return;
                if (thing.MapHeld != map) throw new StaleCommandException();
                Pawn owner = (thing.ParentHolder as Pawn_EquipmentTracker)?.pawn;
                writer.Write(owner?.ThingID ?? string.Empty);
                writer.Write(owner == null ? -1 : owner.equipment.AllEquipmentListForReading.IndexOf(thing as ThingWithComps));
                writer.Write(thing.ThingID);
                writer.Write(thing.def.defName);
            }
            else if (type == typeof(IntVec3)) { IntVec3 cell = (IntVec3)value; writer.Write(cell.x); writer.Write(cell.y); writer.Write(cell.z); }
            else if (type == typeof(bool)) writer.Write((bool)value);
            else if (type == typeof(int)) writer.Write((int)value);
            else if (type == typeof(float)) writer.Write((float)value);
            else if (type == typeof(string)) { writer.Write(value != null); if (value != null) writer.Write((string)value); }
            else throw new InvalidDataException("Unsupported command argument.");
        }

        private static object ReadArgument(BinaryReader reader, Type type)
        {
            if (typeof(Thing).IsAssignableFrom(type))
            {
                if (!reader.ReadBoolean()) return null;
                string ownerId = reader.ReadString(); int index = reader.ReadInt32();
                string id = reader.ReadString(); string defName = reader.ReadString();
                Thing thing = null;
                if (ownerId.Length != 0)
                {
                    Pawn owner = FindThing(ownerId) as Pawn;
                    List<ThingWithComps> equipment = owner?.equipment?.AllEquipmentListForReading;
                    if (equipment != null && index >= 0 && index < equipment.Count) thing = equipment[index];
                }
                else thing = FindThing(id);
                if (thing == null || !type.IsInstanceOfType(thing) || thing.ThingID != id || thing.def.defName != defName)
                    throw new StaleCommandException();
                return thing;
            }
            if (type == typeof(IntVec3)) return new IntVec3(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            if (type == typeof(bool)) return reader.ReadBoolean();
            if (type == typeof(int)) return reader.ReadInt32();
            if (type == typeof(float))
            {
                float value = reader.ReadSingle();
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Non-finite command argument.");
                return value;
            }
            if (type == typeof(string)) return reader.ReadBoolean() ? reader.ReadString() : null;
            throw new InvalidDataException("Unsupported command argument.");
        }

        // Shares the session's spawn/ownership index; command decoding performs no pawn scan.
        private static Thing FindThing(string id) => RimKataMultiplayerTogetherSnapshot.FindThing(map, id);

        private static bool Supported(Type type) => type == typeof(Pawn) || type == typeof(Thing)
            || type == typeof(IntVec3) || type == typeof(bool) || type == typeof(int)
            || type == typeof(float) || type == typeof(string);
        private static string Identity(MethodInfo method) => method.DeclaringType.FullName + "." + method.Name
            + "(" + string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName)) + ")";
        private static Type RequiredType(string name) => RimKataActiveModTypes.Find(name) ?? throw new TypeLoadException(name);
        private static Func<T> Getter<T>(Type owner, string name)
            => (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), AccessTools.PropertyGetter(owner, name));
        private static void TickPrefix(out int __state) { __state = tickDepth; tickDepth++; }
        private static Exception TickFinalizer(Exception __exception, int __state) { tickDepth = __state; return __exception; }
        private static void ImportPartyPrefix(out int __state)
        {
            __state = importingParty;
            if (importingParty == 0) arrivingPawns.Clear();
            importingParty++;
        }
        private static Exception ImportPartyFinalizer(Exception __exception, int __state)
        {
            importingParty = __state;
            if (__exception != null && importingParty == 0) arrivingPawns.Clear();
            return __exception;
        }
        private static void Warn(string message)
        {
            if (warned) return;
            warned = true;
            Log.Warning("[RimKata] Together Preview extension unavailable: " + message
                + " The installed Together version has no native visit-only termination API; host combat remains owned by Together's original session.");
        }
        private static void Fail(string message, bool notifyPeer = true)
        {
            if (faulted) return;
            faulted = true;
            if (notifyPeer && peer != null)
            {
                try { Send(Kind.Failure, peer, null); }
                catch { /* The original failure is the useful diagnostic. Do not retry this session. */ }
            }
            Warn(message);
        }
    }
}
