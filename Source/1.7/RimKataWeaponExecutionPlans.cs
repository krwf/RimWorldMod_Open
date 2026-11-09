using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Xml;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal enum RimKataWeaponExecutionMode { Vanilla, Traced, NeedsRegistration, Live, Copied }
    internal enum RimKataWeaponCompletionContract { Original, NativeLifecycle, Compatibility }
    internal enum RimKataWeaponEffectStatus { NotDetected, Connected, Original }

    internal readonly struct RimKataWeaponStatRead
    {
        internal readonly MethodInfo Method;
        internal readonly StatDef Stat;
        // False also includes an unresolved receiver, which the runtime reader guards.
        internal readonly bool PawnReceiver;
        internal RimKataWeaponStatRead(MethodInfo method, StatDef stat, bool pawnReceiver)
        { Method = method; Stat = stat; PawnReceiver = pawnReceiver; }
    }

    internal sealed class RimKataWeaponExecutionPlan
    {
        internal ThingDef Def;
        internal Type VerbType;
        internal string Key;
        internal string Fingerprint;
        internal string SourceFingerprint;
        internal RimKataWeaponImplementationKind Implementation;
        internal RimKataWeaponExecutionDiscovery.Result Baseline;
        internal bool BaselineComplete, HasExternalChanges, ExternalAnalysisComplete, UsingBaseline;
        internal string BaselineReason, ExternalReason;
        // The chosen executable may use fewer copied bodies than the inspected route.
        // Keep all inspected dependencies for invalidation, never for runtime traversal.
        internal MethodInfo[] DependencyMethods = Array.Empty<MethodInfo>();
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
        // Prepared call sites use the actual owner pawn at execution; definitions alone
        // never substitute the weapon's or target's stats for the shooter's values.
        internal MethodInfo[] StatReadMethods = Array.Empty<MethodInfo>();
        internal StatDef[] PawnReadStats = Array.Empty<StatDef>();
        internal RimKataWeaponStatRead[] StatReads = Array.Empty<RimKataWeaponStatRead>();
        internal bool StatReadsComplete = true;
        internal string StatReadReason;
        internal bool HasDynamicPawnStatRead;
        internal bool HasAdditionalPawnStats;
        internal bool HasWeaponStatEffects;
        internal Func<Verb, int> BurstCountReader;
        internal Func<Verb, int> BurstSpacingReader;
        internal bool AnalysisComplete;
        // Prepared execution ownership, independent of graph coverage and registration.
        internal RimKataWeaponCompletionContract CompletionContract;
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
        private const string Schema = "5";
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

        internal static bool HasAdditionalStatRequirements(ThingDef def)
        {
            if (def == null || !ByDefinition.TryGetValue(def, out var types)) return false;
            foreach (RimKataWeaponExecutionPlan plan in types.Values)
                if (plan.HasAdditionalPawnStats || plan.HasWeaponStatEffects) return true;
            return false;
        }

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
            RimKataWeaponImplementation.Clear();
            if (selected == null) return prepared;
            bool modulesReady = false;
            var scanned = new Dictionary<Type, RimKataWeaponExecutionDiscovery.Result>();
            var baselines = new Dictionary<Type, RimKataWeaponExecutionDiscovery.Result>();
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
                    try
                    {
                        plan.SourceFingerprint = RimKataWeaponImplementation.Fingerprint(def, type);
                        RimKataEquipmentOwnership.RememberExecution(def, plan.Key);
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
                                    // A changed external route can still reuse the validated original analysis.
                                    if (!baselines.TryGetValue(type, out var baseline))
                                    {
                                        baseline = plan.Baseline ?? RimKataWeaponExecutionDiscovery.AnalyzeBaseline(type, plan.HandledByCompatibility);
                                        baselines.Add(type, baseline);
                                    }
                                    result = RimKataWeaponExecutionDiscovery.Analyze(type, plan.HandledByCompatibility, baseline);
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
            RimKataWeaponImplementation.Clear();
            RimKataGripUtility.InvalidateCache();
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
            plan.Baseline = null;
            plan.DependencyMethods = Array.Empty<MethodInfo>();
            plan.StatReadMethods = Array.Empty<MethodInfo>();
            plan.PawnReadStats = Array.Empty<StatDef>();
            plan.StatReads = Array.Empty<RimKataWeaponStatRead>();
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
            plan.Baseline = result.Baseline;
            plan.BaselineComplete = result.Baseline?.Complete == true;
            plan.BaselineReason = result.Baseline?.Reason;
            plan.Implementation = RimKataWeaponImplementation.Classify(plan.Def, plan.VerbType, result.Baseline);
            plan.HasExternalChanges = result.HasExternalChanges;
            plan.ExternalAnalysisComplete = result.Complete;
            plan.ExternalReason = result.Reason;
            plan.UsingBaseline = false;
            var dependencies = new HashSet<MethodInfo>(result.Methods);
            dependencies.UnionWith(result.StatReadMethods);
            if (result.Baseline != null)
            {
                dependencies.UnionWith(result.Baseline.Methods);
                dependencies.UnionWith(result.Baseline.StatReadMethods);
            }
            plan.DependencyMethods = new List<MethodInfo>(dependencies).ToArray();
            plan.WarmupMethod = result.Warmup;
            plan.TickMethod = result.Tick;
            plan.BurstCountMethod = result.Count;
            plan.BurstSpacingMethod = result.Spacing;
            plan.Methods = result.Methods.ToArray();
            plan.BurstMethods = result.Bursts.ToArray();
            plan.StatReadMethods = new List<MethodInfo>(result.StatReadMethods).ToArray();
            plan.PawnReadStats = new List<StatDef>(result.PawnReadStats).ToArray();
            plan.StatReads = result.StatReads.ToArray();
            plan.StatReadsComplete = result.StatReadsComplete;
            plan.StatReadReason = result.StatReadReason;
            plan.HasDynamicPawnStatRead = result.HasDynamicPawnStatRead;
            plan.HasAdditionalPawnStats = result.HasAdditionalPawnStats;
            plan.AnalysisComplete = result.Complete;
            plan.CompletionContract = result.CompletionContract;
            plan.CanControlBurst = result.CanControlBurst;
            plan.Reason = result.Reason;
            plan.Mode = result.CompletionContract == RimKataWeaponCompletionContract.Original
                ? RimKataWeaponExecutionMode.Live
                : plan.HandledByCompatibility || (!result.Custom && result.Complete)
                ? RimKataWeaponExecutionMode.Vanilla
                : result.Complete ? RimKataWeaponExecutionMode.Traced : RimKataWeaponExecutionMode.NeedsRegistration;
            plan.AllowConversion = plan.HandledByCompatibility || (result.Complete && result.CanControlBurst
                && plan.CompletionContract == RimKataWeaponCompletionContract.NativeLifecycle);
            plan.Fingerprint = RouteFingerprint(plan.DependencyMethods);
            if (!result.Complete && !plan.HandledByCompatibility
                && !TryUseBaseline(plan, result.Reason)
                && plan.Implementation != RimKataWeaponImplementationKind.Specialized)
            {
                // An unverified external hook does not turn an ordinary weapon into a
                // special registration request. Preserve the game's patched original.
                plan.Mode = RimKataWeaponExecutionMode.Live;
                plan.CompletionContract = RimKataWeaponCompletionContract.Original;
                plan.AllowConversion = false;
            }
        }

        // Preparation only. This selects original bodies for a smaller snapshot;
        // SnapshotApi still applies every current hook in its original order.
        internal static bool TryUseBaseline(RimKataWeaponExecutionPlan plan, string reason)
        {
            var baseline = plan?.Baseline;
            if (plan == null || plan.UsingBaseline || plan.HandledByCompatibility
                || plan.RegistrationDeclined || baseline?.Complete != true
                || !baseline.CanControlBurst || !plan.CanControlBurst
                || baseline.CompletionContract != RimKataWeaponCompletionContract.NativeLifecycle
                || plan.CompletionContract != RimKataWeaponCompletionContract.NativeLifecycle
                || baseline.Warmup != plan.WarmupMethod || baseline.Tick != plan.TickMethod
                || baseline.Count != plan.BurstCountMethod || baseline.Spacing != plan.BurstSpacingMethod
                || !new HashSet<MethodInfo>(baseline.Bursts).SetEquals(plan.BurstMethods)) return false;
            plan.UsingBaseline = true;
            plan.ExternalReason = reason ?? plan.ExternalReason;
            plan.Methods = baseline.Methods.ToArray();
            plan.BurstMethods = baseline.Bursts.ToArray();
            plan.AnalysisComplete = true; // The selected basic path, not the external analysis.
            plan.Failed = false;
            plan.FiringReady = false;
            plan.Mode = RimKataWeaponExecutionMode.Traced;
            plan.AllowConversion = true;
            plan.Reason = "Using the verified basic route with original external calls. " + plan.ExternalReason;
            BindReaders(plan);
            return true;
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
            plan.AllowConversion = plan.CanControlBurst
                && plan.CompletionContract != RimKataWeaponCompletionContract.Original;
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
            plan.CompletionContract = RimKataWeaponCompletionContract.Original;
            plan.Failed = true;
            if (!string.IsNullOrEmpty(plan.Key))
            {
                if (!Failures.ContainsKey(plan.Key))
                    Log.Warning("[RimKata] Could not prepare firing execution for " + plan.Def.defName
                        + ". Original weapon behavior will be used. " + reason);
                Failures[plan.Key] = reason;
            }
            Pending.Remove(plan);
            // A failed runtime preparation must not delete a successful basic analysis.
            // The failed outcome is persisted separately from that analysis in the same record.
            if (discardCache && plan.Baseline != null && plan.Methods.Length != 0)
            {
                try { Store(plan); }
                catch { /* The current session still retains the original-execution outcome. */ }
            }
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
        {
            if (member == null) return "";
            try
            {
                Module module = member.Module;
                string key = module.Assembly.FullName + "|" + module.ModuleVersionId.ToString("D");
                Modules[key] = module;
                string identity = key + "|" + member.MetadataToken.ToString(CultureInfo.InvariantCulture);
                Type declaring = member.DeclaringType;
                bool constructedType = declaring?.IsGenericType == true && !declaring.IsGenericTypeDefinition;
                MethodInfo method = member as MethodInfo;
                bool constructedMethod = method?.IsGenericMethod == true && !method.IsGenericMethodDefinition;
                if (!constructedType && !constructedMethod) return identity;
                string owner = constructedType ? EncodeType(declaring) : "";
                var arguments = new List<string>();
                if (constructedMethod)
                    foreach (Type argument in method.GetGenericArguments()) arguments.Add(EncodeType(argument));
                return identity + "|" + owner + "|" + string.Join(",", arguments);
            }
            catch
            {
                // A dynamic/open-context member can remain a live dependency. It is
                // never advertised as a restorable disk identity or a firing failure.
                return "runtime|" + member.Name + "|"
                    + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(member).ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string EncodeType(Type type)
        {
            if (type?.AssemblyQualifiedName == null) throw new NotSupportedException("An open generic type has no disk identity.");
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(type.AssemblyQualifiedName));
        }

        private static Type DecodeType(string value)
            => Type.GetType(Encoding.UTF8.GetString(Convert.FromBase64String(value)), name =>
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (!assembly.IsDynamic && assembly.FullName == name.FullName) return assembly;
                return null;
            }, (assembly, name, ignoreCase) => assembly?.GetType(name, false, ignoreCase), false);

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

        private static string PreparedFingerprint(MethodInfo[] firing, MethodInfo[] statReads)
        {
            var methods = new HashSet<MethodInfo>(firing);
            methods.UnionWith(statReads);
            return RouteFingerprint(new List<MethodInfo>(methods).ToArray());
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
            if (data.GetAttribute("source") != plan.SourceFingerprint) return false;
            // Restore the original analysis before checking the current patch route.
            // A patch-only cache miss can reuse this independently validated section.
            plan.Baseline = ReadBaseline(data["baseline"], plan.VerbType);
            if (plan.Baseline == null) return false;
            plan.BaselineComplete = plan.Baseline.Complete;
            plan.BaselineReason = plan.Baseline.Reason;
            var methods = new List<MethodInfo>();
            var bursts = new List<MethodInfo>();
            var statMethods = new List<MethodInfo>();
            var stats = new List<StatDef>();
            var reads = new List<RimKataWeaponStatRead>();
            var dependencies = new List<MethodInfo>();
            foreach (XmlNode node in data.ChildNodes)
            {
                if (!(node is XmlElement item)) return false;
                if (item.Name == "baseline") continue;
                if (item.Name == "dependency")
                {
                    MethodInfo dependency = Resolve(item.GetAttribute("identity"));
                    if (dependency == null || dependencies.Contains(dependency)
                        || dependencies.Count >= RimKataWeaponExecutionDiscovery.MaximumStoredMethods * 6) return false;
                    dependencies.Add(dependency);
                    continue;
                }
                if (item.Name == "pawnStat")
                {
                    StatDef stat = DefDatabase<StatDef>.GetNamedSilentFail(item.GetAttribute("def"));
                    if (stat == null || stats.Contains(stat)) return false;
                    stats.Add(stat);
                    continue;
                }
                if (item.Name == "statRead")
                {
                    MethodInfo caller = Resolve(item.GetAttribute("method"));
                    string name = item.GetAttribute("stat");
                    StatDef stat = name.Length == 0 ? null : DefDatabase<StatDef>.GetNamedSilentFail(name);
                    if (caller == null || (name.Length != 0 && stat == null)
                        || reads.Count >= RimKataWeaponExecutionDiscovery.MaximumInstructions * 2) return false;
                    reads.Add(new RimKataWeaponStatRead(caller, stat, item.GetAttribute("pawn") == "1"));
                    continue;
                }
                if (item.Name == "statMethod")
                {
                    MethodInfo reader = Resolve(item.GetAttribute("identity"));
                    if (reader == null || statMethods.Contains(reader)
                        || statMethods.Count >= RimKataWeaponExecutionDiscovery.MaximumStoredMethods * 2) return false;
                    statMethods.Add(reader);
                    continue;
                }
                if (item.Name != "method") return false;
                MethodInfo method = Resolve(item.GetAttribute("identity"));
                if (method == null || methods.Contains(method)) return false;
                methods.Add(method);
                if (item.GetAttribute("burst") == "1") bursts.Add(method);
            }
            if (methods.Count == 0 || methods.Count > RimKataWeaponExecutionDiscovery.MaximumStoredMethods) return false;
            foreach (RimKataWeaponStatRead read in reads)
                if (!statMethods.Contains(read.Method) || (read.Stat != null && !stats.Contains(read.Stat))) return false;
            if (dependencies.Count == 0) return false;
            string fingerprint = validateRoute ? RouteFingerprint(dependencies.ToArray()) : plan.Fingerprint;
            if (fingerprint != data.GetAttribute("route")) return false;
            if (!Enum.TryParse(data.GetAttribute("implementation"), out RimKataWeaponImplementationKind implementation)
                || !Enum.IsDefined(typeof(RimKataWeaponImplementationKind), implementation)) return false;
            plan.Implementation = implementation;
            plan.HasExternalChanges = data.GetAttribute("external") == "1";
            plan.ExternalAnalysisComplete = data.GetAttribute("externalComplete") == "1";
            plan.ExternalReason = data.GetAttribute("externalReason");
            plan.UsingBaseline = data.GetAttribute("usingBaseline") == "1";
            plan.DependencyMethods = dependencies.ToArray();
            plan.Methods = methods.ToArray();
            plan.BurstMethods = bursts.ToArray();
            plan.StatReadMethods = statMethods.ToArray();
            plan.PawnReadStats = stats.ToArray();
            plan.StatReads = reads.ToArray();
            plan.StatReadsComplete = data.GetAttribute("statReadsComplete") == "1";
            plan.StatReadReason = data.GetAttribute("statReadReason");
            plan.HasDynamicPawnStatRead = data.GetAttribute("dynamicPawnStat") == "1";
            plan.HasAdditionalPawnStats = data.GetAttribute("additionalPawnStats") == "1";
            plan.WarmupMethod = Resolve(data.GetAttribute("warmup"));
            plan.TickMethod = Resolve(data.GetAttribute("tick"));
            plan.BurstCountMethod = Resolve(data.GetAttribute("count"));
            plan.BurstSpacingMethod = Resolve(data.GetAttribute("spacing"));
            if (plan.WarmupMethod == null || plan.TickMethod == null) return false;
            plan.AnalysisComplete = data.GetAttribute("complete") == "1";
            if (!Enum.TryParse(data.GetAttribute("completion"), out RimKataWeaponCompletionContract completion)
                || !Enum.IsDefined(typeof(RimKataWeaponCompletionContract), completion)) return false;
            plan.CompletionContract = completion;
            plan.CanControlBurst = data.GetAttribute("control") == "1";
            plan.RegistrationAccepted = data.GetAttribute("decision") == "accepted";
            plan.RegistrationDeclined = data.GetAttribute("decision") == "declined";
            plan.Reason = data.GetAttribute("reason");
            plan.Failed = data.GetAttribute("failed") == "1";
            bool custom = data.GetAttribute("custom") == "1";
            plan.Mode = plan.RegistrationDeclined
                || plan.CompletionContract == RimKataWeaponCompletionContract.Original ? RimKataWeaponExecutionMode.Live
                // Conversion remains off until the runtime bundle is rebuilt and Register is called.
                : plan.RegistrationAccepted ? RimKataWeaponExecutionMode.Copied
                : (!custom && plan.AnalysisComplete) || plan.HandledByCompatibility ? RimKataWeaponExecutionMode.Vanilla
                : plan.AnalysisComplete ? RimKataWeaponExecutionMode.Traced : RimKataWeaponExecutionMode.NeedsRegistration;
            plan.AllowConversion = plan.HandledByCompatibility || (!plan.RegistrationDeclined
                && !plan.RegistrationAccepted && plan.AnalysisComplete && plan.CanControlBurst
                && plan.CompletionContract == RimKataWeaponCompletionContract.NativeLifecycle);
            plan.Fingerprint = fingerprint;
            plan.CacheStored = true;
            RimKataStartupDiagnostics.Count("weapon_execution_cache_hit");
            return true;
        }

        private static string OriginalFingerprint(RimKataWeaponExecutionDiscovery.Result result)
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (MethodInfo method in result.Methods) identities.Add(MethodIdentity(method));
            foreach (MethodInfo method in result.StatReadMethods) identities.Add(MethodIdentity(method));
            var ordered = new List<string>(identities);
            ordered.Sort(StringComparer.Ordinal);
            return RimKataEquipmentMemory.Fingerprint(string.Join("\n", ordered));
        }

        private static RimKataWeaponExecutionDiscovery.Result ReadBaseline(XmlElement data, Type verbType)
        {
            if (data == null) return null;
            var result = new RimKataWeaponExecutionDiscovery.Result
            {
                VerbType = verbType, Warmup = Resolve(data.GetAttribute("warmup")),
                Tick = Resolve(data.GetAttribute("tick")), Count = Resolve(data.GetAttribute("count")),
                Spacing = Resolve(data.GetAttribute("spacing")), Complete = data.GetAttribute("complete") == "1",
                Custom = data.GetAttribute("custom") == "1", CanControlBurst = data.GetAttribute("control") == "1",
                Reason = data.GetAttribute("reason"), StatReadsComplete = data.GetAttribute("statReadsComplete") == "1",
                StatReadReason = data.GetAttribute("statReadReason"),
                HasDynamicPawnStatRead = data.GetAttribute("dynamicPawnStat") == "1",
                HasAdditionalPawnStats = data.GetAttribute("additionalPawnStats") == "1"
            };
            if (result.Warmup == null || result.Tick == null
                || !Enum.TryParse(data.GetAttribute("completion"), out RimKataWeaponCompletionContract contract)
                || !Enum.IsDefined(typeof(RimKataWeaponCompletionContract), contract)) return null;
            result.CompletionContract = contract;
            foreach (XmlNode node in data.ChildNodes)
            {
                if (!(node is XmlElement item)) return null;
                if (item.Name == "method" || item.Name == "statMethod")
                {
                    MethodInfo method = Resolve(item.GetAttribute("identity"));
                    if (method == null) return null;
                    if (item.Name == "statMethod")
                    {
                        if (!result.StatReadMethods.Add(method)
                            || result.StatReadMethods.Count > RimKataWeaponExecutionDiscovery.MaximumStoredMethods * 2) return null;
                    }
                    else
                    {
                        if (result.Methods.Contains(method)
                            || result.Methods.Count >= RimKataWeaponExecutionDiscovery.MaximumStoredMethods) return null;
                        result.Methods.Add(method);
                        if (item.GetAttribute("burst") == "1") result.Bursts.Add(method);
                    }
                }
                else if (item.Name == "pawnStat")
                {
                    StatDef stat = DefDatabase<StatDef>.GetNamedSilentFail(item.GetAttribute("def"));
                    if (stat == null || !result.PawnReadStats.Add(stat)) return null;
                }
                else if (item.Name == "statRead")
                {
                    MethodInfo method = Resolve(item.GetAttribute("method"));
                    string name = item.GetAttribute("stat");
                    StatDef stat = name.Length == 0 ? null : DefDatabase<StatDef>.GetNamedSilentFail(name);
                    if (method == null || (name.Length != 0 && stat == null)
                        || result.StatReads.Count >= RimKataWeaponExecutionDiscovery.MaximumInstructions * 2) return null;
                    result.StatReads.Add(new RimKataWeaponStatRead(method, stat, item.GetAttribute("pawn") == "1"));
                }
                else return null;
            }
            foreach (RimKataWeaponStatRead read in result.StatReads)
                if (!result.StatReadMethods.Contains(read.Method)
                    || (read.Stat != null && !result.PawnReadStats.Contains(read.Stat))) return null;
            return result.Methods.Count != 0 && OriginalFingerprint(result) == data.GetAttribute("original") ? result : null;
        }

        private static XmlElement WriteBaseline(XmlDocument document, RimKataWeaponExecutionDiscovery.Result result)
        {
            XmlElement data = document.CreateElement("baseline");
            data.SetAttribute("original", OriginalFingerprint(result));
            data.SetAttribute("warmup", MethodIdentity(result.Warmup));
            data.SetAttribute("tick", MethodIdentity(result.Tick));
            data.SetAttribute("count", MethodIdentity(result.Count));
            data.SetAttribute("spacing", MethodIdentity(result.Spacing));
            data.SetAttribute("complete", result.Complete ? "1" : "0");
            data.SetAttribute("custom", result.Custom ? "1" : "0");
            data.SetAttribute("control", result.CanControlBurst ? "1" : "0");
            data.SetAttribute("completion", result.CompletionContract.ToString());
            data.SetAttribute("reason", result.Reason ?? "");
            data.SetAttribute("statReadsComplete", result.StatReadsComplete ? "1" : "0");
            data.SetAttribute("statReadReason", result.StatReadReason ?? "");
            data.SetAttribute("dynamicPawnStat", result.HasDynamicPawnStatRead ? "1" : "0");
            data.SetAttribute("additionalPawnStats", result.HasAdditionalPawnStats ? "1" : "0");
            foreach (MethodInfo method in result.Methods)
            {
                XmlElement item = document.CreateElement("method");
                item.SetAttribute("identity", MethodIdentity(method));
                if (result.Bursts.Contains(method)) item.SetAttribute("burst", "1");
                data.AppendChild(item);
            }
            foreach (MethodInfo method in result.StatReadMethods)
            {
                XmlElement item = document.CreateElement("statMethod");
                item.SetAttribute("identity", MethodIdentity(method)); data.AppendChild(item);
            }
            foreach (StatDef stat in result.PawnReadStats)
            {
                XmlElement item = document.CreateElement("pawnStat");
                item.SetAttribute("def", stat.defName); data.AppendChild(item);
            }
            foreach (RimKataWeaponStatRead read in result.StatReads)
            {
                XmlElement item = document.CreateElement("statRead");
                item.SetAttribute("method", MethodIdentity(read.Method));
                if (read.Stat != null) item.SetAttribute("stat", read.Stat.defName);
                item.SetAttribute("pawn", read.PawnReceiver ? "1" : "0"); data.AppendChild(item);
            }
            return data;
        }

        private static MethodInfo Resolve(string identity)
            => ResolveMember(identity) as MethodInfo;

        internal static MemberInfo ResolveMember(string identity)
        {
            if (string.IsNullOrEmpty(identity)) return null;
            try
            {
                string[] fields = identity.Split('|');
                if ((fields.Length != 3 && fields.Length != 5) || !int.TryParse(fields[2], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int token)
                    || !Modules.TryGetValue(fields[0] + "|" + fields[1], out Module module)) return null;
                MemberInfo member = module.ResolveMember(token);
                if (fields.Length == 5)
                {
                    if (fields[3].Length != 0)
                    {
                        Type owner = DecodeType(fields[3]);
                        if (owner == null || owner.Module != module) return null;
                        member = null;
                        foreach (MemberInfo candidate in owner.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                            if (candidate.MetadataToken == token && candidate.Module == module)
                            { member = candidate; break; }
                        if (member == null) return null;
                    }
                    if (fields[4].Length != 0)
                    {
                        if (!(member is MethodInfo method) || !method.IsGenericMethodDefinition) return null;
                        string[] names = fields[4].Split(',');
                        var arguments = new Type[names.Length];
                        for (int i = 0; i < names.Length; i++)
                            if ((arguments[i] = DecodeType(names[i])) == null) return null;
                        member = method.MakeGenericMethod(arguments);
                    }
                }
                return MemberIdentity(member) == identity ? member : null;
            }
            catch { return null; }
        }

        internal static bool CanStoreMethod(MethodInfo method)
            => method == null || Equals(ResolveMember(MethodIdentity(method)), method);

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
            if (!CanStoreMethod(plan.WarmupMethod) || !CanStoreMethod(plan.TickMethod)
                || !CanStoreMethod(plan.BurstCountMethod) || !CanStoreMethod(plan.BurstSpacingMethod)) return;
            foreach (MethodInfo method in plan.Methods) if (!CanStoreMethod(method)) return;
            foreach (MethodInfo method in plan.StatReadMethods) if (!CanStoreMethod(method)) return;
            if (plan.Baseline != null)
            {
                foreach (MethodInfo method in plan.Baseline.Methods) if (!CanStoreMethod(method)) return;
                foreach (MethodInfo method in plan.Baseline.StatReadMethods) if (!CanStoreMethod(method)) return;
            }
            XmlElement data = document.CreateElement("execution");
            data.SetAttribute("schema", Schema);
            data.SetAttribute("source", plan.SourceFingerprint ?? "");
            data.SetAttribute("implementation", plan.Implementation.ToString());
            data.SetAttribute("external", plan.HasExternalChanges ? "1" : "0");
            data.SetAttribute("externalComplete", plan.ExternalAnalysisComplete ? "1" : "0");
            data.SetAttribute("externalReason", plan.ExternalReason ?? "");
            data.SetAttribute("usingBaseline", plan.UsingBaseline ? "1" : "0");
            data.SetAttribute("failed", plan.Failed ? "1" : "0");
            if (plan.Baseline != null) data.AppendChild(WriteBaseline(document, plan.Baseline));
            foreach (MethodInfo method in plan.DependencyMethods)
            {
                if (!CanStoreMethod(method)) return;
                XmlElement item = document.CreateElement("dependency");
                item.SetAttribute("identity", MethodIdentity(method)); data.AppendChild(item);
            }
            data.SetAttribute("route", plan.Fingerprint ?? PreparedFingerprint(plan.Methods, plan.StatReadMethods));
            data.SetAttribute("complete", plan.AnalysisComplete ? "1" : "0");
            data.SetAttribute("completion", plan.CompletionContract.ToString());
            data.SetAttribute("control", plan.CanControlBurst ? "1" : "0");
            data.SetAttribute("custom", plan.Mode == RimKataWeaponExecutionMode.Vanilla ? "0" : "1");
            data.SetAttribute("decision", plan.RegistrationAccepted ? "accepted" : plan.RegistrationDeclined ? "declined" : "");
            data.SetAttribute("reason", plan.Reason ?? "");
            data.SetAttribute("warmup", MethodIdentity(plan.WarmupMethod));
            data.SetAttribute("tick", MethodIdentity(plan.TickMethod));
            data.SetAttribute("count", MethodIdentity(plan.BurstCountMethod));
            data.SetAttribute("spacing", MethodIdentity(plan.BurstSpacingMethod));
            data.SetAttribute("dynamicPawnStat", plan.HasDynamicPawnStatRead ? "1" : "0");
            data.SetAttribute("additionalPawnStats", plan.HasAdditionalPawnStats ? "1" : "0");
            data.SetAttribute("statReadsComplete", plan.StatReadsComplete ? "1" : "0");
            data.SetAttribute("statReadReason", plan.StatReadReason ?? "");
            var bursts = new HashSet<MethodInfo>(plan.BurstMethods);
            foreach (MethodInfo method in plan.Methods)
            {
                XmlElement item = document.CreateElement("method");
                item.SetAttribute("identity", MethodIdentity(method));
                if (bursts.Contains(method)) item.SetAttribute("burst", "1");
                data.AppendChild(item);
            }
            foreach (MethodInfo method in plan.StatReadMethods)
            {
                XmlElement item = document.CreateElement("statMethod");
                item.SetAttribute("identity", MethodIdentity(method));
                data.AppendChild(item);
            }
            foreach (StatDef stat in plan.PawnReadStats)
            {
                XmlElement item = document.CreateElement("pawnStat");
                item.SetAttribute("def", stat.defName);
                data.AppendChild(item);
            }
            foreach (RimKataWeaponStatRead read in plan.StatReads)
            {
                XmlElement item = document.CreateElement("statRead");
                item.SetAttribute("method", MethodIdentity(read.Method));
                if (read.Stat != null) item.SetAttribute("stat", read.Stat.defName);
                item.SetAttribute("pawn", read.PawnReceiver ? "1" : "0");
                data.AppendChild(item);
            }
            plan.CacheStored = RimKataEquipmentMemory.Current?.Store(Category, plan.Key, BaseFingerprint(plan), data) == true;
        }
    }
}
