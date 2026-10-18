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
        private const string Schema = "2";
        private const int MaximumMethods = 256;
        private const int MaximumEffects = 128;

        internal static bool TryGet(RimKataWeaponExecutionPlan execution, bool force, out RimKataWeaponEffectPlan plan)
            => TryGet(execution, force, out plan, out _);

        internal static bool TryGet(RimKataWeaponExecutionPlan execution, bool force,
            out RimKataWeaponEffectPlan plan, out string reason)
        {
            plan = null;
            reason = null;
            if (execution?.Def == null || execution.VerbType == null || execution.HandledByCompatibility
                || string.IsNullOrEmpty(execution.Key)) return false;
            var memory = RimKataEquipmentMemory.Current;
            if (force) memory?.Remove(Category, execution.Key);
            else
            {
                try
                {
                    if (TryRestore(execution, out plan))
                    {
                        RimKataStartupDiagnostics.Count("weapon_effect_cache_hit");
                        return true;
                    }
                }
                catch { plan = null; } // Stale/corrupt metadata is a miss, not a partial plan.
            }
            memory?.Remove(Category, execution.Key);
            RimKataStartupDiagnostics.Count("weapon_effect_cache_miss");
            try
            {
                if (!RimKataWeaponEffectDiscovery.TryAnalyze(execution, out plan, out reason))
                { plan = null; return false; }
                if (!Valid(execution, plan))
                {
                    plan = null;
                    reason = "The original effect trace returned incomplete metadata.";
                    return false;
                }
            }
            catch (Exception exception)
            {
                plan = null;
                reason = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
            // Missing/unsupported/failed traces are deliberately not cached; later preparation retries them.
            reason = null;
            try { Store(execution, plan); }
            catch (Exception exception)
            {
                memory?.Remove(Category, execution.Key);
                Log.Warning("[RimKata] Could not remember the weapon effect path for " + execution.Def.defName
                    + ". The verified in-memory plan remains available. " + exception.GetType().Name + ": " + exception.Message);
            }
            return true;
        }

        private static string BaseFingerprint(RimKataWeaponExecutionPlan execution)
            => RimKataEquipmentMemory.Fingerprint(Schema + "\n" + execution.Def.defName
                + "\n" + TypeIdentity(execution.VerbType) + "\n" + execution.Fingerprint
                + "\n" + RimKataWeaponEffectDiscovery.AccessFingerprint()
                + "\n" + TypeIdentity(typeof(Verb)) + "\n" + TypeIdentity(typeof(Harmony))
                + "\n" + TypeIdentity(typeof(RimKataWeaponEffectCache)));

        private static bool TryRestore(RimKataWeaponExecutionPlan execution, out RimKataWeaponEffectPlan plan)
        {
            plan = null;
            var memory = RimKataEquipmentMemory.Current;
            if (memory == null || !memory.TryRead(Category, execution.Key, BaseFingerprint(execution), out XmlElement data)
                || data.GetAttribute("schema") != Schema || data.GetAttribute("weapon") != execution.Def.defName) return false;
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
            if (!Valid(execution, restored) || Snapshot(execution, restored) != data.GetAttribute("snapshot")) return false;
            plan = restored;
            return true;
        }

        private static bool Valid(RimKataWeaponExecutionPlan execution, RimKataWeaponEffectPlan plan)
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
            foreach (HediffDef manager in managers)
                if (!RimKataWeaponEffectDiscovery.ManagerOnly(manager)) return false;
            foreach (HediffDef effect in plan.Effects)
                if (managers.Contains(effect) || !RimKataWeaponEffectDiscovery.StatOnly(effect)) return false;
            return true;
        }

        private static bool Unique(HediffDef[] definitions)
        {
            var seen = new HashSet<HediffDef>();
            foreach (HediffDef def in definitions)
                if (def == null || string.IsNullOrEmpty(def.defName) || !seen.Add(def)) return false;
            return true;
        }

        private static void Store(RimKataWeaponExecutionPlan execution, RimKataWeaponEffectPlan plan)
        {
            XmlElement data = RimKataEquipmentMemory.NewData();
            data.SetAttribute("schema", Schema);
            data.SetAttribute("weapon", execution.Def.defName);
            data.SetAttribute("snapshot", Snapshot(execution, plan));
            foreach (MethodInfo method in plan.AnalyzedMethods)
            {
                XmlElement item = data.OwnerDocument.CreateElement("method");
                item.SetAttribute("identity", MemberIdentity(method));
                data.AppendChild(item);
            }
            AppendDefinitions(data, "manager", plan.ManagerDefs);
            AppendDefinitions(data, "effect", plan.Effects);
            RimKataEquipmentMemory.Current?.Store(Category, execution.Key, BaseFingerprint(execution), data);
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
            Token(text, RouteFingerprint(plan.AnalyzedMethods));
            var writer = new DataWriter(text);
            writer.Value(execution.Def.Verbs);
            writer.Value(execution.Def.comps);
            Token(text, "managers");
            foreach (HediffDef manager in plan.ManagerDefs) writer.Definition(manager);
            Token(text, "effects");
            foreach (HediffDef effect in plan.Effects) writer.Definition(effect);
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
