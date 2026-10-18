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

        internal static void Refresh(bool force = false)
        {
            Revision++;
            if (force)
                foreach (RimKataWeaponExecutionPlan previous in All)
                    RimKataEquipmentMemory.Current?.Remove(Category, previous.Key);
            ByDefinition.Clear();
            All.Clear();
            Pending.Clear();
            Modules.Clear();
            MethodFingerprints.Clear();
            Readers.Clear();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (!assembly.IsDynamic)
                    foreach (Module module in assembly.GetModules())
                        Modules[assembly.FullName + "|" + module.ModuleVersionId.ToString("D")] = module;
            var selected = RimKataMod.Settings?.enabledWeaponDefNames;
            if (selected == null) return;
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
                    var plan = new RimKataWeaponExecutionPlan
                    {
                        Def = def, VerbType = type,
                        Key = RimKataEquipmentMemory.Fingerprint(def.defName + "\n" + TypeIdentity(type)),
                        HandledByCompatibility = compatibilityHandler?.Invoke(type) == true
                    };
                    try
                    {
                        if (force) RimKataEquipmentMemory.Current?.Remove(Category, plan.Key);
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
                        BindReaders(plan);
                    }
                    catch (Exception exception)
                    {
                        plan.Mode = RimKataWeaponExecutionMode.Live;
                        plan.AllowConversion = false;
                        plan.AnalysisComplete = false;
                        plan.Reason = exception.GetType().Name + ": " + exception.Message;
                        // A failed scan is not a successful cache record, and is retried later.
                        RimKataEquipmentMemory.Current?.Remove(Category, plan.Key);
                        Log.Warning("[RimKata] Could not prepare the firing path for " + def.defName
                            + ". Original live weapon data will be used. " + plan.Reason);
                    }
                    types.Add(type, plan);
                    All.Add(plan);
                    if (plan.Mode == RimKataWeaponExecutionMode.NeedsRegistration) Pending.Add(plan);
                }
            }
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

        internal static void FailRegistration(RimKataWeaponExecutionPlan plan, string reason)
        {
            if (plan == null) return;
            Revision++;
            plan.RegistrationAccepted = false;
            plan.RegistrationDeclined = false;
            plan.FiringReady = false;
            plan.Mode = RimKataWeaponExecutionMode.Live;
            plan.AllowConversion = false;
            plan.Reason = reason;
            Pending.Remove(plan);
            RimKataEquipmentMemory.Current?.Remove(Category, plan.Key);
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

        private static bool TryRestore(RimKataWeaponExecutionPlan plan)
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
            if (methods.Count == 0 || methods.Count > 160) return false;
            string fingerprint = RouteFingerprint(methods.ToArray());
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
            if (plan.Methods.Length == 0 || plan.WarmupMethod == null || plan.TickMethod == null) return;
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
            RimKataEquipmentMemory.Current?.Store(Category, plan.Key, BaseFingerprint(plan), data);
        }
    }
}
