using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.SYSYayoCompat
{
    public sealed class SysYayoCompatMod : Mod
    {
        public SysYayoCompatMod(ModContentPack content) : base(content)
        {
            SysYayoCompatBootstrap.RegisterAfterStaticConstructors();
        }
    }

    internal static class SysYayoCompatBootstrap
    {
        private enum InitializationState
        {
            Waiting,
            Applying,
            Applied,
            CompletedNoTarget,
            Failed
        }

        private const string HarmonyId = "krwf.sysgunspinningcompat";
        private const string SysHarmonyId = "com.SYS.rimworld.mod";
        private const string SysPatchTypeName = "SYS.DrawEquipment_WeaponBackPatch";

        private static readonly Harmony Harmony = new Harmony(HarmonyId);
        private static bool startupHookInstalled;
        private static bool startupConstructorsFinished;
        private static InitializationState initializationState;

        internal static void RegisterAfterStaticConstructors()
        {
            if (startupHookInstalled)
            {
                return;
            }

            MethodInfo callAll = AccessTools.Method(
                typeof(StaticConstructorOnStartupUtility),
                nameof(StaticConstructorOnStartupUtility.CallAll));
            MethodInfo callback = AccessTools.Method(
                typeof(SysYayoCompatBootstrap),
                nameof(AfterStaticConstructors));
            if (callAll == null || callback == null)
            {
                Log.Error("[SYS Gun Spinning] RimWorld startup completion method was not found; compatibility patch was not scheduled.");
                return;
            }

            try
            {
                Harmony.Patch(
                    callAll,
                    postfix: new HarmonyMethod(callback)
                    {
                        priority = Priority.Last
                    });
                startupHookInstalled = true;
            }
            catch (Exception exception)
            {
                initializationState = InitializationState.Failed;
                Log.Error(
                    $"[SYS Gun Spinning] Could not schedule compatibility initialization after startup constructors: {exception}");
            }
        }

        public static void AfterStaticConstructors()
        {
            startupConstructorsFinished = true;
            Initialize();
        }

        internal static void Initialize()
        {
            if (!startupConstructorsFinished
                || initializationState != InitializationState.Waiting)
            {
                return;
            }

            MethodInfo drawExtras = AccessTools.Method(
                typeof(PawnRenderUtility),
                nameof(PawnRenderUtility.DrawEquipmentAndApparelExtras),
                new[] { typeof(Pawn), typeof(Vector3), typeof(Rot4), typeof(PawnRenderFlags) });

            if (drawExtras == null)
            {
                initializationState = InitializationState.Failed;
                Log.Error("[SYS Gun Spinning] RimWorld equipment render method was not found; compatibility patch was not applied.");
                return;
            }

            Patches extrasPatchInfo = Harmony.GetPatchInfo(drawExtras);
            List<Patch> sysPrefixes = FindSysOwnedPrefixes(extrasPatchInfo);
            if (sysPrefixes.Count == 0)
            {
                initializationState = InitializationState.CompletedNoTarget;
                Log.Message("[SYS Gun Spinning] No active SYS weapon render prefix was found; no compatibility patch was needed.");
                return;
            }

            if (!TryValidateSysPrefixes(sysPrefixes, out string validationError))
            {
                initializationState = InitializationState.Failed;
                Log.Error(
                    $"[SYS Gun Spinning] An unfamiliar SYS prefix was found on the equipment render method ({validationError}); nothing was changed.");
                return;
            }

            initializationState = InitializationState.Applying;

            List<SysSheathAdapter> adapters = new List<SysSheathAdapter>();
            foreach (Assembly assembly in sysPrefixes
                .Select(patch => patch.PatchMethod.DeclaringType.Assembly)
                .Distinct())
            {
                if (!SysSheathAdapter.TryCreate(assembly, out SysSheathAdapter adapter, out string error))
                {
                    initializationState = InitializationState.Failed;
                    Log.Error($"[SYS Gun Spinning] Could not preserve SYS sheath rendering from {assembly.Location}: {error}. SYS was left unchanged.");
                    return;
                }

                adapters.Add(adapter);
            }

            SysSheathRenderPatch.Configure(adapters);

            MethodInfo compatPostfixMethod = AccessTools.Method(
                typeof(SysSheathRenderPatch),
                nameof(SysSheathRenderPatch.Postfix));
            if (compatPostfixMethod == null)
            {
                initializationState = InitializationState.Failed;
                Log.Error("[SYS Gun Spinning] Compatibility render methods were not found; SYS was left unchanged.");
                return;
            }

            HarmonyMethod postfix = new HarmonyMethod(compatPostfixMethod)
            {
                priority = Priority.Last
            };

            try
            {
                // Add only the SYS sheath postfix before removing the blocker. Weapon
                // rendering remains owned by RimWorld and the other active render mods.
                Harmony.Patch(drawExtras, postfix: postfix);
                if (!HasOwnedPostfix(drawExtras, HarmonyId))
                {
                    throw new InvalidOperationException("the SYS sheath postfix was not registered");
                }

                Harmony.Unpatch(drawExtras, HarmonyPatchType.Prefix, SysHarmonyId);

                if (FindSysOwnedPrefixes(Harmony.GetPatchInfo(drawExtras)).Count != 0)
                {
                    throw new InvalidOperationException("one or more SYS prefixes remained active");
                }
            }
            catch (Exception exception)
            {
                initializationState = InitializationState.Failed;
                bool compatibilityPatchRemoved = true;
                try
                {
                    Harmony.Unpatch(drawExtras, HarmonyPatchType.Postfix, HarmonyId);
                    compatibilityPatchRemoved = !HasOwnedPostfix(drawExtras, HarmonyId);
                    if (!compatibilityPatchRemoved)
                    {
                        Log.Error("[SYS Gun Spinning] The compatibility postfix remained active during rollback.");
                    }
                }
                catch (Exception rollbackException)
                {
                    compatibilityPatchRemoved = false;
                    Log.Error(
                        $"[SYS Gun Spinning] Compatibility rollback also failed: {rollbackException}");
                }

                bool sysRestored;
                string restoreError;
                try
                {
                    sysRestored = TryRestoreSysPrefixes(drawExtras, sysPrefixes, out restoreError);
                }
                catch (Exception restoreException)
                {
                    sysRestored = false;
                    restoreError = restoreException.GetType().Name + ": " + restoreException.Message;
                }

                if (sysRestored && compatibilityPatchRemoved)
                {
                    Log.Error(
                        $"[SYS Gun Spinning] Could not replace the blocking SYS prefix; the SYS prefix set was restored and the compatibility patch was removed: {exception}");
                }
                else
                {
                    string rollbackStatus = "SYS restore: "
                        + (sysRestored ? "complete" : restoreError)
                        + "; compatibility postfix removal: "
                        + (compatibilityPatchRemoved ? "complete" : "failed");
                    Log.Error(
                        $"[SYS Gun Spinning] Could not replace the blocking SYS prefix, and rollback was incomplete ({rollbackStatus}): {exception}");
                }

                return;
            }

            initializationState = InitializationState.Applied;
            Log.Message(
                $"[SYS Gun Spinning] Restored the vanilla/active-mod weapon render path; removed {sysPrefixes.Count} blocking SYS prefix(es) and retained {adapters.Count} SYS sheath renderer(s).");
        }

        private static bool TryRestoreSysPrefixes(
            MethodInfo original,
            IEnumerable<Patch> originalPrefixes,
            out string error)
        {
            List<Patch> snapshots = originalPrefixes.ToList();
            List<string> failures = new List<string>();
            HashSet<MethodInfo> activeMethods = new HashSet<MethodInfo>(
                FindSysOwnedPrefixes(Harmony.GetPatchInfo(original))
                    .Select(patch => patch.PatchMethod));

            foreach (Patch patch in snapshots)
            {
                MethodInfo patchMethod = patch.PatchMethod;
                if (patchMethod == null || activeMethods.Contains(patchMethod))
                {
                    continue;
                }

                try
                {
                    HarmonyMethod restoredPrefix = new HarmonyMethod(
                        patchMethod,
                        patch.priority,
                        patch.before,
                        patch.after,
                        patch.debug);
                    new Harmony(patch.owner).Patch(original, prefix: restoredPrefix);
                    activeMethods.Add(patchMethod);
                }
                catch (Exception exception)
                {
                    failures.Add(patchMethod.DeclaringType?.FullName + "." + patchMethod.Name
                        + ": " + exception.GetType().Name + ": " + exception.Message);
                }
            }

            List<Patch> restoredPrefixes = FindSysOwnedPrefixes(Harmony.GetPatchInfo(original));
            foreach (Patch patch in snapshots)
            {
                if (!restoredPrefixes.Any(restored => HasEquivalentMetadata(patch, restored)))
                {
                    MethodInfo patchMethod = patch.PatchMethod;
                    failures.Add(patchMethod.DeclaringType?.FullName + "." + patchMethod.Name
                        + " was not restored with its original ordering metadata");
                }
            }

            if (restoredPrefixes.Count != snapshots.Count)
            {
                failures.Add("the restored SYS prefix count did not match the original snapshot");
            }

            error = failures.Count == 0 ? null : string.Join("; ", failures);
            return failures.Count == 0;
        }

        private static bool HasEquivalentMetadata(Patch expected, Patch actual)
        {
            return expected.PatchMethod == actual.PatchMethod
                && expected.owner == actual.owner
                && expected.priority == actual.priority
                && expected.debug == actual.debug
                && SequenceEqual(expected.before, actual.before)
                && SequenceEqual(expected.after, actual.after);
        }

        private static bool SequenceEqual(string[] left, string[] right)
        {
            return (left ?? Array.Empty<string>())
                .SequenceEqual(right ?? Array.Empty<string>());
        }

        private static bool HasOwnedPostfix(MethodInfo original, string owner)
        {
            return Harmony.GetPatchInfo(original)?.Postfixes?
                .Any(patch => patch.owner == owner) == true;
        }

        private static List<Patch> FindSysOwnedPrefixes(Patches patches)
        {
            List<Patch> result = new List<Patch>();
            if (patches?.Prefixes == null)
            {
                return result;
            }

            foreach (Patch patch in patches.Prefixes)
            {
                if (patch.owner != SysHarmonyId)
                {
                    continue;
                }

                result.Add(patch);
            }

            return result;
        }

        private static bool TryValidateSysPrefixes(IEnumerable<Patch> prefixes, out string error)
        {
            List<string> failures = new List<string>();
            HashSet<MethodInfo> methods = new HashSet<MethodInfo>();
            foreach (Patch patch in prefixes)
            {
                MethodInfo method = patch.PatchMethod;
                string methodName = method == null
                    ? null
                    : method.DeclaringType?.FullName + "." + method.Name;
                if (!IsBlockingSysPrefix(method))
                {
                    failures.Add(string.IsNullOrEmpty(methodName) ? "unresolved patch method" : methodName);
                }
                else if (!methods.Add(method))
                {
                    failures.Add(methodName + " was registered more than once");
                }
            }

            error = failures.Count == 0 ? null : string.Join("; ", failures);
            return failures.Count == 0;
        }

        private static bool IsBlockingSysPrefix(MethodInfo method)
        {
            // Ratkin and Miho use the same blocker type but reverse these two arguments.
            if (method == null
                || !method.IsStatic
                || method.ReturnType != typeof(bool)
                || method.DeclaringType?.FullName != SysPatchTypeName)
            {
                return false;
            }

            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 2
                && ((parameters[0].ParameterType == typeof(Pawn)
                        && parameters[1].ParameterType == typeof(Vector3))
                    || (parameters[0].ParameterType == typeof(Vector3)
                        && parameters[1].ParameterType == typeof(Pawn)));
        }
    }

    internal sealed class SysSheathAdapter
    {
        private delegate Graphic GraphicGetter(ThingComp comp);
        private delegate void DrawSheathDelegate(ThingComp comp, Pawn pawn, Vector3 drawLoc, Graphic graphic);

        private readonly GraphicGetter getFullGraphic;
        private readonly GraphicGetter getSheathOnlyGraphic;
        private readonly DrawSheathDelegate drawSheath;

        private SysSheathAdapter(
            Type compType,
            GraphicGetter getFullGraphic,
            GraphicGetter getSheathOnlyGraphic,
            DrawSheathDelegate drawSheath)
        {
            CompType = compType;
            this.getFullGraphic = getFullGraphic;
            this.getSheathOnlyGraphic = getSheathOnlyGraphic;
            this.drawSheath = drawSheath;
        }

        internal Type CompType { get; }

        internal Graphic GetFullGraphic(ThingComp comp)
        {
            return getFullGraphic(comp);
        }

        internal Graphic GetSheathOnlyGraphic(ThingComp comp)
        {
            return getSheathOnlyGraphic(comp);
        }

        internal void Draw(ThingComp comp, Pawn pawn, Vector3 drawLoc, Graphic graphic)
        {
            drawSheath(comp, pawn, drawLoc, graphic);
        }

        internal static bool TryCreate(Assembly sysAssembly, out SysSheathAdapter adapter, out string error)
        {
            adapter = null;
            error = null;

            try
            {
                Type compType = sysAssembly.GetType("SYS.CompSheath", false);
                Type drawType = sysAssembly.GetType("SYS.DrawEquipment_WeaponBackPatch", false);
                if (compType == null || drawType == null || !typeof(ThingComp).IsAssignableFrom(compType))
                {
                    error = "required SYS sheath types were not found";
                    return false;
                }

                MethodInfo fullGraphicGetter = AccessTools.PropertyGetter(compType, "FullGraphic");
                MethodInfo sheathOnlyGraphicGetter = AccessTools.PropertyGetter(compType, "SheathOnlyGraphic");
                MethodInfo drawSheathMethod = drawType
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(method => IsDrawSheathMethod(method, compType));

                if (fullGraphicGetter == null || sheathOnlyGraphicGetter == null || drawSheathMethod == null)
                {
                    error = "required SYS sheath members were not found";
                    return false;
                }

                adapter = new SysSheathAdapter(
                    compType,
                    CreateGraphicGetter(compType, fullGraphicGetter, "Full"),
                    CreateGraphicGetter(compType, sheathOnlyGraphicGetter, "Empty"),
                    CreateDrawSheathDelegate(compType, drawSheathMethod));
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        private static bool IsDrawSheathMethod(MethodInfo method, Type compType)
        {
            if (method.Name != "DrawSheath" || method.ReturnType != typeof(void))
            {
                return false;
            }

            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 4
                && parameters[0].ParameterType == compType
                && parameters[1].ParameterType == typeof(Pawn)
                && parameters[2].ParameterType == typeof(Vector3)
                && parameters[3].ParameterType == typeof(Graphic);
        }

        private static GraphicGetter CreateGraphicGetter(Type compType, MethodInfo getter, string suffix)
        {
            DynamicMethod dynamicMethod = new DynamicMethod(
                "SYSYayoCompat_Get" + suffix + "SheathGraphic",
                typeof(Graphic),
                new[] { typeof(ThingComp) },
                typeof(SysSheathAdapter),
                true);
            ILGenerator il = dynamicMethod.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, compType);
            il.Emit(getter.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, getter);
            il.Emit(OpCodes.Ret);
            return (GraphicGetter)dynamicMethod.CreateDelegate(typeof(GraphicGetter));
        }

        private static DrawSheathDelegate CreateDrawSheathDelegate(Type compType, MethodInfo drawMethod)
        {
            DynamicMethod dynamicMethod = new DynamicMethod(
                "SYSYayoCompat_DrawSheath",
                typeof(void),
                new[] { typeof(ThingComp), typeof(Pawn), typeof(Vector3), typeof(Graphic) },
                typeof(SysSheathAdapter),
                true);
            ILGenerator il = dynamicMethod.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, compType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Call, drawMethod);
            il.Emit(OpCodes.Ret);
            return (DrawSheathDelegate)dynamicMethod.CreateDelegate(typeof(DrawSheathDelegate));
        }
    }

    internal static class SysSheathRenderPatch
    {
        private static Dictionary<Type, SysSheathAdapter> adaptersByCompType;

        internal static void Configure(IEnumerable<SysSheathAdapter> adapters)
        {
            adaptersByCompType = adapters.ToDictionary(adapter => adapter.CompType);
        }

        public static void Postfix(
            Pawn pawn,
            Vector3 drawPos,
            PawnRenderFlags flags)
        {
            if (adaptersByCompType == null
                || pawn == null
                || pawn.Dead
                || !pawn.Spawned
                || pawn.equipment?.Primary == null
                || pawn.CurJob?.def?.neverShowWeapon == true)
            {
                return;
            }

            ThingWithComps primary = pawn.equipment.Primary;
            if (!TryGetSheath(primary, out SysSheathAdapter adapter, out ThingComp sheathComp))
            {
                return;
            }

            Stance_Busy busy = pawn.stances?.curStance as Stance_Busy;
            bool aiming = (flags & PawnRenderFlags.NeverAimWeapon) == 0
                && busy != null
                && !busy.neverAimWeapon
                && busy.focusTarg.IsValid;
            bool weaponVisible = aiming || PawnRenderUtility.CarryWeaponOpenly(pawn);

            // Preserve only SYS's sheath state. Weapon placement and animation stay in
            // the vanilla/active-mod render chain that the removed SYS prefix blocked.
            Graphic graphic;
            if (weaponVisible)
            {
                graphic = adapter.GetSheathOnlyGraphic(sheathComp);
            }
            else
            {
                if (RestUtility.InBed(pawn) || PawnUtility.GetPosture(pawn) != PawnPosture.Standing)
                {
                    return;
                }

                graphic = adapter.GetFullGraphic(sheathComp);
            }

            if (graphic != null)
            {
                adapter.Draw(sheathComp, pawn, drawPos, graphic);
            }
        }

        private static bool TryGetSheath(
            ThingWithComps primary,
            out SysSheathAdapter adapter,
            out ThingComp sheathComp)
        {
            List<ThingComp> comps = primary.AllComps;
            if (comps != null)
            {
                for (int index = 0; index < comps.Count; index++)
                {
                    ThingComp comp = comps[index];
                    if (comp == null)
                    {
                        continue;
                    }

                    Type runtimeType = comp.GetType();
                    if (adaptersByCompType.TryGetValue(runtimeType, out adapter))
                    {
                        sheathComp = comp;
                        return true;
                    }

                    foreach (KeyValuePair<Type, SysSheathAdapter> pair in adaptersByCompType)
                    {
                        if (pair.Key.IsAssignableFrom(runtimeType))
                        {
                            adapter = pair.Value;
                            sheathComp = comp;
                            return true;
                        }
                    }
                }
            }

            adapter = null;
            sheathComp = null;
            return false;
        }
    }
}
