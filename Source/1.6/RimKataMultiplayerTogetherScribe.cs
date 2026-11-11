using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    // A packet-local Scribe context. Existing pawns are references, never deep copies.
    internal static class RimKataMultiplayerTogetherScribe
    {
        private const int Version = 1, MaximumDocument = 16 * 1024 * 1024, MaximumReferences = 32768;
        private static bool installed;
        [ThreadStatic] private static Scope current;
        private static readonly AccessTools.FieldRef<ScribeSaver, XmlWriter> writerField =
            AccessTools.FieldRefAccess<ScribeSaver, XmlWriter>("writer");
        private static readonly AccessTools.FieldRef<CrossRefHandler, LoadedObjectDirectory> directoryField =
            AccessTools.FieldRefAccess<CrossRefHandler, LoadedObjectDirectory>("loadedObjectDirectory");
        private static readonly AccessTools.FieldRef<LoadedObjectDirectory, Dictionary<string, ILoadReferenceable>> objectsField =
            AccessTools.FieldRefAccess<LoadedObjectDirectory, Dictionary<string, ILoadReferenceable>>("allObjectsByLoadID");

        internal static void Initialize(Harmony harmony)
        {
            if (installed) return;
            harmony.Patch(AccessTools.DeclaredMethod(typeof(DebugLoadIDsSavingErrorsChecker), "RegisterReferenced"),
                prefix: new HarmonyMethod(typeof(RimKataMultiplayerTogetherScribe), nameof(Referenced)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(DebugLoadIDsSavingErrorsChecker), "RegisterDeepSaved"),
                prefix: new HarmonyMethod(typeof(RimKataMultiplayerTogetherScribe), nameof(DeepSaved)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Log), "Error", new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(RimKataMultiplayerTogetherScribe), nameof(LoggedError)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Pawn), nameof(Pawn.ExposeData)),
                prefix: new HarmonyMethod(typeof(RimKataMultiplayerTogetherScribe), nameof(PawnDeepGuard)));
            installed = true;
        }

        private static void Referenced(DebugLoadIDsSavingErrorsChecker __instance, ILoadReferenceable obj)
        {
            if (current == null || Scribe.mode != LoadSaveMode.Saving || obj == null
                || __instance != Scribe.saver.loadIDsErrorsChecker) return;
            string id = obj.GetUniqueLoadID();
            if (current.references.TryGetValue(id, out var existing) && !ReferenceEquals(existing, obj))
                throw new InvalidDataException("Duplicate RK snapshot reference: " + id);
            current.references[id] = obj;
            if (current.references.Count > MaximumReferences) throw new InvalidDataException("Too many RK snapshot references.");
        }
        private static void DeepSaved(DebugLoadIDsSavingErrorsChecker __instance, object obj)
        {
            if (current != null && Scribe.mode == LoadSaveMode.Saving
                && __instance == Scribe.saver.loadIDsErrorsChecker && obj is ILoadReferenceable reference)
                current.deep.Add(reference.GetUniqueLoadID());
        }
        private static void LoggedError(string text)
        {
            if (current != null && current.error == null) current.error = text;
        }
        private static void PawnDeepGuard()
        {
            if (current != null) throw new InvalidDataException("A Together RK snapshot cannot deep-copy a pawn.");
        }

        internal static byte[] Save(IExposable root, Map map)
        {
            if (!installed || root == null) throw new InvalidOperationException("Together snapshot Scribe is not ready.");
            using (var scope = new Scope())
            using (var xml = new MemoryStream())
            {
                using (XmlWriter writer = XmlWriter.Create(xml, new XmlWriterSettings
                    { Encoding = new UTF8Encoding(false), CloseOutput = false, Indent = false }))
                {
                    writerField(Scribe.saver) = writer;
                    Scribe.mode = LoadSaveMode.Saving;
                    Scribe.saver.savingForDebug = true;
                    writer.WriteStartElement("RimKataSnapshot");
                    root.ExposeData();
                    writer.WriteEndElement();
                    writer.Flush();
                    writerField(Scribe.saver) = null;
                }
                scope.Check();
                if (xml.Length > MaximumDocument) throw new InvalidDataException("RK snapshot XML is too large.");
                var refs = scope.references.Where(p => !scope.deep.Contains(p.Key))
                    .OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
                using (var output = new MemoryStream())
                {
                    using (var zip = new GZipStream(output, CompressionMode.Compress, true))
                    using (var packet = new BinaryWriter(zip, Encoding.UTF8, true))
                    {
                        packet.Write(Version);
                        packet.Write(refs.Length);
                        foreach (var pair in refs) Describe(pair.Key, pair.Value, scope, map).Write(packet);
                        byte[] bytes = xml.ToArray();
                        packet.Write(bytes.Length);
                        packet.Write(bytes);
                    }
                    if (output.Length > 2 * 1024 * 1024 - 1024)
                        throw new InvalidDataException("RK snapshot packet is too large.");
                    return output.ToArray();
                }
            }
        }

        internal static T Load<T>(byte[] bytes, Map map) where T : class, IExposable
        {
            if (!installed || bytes == null || bytes.Length > 2 * 1024 * 1024)
                throw new InvalidDataException("Invalid RK snapshot packet.");
            byte[] document;
            var descriptors = new List<Reference>();
            using (var raw = new MemoryStream(bytes, false))
            using (var zip = new GZipStream(raw, CompressionMode.Decompress))
            using (var unpacked = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                while ((read = zip.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (unpacked.Length + read > MaximumDocument)
                        throw new InvalidDataException("RK snapshot exceeds the decompression limit.");
                    unpacked.Write(buffer, 0, read);
                }
                unpacked.Position = 0;
                using (var packet = new BinaryReader(unpacked, Encoding.UTF8, true))
                {
                    if (packet.ReadInt32() != Version) throw new InvalidDataException("RK snapshot codec version mismatch.");
                    int count = packet.ReadInt32();
                    if (count < 0 || count > MaximumReferences) throw new InvalidDataException("Invalid RK snapshot reference count.");
                    for (int i = 0; i < count; i++) descriptors.Add(Reference.Read(packet));
                    int length = packet.ReadInt32();
                    if (length < 0 || length != unpacked.Length - unpacked.Position)
                        throw new InvalidDataException("Invalid RK snapshot document length.");
                    document = packet.ReadBytes(length);
                }
            }
            var dom = new XmlDocument { XmlResolver = null };
            using (var xml = XmlReader.Create(new MemoryStream(document, false), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumDocument }))
                dom.Load(xml);
            if (dom.DocumentElement?.Name != "RimKataSnapshot") throw new InvalidDataException("Invalid RK snapshot root.");
            using (var scope = new Scope())
            {
                Dictionary<string, ILoadReferenceable> aliases = objectsField(directoryField(Scribe.loader.crossRefs));
                foreach (Reference descriptor in descriptors)
                {
                    if (aliases.ContainsKey(descriptor.id)) throw new InvalidDataException("Duplicate RK snapshot alias.");
                    // Null explicitly represents an expired projectile/job/target. The
                    // snapshot validates required actor and ownership references before commit.
                    aliases.Add(descriptor.id, Resolve(descriptor, map));
                }
                T result = (T)Activator.CreateInstance(typeof(T), true);
                Scribe.mode = LoadSaveMode.LoadingVars;
                Scribe.loader.curXmlParent = dom.DocumentElement;
                Scribe.loader.curParent = result;
                Scribe.loader.crossRefs.RegisterForCrossRefResolve(result);
                Scribe.loader.initer.RegisterForPostLoadInit(result);
                result.ExposeData();
                scope.Check();
                Scribe.loader.FinalizeLoading();
                scope.Check();
                return result;
            }
        }

        private static Reference Describe(string id, ILoadReferenceable value, Scope scope, Map map)
        {
            var result = new Reference { id = id, type = value.GetType().FullName };
            if (value is Thing thing)
            {
                result.kind = 1; result.owner = thing.ThingID; result.def = thing.def.defName;
                RimKataMultiplayerTogetherSnapshot.RegisterKnownThing(map, thing);
            }
            else if (value is Map sourceMap)
            {
                if (sourceMap != map) throw new InvalidDataException("RK snapshot refers to another map.");
                result.kind = 2;
            }
            else if (value is Verb verb)
            {
                result.kind = 3; result.owner = verb.CasterPawn?.ThingID;
                result.extra = verb.EquipmentSource?.ThingID;
                result.index = verb.EquipmentSource?.TryGetComp<CompEquippable>()?.AllVerbs.IndexOf(verb) ?? -1;
            }
            else if (value is Hediff hediff)
            {
                result.kind = 4; result.owner = hediff.pawn?.ThingID; result.def = hediff.def.defName;
                result.index = hediff.Part == null ? -1 : hediff.pawn.RaceProps.body.AllParts.IndexOf(hediff.Part);
            }
            else if (value is Faction faction)
            {
                result.kind = 5; result.def = faction.def.defName;
            }
            else if (value is Job job)
            {
                result.kind = 6;
                Pawn owner = scope.references.Values.OfType<Pawn>().FirstOrDefault(p => p.CurJob == job);
                result.owner = owner?.ThingID;
                result.def = job.def.defName;
            }
            // Other reference types cannot become arbitrary objects via reflection.
            // They remain explicit null aliases; required references are checked by Snapshot.
            return result;
        }

        private static ILoadReferenceable Resolve(Reference reference, Map map)
        {
            if (reference.kind == 2) return map;
            Thing FindThing(string id) => string.IsNullOrEmpty(id) ? null : RimKataMultiplayerTogetherSnapshot.FindThing(map, id);
            if (reference.kind == 1)
            {
                Thing thing = FindThing(reference.owner);
                return thing?.def.defName == reference.def && thing.GetType().FullName == reference.type ? thing : null;
            }
            Pawn pawn = FindThing(reference.owner) as Pawn;
            if (reference.kind == 3)
            {
                if (pawn == null) return null;
                if (!string.IsNullOrEmpty(reference.extra))
                {
                    var weapon = FindThing(reference.extra) as ThingWithComps;
                    var verbs = weapon?.TryGetComp<CompEquippable>()?.AllVerbs;
                    if (verbs == null || reference.index < 0 || reference.index >= verbs.Count) return null;
                    Verb verb = verbs[reference.index];
                    return verb?.CasterPawn == pawn && verb.GetType().FullName == reference.type
                        && verb.GetUniqueLoadID() == reference.id ? verb : null;
                }
                var melee = pawn.meleeVerbs?.GetUpdatedAvailableVerbsList(true);
                if (melee != null)
                    foreach (var entry in melee)
                        if (entry.verb?.GetUniqueLoadID() == reference.id && entry.verb.GetType().FullName == reference.type)
                            return entry.verb;
                return null;
            }
            if (reference.kind == 4)
            {
                var hediffs = pawn?.health?.hediffSet?.hediffs;
                if (hediffs == null) return null;
                Hediff found = null;
                foreach (Hediff hediff in hediffs)
                {
                    if (hediff.def.defName != reference.def || hediff.GetType().FullName != reference.type) continue;
                    if (hediff.GetUniqueLoadID() == reference.id) return hediff;
                    int part = hediff.Part == null ? -1 : pawn.RaceProps.body.AllParts.IndexOf(hediff.Part);
                    if (part != reference.index) continue;
                    if (found != null) return null;
                    found = hediff;
                }
                return found;
            }
            if (reference.kind == 5)
                return Find.FactionManager?.AllFactionsListForReading.FirstOrDefault(f =>
                    f.GetUniqueLoadID() == reference.id && f.def.defName == reference.def);
            if (reference.kind == 6)
                return pawn?.CurJob?.GetUniqueLoadID() == reference.id && pawn.CurJob.def.defName == reference.def ? pawn.CurJob : null;
            return null;
        }

        private sealed class Reference
        {
            internal string id, owner, extra, type, def;
            internal byte kind;
            internal int index = -1;
            internal void Write(BinaryWriter writer)
            {
                writer.Write(id); writer.Write(kind); writer.Write(owner ?? ""); writer.Write(extra ?? "");
                writer.Write(type ?? ""); writer.Write(def ?? ""); writer.Write(index);
            }
            internal static Reference Read(BinaryReader reader) => new Reference
            {
                id = reader.ReadString(), kind = reader.ReadByte(), owner = reader.ReadString(),
                extra = reader.ReadString(), type = reader.ReadString(), def = reader.ReadString(), index = reader.ReadInt32()
            };
        }

        private sealed class Scope : IDisposable
        {
            private readonly ScribeSaver saver = Scribe.saver;
            private readonly ScribeLoader loader = Scribe.loader;
            private readonly LoadSaveMode mode = Scribe.mode;
            private readonly Scope previous = current;
            internal readonly Dictionary<string, ILoadReferenceable> references = new(StringComparer.Ordinal);
            internal readonly HashSet<string> deep = new(StringComparer.Ordinal);
            internal string error;
            internal Scope()
            {
                if (current != null) throw new InvalidOperationException("Nested RK snapshot serialization.");
                Scribe.saver = new ScribeSaver();
                Scribe.loader = new ScribeLoader();
                Scribe.mode = LoadSaveMode.Inactive;
                current = this;
            }
            internal void Check()
            {
                if (error != null) throw new InvalidDataException("RK snapshot Scribe failed: " + error);
            }
            public void Dispose()
            {
                try { Scribe.saver.ForceStop(); Scribe.loader.ForceStop(); }
                finally { Scribe.saver = saver; Scribe.loader = loader; Scribe.mode = mode; current = previous; }
            }
        }
    }
}
