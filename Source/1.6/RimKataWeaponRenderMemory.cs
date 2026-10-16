using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static partial class RimKataWeaponRenderDiscovery
    {
        private const string MemoryCategory = "weapon-renderers";

        internal sealed class DiscoveryPlan
        {
            private readonly RimKataEquipmentMemory.View memory;
            private readonly List<Candidate> candidates = new List<Candidate>();
            private readonly List<Renderer> prepared = new List<Renderer>();
            private string environment;

            internal DiscoveryPlan(RimKataEquipmentMemory.View memory) { this.memory = memory; }
            internal IReadOnlyList<Renderer> Renderers => prepared;

            internal IEnumerable<object> Scan()
            {
                environment = RimKataEquipmentMemory.Fingerprint("renderer-1\n"
                    + typeof(RimKataWeaponRenderDiscovery).Module.ModuleVersionId.ToString("D")
                    + RimKataDoorCacheEnvironment.Capture().OuterXml);
                Patches patches = Harmony.GetPatchInfo(DrawExtras);
                if (patches == null) yield break;
                var seen = new HashSet<MethodInfo>();
                foreach (Patch patch in patches.Prefixes)
                {
                    MethodInfo prefix = patch.PatchMethod;
                    if (candidates.Count >= MaximumRenderers || prefix == null || !seen.Add(prefix)
                        || !IsExternalAssembly(prefix.DeclaringType?.Assembly)
                        || !TryMapArguments(prefix, out int[] arguments)) continue;
                    ReadCandidate(prefix, arguments, false);
                    yield return null;
                }
                foreach (Patch patch in patches.Postfixes)
                {
                    MethodInfo postfix = patch.PatchMethod;
                    if (postfix?.DeclaringType?.FullName != "KRWF.SYSYayoCompat.SysSheathRenderPatch"
                        || postfix.Name != "Postfix" || !seen.Add(postfix)) continue;
                    ReadCandidate(postfix, null, true);
                    yield return null;
                }
            }

            private void ReadCandidate(MethodInfo method, int[] arguments, bool sheath)
            {
                try
                {
                    Candidate candidate;
                    if (sheath)
                        candidate = CreateSysSheathPostfix(method, memory, environment);
                    else
                    {
                        candidate = new Candidate(method, arguments) { fingerprint = environment };
                        if (!TryRestore(candidate, memory, environment)
                            && (!Visit(candidate, method, 0) || !candidate.hasCustomDraw))
                            candidate = null;
                    }
                    if (candidate == null)
                    {
                        Forget(memory, method);
                        return;
                    }
                    XmlElement data = null;
                    string key = candidate.restoredKey;
                    if (key == null)
                    {
                        try
                        {
                            data = SaveCandidate(candidate);
                            key = RimKataEquipmentMemory.Fingerprint(MethodKey(method) + candidate.fingerprint + data.OuterXml);
                            data.SetAttribute("key", key);
                        }
                        catch
                        {
                            Forget(memory, method);
                            key = "uncached-" + Guid.NewGuid().ToString("N");
                        }
                    }
                    candidate.renderer = new Renderer(
                        candidate.invoke ?? CompileInvoker(candidate.prefix, candidate.arguments),
                        candidate.compTypes, candidate.accessoriesOnly, key);
                    if (data != null) memory.Store(MemoryCategory, MethodKey(method), candidate.fingerprint, data);
                    candidates.Add(candidate);
                    prepared.Add(candidate.renderer);
                }
                catch (Exception exception)
                {
                    Forget(memory, method);
                    ReportUnsupported(method, exception);
                }
            }

            internal IReadOnlyList<Renderer> Install()
            {
                if (patcher == null) throw new InvalidOperationException("Weapon renderer patching is unavailable.");
                var installed = new List<Renderer>();
                foreach (Candidate candidate in candidates)
                {
                    try
                    {
                        foreach (MethodInfo method in candidate.methodsToInstrument)
                        {
                            if (instrumented.Contains(method))
                            {
                                RimKataStartupDiagnostics.Count("render_patch_skipped");
                                continue;
                            }
                            RimKataStartupPatches.Patch(patcher, method, transpiler: new HarmonyMethod(TranspilerMethod) { priority = Priority.Last });
                            instrumented.Add(method);
                        }
                        installed.Add(candidate.renderer);
                    }
                    catch (Exception exception)
                    {
                        Forget(memory, candidate.prefix);
                        ReportUnsupported(candidate.prefix, exception);
                    }
                }
                return installed.AsReadOnly();
            }
        }

        internal static void Apply(IReadOnlyList<Renderer> installed) { renderers = installed; }

        private static void Forget(RimKataEquipmentMemory.View memory, MethodInfo method)
        {
            try { memory.Remove(MemoryCategory, MethodKey(method)); }
            catch { }
        }

        private static string MethodKey(MethodInfo method)
            => method.DeclaringType.AssemblyQualifiedName + ":" + method.MetadataToken.ToString(CultureInfo.InvariantCulture);

        private static XmlElement SaveCandidate(Candidate candidate)
        {
            XmlElement data = RimKataEquipmentMemory.NewData();
            data.SetAttribute("schema", "2");
            data.SetAttribute("accessory", candidate.accessoriesOnly ? "1" : "0");
            var methods = new List<MethodInfo>(candidate.visited);
            methods.Sort((left, right) => StringComparer.Ordinal.Compare(MethodKey(left), MethodKey(right)));
            foreach (MethodInfo method in methods)
            {
                XmlElement element = data.OwnerDocument.CreateElement("method");
                element.SetAttribute("type", method.DeclaringType.AssemblyQualifiedName);
                element.SetAttribute("module", method.Module.ModuleVersionId.ToString("D"));
                element.SetAttribute("token", method.MetadataToken.ToString(CultureInfo.InvariantCulture));
                element.SetAttribute("patches", PatchFingerprint(method));
                element.SetAttribute("instrument", candidate.methodsToInstrument.Contains(method) ? "1" : "0");
                data.AppendChild(element);
            }
            var types = new List<Type>(candidate.compTypes);
            types.Sort((left, right) => StringComparer.Ordinal.Compare(left.AssemblyQualifiedName, right.AssemblyQualifiedName));
            foreach (Type type in types)
            {
                XmlElement element = data.OwnerDocument.CreateElement("comp");
                element.SetAttribute("type", type.AssemblyQualifiedName);
                element.SetAttribute("module", type.Module.ModuleVersionId.ToString("D"));
                data.AppendChild(element);
            }
            return data;
        }

        private static bool TryRestore(Candidate candidate, RimKataEquipmentMemory.View memory, string fingerprint)
        {
            bool restored = false;
            try
            {
                if (!memory.TryRead(MemoryCategory, MethodKey(candidate.prefix), fingerprint, out XmlElement data)
                    || data.GetAttribute("schema") != "2" || data.GetAttribute("key").Length != 64
                    || data.GetAttribute("accessory") != (candidate.accessoriesOnly ? "1" : "0")) return false;
                foreach (XmlNode node in data.ChildNodes)
                {
                    if (!(node is XmlElement element)) return false;
                    Type type = Type.GetType(element.GetAttribute("type"), false);
                    if (type == null || type.Module.ModuleVersionId.ToString("D") != element.GetAttribute("module")) return false;
                    if (element.Name == "comp")
                    {
                        if (!typeof(ThingComp).IsAssignableFrom(type)) return false;
                        candidate.compTypes.Add(type);
                        continue;
                    }
                    if (element.Name != "method"
                        || !int.TryParse(element.GetAttribute("token"), NumberStyles.None, CultureInfo.InvariantCulture, out int token)
                        || !(type.Module.ResolveMethod(token) is MethodInfo method)
                        || method.DeclaringType != type || method.ContainsGenericParameters || method.IsGenericMethod
                        || PatchFingerprint(method) != element.GetAttribute("patches")) return false;
                    candidate.visited.Add(method);
                    if (element.GetAttribute("instrument") == "1") candidate.methodsToInstrument.Add(method);
                }
                if (!candidate.visited.Contains(candidate.prefix) || candidate.methodsToInstrument.Count == 0) return false;
                candidate.hasCustomDraw = true;
                candidate.restoredKey = data.GetAttribute("key");
                restored = true;
                return true;
            }
            catch { return false; }
            finally
            {
                RimKataStartupDiagnostics.Count(restored ? "render_cache_hit" : "render_cache_miss");
                if (!restored)
                {
                    candidate.restoredKey = null;
                    candidate.visited.Clear();
                    candidate.methodsToInstrument.Clear();
                    candidate.compTypes.Clear();
                }
            }
        }

        private static string PatchFingerprint(MethodInfo method)
        {
            Patches patches = Harmony.GetPatchInfo(method);
            StringBuilder text = new StringBuilder();
            if (patches != null)
            {
                AppendPatches(text, "prefix", patches.Prefixes);
                AppendPatches(text, "postfix", patches.Postfixes);
                AppendPatches(text, "transpiler", patches.Transpilers);
                AppendPatches(text, "finalizer", patches.Finalizers);
            }
            return RimKataEquipmentMemory.Fingerprint(text.ToString());
        }

        private static void AppendPatches(StringBuilder text, string kind, IEnumerable<Patch> patches)
        {
            foreach (Patch patch in patches)
            {
                MethodInfo method = patch.PatchMethod;
                if (method == TranspilerMethod) continue;
                text.Append(kind).Append('|').Append(patch.owner).Append('|').Append(patch.priority)
                    .Append('|').Append(string.Join(",", patch.before ?? Array.Empty<string>()))
                    .Append('|').Append(string.Join(",", patch.after ?? Array.Empty<string>()))
                    .Append('|').Append(MethodKey(method)).Append('|').Append(method.Module.ModuleVersionId).Append('\n');
            }
        }

        private static MethodInfo OriginalForProbe(MethodInfo called)
        {
            if (called == ProbeDrawEquipment) return NativeDrawEquipment;
            if (called == ProbePrimary) return NativePrimary;
            if (called == ProbeDrawWornExtras) return NativeDrawWornExtras;
            if (called == ProbeSheathMesh) return NativeSheathMesh;
            if (called.DeclaringType != typeof(RimKataWeaponDrawCapture)) return called;
            string name = called.Name == "DrawMeshInternal" ? "Internal_DrawMesh" : called.Name;
            if (name != "Internal_DrawMesh" && name != "DrawMesh") return called;
            ParameterInfo[] parameters = called.GetParameters();
            var types = new Type[parameters.Length];
            for (int i = 0; i < types.Length; i++) types[i] = parameters[i].ParameterType;
            return typeof(Graphics).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, types, null) ?? called;
        }
    }
}
