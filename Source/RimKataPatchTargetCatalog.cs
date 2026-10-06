using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Xml;
using Verse;

namespace KRWF.RimKata
{
    // Patch discovery and manual refresh run on the main thread. AssemblyLoad only marks the snapshot dirty.
    internal static class RimKataPatchTargetCatalog
    {
        private const string Category = "patch-target-types";
        private const string Schema = "1";
        private static readonly Type[] RefreshBaseTypes = { typeof(Verb), typeof(Projectile) };
        private static readonly Dictionary<Assembly, AssemblyTypes> Runtime =
            new Dictionary<Assembly, AssemblyTypes>();
        private static readonly Dictionary<Type, Type[]> Combined = new Dictionary<Type, Type[]>();
        private static readonly Dictionary<Assembly, Dictionary<Type, Tuple<string, string>>> Identities =
            new Dictionary<Assembly, Dictionary<Type, Tuple<string, string>>>();
        private static Assembly[] assemblies;
        private static int assembliesDirty = 1;

        private sealed class AssemblyTypes
        {
            internal Type[] completeTypes;
            internal readonly Dictionary<Type, Type[]> derivedTypes = new Dictionary<Type, Type[]>();
        }

        static RimKataPatchTargetCatalog()
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_, loaded) =>
            {
                if (!loaded.LoadedAssembly.IsDynamic) Interlocked.Exchange(ref assembliesDirty, 1);
            };
        }

        internal static IEnumerable<Type> TypesDerivedFrom(Type baseType)
        {
            using (RimKataStartupDiagnostics.Measure("discovery", "patch_target_catalog")) return ResolveTypes(baseType);
        }

        private static Type[] ResolveTypes(Type baseType)
        {
            if (baseType == null) return Type.EmptyTypes;
            Assembly[] snapshot = Assemblies();
            if (Combined.TryGetValue(baseType, out Type[] combined))
            {
                RimKataStartupDiagnostics.Count("type_catalog_reused");
                return combined;
            }
            var result = new List<Type>();
            bool allComplete = true;
            for (int i = 0; i < snapshot.Length; i++)
            {
                Assembly assembly = snapshot[i];
                if (assembly.IsDynamic) continue;
                if (!Runtime.TryGetValue(assembly, out AssemblyTypes entry))
                {
                    entry = new AssemblyTypes();
                    Runtime.Add(assembly, entry);
                }

                if (!entry.derivedTypes.TryGetValue(baseType, out Type[] derived))
                {
                    RimKataEquipmentMemory.View view = RimKataEquipmentMemory.Current;
                    bool persistent = TryRecordIdentity(assembly, baseType, out string key, out string fingerprint);
                    if (persistent && TryRead(view, assembly, baseType, key, fingerprint, out derived))
                    {
                        entry.derivedTypes.Add(baseType, derived);
                        RimKataStartupDiagnostics.Count("type_cache_hit");
                    }
                    else
                    {
                        RimKataStartupDiagnostics.Count(persistent ? "type_cache_miss" : "type_cache_bypassed");
                        Type[] types = entry.completeTypes;
                        bool complete = types != null;
                        if (!complete) complete = TryReadAllTypes(assembly, out types);
                        derived = Filter(types, baseType);
                        if (complete)
                        {
                            entry.completeTypes = types;
                            entry.derivedTypes.Add(baseType, derived);
                            if (persistent) Store(view, key, fingerprint, derived, assembly);
                        }
                        else if (persistent)
                        {
                            view?.Remove(Category, key);
                        }
                        if (!complete) allComplete = false;
                    }
                }

                result.AddRange(derived);
            }
            combined = result.ToArray();
            if (allComplete) Combined[baseType] = combined;
            return combined;
        }

        // The supplied view is a staging view. Nothing here replaces active runtime lists or writes a file.
        internal static IEnumerable<object> ScanRefresh(RimKataEquipmentMemory.View view)
        {
            Assembly[] snapshot = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < snapshot.Length; i++)
            {
                Assembly assembly = snapshot[i];
                if (assembly.IsDynamic) continue;
                bool complete = TryReadAllTypes(assembly, out Type[] types);
                for (int j = 0; j < RefreshBaseTypes.Length; j++)
                {
                    Type baseType = RefreshBaseTypes[j];
                    if (TryRecordIdentity(assembly, baseType, out string key, out string fingerprint))
                    {
                        if (complete) Store(view, key, fingerprint, Filter(types, baseType), assembly);
                        else view?.Remove(Category, key);
                    }
                    yield return null;
                }
            }
        }

        internal static void Invalidate()
        {
            Runtime.Clear();
            Combined.Clear();
            Interlocked.Exchange(ref assembliesDirty, 1);
        }

        private static Assembly[] Assemblies()
        {
            if (Interlocked.Exchange(ref assembliesDirty, 0) != 0 || assemblies == null)
            {
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
                Combined.Clear();
            }
            return assemblies;
        }

        private static bool TryReadAllTypes(Assembly assembly, out Type[] types)
        {
            try
            {
                RimKataStartupDiagnostics.Count("assemblies_reflected");
                types = assembly.GetTypes();
                return true;
            }
            catch (ReflectionTypeLoadException exception)
            {
                // Harmony previously returned these loaded types. Keep that behavior for this call only.
                types = exception.Types ?? Type.EmptyTypes;
                return false;
            }
            catch
            {
                types = Type.EmptyTypes;
                return false;
            }
        }

        private static Type[] Filter(Type[] types, Type baseType)
        {
            var result = new List<Type>();
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type != null && baseType.IsAssignableFrom(type)) result.Add(type);
            }
            return result.ToArray();
        }

        private static bool TryRecordIdentity(Assembly assembly, Type baseType, out string key, out string fingerprint)
        {
            key = null;
            fingerprint = null;
            try
            {
                if (Identities.TryGetValue(assembly, out var records))
                {
                    if (records == null) return false;
                    if (records.TryGetValue(baseType, out var record))
                    {
                        key = record.Item1;
                        fingerprint = record.Item2;
                        return true;
                    }
                }
                else if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                {
                    Identities.Add(assembly, null);
                    return false;
                }
                string identity = AssemblyIdentity(assembly) + "\n" + baseType.AssemblyQualifiedName;
                key = RimKataEquipmentMemory.Fingerprint(identity);
                fingerprint = RimKataEquipmentMemory.Fingerprint(Schema + "\n" + identity + "\n"
                    + AssemblyIdentity(baseType.Assembly) + "\n" + AssemblyIdentity(typeof(Verb).Assembly)
                    + "\n" + AssemblyIdentity(typeof(RimKataPatchTargetCatalog).Assembly));
                if (records == null)
                {
                    records = new Dictionary<Type, Tuple<string, string>>();
                    Identities.Add(assembly, records);
                }
                records[baseType] = Tuple.Create(key, fingerprint);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string AssemblyIdentity(Assembly assembly)
            => assembly.FullName + "\n" + assembly.ManifestModule.ModuleVersionId.ToString("D");

        private static bool TryRead(RimKataEquipmentMemory.View view, Assembly assembly, Type baseType,
            string key, string fingerprint, out Type[] types)
        {
            types = null;
            if (view == null || view.ForceRefresh) return false;
            try
            {
                if (!view.TryRead(Category, key, fingerprint, out XmlElement data)
                    || data.GetAttribute("complete") != Schema
                    || !int.TryParse(data.GetAttribute("count"), NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                    || count < 0 || count != data.ChildNodes.Count) return false;
                var result = new List<Type>(count);
                var seen = new HashSet<Type>();
                foreach (XmlNode node in data.ChildNodes)
                {
                    if (!(node is XmlElement element) || element.Name != "type") return false;
                    string name = element.GetAttribute("name");
                    if (name.Length == 0) return false;
                    Type type = assembly.GetType(name, false, false);
                    if (type == null || type.Assembly != assembly || !baseType.IsAssignableFrom(type)
                        || !seen.Add(type)) return false;
                    result.Add(type);
                }
                types = result.ToArray();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Store(RimKataEquipmentMemory.View view, string key, string fingerprint, Type[] types, Assembly assembly)
        {
            if (view == null) return;
            try
            {
                XmlElement data = RimKataEquipmentMemory.NewData();
                data.SetAttribute("complete", Schema);
                data.SetAttribute("assembly", assembly.FullName);
                data.SetAttribute("module", assembly.ManifestModule.ModuleVersionId.ToString("D"));
                data.SetAttribute("count", types.Length.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < types.Length; i++)
                {
                    string name = types[i].FullName;
                    if (string.IsNullOrEmpty(name)) return;
                    XmlElement element = data.OwnerDocument.CreateElement("type");
                    element.SetAttribute("name", name);
                    data.AppendChild(element);
                }
                view.Store(Category, key, fingerprint, data);
            }
            catch
            {
                // A cache failure must not prevent Harmony target discovery.
            }
        }
    }
}
