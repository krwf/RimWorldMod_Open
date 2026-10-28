using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml;
using HarmonyLib;
using Verse;
using static KRWF.RimKata.RimKataWeaponExecutionPlans;

namespace KRWF.RimKata
{
    // Metadata only: original methods still perform the effect work. Cache hits restore
    // a verified trace; they never create a weapon, pawn or replacement effect formula.
    internal static class RimKataWeaponEffectCache
    {
        private const string Category = "weapon_effects";
        private const string StatusCategory = "weapon_effect_status";
        private const string Schema = "3";
        private const int MaximumMethods = RimKataWeaponExecutionDiscovery.MaximumMethods;
        private const int MaximumEffects = 128;
        // A rejection survives unequipping and ordinary settings refreshes, but never a new process.
        // Keep only the existing route fingerprint and reason; no failed graph or XML is retained.
        private static readonly Dictionary<string, Rejection> rejected = new Dictionary<string, Rejection>(StringComparer.Ordinal);

        private sealed class Rejection
        {
            internal string Fingerprint;
            internal string Reason;
            internal bool Reported;
        }

        internal static void Invalidate(string key)
        {
            rejected.Remove(key);
            RimKataEquipmentMemory.Current?.Remove(Category, key);
            RimKataEquipmentMemory.Current?.Remove(StatusCategory, key);
        }

        internal static bool RememberUnsupported(RimKataWeaponExecutionPlan execution, string reason)
        {
            if (rejected.TryGetValue(execution.Key, out Rejection previous)
                && previous.Fingerprint == execution.Fingerprint && previous.Reason == reason)
            {
                if (previous.Reported) return false;
                previous.Reported = true;
                return true;
            }
            rejected[execution.Key] = new Rejection
                { Fingerprint = execution.Fingerprint, Reason = reason, Reported = true };
            return true;
        }

        // Called only by explicit catalog preparation. Equipment acquisition restores below;
        // a cache miss during a game must never expand the analysis scope or ask to register.
        internal static bool Prepare(RimKataWeaponExecutionPlan execution, bool force = false)
        {
            if (!CanRead(execution) || execution.Failed || execution.RegistrationDeclined
                || (!execution.AnalysisComplete && execution.Mode != RimKataWeaponExecutionMode.Copied
                    && !execution.RegistrationAccepted)) return false;
            execution.EffectCacheFingerprint = null;
            execution.EffectCacheSnapshot = null;
            var memory = RimKataEquipmentMemory.Current;
            if (force) Invalidate(execution.Key);
            else
            {
                try
                {
                    if (TryRestore(execution, true, out _))
                    {
                        rejected.Remove(execution.Key);
                        RimKataStartupDiagnostics.Count("weapon_effect_cache_hit");
                        return true;
                    }
                }
                catch { } // Only this preparation boundary may replace stale metadata.
            }
            memory?.Remove(Category, execution.Key);
            memory?.Remove(StatusCategory, execution.Key);
            RimKataStartupDiagnostics.Count("weapon_effect_cache_miss");
            RimKataWeaponEffectPlan plan = null;
            string reason = null;
            try
            {
                if (!RimKataWeaponEffectDiscovery.TryAnalyze(execution, out plan, out reason))
                {
                    plan = null;
                    if (reason != null) { RememberUnavailable(execution, reason); return false; }
                    // A completed check with no effects is different from missing metadata.
                }
                else if (!Valid(execution, plan))
                {
                    RememberUnavailable(execution, "The original effect trace returned incomplete metadata.");
                    return false;
                }
            }
            catch (Exception exception)
            {
                RememberUnavailable(execution, exception.GetType().Name + ": " + exception.Message);
                return false;
            }
            try
            {
                if (!Store(execution, plan))
                    throw new InvalidOperationException("The prepared effect metadata could not be stored.");
                rejected.Remove(execution.Key);
                return true;
            }
            catch (Exception exception)
            {
                memory?.Remove(Category, execution.Key);
                memory?.Remove(StatusCategory, execution.Key);
                RememberUnavailable(execution, plan == null ? null
                    : "Prepared weapon effect metadata is unavailable. Use Refresh to prepare it again.");
                Log.Warning("[RimKata] Could not remember the weapon effect path for " + execution.Def.defName
                    + ". Existing effect connections remain available; new connections will keep the original effect path. "
                    + exception.GetType().Name + ": " + exception.Message);
                return false;
            }
        }

        internal static bool TryGet(RimKataWeaponExecutionPlan execution,
            out RimKataWeaponEffectPlan plan, out string reason)
        {
            plan = null;
            reason = null;
            if (!CanRead(execution)) return false;
            if (rejected.TryGetValue(execution.Key, out Rejection previous)
                && previous.Fingerprint == execution.Fingerprint)
            {
                reason = previous.Reason;
                return false;
            }
            try
            {
                if (TryRestore(execution, false, out plan)) return plan != null;
            }
            catch { plan = null; }
            reason = "Prepared weapon effect metadata is unavailable. Use Refresh to prepare it again.";
            return false;
        }

        private static bool CanRead(RimKataWeaponExecutionPlan execution)
            => execution?.Def != null && execution.VerbType != null && !execution.HandledByCompatibility
                && !string.IsNullOrEmpty(execution.Key);

        private static void RememberUnavailable(RimKataWeaponExecutionPlan execution, string reason)
        {
            if (rejected.TryGetValue(execution.Key, out Rejection previous)
                && previous.Fingerprint == execution.Fingerprint && previous.Reason == reason) return;
            rejected[execution.Key] = new Rejection { Fingerprint = execution.Fingerprint, Reason = reason };
        }

        private static string BaseFingerprint(RimKataWeaponExecutionPlan execution)
            => RimKataEquipmentMemory.Fingerprint(Schema + "\n" + execution.Def.defName
                + "\n" + TypeIdentity(execution.VerbType) + "\n" + execution.Fingerprint
                + "\n" + RimKataWeaponEffectDiscovery.AccessFingerprint()
                + "\n" + TypeIdentity(typeof(Verb)) + "\n" + TypeIdentity(typeof(Harmony))
                + "\n" + TypeIdentity(typeof(RimKataWeaponEffectCache)));

        private static bool TryRestore(RimKataWeaponExecutionPlan execution, bool validateSource,
            out RimKataWeaponEffectPlan plan)
        {
            plan = null;
            var memory = RimKataEquipmentMemory.Current;
            string fingerprint = validateSource ? BaseFingerprint(execution) : execution.EffectCacheFingerprint;
            if (string.IsNullOrEmpty(fingerprint)) return false;
            if (memory != null && memory.TryRead(StatusCategory, execution.Key, fingerprint, out XmlElement status)
                && status.GetAttribute("schema") == Schema && status.GetAttribute("weapon") == execution.Def.defName
                && status.GetAttribute("status") == "none" && !status.HasChildNodes
                && MatchesSnapshot(execution, null, status.GetAttribute("snapshot"), validateSource))
            {
                RememberPrepared(execution, fingerprint, status.GetAttribute("snapshot"), validateSource);
                return true;
            }
            if (memory == null || !memory.TryRead(Category, execution.Key, fingerprint, out XmlElement data)
                || data.GetAttribute("schema") != Schema || data.GetAttribute("weapon") != execution.Def.defName) return false;
            if (data.GetAttribute("status") != "prepared") return false;
            var methods = new List<MethodInfo>();
            var managers = new List<HediffDef>();
            var effects = new List<HediffDef>();
            foreach (XmlNode child in data.ChildNodes)
            {
                if (!(child is XmlElement item)) return false;
                if (item.Name == "method")
                {
                    var method = ResolveMember(item.GetAttribute("identity")) as MethodInfo;
                    if (method == null || methods.Contains(method) || methods.Count >= MaximumMethods) return false;
                    methods.Add(method);
                }
                else if (item.Name == "manager" || item.Name == "effect")
                {
                    HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(item.GetAttribute("def"));
                    List<HediffDef> list = item.Name == "manager" ? managers : effects;
                    if (def == null || list.Contains(def) || list.Count >= MaximumEffects) return false;
                    list.Add(def);
                }
                else return false;
            }
            var restored = new RimKataWeaponEffectPlan
            {
                WeaponDef = execution.Def, VerbType = execution.VerbType,
                ManagerDefs = managers.ToArray(), Effects = effects.ToArray(), AnalyzedMethods = methods.ToArray()
            };
            if (!Valid(execution, restored, validateSource)
                || !MatchesSnapshot(execution, restored, data.GetAttribute("snapshot"), validateSource)) return false;
            RememberPrepared(execution, fingerprint, data.GetAttribute("snapshot"), validateSource);
            plan = restored;
            return true;
        }

        private static bool MatchesSnapshot(RimKataWeaponExecutionPlan execution, RimKataWeaponEffectPlan plan,
            string snapshot, bool validateSource)
            => !string.IsNullOrEmpty(snapshot)
                && (validateSource ? Snapshot(execution, plan) : execution.EffectCacheSnapshot) == snapshot;

        private static void RememberPrepared(RimKataWeaponExecutionPlan execution, string fingerprint,
            string snapshot, bool validateSource = true)
        {
            if (!validateSource) return;
            execution.EffectCacheFingerprint = fingerprint;
            execution.EffectCacheSnapshot = snapshot;
        }

        private static bool Valid(RimKataWeaponExecutionPlan execution, RimKataWeaponEffectPlan plan,
            bool validateDefinitions = true)
        {
            if (plan == null || plan.WeaponDef != execution.Def || plan.VerbType != execution.VerbType
                || plan.AnalyzedMethods == null || plan.AnalyzedMethods.Length == 0
                || plan.AnalyzedMethods.Length > MaximumMethods || plan.ManagerDefs == null
                || plan.Effects == null || plan.Effects.Length == 0
                || plan.ManagerDefs.Length > MaximumEffects || plan.Effects.Length > MaximumEffects) return false;
            var methods = new HashSet<MethodInfo>(plan.AnalyzedMethods);
            if (methods.Contains(null) || methods.Count != plan.AnalyzedMethods.Length) return false;
            foreach (MethodInfo method in methods)
                if (method.ContainsGenericParameters || method.DeclaringType == null) return false;
            if (!Unique(plan.ManagerDefs) || !Unique(plan.Effects)) return false;
            var managers = new HashSet<HediffDef>(plan.ManagerDefs);
            if (validateDefinitions)
                foreach (HediffDef manager in managers)
                    if (!RimKataWeaponEffectDiscovery.ManagerOnly(manager)) return false;
            foreach (HediffDef effect in plan.Effects)
                if (managers.Contains(effect)
                    || (validateDefinitions && !RimKataWeaponEffectDiscovery.StatOnly(effect))) return false;
            return true;
        }

        private static bool Unique(HediffDef[] definitions)
        {
            var seen = new HashSet<HediffDef>();
            foreach (HediffDef def in definitions)
                if (def == null || string.IsNullOrEmpty(def.defName) || !seen.Add(def)) return false;
            return true;
        }

        private static bool Store(RimKataWeaponExecutionPlan execution, RimKataWeaponEffectPlan plan)
        {
            XmlElement data = RimKataEquipmentMemory.NewData();
            data.SetAttribute("schema", Schema);
            data.SetAttribute("weapon", execution.Def.defName);
            data.SetAttribute("status", plan == null ? "none" : "prepared");
            string snapshot = Snapshot(execution, plan);
            string fingerprint = BaseFingerprint(execution);
            data.SetAttribute("snapshot", snapshot);
            if (plan != null)
            {
                foreach (MethodInfo method in plan.AnalyzedMethods)
                {
                    XmlElement item = data.OwnerDocument.CreateElement("method");
                    item.SetAttribute("identity", MemberIdentity(method));
                    data.AppendChild(item);
                }
                AppendDefinitions(data, "manager", plan.ManagerDefs);
                AppendDefinitions(data, "effect", plan.Effects);
            }
            // No-effect weapons need one small common-cache record, not an analysis XML file.
            bool stored = RimKataEquipmentMemory.Current?.Store(plan == null ? StatusCategory : Category,
                execution.Key, fingerprint, data) == true;
            if (stored) RememberPrepared(execution, fingerprint, snapshot);
            return stored;
        }

        private static void AppendDefinitions(XmlElement data, string kind, HediffDef[] definitions)
        {
            foreach (HediffDef def in definitions)
            {
                XmlElement item = data.OwnerDocument.CreateElement(kind);
                item.SetAttribute("def", def.defName);
                data.AppendChild(item);
            }
        }

        private static string Snapshot(RimKataWeaponExecutionPlan execution, RimKataWeaponEffectPlan plan)
        {
            var text = new StringBuilder();
            Token(text, RimKataAllowedWeaponStore.CapturedSourceXml(execution.Def));
            Token(text, RouteFingerprint(plan?.AnalyzedMethods ?? Array.Empty<MethodInfo>()));
            var writer = new DataWriter(text);
            writer.Value(execution.Def.Verbs);
            writer.Value(execution.Def.comps);
            Token(text, "managers");
            if (plan != null) foreach (HediffDef manager in plan.ManagerDefs) writer.Definition(manager);
            Token(text, "effects");
            if (plan != null) foreach (HediffDef effect in plan.Effects) writer.Definition(effect);
            return RimKataEquipmentMemory.Fingerprint(text.ToString());
        }
        private static void Token(StringBuilder text, string value)
        {
            if (value == null) { text.Append("-1:"); return; }
            text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        }

        // Read fields only, never properties. Def references terminate recursion; the
        // manager/effect definitions are expanded explicitly, including their stages.
        private sealed class DataWriter
        {
            private readonly StringBuilder text;
            private readonly HashSet<object> active = new HashSet<object>(RimKataReferenceComparer<object>.Instance);
            private int remaining = 8192;
            internal DataWriter(StringBuilder text) { this.text = text; }

            internal void Definition(HediffDef def)
            {
                Token(text, TypeIdentity(def.GetType()));
                Token(text, def.defName);
                Token(text, def.modContentPack?.PackageIdPlayerFacing);
                Fields(def, 0, typeof(Def), false);
            }

            internal void Value(object value, int depth = 0)
            {
                if (--remaining < 0 || depth > 12) throw new InvalidOperationException("Effect data exceeded the bounded snapshot limit.");
                if (value == null) { Token(text, null); return; }
                Type type = value.GetType();
                Token(text, TypeIdentity(type));
                if (value is Type referencedType) { Token(text, TypeIdentity(referencedType)); return; }
                if (value is Def referencedDef)
                {
                    Token(text, referencedDef.defName);
                    Token(text, referencedDef.modContentPack?.PackageIdPlayerFacing);
                    return;
                }
                if (value is string stringValue) { Token(text, stringValue); return; }
                if (value is float single) { Token(text, single.ToString("R", CultureInfo.InvariantCulture)); return; }
                if (value is double number) { Token(text, number.ToString("R", CultureInfo.InvariantCulture)); return; }
                if (type.IsPrimitive || type.IsEnum || value is decimal)
                {
                    Token(text, Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                }
                if (value is Delegate || value is MemberInfo || typeof(UnityEngine.Object).IsAssignableFrom(type))
                    throw new InvalidOperationException("Effect data contains a runtime object.");
                if (!active.Add(value)) throw new InvalidOperationException("Effect data contains a reference cycle.");
                try
                {
                    if (value is IList list)
                    {
                        Token(text, list.Count.ToString(CultureInfo.InvariantCulture));
                        foreach (object item in list) Value(item, depth + 1);
                    }
                    else if (value is IDictionary)
                        throw new InvalidOperationException("Effect data contains an unordered mapping.");
                    else Fields(value, depth, typeof(object), true);
                }
                finally { active.Remove(value); }
            }

            private void Fields(object value, int depth, Type stop, bool includePrivate)
            {
                var fields = new List<FieldInfo>();
                for (Type type = value.GetType(); type != null && type != stop; type = type.BaseType)
                    fields.AddRange(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly
                        | (includePrivate ? BindingFlags.NonPublic : 0)));
                fields.Sort((a, b) => string.CompareOrdinal(MemberIdentity(a), MemberIdentity(b)));
                Token(text, fields.Count.ToString(CultureInfo.InvariantCulture));
                foreach (FieldInfo field in fields)
                {
                    Token(text, MemberIdentity(field));
                    Value(field.GetValue(value), depth + 1);
                }
            }
        }
    }
}
