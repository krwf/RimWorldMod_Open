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
        private const string YayoHarmonyId = "com.yayo.yayoAni";
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
                Initialize();
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
            if (initializationState != InitializationState.Waiting)
            {
                return;
            }

            MethodInfo drawExtras = AccessTools.Method(
                typeof(PawnRenderUtility),
                nameof(PawnRenderUtility.DrawEquipmentAndApparelExtras),
                new[] { typeof(Pawn), typeof(Vector3), typeof(Rot4), typeof(PawnRenderFlags) });
            MethodInfo drawCarriedWeapon = AccessTools.Method(
                typeof(PawnRenderUtility),
                nameof(PawnRenderUtility.DrawCarriedWeapon),
                new[] { typeof(ThingWithComps), typeof(Vector3), typeof(Rot4), typeof(float) });

            if (drawExtras == null || drawCarriedWeapon == null)
            {
                initializationState = InitializationState.Failed;
                Log.Error("[SYS Gun Spinning] RimWorld 1.6 weapon render methods were not found; compatibility patch was not applied.");
                return;
            }

            Patches extrasPatchInfo = Harmony.GetPatchInfo(drawExtras);
            Patches carriedPatchInfo = Harmony.GetPatchInfo(drawCarriedWeapon);
            if (!HasTranspiler(extrasPatchInfo, YayoHarmonyId)
                || !HasTranspiler(carriedPatchInfo, YayoHarmonyId))
            {
                if (!startupConstructorsFinished)
                {
                    return;
                }

                initializationState = InitializationState.Failed;
                Log.Warning("[SYS Gun Spinning] Yayo's Animation weapon patches were not detected; SYS was left unchanged.");
                return;
            }

            List<MethodInfo> sysPrefixes = FindSysPrefixes(extrasPatchInfo);
            if (sysPrefixes.Count == 0)
            {
                if (!startupConstructorsFinished)
                {
                    return;
                }

                initializationState = InitializationState.CompletedNoTarget;
                Log.Message("[SYS Gun Spinning] No active SYS weapon render prefix was found; no compatibility patch was needed.");
                return;
            }

            initializationState = InitializationState.Applying;

            List<SysSheathAdapter> adapters = new List<SysSheathAdapter>();
            foreach (Assembly assembly in sysPrefixes.Select(method => method.DeclaringType.Assembly).Distinct())
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

            MethodInfo compatPrefixMethod = AccessTools.Method(
                typeof(SysSheathRenderPatch),
                nameof(SysSheathRenderPatch.Prefix));
            MethodInfo compatPostfixMethod = AccessTools.Method(
                typeof(SysSheathRenderPatch),
                nameof(SysSheathRenderPatch.Postfix));
            if (compatPrefixMethod == null || compatPostfixMethod == null)
            {
                initializationState = InitializationState.Failed;
                Log.Error("[SYS Gun Spinning] Compatibility render methods were not found; SYS was left unchanged.");
                return;
            }

            HarmonyMethod prefix = new HarmonyMethod(compatPrefixMethod)
            {
                priority = Priority.Last
            };
            HarmonyMethod postfix = new HarmonyMethod(compatPostfixMethod)
            {
                priority = Priority.Last
            };

            try
            {
                // Register the replacement first so a failure while adding our patch
                // leaves the still-active SYS renderer untouched.
                Harmony.Patch(drawExtras, prefix: prefix, postfix: postfix);

                foreach (MethodInfo sysPrefix in sysPrefixes)
                {
                    Harmony.Unpatch(drawExtras, sysPrefix);
                }

                if (FindSysPrefixes(Harmony.GetPatchInfo(drawExtras)).Count != 0)
                {
                    throw new InvalidOperationException("one or more SYS prefixes remained active");
                }
            }
            catch (Exception exception)
            {
                initializationState = InitializationState.Failed;
                try
                {
                    Harmony.Unpatch(drawExtras, compatPrefixMethod);
                    Harmony.Unpatch(drawExtras, compatPostfixMethod);
                }
                catch (Exception rollbackException)
                {
                    Log.Error(
                        $"[SYS Gun Spinning] Compatibility rollback also failed: {rollbackException}");
                }

                Log.Error(
                    $"[SYS Gun Spinning] Could not replace the blocking SYS prefix; compatibility patch was rolled back: {exception}");
                return;
            }

            initializationState = InitializationState.Applied;
            Log.Message(
                $"[SYS Gun Spinning] Restored the original/Yayo weapon render path; removed {sysPrefixes.Count} blocking SYS prefix(es) and retained {adapters.Count} SYS sheath renderer(s).");
        }

        private static bool HasTranspiler(Patches patches, string owner)
        {
            return patches?.Transpilers?.Any(patch => patch.owner == owner) == true;
        }

        private static List<MethodInfo> FindSysPrefixes(Patches patches)
        {
            List<MethodInfo> result = new List<MethodInfo>();
            if (patches?.Prefixes == null)
            {
                return result;
            }

            foreach (Patch patch in patches.Prefixes)
            {
                MethodInfo method = patch.PatchMethod;
                if (patch.owner != SysHarmonyId
                    || method == null
                    || method.ReturnType != typeof(bool)
                    || method.DeclaringType?.FullName != SysPatchTypeName)
                {
                    continue;
                }

                if (!result.Contains(method))
                {
                    result.Add(method);
                }
            }

            return result;
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
        internal struct RenderState
        {
            internal bool Captured;
            internal Vector3 RootLoc;
        }

        private static Dictionary<Type, SysSheathAdapter> adaptersByCompType;

        internal static void Configure(IEnumerable<SysSheathAdapter> adapters)
        {
            adaptersByCompType = adapters.ToDictionary(adapter => adapter.CompType);
        }

        public static void Prefix(Vector3 drawPos, out RenderState __state)
        {
            __state = new RenderState
            {
                Captured = true,
                RootLoc = drawPos
            };
        }

        public static void Postfix(
            Pawn pawn,
            PawnRenderFlags flags,
            RenderState __state)
        {
            if (!__state.Captured
                || adaptersByCompType == null
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
                adapter.Draw(sheathComp, pawn, __state.RootLoc, graphic);
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
