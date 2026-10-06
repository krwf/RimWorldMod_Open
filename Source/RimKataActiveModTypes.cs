using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataActiveModTypes
    {
        private static readonly Dictionary<string, Type> Types = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly HashSet<Assembly> ActiveAssemblies = new HashSet<Assembly>();
        private static Assembly[] assemblies;

        internal static void Initialize()
        {
            using (RimKataStartupDiagnostics.Measure("discovery", "active_mod_assemblies"))
            {
                Types.Clear();
                ActiveAssemblies.Clear();
                var ordered = new List<Assembly>();
                foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                {
                    if (mod.assemblies?.loadedAssemblies == null) continue;
                    foreach (Assembly assembly in mod.assemblies.loadedAssemblies)
                        if (assembly != null && ActiveAssemblies.Add(assembly)) ordered.Add(assembly);
                }
                assemblies = ordered.ToArray();
                RimKataStartupDiagnostics.Count("active_mod_assemblies", assemblies.Length);
            }
        }

        internal static Type Find(string fullName)
        {
            if (assemblies == null) Initialize();
            if (Types.TryGetValue(fullName, out Type result)) return result;
            for (int i = 0; i < assemblies.Length; i++)
            {
                result = ExactType(assemblies[i], fullName);
                if (result != null) break;
            }
            Types[fullName] = result;
            return result;
        }

        internal static Type Find(Assembly owner, string fullName)
        {
            if (assemblies == null) Initialize();
            return owner != null && ActiveAssemblies.Contains(owner) ? ExactType(owner, fullName) : null;
        }

        private static Type ExactType(Assembly assembly, string fullName)
        {
            try { return assembly.GetType(fullName, false, false); }
            catch (TypeLoadException) { return null; }
            catch (System.IO.FileNotFoundException) { return null; }
            catch (System.IO.FileLoadException) { return null; }
        }
    }
}
