using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Together replicates the authoritative participant records, not another simulation.
    // The map index is seeded once; later lookups use spawn events and known holders.
    internal static class RimKataMultiplayerTogetherSnapshot
    {
        internal const int SchemaVersion = 1;
        private const int Magic = 0x524B5350, MaximumRecords = 8192, MaximumPacket = 2 * 1024 * 1024;
        private static readonly Dictionary<Map, Context> contexts = new Dictionary<Map, Context>();
        private static bool initialized;
        private static Map applyingMap;
        private static readonly AccessTools.FieldRef<RimKataMapComponent, List<RimKataPawnCombatState>> combatStates =
            AccessTools.FieldRefAccess<RimKataMapComponent, List<RimKataPawnCombatState>>("states");
        private static readonly AccessTools.FieldRef<RimKataSecondaryWeaponRegistry, List<Pawn>> secondaryPawns =
            AccessTools.FieldRefAccess<RimKataSecondaryWeaponRegistry, List<Pawn>>("pawns");
        private static readonly AccessTools.FieldRef<RimKataSecondaryWeaponRegistry, List<ThingWithComps>> secondaryWeapons =
            AccessTools.FieldRefAccess<RimKataSecondaryWeaponRegistry, List<ThingWithComps>>("weapons");
        private static readonly AccessTools.FieldRef<RimKataDownedWeaponRegistry, Dictionary<Pawn, RimKataDownedWeaponState>> crawlStates =
            AccessTools.FieldRefAccess<RimKataDownedWeaponRegistry, Dictionary<Pawn, RimKataDownedWeaponState>>("states");
        private static readonly Action<RimKataSecondaryWeaponRegistry> resetSecondaryLookup =
            AccessTools.MethodDelegate<Action<RimKataSecondaryWeaponRegistry>>(
                AccessTools.DeclaredMethod(typeof(RimKataSecondaryWeaponRegistry), "ResetRegisteredWeaponLookupCache"));
        private static readonly Action<RimKataMapComponent> rebuildMap = AccessTools.MethodDelegate<Action<RimKataMapComponent>>(
            AccessTools.DeclaredMethod(typeof(RimKataMapComponent), "RebuildStateIndex"));
        private static readonly AccessTools.FieldRef<RimKataPushMotion, bool> finishingPush =
            AccessTools.FieldRefAccess<RimKataPushMotion, bool>("finishing");
        private static readonly AccessTools.FieldRef<RimKataThrownPawn, bool> resolvingThrow =
            AccessTools.FieldRefAccess<RimKataThrownPawn, bool>("resolving");

        private sealed class Context
        {
            internal readonly Map map;
            internal readonly Dictionary<string, Thing> known = new Dictionary<string, Thing>(StringComparer.Ordinal);
            internal readonly Dictionary<string, Thing> pending = new Dictionary<string, Thing>(StringComparer.Ordinal);
            internal readonly HashSet<Thing> generated = new HashSet<Thing>();
            internal readonly HashSet<Thing> managed = new HashSet<Thing>();
            internal readonly HashSet<Pawn> positioned = new HashSet<Pawn>();
            internal readonly HashSet<Pawn> received = new HashSet<Pawn>();
            internal int lastTick = int.MinValue, dirty, lastDirty = -1;
            internal bool sentEmpty, receivedReplica;
            internal Context(Map map) { this.map = map; }
        }

        internal static void Initialize(Harmony harmony)
        {
            if (initialized) return;
            RimKataMultiplayerTogetherScribe.Initialize(harmony);
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Thing), nameof(Thing.SpawnSetup)),
                postfix: new HarmonyMethod(typeof(RimKataMultiplayerTogetherSnapshot), nameof(Spawned)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(ThingOwner), "NotifyAdded", new[] { typeof(Thing) }),
                postfix: new HarmonyMethod(typeof(RimKataMultiplayerTogetherSnapshot), nameof(AddedToHolder)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Thing), nameof(Thing.Destroy)),
                postfix: new HarmonyMethod(typeof(RimKataMultiplayerTogetherSnapshot), nameof(Destroyed)));
            initialized = true;
        }

        internal static bool IsApplying(Map map) => map != null && applyingMap == map;
        internal static void MarkDirty(Map map)
        {
            if (map != null && contexts.TryGetValue(map, out Context context)) unchecked { ++context.dirty; }
        }

        private static Context Get(Map map)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (contexts.TryGetValue(map, out Context existing)) return existing;
            var context = new Context(map);
            contexts.Add(map, context);
            try
            {
                var visited = new HashSet<Thing>();
                foreach (Thing thing in map.listerThings.AllThings) RegisterTree(context, thing, visited);
                return context;
            }
            catch { contexts.Remove(map); throw; }
        }

        private static void Spawned(Thing __instance, Map map)
        {
            if (map != null && contexts.TryGetValue(map, out Context context))
                RegisterTree(context, __instance, new HashSet<Thing>());
        }

        private static void AddedToHolder(Thing item)
        {
            Map map = item?.MapHeld;
            if (map != null && contexts.TryGetValue(map, out Context context))
                RegisterTree(context, item, new HashSet<Thing>());
        }

        private static void Destroyed(Thing __instance)
        {
            if (!__instance.Destroyed) return;
            foreach (Context context in contexts.Values)
            {
                if (context.known.TryGetValue(__instance.ThingID, out Thing known) && known == __instance)
                    context.known.Remove(__instance.ThingID);
            }
        }

        internal static void RegisterKnownThing(Map map, Thing thing)
        {
            if (thing == null || thing.Destroyed) return;
            Context context = Get(map);
            if (context.known.TryGetValue(thing.ThingID, out Thing existing) && existing == thing) return;
            RegisterTree(context, thing, new HashSet<Thing>());
        }

        private static void RegisterTree(Context context, Thing thing, HashSet<Thing> visited)
        {
            if (thing == null || thing.Destroyed || !visited.Add(thing)) return;
            string id = thing.ThingID;
            if (context.known.TryGetValue(id, out Thing old) && old != thing && !old.Destroyed)
                throw new InvalidDataException("Together RK has two objects with the same ID: " + id);
            context.known[id] = thing;
            if (thing is Pawn pawn)
            {
                if (pawn.equipment != null)
                    foreach (Thing equipment in pawn.equipment.AllEquipmentListForReading) RegisterTree(context, equipment, visited);
                if (pawn.apparel != null)
                    foreach (Thing apparel in pawn.apparel.WornApparel) RegisterTree(context, apparel, visited);
                RegisterContents(context, pawn.inventory?.innerContainer, visited);
                RegisterContents(context, pawn.carryTracker?.innerContainer, visited);
            }
            else if (thing is IThingHolder holder) RegisterContents(context, holder.GetDirectlyHeldThings(), visited);
        }

        private static void RegisterContents(Context context, ThingOwner owner, HashSet<Thing> visited)
        {
            if (owner == null) return;
            for (int i = 0; i < owner.Count; ++i) RegisterTree(context, owner[i], visited);
        }

        internal static Thing FindThing(Map map, string id)
        {
            if (map == null || string.IsNullOrEmpty(id)) return null;
            Context context = Get(map);
            if (context.pending.TryGetValue(id, out Thing pending)) return pending;
            if (!context.known.TryGetValue(id, out Thing result)) return null;
            if (!result.Destroyed && result.MapHeld == map) return result;
            context.known.Remove(id);
            return null;
        }

        internal static byte[] Capture(Map map)
        {
            if (RimKataMultiplayerTogetherWire.IsReplicaClient(map)) return null;
            Context context = Get(map);
            int tick = Find.TickManager?.TicksGame ?? 0;
            if (context.lastTick == tick && context.lastDirty == context.dirty) return null;
            var packet = CaptureRecords(context, tick);
            bool empty = packet.Empty;
            if (empty && context.sentEmpty)
            { context.lastTick = tick; context.lastDirty = context.dirty; return null; }
            byte[] document = RimKataMultiplayerTogetherScribe.Save(packet, map);
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic); writer.Write(SchemaVersion); writer.Write(packet.special.Count);
                foreach (SpecialRecord special in packet.special)
                { writer.Write(special.id); writer.Write(special.kind); }
                writer.Write(document.Length); writer.Write(document);
                if (stream.Length > MaximumPacket) throw new InvalidDataException("Together RK snapshot exceeds the transport limit.");
                context.lastTick = tick; context.lastDirty = context.dirty; context.sentEmpty = empty;
                return stream.ToArray();
            }
        }

        private static Packet CaptureRecords(Context context, int tick)
        {
            Map map = context.map;
            var packet = new Packet { hostTick = tick };
            var component = map.GetComponent<RimKataMapComponent>();
            if (component != null) packet.combat.AddRange(combatStates(component).Where(s => s?.pawn?.MapHeld == map));
            var breach = Current.Game.GetComponent<RimKataBreachRegistry>();
            if (breach != null) packet.breach.AddRange(breach.states.Values.Where(s => s?.pawn?.MapHeld == map));
            var reactive = Current.Game.GetComponent<RimKataReactiveRegistry>();
            if (reactive != null) packet.reactive.AddRange(reactive.states.Values.Where(s => s?.pawn?.MapHeld == map));
            var subdue = Current.Game.GetComponent<RimKataSubdueRegistry>();
            if (subdue != null) packet.subdue.AddRange(subdue.active.Where(s => s?.pawn?.MapHeld == map));
            var crawl = Current.Game.GetComponent<RimKataDownedWeaponRegistry>();
            if (crawl != null) packet.crawl.AddRange(crawlStates(crawl).Values.Where(s => s?.pawn?.MapHeld == map));
            var secondary = Current.Game.GetComponent<RimKataSecondaryWeaponRegistry>();
            if (secondary != null)
            {
                var pawns = secondaryPawns(secondary); var weapons = secondaryWeapons(secondary);
                for (int i = 0; i < pawns.Count; ++i)
                    if (pawns[i]?.MapHeld == map)
                        packet.secondary.Add(new SecondaryRecord { pawn = pawns[i], secondary = weapons[i], primary = pawns[i].equipment?.Primary });
            }
            packet.qualifications = RimKataMultiplayerTogetherQualifications.Capture(map);
            foreach (Thing thing in map.listerThings.ThingsOfDef(RimKataDefOf.RimKata_PushMotion))
            { SpecialRecord record = SpecialRecord.Capture(thing); if (record != null) packet.special.Add(record); }
            foreach (Thing thing in map.listerThings.ThingsOfDef(RimKataDefOf.RimKata_ThrownPawn))
            { SpecialRecord record = SpecialRecord.Capture(thing); if (record != null) packet.special.Add(record); }

            var positioned = new HashSet<Pawn>();
            foreach (var state in packet.combat)
                if (state.DodgeMovementActive || state.kick != null || state.flyingKick != null) positioned.Add(state.pawn);
            foreach (var state in packet.breach) positioned.Add(state.pawn);
            foreach (var state in packet.reactive) positioned.Add(state.pawn);
            foreach (var state in packet.subdue) { positioned.Add(state.pawn); positioned.Add(state.target); }
            foreach (var special in packet.special) { positioned.Add(special.victim); }
            // A participant leaving a motion needs its final canonical placement once.
            foreach (Pawn pawn in positioned.Concat(context.positioned).Distinct())
                if (pawn != null && !pawn.Destroyed && !pawn.Dead && pawn.MapHeld == map)
                { RegisterKnownThing(map, pawn); packet.placements.Add(Placement.Capture(pawn)); }
            context.positioned.Clear(); context.positioned.UnionWith(positioned.Where(p => p != null));
            var required = new HashSet<Thing>();
            foreach (var state in packet.combat)
            { required.Add(state.pawn); required.Add(state.primaryWeaponCycle?.weapon); required.Add(state.secondaryWeaponCycle?.weapon); }
            foreach (var state in packet.reactive)
            { required.Add(state.pawn); required.Add(state.primaryWeapon); required.Add(state.secondaryWeapon); }
            foreach (var state in packet.subdue) { required.Add(state.pawn); required.Add(state.target); }
            foreach (var state in packet.crawl) { required.Add(state.pawn); required.Add(state.weapon); }
            foreach (var state in packet.secondary) { required.Add(state.pawn); required.Add(state.primary); required.Add(state.secondary); }
            foreach (var place in packet.placements) { required.Add(place.pawn); required.Add(place.carrier); required.Add(place.container); }
            foreach (var special in packet.special) required.Add(special.victim);
            packet.required.AddRange(required.Where(t => t != null && !t.Destroyed));
            return packet;
        }

        internal static void Apply(Map map, byte[] bytes)
        {
            if (!RimKataMultiplayerTogetherWire.IsReplicaClient(map))
                throw new InvalidOperationException("RK snapshot received for an authoritative map.");
            if (bytes == null || bytes.Length > MaximumPacket) throw new InvalidDataException("Invalid RK snapshot size.");
            Context context = Get(map);
            var headers = new Dictionary<int, byte>();
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != SchemaVersion) throw new InvalidDataException("Wrong RK snapshot schema.");
                    int count = reader.ReadInt32(); CheckCount(count);
                    for (int i = 0; i < count; ++i)
                    {
                        int id = reader.ReadInt32(); byte kind = reader.ReadByte();
                        if (id <= 0 || (kind != 1 && kind != 2) || headers.ContainsKey(id)) throw new InvalidDataException("Invalid RK motion identity.");
                        headers.Add(id, kind);
                        ThingDef def = kind == 1 ? RimKataDefOf.RimKata_PushMotion : RimKataDefOf.RimKata_ThrownPawn;
                        string loadId = def.defName + id;
                        Thing existing = FindThing(map, loadId);
                        if (existing != null)
                        {
                            if (existing.def != def || existing.MapHeld != map) throw new InvalidDataException("Conflicting RK motion identity.");
                            context.pending.Add(loadId, existing);
                        }
                        else
                        {
                            // Construct without ThingMaker: no new global ID is consumed on a guest.
                            Thing candidate = kind == 1 ? (Thing)new RimKataPushMotion() : new RimKataThrownPawn();
                            candidate.def = def; candidate.thingIDNumber = id;
                            context.pending.Add(loadId, candidate);
                        }
                    }
                    int length = reader.ReadInt32();
                    if (length <= 0 || length != stream.Length - stream.Position) throw new InvalidDataException("Invalid RK snapshot document.");
                    Packet packet = RimKataMultiplayerTogetherScribe.Load<Packet>(reader.ReadBytes(length), map);
                    Validate(context, packet, headers);
                    Commit(context, packet);
                }
            }
            finally { context.pending.Clear(); }
        }

        private static void CheckCount(int count)
        { if (count < 0 || count > MaximumRecords) throw new InvalidDataException("Invalid RK participant count."); }

        private static void Validate(Context context, Packet packet, Dictionary<int, byte> headers)
        {
            if (packet == null || packet.hostTick < 0) throw new InvalidDataException("Invalid RK snapshot.");
            ValidatePawns(packet.combat, s => s?.pawn, context.map);
            ValidatePawns(packet.breach, s => s?.pawn, context.map);
            ValidatePawns(packet.reactive, s => s?.pawn, context.map);
            ValidatePawns(packet.subdue, s => s?.pawn, context.map);
            ValidatePawns(packet.crawl, s => s?.pawn, context.map);
            ValidatePawns(packet.secondary, s => s?.pawn, context.map);
            ValidatePawns(packet.qualifications, s => s?.pawn, context.map);
            RimKataMultiplayerTogetherQualifications.Validate(packet.qualifications);
            ValidatePawns(packet.placements, s => s?.pawn, context.map);
            if (packet.required == null) throw new InvalidDataException("Missing RK required references.");
            CheckCount(packet.required.Count);
            if (packet.required.Any(t => t == null || t.Destroyed)) throw new InvalidDataException("An RK participant or held weapon is missing.");
            CheckCount(packet.special.Count);
            if (packet.special.Count != headers.Count) throw new InvalidDataException("RK motion header mismatch.");
            var specials = new HashSet<int>();
            var held = new HashSet<Pawn>();
            foreach (SpecialRecord special in packet.special)
            {
                if (special == null || !specials.Add(special.id) || !headers.TryGetValue(special.id, out byte kind)
                    || kind != special.kind || !special.position.InBounds(context.map) || !special.rotation.IsValid
                    || special.victim == null || special.victim.Destroyed || special.elapsed < 0)
                    throw new InvalidDataException("Unresolved RK motion participant.");
                if (kind == 2 && !held.Add(special.victim)) throw new InvalidDataException("Duplicate RK held pawn.");
            }
            foreach (Placement place in packet.placements)
            {
                if (!place.position.InBounds(context.map) || !place.rotation.IsValid || place.kind < 0 || place.kind > 2)
                    throw new InvalidDataException("Invalid RK pawn placement.");
                if (place.kind == 1 && (place.carrier?.carryTracker == null || place.carrier == place.pawn))
                    throw new InvalidDataException("Unresolved RK carrier.");
                if (place.kind == 2 && !(place.container is RimKataThrownPawn))
                    throw new InvalidDataException("Unresolved RK throwing container.");
                if (place.kind != 0 && place.pawn.Dead) throw new InvalidDataException("A dead pawn cannot become an RK held pawn.");
                ThingOwner destination = place.Destination;
                if (destination != null)
                    for (int i = 0; i < destination.Count; ++i)
                    {
                        Thing existing = destination[i];
                        if (existing == place.pawn) continue;
                        if (!(existing is Pawn other) || !packet.placements.Any(p => p.pawn == other && p.Destination != destination))
                            throw new InvalidDataException("RK held ownership conflicts with an existing occupant.");
                    }
            }
            foreach (var state in packet.subdue)
            {
                if (state.target == null || !held.Add(state.target)
                    || !packet.placements.Any(p => p.pawn == state.target && p.kind == 1 && p.carrier == state.pawn))
                    throw new InvalidDataException("Unresolved RK subdue relation.");
            }
            foreach (var special in packet.special)
                if (special.kind == 2 && !packet.placements.Any(p => p.pawn == special.victim && p.kind == 2
                    && p.container?.thingIDNumber == special.id)) throw new InvalidDataException("Unresolved RK thrown ownership.");
            foreach (SecondaryRecord secondary in packet.secondary)
            {
                var equipment = secondary.pawn.equipment?.AllEquipmentListForReading;
                if (secondary.secondary == null || equipment == null || !equipment.Contains(secondary.secondary)
                    || secondary.primary == null || !equipment.Contains(secondary.primary) || secondary.primary == secondary.secondary)
                    throw new InvalidDataException("Unresolved RK secondary equipment.");
            }
        }

        private static void ValidatePawns<T>(List<T> values, Func<T, Pawn> owner, Map map)
        {
            if (values == null) throw new InvalidDataException("Missing RK participant list.");
            CheckCount(values.Count);
            var seen = new HashSet<Pawn>();
            foreach (T value in values)
            {
                Pawn pawn = owner(value);
                if (pawn == null || pawn.Destroyed || !seen.Add(pawn) || (pawn.MapHeld != null && pawn.MapHeld != map))
                    throw new InvalidDataException("Unresolved or duplicate RK pawn reference.");
            }
        }

        private static bool Belongs(Context context, Pawn pawn)
            => pawn != null && (pawn.MapHeld == context.map || (pawn.MapHeld == null && context.received.Contains(pawn)));

        private static void Merge<T>(Dictionary<Pawn, T> destination, List<T> incoming, Func<T, Pawn> owner, Context context)
        {
            foreach (Pawn pawn in destination.Keys.Where(p => Belongs(context, p)).ToArray()) destination.Remove(pawn);
            foreach (T state in incoming) destination.Add(owner(state), state);
        }

        private static void Commit(Context context, Packet packet, bool ending = false)
        {
            Map map = context.map;
            Map previousApplying = applyingMap;
            applyingMap = map;
            try
            {
                var component = map.GetComponent<RimKataMapComponent>();
                var breach = Current.Game.GetComponent<RimKataBreachRegistry>();
                var reactive = Current.Game.GetComponent<RimKataReactiveRegistry>();
                var subdue = Current.Game.GetComponent<RimKataSubdueRegistry>();
                var secondary = Current.Game.GetComponent<RimKataSecondaryWeaponRegistry>();
                var crawl = Current.Game.GetComponent<RimKataDownedWeaponRegistry>();
                if (component == null || breach == null || reactive == null || subdue == null || secondary == null || crawl == null)
                    throw new InvalidOperationException("RK replica components are unavailable.");
                foreach (var old in subdue.active) if (Belongs(context, old.pawn)) old.Detach();
                // Parent holders must exist before canonical pawns are moved into them.
                foreach (SpecialRecord record in packet.special)
                {
                    Thing thing = context.pending[record.LoadId];
                    record.ApplyFields(thing);
                    if (!thing.Spawned)
                    { GenSpawn.Spawn(thing, record.position, map, record.rotation); context.generated.Add(thing); }
                    else { thing.Position = record.position; thing.Rotation = record.rotation; }
                    context.managed.Add(thing);
                }
                foreach (Placement placement in packet.placements) placement.DetachForTransfer();
                foreach (Placement placement in packet.placements) placement.Apply(map);
                var liveSpecials = new HashSet<int>(packet.special.Select(s => s.id));
                foreach (Thing old in (ending ? context.generated : context.managed)
                    .Where(t => !liveSpecials.Contains(t.thingIDNumber)).ToArray())
                    RemoveGenerated(context, old);

                int offset = (Find.TickManager?.TicksGame ?? 0) - packet.hostTick;
                foreach (var state in packet.reactive) state.startTick = Shift(state.startTick, offset);
                foreach (var state in packet.subdue)
                {
                    state.struggleStartTick = Shift(state.struggleStartTick, offset);
                    RimKataMultiplayerReplica.AdjustVisualTicks(state, offset);
                }
                combatStates(component) = packet.combat;
                Merge(breach.states, packet.breach, s => s.pawn, context);
                breach.waiting.RemoveWhere(p => Belongs(context, p));
                foreach (var state in packet.breach) if (state.phase == BreachPhase.Wait) breach.waiting.Add(state.pawn);
                Merge(reactive.states, packet.reactive, s => s.pawn, context);
                Merge(subdue.states, packet.subdue, s => s.pawn, context);
                subdue.active.RemoveAll(s => Belongs(context, s.pawn)); subdue.active.AddRange(packet.subdue);
                Merge(crawlStates(crawl), packet.crawl, s => s.pawn, context);
                var pawns = secondaryPawns(secondary); var weapons = secondaryWeapons(secondary);
                for (int i = pawns.Count - 1; i >= 0; --i)
                    if (Belongs(context, pawns[i])) { pawns.RemoveAt(i); weapons.RemoveAt(i); }
                foreach (SecondaryRecord record in packet.secondary)
                {
                    record.ApplyOrder(); pawns.Add(record.pawn); weapons.Add(record.secondary);
                }
                resetSecondaryLookup(secondary);
                RimKataMultiplayerTogetherQualifications.Apply(map, packet.qualifications);
                RimKataMultiplayerReplica.PublishMap(component);
                RimKataMultiplayerReplica.PublishBreach(breach);
                RimKataMultiplayerReplica.PublishReactive(reactive);
                RimKataMultiplayerReplica.PublishSubdue(subdue);
                RimKataMultiplayerReplica.PublishSecondary(secondary);
                RimKataMultiplayerReplica.PublishCrawl(crawl);
                foreach (SpecialRecord record in packet.special) RimKataMultiplayerReplica.PublishSpecial(context.pending[record.LoadId]);
                context.received.Clear();
                foreach (var state in packet.combat) context.received.Add(state.pawn);
                foreach (var state in packet.breach) context.received.Add(state.pawn);
                foreach (var state in packet.reactive) context.received.Add(state.pawn);
                foreach (var state in packet.subdue) { context.received.Add(state.pawn); context.received.Add(state.target); }
                foreach (var state in packet.secondary) context.received.Add(state.pawn);
                foreach (var state in packet.crawl) context.received.Add(state.pawn);
                foreach (var place in packet.placements) context.received.Add(place.pawn);
                context.receivedReplica = true;
            }
            finally { applyingMap = previousApplying; }
        }

        private static int Shift(int tick, int offset) => tick < 0 ? tick
            : (int)Math.Max(0L, Math.Min(int.MaxValue, (long)tick + offset));

        private static void RemoveGenerated(Context context, Thing thing)
        {
            if (thing is RimKataPushMotion push) finishingPush(push) = true;
            if (thing is RimKataThrownPawn thrown)
            {
                resolvingThrow(thrown) = true;
                ThingOwner contents = thrown.GetDirectlyHeldThings();
                for (int i = contents.Count - 1; i >= 0; --i)
                {
                    Thing held = contents[i]; contents.Remove(held);
                    PreserveDetached(held, thing.Position, context.map);
                }
            }
            if (!thing.Destroyed) thing.Destroy();
            context.generated.Remove(thing); context.managed.Remove(thing); context.known.Remove(thing.ThingID);
        }

        private static void PreserveDetached(Thing thing, IntVec3 cell, Map map)
        {
            if (thing.Destroyed || thing.Spawned) return;
            if (Find.Maps.Contains(map) && cell.InBounds(map)) GenSpawn.Spawn(thing, cell, map, thing.Rotation);
            else if (thing is Pawn pawn && !Find.WorldPawns.Contains(pawn))
                Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Decide);
        }

        internal static void Clear(Map map)
        {
            if (map == null || !contexts.TryGetValue(map, out Context context)) return;
            // The host's live state is never changed by ending the replication channel.
            if (context.receivedReplica)
            {
                var empty = new Packet { hostTick = Find.TickManager?.TicksGame ?? 0 };
                var subdue = Current.Game?.GetComponent<RimKataSubdueRegistry>();
                if (subdue != null)
                    foreach (var state in subdue.active)
                        if (Belongs(context, state.pawn) && state.target?.MapHeld == map && !state.target.Spawned
                            && state.target.ParentHolder == state.pawn.carryTracker)
                        {
                            if (Find.Maps.Contains(map))
                                empty.placements.Add(new Placement { pawn = state.target, position = state.target.PositionHeld, rotation = state.target.Rotation });
                            else
                            {
                                state.Detach();
                                state.target.holdingOwner?.Remove(state.target);
                                PreserveDetached(state.target, state.pawn.PositionHeld, map);
                            }
                        }
                Commit(context, empty, ending: true);
                foreach (Thing thing in context.generated.ToArray()) RemoveGenerated(context, thing);
                // CleanValues can already have cleared Together's session map. Rebuilding an
                // empty local index performs no combat restore and removes stale display entries.
                if (!RimKataMultiplayerTogetherWire.IsReplicaClient(map))
                {
                    RimKataResponseVisualParticipantCache.ClearForMap(map);
                    var component = map.GetComponent<RimKataMapComponent>();
                    if (component != null) rebuildMap(component);
                }
                RimKataMultiplayerTogetherQualifications.Clear(map);
            }
            contexts.Remove(map);
        }

        internal sealed class Packet : IExposable
        {
            public int hostTick;
            public List<RimKataPawnCombatState> combat = new List<RimKataPawnCombatState>();
            public List<RimKataBreachState> breach = new List<RimKataBreachState>();
            public List<RimKataReactiveMotionState> reactive = new List<RimKataReactiveMotionState>();
            public List<RimKataSubdueState> subdue = new List<RimKataSubdueState>();
            public List<RimKataDownedWeaponState> crawl = new List<RimKataDownedWeaponState>();
            public List<SecondaryRecord> secondary = new List<SecondaryRecord>();
            public List<SpecialRecord> special = new List<SpecialRecord>();
            public List<Placement> placements = new List<Placement>();
            public List<RimKataMultiplayerTogetherQualifications.View> qualifications = new List<RimKataMultiplayerTogetherQualifications.View>();
            public List<Thing> required = new List<Thing>();
            internal bool Empty => combat.Count + breach.Count + reactive.Count + subdue.Count + crawl.Count
                + secondary.Count + special.Count + placements.Count + qualifications.Count == 0;
            public void ExposeData()
            {
                Scribe_Values.Look(ref hostTick, "hostTick");
                Scribe_Collections.Look(ref combat, "combat", LookMode.Deep);
                Scribe_Collections.Look(ref breach, "breach", LookMode.Deep);
                Scribe_Collections.Look(ref reactive, "reactive", LookMode.Deep);
                Scribe_Collections.Look(ref subdue, "subdue", LookMode.Deep);
                Scribe_Collections.Look(ref crawl, "crawl", LookMode.Deep);
                Scribe_Collections.Look(ref secondary, "secondary", LookMode.Deep);
                Scribe_Collections.Look(ref special, "special", LookMode.Deep);
                Scribe_Collections.Look(ref placements, "placements", LookMode.Deep);
                Scribe_Collections.Look(ref qualifications, "qualifications", LookMode.Deep);
                Scribe_Collections.Look(ref required, "required", LookMode.Reference);
            }
        }

        internal sealed class SecondaryRecord : IExposable
        {
            public Pawn pawn;
            public ThingWithComps primary, secondary;
            public void ExposeData()
            {
                Scribe_References.Look(ref pawn, "pawn"); Scribe_References.Look(ref primary, "primary");
                Scribe_References.Look(ref secondary, "secondary");
            }
            internal void ApplyOrder()
            {
                var equipment = pawn.equipment.AllEquipmentListForReading;
                int index = equipment.IndexOf(primary);
                if (index <= 0) return;
                ThingWithComps old = equipment[0]; equipment[0] = primary; equipment[index] = old;
            }
        }

        internal sealed class Placement : IExposable
        {
            public Pawn pawn, carrier;
            public Thing container;
            public IntVec3 position;
            public Rot4 rotation;
            public int kind;
            internal ThingOwner Destination => kind == 1 ? carrier.carryTracker.innerContainer
                : kind == 2 ? ((RimKataThrownPawn)container).GetDirectlyHeldThings() : null;
            internal static Placement Capture(Pawn pawn)
            {
                var result = new Placement { pawn = pawn, position = pawn.PositionHeld, rotation = pawn.Rotation };
                if (pawn.Spawned) return result;
                if (pawn.ParentHolder is Pawn_CarryTracker tracker)
                { result.kind = 1; result.carrier = tracker.pawn; }
                else if (pawn.ParentHolder is RimKataThrownPawn thrown)
                { result.kind = 2; result.container = thrown; }
                else throw new InvalidDataException("Unsupported RK pawn holder.");
                return result;
            }
            public void ExposeData()
            {
                Scribe_References.Look(ref pawn, "pawn"); Scribe_References.Look(ref carrier, "carrier");
                Scribe_References.Look(ref container, "container"); Scribe_Values.Look(ref kind, "kind");
                Scribe_Values.Look(ref position, "position"); Scribe_Values.Look(ref rotation, "rotation");
            }
            internal void Apply(Map map)
            {
                ThingOwner desired = Destination;
                if (desired != null)
                {
                    if (pawn.Spawned) pawn.DeSpawn();
                    if (pawn.holdingOwner != desired)
                    {
                        pawn.holdingOwner?.Remove(pawn);
                        if (!desired.TryAdd(pawn, false)) throw new InvalidOperationException("RK canonical pawn transfer failed.");
                    }
                }
                else
                {
                    pawn.holdingOwner?.Remove(pawn);
                    if (!pawn.Spawned) GenSpawn.Spawn(pawn, position, map, rotation);
                    else if (pawn.Position != position) { pawn.Position = position; pawn.Notify_Teleported(false, true); }
                }
                pawn.Rotation = rotation;
            }
            internal void DetachForTransfer()
            {
                ThingOwner desired = Destination;
                if (desired != null && pawn.Spawned) pawn.DeSpawn();
                if (pawn.holdingOwner != desired) pawn.holdingOwner?.Remove(pawn);
            }
        }

        internal sealed class SpecialRecord : IExposable
        {
            public int id, elapsed, crossedCells, stunTicks;
            public byte kind;
            public Pawn victim, actor;
            public IntVec3 position, origin, pushDirection;
            public Rot4 rotation, pushFacing;
            public Vector3 start, end, launchOffset;
            public bool pushed, wasDrafted, fireAtWill;
            public DamageDef damage;
            internal string LoadId => (kind == 1 ? RimKataDefOf.RimKata_PushMotion : RimKataDefOf.RimKata_ThrownPawn).defName + id;
            private static AccessTools.FieldRef<T, V> Field<T, V>(string name) where T : class => AccessTools.FieldRefAccess<T, V>(name);
            private static readonly AccessTools.FieldRef<RimKataPushMotion, Pawn> pushVictim = Field<RimKataPushMotion, Pawn>("victim"), pushActor = Field<RimKataPushMotion, Pawn>("attacker");
            private static readonly AccessTools.FieldRef<RimKataPushMotion, Vector3> pushStart = Field<RimKataPushMotion, Vector3>("start"), pushEnd = Field<RimKataPushMotion, Vector3>("end");
            private static readonly AccessTools.FieldRef<RimKataPushMotion, DamageDef> pushDamage = Field<RimKataPushMotion, DamageDef>("damageDef");
            private static readonly AccessTools.FieldRef<RimKataPushMotion, int> pushElapsed = Field<RimKataPushMotion, int>("elapsed");
            private static readonly AccessTools.FieldRef<RimKataThrownPawn, Pawn> throwActor = Field<RimKataThrownPawn, Pawn>("thrower");
            private static readonly AccessTools.FieldRef<RimKataThrownPawn, Vector3> throwOffset = Field<RimKataThrownPawn, Vector3>("launchOffset");
            private static readonly AccessTools.FieldRef<RimKataThrownPawn, IntVec3> throwOrigin = Field<RimKataThrownPawn, IntVec3>("origin"), throwDirection = Field<RimKataThrownPawn, IntVec3>("pushDirection");
            private static readonly AccessTools.FieldRef<RimKataThrownPawn, Rot4> throwFacing = Field<RimKataThrownPawn, Rot4>("pushFacing");
            private static readonly AccessTools.FieldRef<RimKataThrownPawn, int> throwElapsed = Field<RimKataThrownPawn, int>("elapsed"), throwCrossed = Field<RimKataThrownPawn, int>("crossedCells"), throwStun = Field<RimKataThrownPawn, int>("stunTicks");
            private static readonly AccessTools.FieldRef<RimKataThrownPawn, bool> throwPushed = Field<RimKataThrownPawn, bool>("pushed"), throwDrafted = Field<RimKataThrownPawn, bool>("wasDrafted"), throwFireAtWill = Field<RimKataThrownPawn, bool>("fireAtWill");
            private static readonly AccessTools.FieldRef<RimKataThrownPawn, DamageDef> throwDamage = Field<RimKataThrownPawn, DamageDef>("pushDamage");

            internal static SpecialRecord Capture(Thing thing)
            {
                var record = new SpecialRecord { id = thing.thingIDNumber, position = thing.Position, rotation = thing.Rotation };
                if (thing is RimKataPushMotion push)
                {
                    if (finishingPush(push) || pushVictim(push)?.Spawned != true
                        || pushVictim(push).CurJobDef != RimKataDefOf.RimKata_Pushed
                        || pushVictim(push).CurJob.targetA.Thing != push) return null;
                    record.kind = 1; record.victim = pushVictim(push); record.actor = pushActor(push);
                    record.start = pushStart(push); record.end = pushEnd(push); record.damage = pushDamage(push); record.elapsed = pushElapsed(push);
                }
                else if (thing is RimKataThrownPawn thrown)
                {
                    ThingOwner contents = thrown.GetDirectlyHeldThings();
                    if (resolvingThrow(thrown) || contents.Count != 1 || !(contents[0] is Pawn)) return null;
                    record.kind = 2; record.victim = contents.Count == 1 ? contents[0] as Pawn : null; record.actor = throwActor(thrown);
                    record.origin = throwOrigin(thrown); record.launchOffset = throwOffset(thrown); record.elapsed = throwElapsed(thrown);
                    record.crossedCells = throwCrossed(thrown); record.stunTicks = throwStun(thrown); record.pushed = throwPushed(thrown);
                    record.wasDrafted = throwDrafted(thrown); record.fireAtWill = throwFireAtWill(thrown);
                    record.pushDirection = throwDirection(thrown); record.pushFacing = throwFacing(thrown); record.damage = throwDamage(thrown);
                }
                return record;
            }
            internal void ApplyFields(Thing thing)
            {
                if (thing is RimKataPushMotion push)
                {
                    pushVictim(push) = victim; pushActor(push) = actor; pushStart(push) = start; pushEnd(push) = end;
                    pushDamage(push) = damage; pushElapsed(push) = elapsed;
                }
                else if (thing is RimKataThrownPawn thrown)
                {
                    throwActor(thrown) = actor; throwOrigin(thrown) = origin; throwOffset(thrown) = launchOffset;
                    throwElapsed(thrown) = elapsed; throwCrossed(thrown) = crossedCells; throwStun(thrown) = stunTicks;
                    throwPushed(thrown) = pushed; throwDrafted(thrown) = wasDrafted; throwFireAtWill(thrown) = fireAtWill;
                    throwDirection(thrown) = pushDirection; throwFacing(thrown) = pushFacing; throwDamage(thrown) = damage;
                }
            }
            public void ExposeData()
            {
                Scribe_Values.Look(ref id, "id"); Scribe_Values.Look(ref kind, "kind");
                Scribe_References.Look(ref victim, "victim"); Scribe_References.Look(ref actor, "actor");
                Scribe_Values.Look(ref position, "position"); Scribe_Values.Look(ref rotation, "rotation");
                Scribe_Values.Look(ref start, "start"); Scribe_Values.Look(ref end, "end");
                Scribe_Values.Look(ref origin, "origin"); Scribe_Values.Look(ref launchOffset, "launchOffset");
                Scribe_Values.Look(ref elapsed, "elapsed"); Scribe_Values.Look(ref crossedCells, "crossedCells");
                Scribe_Values.Look(ref stunTicks, "stunTicks"); Scribe_Values.Look(ref pushed, "pushed");
                Scribe_Values.Look(ref wasDrafted, "wasDrafted"); Scribe_Values.Look(ref fireAtWill, "fireAtWill");
                Scribe_Values.Look(ref pushDirection, "pushDirection"); Scribe_Values.Look(ref pushFacing, "pushFacing");
                Scribe_Defs.Look(ref damage, "damage");
            }
        }
    }
}
