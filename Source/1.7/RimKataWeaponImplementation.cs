using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    internal enum RimKataWeaponImplementationKind { Unknown, Standard, Specialized }

    // Preparation-only provenance. An allowed owner does not exempt its code from analysis.
    internal static class RimKataWeaponImplementation
    {
        private static readonly Assembly Game = typeof(Verb).Assembly;
        private static readonly Dictionary<ModContentPack, Origin> origins = new Dictionary<ModContentPack, Origin>();
        private static Dictionary<string, List<ModContentPack>> packages;
        private static HashSet<ModContentPack> active;

        private sealed class Origin
        {
            internal bool known;
            internal readonly HashSet<Assembly> assemblies = new HashSet<Assembly>();
            internal string fingerprint;
        }

        internal static RimKataWeaponImplementationKind Classify(ThingDef def, Type verbType,
            RimKataWeaponExecutionDiscovery.Result baseline)
        {
            if (def?.modContentPack == null || verbType == null || baseline == null)
                return RimKataWeaponImplementationKind.Unknown;
            Origin origin = GetOrigin(def.modContentPack);
            if (!origin.known || !Owned(verbType.Assembly, origin)
                || (def.modContentPack.IsOfficialMod && verbType.Assembly != Game))
                return RimKataWeaponImplementationKind.Unknown;
            // Only code reached by the raw implementation participates. Render-only
            // Comps and unrelated code in a dependency cannot make a weapon specialized.
            foreach (MethodInfo method in baseline.Methods)
                if (method?.DeclaringType == null || !Owned(method.DeclaringType.Assembly, origin))
                    return RimKataWeaponImplementationKind.Unknown;
            if (baseline.Custom) return RimKataWeaponImplementationKind.Specialized;
            // A weapon/dependency can implement its feature through Harmony rather
            // than a custom Verb. Raw IL alone cannot classify that feature as plain.
            foreach (MethodInfo method in baseline.Methods)
            {
                Patches patches = Harmony.GetPatchInfo(method);
                if (patches != null && (HasOwnedHook(patches.Prefixes, origin)
                    || HasOwnedHook(patches.Postfixes, origin) || HasOwnedHook(patches.Transpilers, origin)
                    || HasOwnedHook(patches.Finalizers, origin)
                    || HasOwnedHook(RimKataWeaponExecutionPlans.ExtraPatches(patches, "InnerPrefixes"), origin)
                    || HasOwnedHook(RimKataWeaponExecutionPlans.ExtraPatches(patches, "InnerPostfixes"), origin)
                    || HasOwnedHook(RimKataWeaponExecutionPlans.ExtraPatches(patches, "InnerFinalizers"), origin)))
                    return RimKataWeaponImplementationKind.Unknown;
            }
            return baseline.Complete ? RimKataWeaponImplementationKind.Standard
                : RimKataWeaponImplementationKind.Unknown;
        }

        internal static string Fingerprint(ThingDef def, Type verbType)
        {
            string source = def?.modContentPack == null ? "unknown" : GetOrigin(def.modContentPack).fingerprint;
            return RimKataEquipmentMemory.Fingerprint("1\n" + source + "\n"
                + RimKataWeaponExecutionPlans.TypeIdentity(verbType));
        }

        internal static void Clear()
        {
            origins.Clear();
            packages = null;
            active = null;
        }

        private static bool Owned(Assembly assembly, Origin origin)
            => assembly == Game || (assembly != null && origin.assemblies.Contains(assembly));

        private static bool HasOwnedHook(IEnumerable<Patch> patches, Origin origin)
        {
            foreach (Patch patch in patches)
            {
                Assembly owner = patch.PatchMethod?.DeclaringType?.Assembly;
                if (owner != null && owner != Game && owner != typeof(RimKataWeaponImplementation).Assembly
                    && origin.assemblies.Contains(owner)) return true;
            }
            return false;
        }

        private static Origin GetOrigin(ModContentPack source)
        {
            if (origins.TryGetValue(source, out Origin result)) return result;
            EnsurePackages();
            result = new Origin { known = active.Contains(source) };
            var tokens = new List<string> { "source|" + Package(source), "known|" + result.known,
                "official|" + source.IsOfficialMod };
            var visited = new HashSet<ModContentPack>();
            var pending = new Stack<ModContentPack>();
            pending.Push(source);
            while (pending.Count != 0)
            {
                ModContentPack mod = pending.Pop();
                if (!visited.Add(mod)) continue;
                string package = Package(mod);
                tokens.Add("mod|" + package);
                if (mod.assemblies?.loadedAssemblies != null)
                    foreach (Assembly assembly in mod.assemblies.loadedAssemblies)
                    {
                        if (assembly == null) continue;
                        result.assemblies.Add(assembly);
                        tokens.Add("assembly|" + package + "|" + assembly.FullName + "|"
                            + assembly.ManifestModule.ModuleVersionId.ToString("D"));
                    }
                List<ModDependency> dependencies = mod.ModMetaData?.Dependencies;
                if (dependencies == null) continue;
                foreach (ModDependency dependency in dependencies)
                {
                    if (dependency == null) continue;
                    AddDependency(package, dependency.packageId, pending, tokens);
                    if (dependency.alternativePackageIds != null)
                        foreach (string alternative in dependency.alternativePackageIds)
                            AddDependency(package, alternative, pending, tokens);
                }
            }
            tokens.Sort(StringComparer.Ordinal);
            result.fingerprint = RimKataEquipmentMemory.Fingerprint(string.Join("\n", tokens));
            origins.Add(source, result);
            return result;
        }

        private static void AddDependency(string owner, string id, Stack<ModContentPack> pending, List<string> tokens)
        {
            string key = Normalize(id);
            if (key.Length == 0) return;
            tokens.Add("dependency|" + owner + "|" + key);
            if (packages.TryGetValue(key, out List<ModContentPack> matches))
                foreach (ModContentPack mod in matches) pending.Push(mod);
        }

        private static void EnsurePackages()
        {
            if (packages != null) return;
            packages = new Dictionary<string, List<ModContentPack>>(StringComparer.Ordinal);
            active = new HashSet<ModContentPack>();
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod == null || !active.Add(mod)) continue;
                string key = Package(mod);
                if (key.Length == 0) continue;
                if (!packages.TryGetValue(key, out List<ModContentPack> values))
                    packages.Add(key, values = new List<ModContentPack>());
                values.Add(mod);
            }
        }

        private static string Package(ModContentPack mod)
            => Normalize(mod.ModMetaData?.PackageIdNonUnique ?? mod.PackageIdPlayerFacing);

        private static string Normalize(string value)
        {
            string result = (value ?? string.Empty).Trim().ToLowerInvariant();
            string postfix = ModMetaData.SteamModPostfix;
            return result.EndsWith(postfix, StringComparison.Ordinal)
                ? result.Substring(0, result.Length - postfix.Length) : result;
        }
    }
}
