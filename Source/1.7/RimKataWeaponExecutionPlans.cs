using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Xml;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    internal enum RimKataWeaponExecutionMode { Vanilla, Traced, NeedsRegistration, Live, Copied }
    internal enum RimKataWeaponEffectStatus { NotDetected, Connected, Original }

    internal sealed class RimKataWeaponExecutionPlan
    {
        internal ThingDef Def;
        internal Type VerbType;
        internal string Key;
        internal string Fingerprint;
        internal RimKataWeaponExecutionMode Mode;
        internal bool AllowConversion;
        internal bool HandledByCompatibility;
        internal string Reason;
        internal MethodInfo WarmupMethod;
        internal MethodInfo TickMethod;
        internal MethodInfo BurstCountMethod;
        internal MethodInfo BurstSpacingMethod;
        internal MethodInfo[] Methods = Array.Empty<MethodInfo>();
        internal MethodInfo[] BurstMethods = Array.Empty<MethodInfo>();
        internal Func<Verb, int> BurstCountReader;
        internal Func<Verb, int> BurstSpacingReader;
        internal bool AnalysisComplete;
        internal bool CanControlBurst;
        internal bool RegistrationAccepted;
        internal bool RegistrationDeclined;
        // Session-only: discovery/cache success is not proof that runtime connections succeeded.
        internal bool FiringReady;
        // Firing registration never implies that weapon-owned effects were connected.
        internal RimKataWeaponEffectStatus EffectStatus;
        internal string EffectReason;
        internal string EffectCacheFingerprint;
        internal string EffectCacheSnapshot;
        internal bool Loaded;
        internal bool Failed;
        internal bool CacheStored;
        internal int Residents;
    }

    // Only startup/settings preparation mutates plans. Combat consumers perform a lookup
    // and call prepared delegates on their existing Verb; discovery never creates a pawn.
    internal static class RimKataWeaponExecutionPlans
    {
        private const string Category = "weapon_execution";
        private const string Schema = "1";
        private static readonly Dictionary<ThingDef, Dictionary<Type, RimKataWeaponExecutionPlan>> ByDefinition =
            new Dictionary<ThingDef, Dictionary<Type, RimKataWeaponExecutionPlan>>();
        private static readonly List<RimKataWeaponExecutionPlan> All = new List<RimKataWeaponExecutionPlan>();
        private static readonly List<RimKataWeaponExecutionPlan> Pending = new List<RimKataWeaponExecutionPlan>();
        private static readonly Dictionary<string, Module> Modules = new Dictionary<string, Module>(StringComparer.Ordinal);
        private static readonly Dictionary<MethodInfo, string> MethodFingerprints = new Dictionary<MethodInfo, string>();
        private static readonly Dictionary<MethodInfo, Func<Verb, int>> Readers = new Dictionary<MethodInfo, Func<Verb, int>>();
        private static readonly Dictionary<string, string> Failures = new Dictionary<string, string>(StringComparer.Ordinal);
        private static Func<Type, bool> compatibilityHandler;
        private static bool initialized;

        internal static IReadOnlyList<RimKataWeaponExecutionPlan> Plans => All;
        internal static IReadOnlyList<RimKataWeaponExecutionPlan> PendingRegistration => Pending;
        internal static int Revision { get; private set; }

        internal static void SetCompatibilityHandler(Func<Type, bool> handler) => compatibilityHandler = handler;

        internal static void Initialize(Harmony harmony)
        {
            if (initialized || harmony == null) return;
            initialized = true;
            Refresh();
        }

        internal static RimKataWeaponExecutionPlan Get(ThingDef def, Type verbType)
        {
            if (def == null || verbType == null || !ByDefinition.TryGetValue(def, out var types)) return null;
            return types.TryGetValue(verbType, out var plan) ? plan : null;
        }

        internal static IEnumerable<RimKataWeaponExecutionPlan> ForDefinition(ThingDef def)
            => def != null && ByDefinition.TryGetValue(def, out var types)
                ? (IEnumerable<RimKataWeaponExecutionPlan>)types.Values : Array.Empty<RimKataWeaponExecutionPlan>();

        internal static HashSet<ThingDef> ChangedDefinitions()
        {
            var selected = new HashSet<string>(RimKataMod.Settings?.enabledWeaponDefNames
                ?? new List<string>(), StringComparer.Ordinal);
            var changed = new HashSet<ThingDef>();
            foreach (ThingDef def in ByDefinition.Keys)
                if (!selected.Contains(def.defName)) changed.Add(def);
            foreach (string name in selected)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                if (def?.Verbs != null && !ByDefinition.ContainsKey(def)) changed.Add(def);
            }
            return changed;
        }

        internal static bool IsCurrent(RimKataWeaponExecutionPlan plan)
            => plan != null && ReferenceEquals(Get(plan.Def, plan.VerbType), plan);

        internal static bool IsRegistrationStored(RimKataWeaponExecutionPlan plan)
            => plan.CacheStored && RimKataEquipmentMemory.Current?.Contains(Category, plan.Key, BaseFingerprint(plan)) == true;

        // Only startup, a confirmed manual refresh, or newly enabled definitions enter discovery.
        internal static List<RimKataWeaponExecutionPlan> Refresh(bool force = false)
        {
            Revision++;
            var selected = RimKataMod.Settings?.enabledWeaponDefNames;
            var allowed = new HashSet<string>(selected ?? new List<string>(), StringComparer.Ordinal);
            var prepared = new List<RimKataWeaponExecutionPlan>();
            if (force) Failures.Clear();
            foreach (ThingDef def in new List<ThingDef>(ByDefinition.Keys))
            {
                bool disabled = !allowed.Contains(def.defName);
                if (!force && !disabled) continue;
                foreach (RimKataWeaponExecutionPlan previous in ByDefinition[def].Values)
                {
                    RimKataEquipmentMemory.Current?.Remove(Category, previous.Key);
                    RimKataWeaponEffectCache.Invalidate(previous.Key);
                    Failures.Remove(previous.Key);
                    All.Remove(previous);
                    Pending.Remove(previous);
                }
                ByDefinition.Remove(def);
                if (disabled) RimKataCloseFireMeleeStore.RemoveDefinition(def.defName);
            }
            MethodFingerprints.Clear();
            Readers.Clear();
            if (selected == null) return prepared;
            bool modulesReady = false;
            var scanned = new Dictionary<Type, RimKataWeaponExecutionDiscovery.Result>();
            using (RimKataStartupDiagnostics.Measure("discovery", "weapon_execution"))
            foreach (string name in selected)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                if (def?.Verbs == null || ByDefinition.ContainsKey(def)) continue;
                var types = new Dictionary<Type, RimKataWeaponExecutionPlan>();
                ByDefinition.Add(def, types);
                foreach (VerbProperties properties in def.Verbs)
                {
                    Type type = properties?.verbClass;
                    if (properties == null || properties.IsMeleeAttack || type == null
                        || !typeof(Verb).IsAssignableFrom(type) || types.ContainsKey(type)) continue;
                    if (!modulesReady)
                    {
                        Modules.Clear();
                        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                            if (!assembly.IsDynamic)
                                foreach (Module module in assembly.GetModules())
                                    Modules[assembly.FullName + "|" + module.ModuleVersionId.ToString("D")] = module;
                        modulesReady = true;
                    }
                    var plan = new RimKataWeaponExecutionPlan
                    {
                        Def = def, VerbType = type,
                        Key = RimKataEquipmentMemory.Fingerprint(def.defName + "\n" + TypeIdentity(type)),
                        HandledByCompatibility = compatibilityHandler?.Invoke(type) == true
                    };
                    RimKataEquipmentOwnership.RememberExecution(def, plan.Key);
                    try
                    {
                        if (Failures.TryGetValue(plan.Key, out string failure))
                        {
                            plan.Failed = true;
                            plan.Mode = RimKataWeaponExecutionMode.Live;
                            plan.Reason = failure;
                        }
                        else
                        {
                            if (force)
                            {
                                RimKataEquipmentMemory.Current?.Remove(Category, plan.Key);
                                RimKataWeaponEffectCache.Invalidate(plan.Key);
                            }
                            if (!TryRestore(plan))
                            {
                                if (!scanned.TryGetValue(type, out var result))
                                {
                                    result = RimKataWeaponExecutionDiscovery.Analyze(type, plan.HandledByCompatibility);
                                    scanned.Add(type, result);
                                }
                                Apply(plan, result);
                                Store(plan);
                            }
                            RimKataWeaponEffectCache.Prepare(plan);
                        }
                    }
                    catch (Exception exception)
                    {
                        FailRegistration(plan, exception.GetType().Name + ": " + exception.Message);
                    }
                    types.Add(type, plan);
                    All.Add(plan);
                    prepared.Add(plan);
                    if (plan.Mode == RimKataWeaponExecutionMode.NeedsRegistration) Pending.Add(plan);
                    Unload(plan);
                }
                var currentKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (RimKataWeaponExecutionPlan plan in types.Values) currentKeys.Add(plan.Key);
                RimKataEquipmentOwnership.ForgetObsoleteExecutions(def, currentKeys);
            }
            MethodFingerprints.Clear();
            Readers.Clear();
            return prepared;
        }

        // Called only by equipment/registration preparation, never by a firing or render getter.
        internal static bool Load(RimKataWeaponExecutionPlan plan)
        {
            if (plan == null || plan.Failed) return false;
            if (plan.Loaded) return true;
            try
            {
                if (!TryRestore(plan, validateRoute: false))
                {
                    FailRegistration(plan, "Prepared firing data could not be restored. Use the equipment refresh to prepare it again.", discardCache: false);
                    return false;
                }
                BindReaders(plan);
                if (plan.RegistrationDeclined) plan.AllowConversion = false;
                plan.Loaded = true;
                return true;
            }
            catch (Exception exception)
            {
                FailRegistration(plan, exception.GetType().Name + ": " + exception.Message, discardCache: false);
                Unload(plan);
                return false;
            }
            finally
            {
                MethodFingerprints.Clear();
                Readers.Clear();
            }
        }

        internal static void LoadOriginalEntries(RimKataWeaponExecutionPlan plan)
        {
            // Failed analysis does not disable the weapon's original lifecycle.
            plan.WarmupMethod = RimKataWeaponExecutionDiscovery.ResolveSlot(plan.VerbType,
                AccessTools.Method(typeof(Verb), nameof(Verb.WarmupComplete)));
            plan.TickMethod = RimKataWeaponExecutionDiscovery.ResolveSlot(plan.VerbType,
                AccessTools.Method(typeof(Verb), nameof(Verb.VerbTick)));
        }

        internal static void Unload(RimKataWeaponExecutionPlan plan)
        {
            plan.Loaded = false;
            plan.FiringReady = false;
            plan.AllowConversion = false;
            plan.Methods = plan.BurstMethods = Array.Empty<MethodInfo>();
            if (plan.Residents == 0) plan.WarmupMethod = plan.TickMethod = null;
            plan.BurstCountMethod = plan.BurstSpacingMethod = null;
            plan.BurstCountReader = plan.BurstSpacingReader = null;
        }

        internal static void ClearPreparationScratch()
        {
            MethodFingerprints.Clear();
            Readers.Clear();
        }

        private static void Apply(RimKataWeaponExecutionPlan plan, RimKataWeaponExecutionDiscovery.Result result)
        {
            plan.WarmupMethod = result.Warmup;
            plan.TickMethod = result.Tick;
            plan.BurstCountMethod = result.Count;
            plan.BurstSpacingMethod = result.Spacing;
            plan.Methods = result.Methods.ToArray();
            plan.BurstMethods = result.Bursts.ToArray();
            plan.AnalysisComplete = result.Complete;
            plan.CanControlBurst = result.CanControlBurst;
            plan.Reason = result.Reason;
            plan.Mode = plan.HandledByCompatibility || (!result.Custom && result.Complete)
                ? RimKataWeaponExecutionMode.Vanilla
                : result.Complete ? RimKataWeaponExecutionMode.Traced : RimKataWeaponExecutionMode.NeedsRegistration;
            plan.AllowConversion = plan.HandledByCompatibility || (result.Complete && result.CanControlBurst);
            plan.Fingerprint = RouteFingerprint(plan.Methods);
        }

        // Called only after the separate copied execution bundle was successfully installed.
        internal static void Register(RimKataWeaponExecutionPlan plan)
        {
            if (plan == null) return;
            Revision++;
            plan.RegistrationAccepted = true;
            plan.RegistrationDeclined = false;
            plan.FiringReady = true;
            plan.Mode = RimKataWeaponExecutionMode.Copied;
            plan.AllowConversion = plan.CanControlBurst;
            Pending.Remove(plan);
            Store(plan);
        }

        internal static void Decline(RimKataWeaponExecutionPlan plan)
        {
            if (plan == null) return;
            Revision++;
            plan.RegistrationDeclined = true;
            plan.RegistrationAccepted = false;
            plan.FiringReady = false;
            plan.Mode = RimKataWeaponExecutionMode.Live;
            plan.AllowConversion = false;
            Pending.Remove(plan);
            Store(plan);
        }

        internal static void FailRegistration(RimKataWeaponExecutionPlan plan, string reason, bool discardCache = true)
        {
            if (plan == null) return;
            Revision++;
            if (discardCache)
            {
                plan.RegistrationAccepted = false;
                plan.RegistrationDeclined = false;
            }
            plan.FiringReady = false;
            plan.Mode = RimKataWeaponExecutionMode.Live;
            plan.AllowConversion = false;
            plan.Reason = reason;
            plan.Failed = true;
            if (!string.IsNullOrEmpty(plan.Key))
            {
                if (!Failures.ContainsKey(plan.Key))
                    Log.Warning("[RimKata] Could not prepare firing execution for " + plan.Def.defName
                        + ". Original weapon behavior will be used. " + reason);
                Failures[plan.Key] = reason;
            }
            Pending.Remove(plan);
            if (discardCache) RimKataEquipmentMemory.Current?.Remove(Category, plan.Key);
            RimKataWeaponRegistrationFailures.Record(plan.Def, reason);
        }

        private static void BindReaders(RimKataWeaponExecutionPlan plan)
        {
            plan.BurstCountReader = Reader(plan.BurstCountMethod);
            plan.BurstSpacingReader = Reader(plan.BurstSpacingMethod);
            if (plan.BurstCountReader == null || plan.BurstSpacingReader == null)
                plan.AllowConversion = plan.HandledByCompatibility;
        }

        private static Func<Verb, int> Reader(MethodInfo method)
        {
            if (method == null || method.IsStatic || method.ReturnType != typeof(int)
                || method.GetParameters().Length != 0 || method.ContainsGenericParameters
                || !typeof(Verb).IsAssignableFrom(method.DeclaringType)) return null;
            if (Readers.TryGetValue(method, out var reader)) return reader;
            ParameterExpression verb = Expression.Parameter(typeof(Verb), "verb");
            reader = Expression.Lambda<Func<Verb, int>>(Expression.Call(
                Expression.Convert(verb, method.DeclaringType), method), verb).Compile();
            Readers.Add(method, reader);
            return reader;
        }

        private static string BaseFingerprint(RimKataWeaponExecutionPlan plan)
            => RimKataEquipmentMemory.Fingerprint(Schema + "\n" + TypeIdentity(plan.VerbType)
                + "\n" + TypeIdentity(typeof(Verb)) + "\n" + TypeIdentity(typeof(Harmony))
                + "\n" + TypeIdentity(typeof(RimKataWeaponExecutionPlans))
                + "\n" + plan.HandledByCompatibility);

        internal static string TypeIdentity(Type type)
            => type == null ? "" : type.AssemblyQualifiedName + "\n" + type.Module.ModuleVersionId.ToString("D");

        internal static string MethodIdentity(MethodInfo method)
            => MemberIdentity(method);

        internal static string MemberIdentity(MemberInfo member)
            => member == null ? "" : member.Module.Assembly.FullName + "|"
                + member.Module.ModuleVersionId.ToString("D") + "|"
                + member.MetadataToken.ToString(CultureInfo.InvariantCulture);

        internal static string RouteFingerprint(MethodInfo[] methods)
        {
            var entries = new List<string>();
            foreach (MethodInfo method in methods)
            {
                if (MethodFingerprints.TryGetValue(method, out string known))
                {
                    entries.Add(known);
                    continue;
                }
                var text = new StringBuilder(MethodIdentity(method));
                Patches patches = Harmony.GetPatchInfo(method);
                if (patches != null)
                {
                    AppendPatches(text, "pre", patches.Prefixes);
                    AppendPatches(text, "post", patches.Postfixes);
                    AppendPatches(text, "trans", patches.Transpilers);
                    AppendPatches(text, "final", patches.Finalizers);
                    AppendPatches(text, "inner-pre", ExtraPatches(patches, "InnerPrefixes"));
                    AppendPatches(text, "inner-post", ExtraPatches(patches, "InnerPostfixes"));
                    AppendPatches(text, "inner-final", ExtraPatches(patches, "InnerFinalizers"));
                }
                string identity = text.ToString();
                MethodFingerprints.Add(method, identity);
                entries.Add(identity);
            }
            entries.Sort(StringComparer.Ordinal);
            return RimKataEquipmentMemory.Fingerprint(string.Join("\n", entries));
        }

        private static void AppendPatches(StringBuilder text, string kind, IEnumerable<Patch> patches)
        {
            foreach (Patch patch in patches)
            {
                MethodInfo method = patch.PatchMethod;
                if (method == null || method.DeclaringType?.Assembly == typeof(RimKataWeaponExecutionPlans).Assembly) continue;
                text.Append('\n').Append(kind).Append('|').Append(MethodIdentity(method))
                    .Append('|').Append(patch.owner).Append('|').Append(patch.priority)
                    .Append('|').Append(patch.index).Append('|').Append(string.Join(",", patch.before ?? Array.Empty<string>()))
                    .Append('|').Append(string.Join(",", patch.after ?? Array.Empty<string>()));
            }
        }

        internal static IEnumerable<Patch> ExtraPatches(Patches patches, string kind)
        {
            if (patches != null && AccessTools.Field(typeof(Patches), kind)?.GetValue(patches) is System.Collections.IEnumerable values)
                foreach (object value in values) if (value is Patch patch) yield return patch;
        }

        private static bool TryRestore(RimKataWeaponExecutionPlan plan, bool validateRoute = true)
        {
            var memory = RimKataEquipmentMemory.Current;
            if (memory == null || !memory.TryRead(Category, plan.Key, BaseFingerprint(plan), out XmlElement data)
                || data.GetAttribute("schema") != Schema) return false;
            var methods = new List<MethodInfo>();
            var bursts = new List<MethodInfo>();
            foreach (XmlNode node in data.ChildNodes)
            {
                if (!(node is XmlElement item) || item.Name != "method") return false;
                MethodInfo method = Resolve(item.GetAttribute("identity"));
                if (method == null) return false;
                methods.Add(method);
                if (item.GetAttribute("burst") == "1") bursts.Add(method);
            }
            if (methods.Count == 0 || methods.Count > RimKataWeaponExecutionDiscovery.MaximumStoredMethods) return false;
            string fingerprint = validateRoute ? RouteFingerprint(methods.ToArray()) : plan.Fingerprint;
            if (fingerprint != data.GetAttribute("route")) return false;
            plan.Methods = methods.ToArray();
            plan.BurstMethods = bursts.ToArray();
            plan.WarmupMethod = Resolve(data.GetAttribute("warmup"));
            plan.TickMethod = Resolve(data.GetAttribute("tick"));
            plan.BurstCountMethod = Resolve(data.GetAttribute("count"));
            plan.BurstSpacingMethod = Resolve(data.GetAttribute("spacing"));
            if (plan.WarmupMethod == null || plan.TickMethod == null) return false;
            plan.AnalysisComplete = data.GetAttribute("complete") == "1";
            plan.CanControlBurst = data.GetAttribute("control") == "1";
            plan.RegistrationAccepted = data.GetAttribute("decision") == "accepted";
            plan.RegistrationDeclined = data.GetAttribute("decision") == "declined";
            plan.Reason = data.GetAttribute("reason");
            bool custom = data.GetAttribute("custom") == "1";
            plan.Mode = plan.RegistrationDeclined ? RimKataWeaponExecutionMode.Live
                // Conversion remains off until the runtime bundle is rebuilt and Register is called.
                : plan.RegistrationAccepted ? RimKataWeaponExecutionMode.Copied
                : (!custom && plan.AnalysisComplete) || plan.HandledByCompatibility ? RimKataWeaponExecutionMode.Vanilla
                : plan.AnalysisComplete ? RimKataWeaponExecutionMode.Traced : RimKataWeaponExecutionMode.NeedsRegistration;
            plan.AllowConversion = plan.HandledByCompatibility || (!plan.RegistrationDeclined
                && !plan.RegistrationAccepted && plan.AnalysisComplete && plan.CanControlBurst);
            plan.Fingerprint = fingerprint;
            plan.CacheStored = true;
            RimKataStartupDiagnostics.Count("weapon_execution_cache_hit");
            return true;
        }

        private static MethodInfo Resolve(string identity)
            => ResolveMember(identity) as MethodInfo;

        internal static MemberInfo ResolveMember(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return null;
            string[] fields = identity.Split('|');
            if (fields.Length != 3 || !int.TryParse(fields[2], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int token)) return null;
            return Modules.TryGetValue(fields[0] + "|" + fields[1], out Module module)
                ? module.ResolveMember(token) : null;
        }

        private static void Store(RimKataWeaponExecutionPlan plan)
        {
            plan.CacheStored = false;
            if (plan.Methods.Length == 0 || plan.WarmupMethod == null || plan.TickMethod == null)
            {
                // Registration decisions do not require keeping an unequipped execution graph alive.
                if (RimKataEquipmentMemory.Current?.TryRead(Category, plan.Key, BaseFingerprint(plan), out XmlElement saved) == true)
                {
                    var changed = (XmlElement)saved.CloneNode(true);
                    changed.SetAttribute("decision", plan.RegistrationAccepted ? "accepted" : plan.RegistrationDeclined ? "declined" : "");
                    plan.CacheStored = RimKataEquipmentMemory.Current.Store(Category, plan.Key, BaseFingerprint(plan), changed);
                }
                return;
            }
            var document = new XmlDocument();
            XmlElement data = document.CreateElement("execution");
            data.SetAttribute("schema", Schema);
            data.SetAttribute("route", plan.Fingerprint ?? RouteFingerprint(plan.Methods));
            data.SetAttribute("complete", plan.AnalysisComplete ? "1" : "0");
            data.SetAttribute("control", plan.CanControlBurst ? "1" : "0");
            data.SetAttribute("custom", plan.Mode == RimKataWeaponExecutionMode.Vanilla ? "0" : "1");
            data.SetAttribute("decision", plan.RegistrationAccepted ? "accepted" : plan.RegistrationDeclined ? "declined" : "");
            data.SetAttribute("reason", plan.Reason ?? "");
            data.SetAttribute("warmup", MethodIdentity(plan.WarmupMethod));
            data.SetAttribute("tick", MethodIdentity(plan.TickMethod));
            data.SetAttribute("count", MethodIdentity(plan.BurstCountMethod));
            data.SetAttribute("spacing", MethodIdentity(plan.BurstSpacingMethod));
            var bursts = new HashSet<MethodInfo>(plan.BurstMethods);
            foreach (MethodInfo method in plan.Methods)
            {
                XmlElement item = document.CreateElement("method");
                item.SetAttribute("identity", MethodIdentity(method));
                if (bursts.Contains(method)) item.SetAttribute("burst", "1");
                data.AppendChild(item);
            }
            plan.CacheStored = RimKataEquipmentMemory.Current?.Store(Category, plan.Key, BaseFingerprint(plan), data) == true;
        }
    }
}
