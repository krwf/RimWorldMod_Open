using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    // Optional input adapter. No PS reference assembly and no additional pawn update loop.
    internal static partial class RimKataPerspectiveShiftCompat
    {
        private const string HarmonyId = "krwf.rimkata.perspectiveshift";
        internal static bool Enabled { get; private set; }
        internal static Pawn ControlledPawn { get; private set; }
        internal static bool OffenseStopped;
        internal static bool ManualMovement;
        private static object controlledAvatar;
        private static Game sessionGame;
        private static bool previousMovement;
        private static bool waitForFireRelease;
        private static FieldInfo avatarField;
        private static AccessTools.FieldRef<object, Pawn> avatarPawn;
        private static AccessTools.FieldRef<object, Vector3?> physicsPosition;
        private static AccessTools.FieldRef<object, Vector3> moveInput;
        private static AccessTools.FieldRef<object, bool> wasMoving, passedOut;
        private static Func<object, bool> mouseOverUI, mouseOverColonists, abilityJob;
        private static Func<bool> cameraLocked;
        private static Func<Pawn, bool> originalRunAndGun;
        private static KeyBindingDef forward, back, left, right;
        private delegate bool JobPrefix(Pawn pawn, ref Job result);
        private delegate bool WaitPrefix(JobDriver_Wait driver);
        private delegate bool FirePrefix(Pawn_DraftController controller, ref bool result);
        private delegate bool OverridePrefix(Pawn_JobTracker tracker);
        private static JobPrefix originalJobPrefix;
        private static WaitPrefix originalWaitPrefix;
        private static FirePrefix originalFirePrefix;
        private static OverridePrefix originalOverridePrefix;

        internal static bool IsControlled(Pawn pawn) => Enabled && pawn != null && pawn == ControlledPawn
            && sessionGame == Current.Game && !pawn.Dead && RimKataEligibilityCache.IsCachedQualifiedPawn(pawn);

        internal static void Apply(Harmony unused)
        {
            if (Enabled) return;
            Type avatar = RimKataActiveModTypes.Find("PerspectiveShift.Avatar");
            if (avatar == null) return;
            Assembly assembly = avatar.Assembly;
            Type state = assembly.GetType("PerspectiveShift.State", true);
            var harmony = new Harmony(HarmonyId);
            var replaced = new List<KeyValuePair<MethodBase, Patch>>();
            try
            {
                avatarField = AccessTools.Field(state, "Avatar");
                avatarPawn = AccessTools.FieldRefAccess<Pawn>(avatar, "pawn");
                physicsPosition = AccessTools.FieldRefAccess<Vector3?>(avatar, "physicsPosition");
                moveInput = AccessTools.FieldRefAccess<Vector3>(avatar, "moveInput");
                wasMoving = AccessTools.FieldRefAccess<bool>(avatar, "wasMovingLastFrame");
                passedOut = AccessTools.FieldRefAccess<bool>(avatar, "passedOut");
                mouseOverUI = AvatarPredicate(avatar, "IsMouseOverUI");
                mouseOverColonists = AvatarPredicate(avatar, "IsMouseOverColonistBar");
                abilityJob = AvatarPredicate(avatar, "IsAbilityCastJob");
                FieldInfo camera = AccessTools.Field(state, "CameraLockPosition");
                var reader = new DynamicMethod("RimKataPSCameraLock", typeof(Vector3?), Type.EmptyTypes,
                    typeof(RimKataPerspectiveShiftCompat), true);
                ILGenerator cameraIL = reader.GetILGenerator();
                cameraIL.Emit(OpCodes.Ldsfld, camera);
                cameraIL.Emit(OpCodes.Ret);
                var cameraReader = (Func<Vector3?>)reader.CreateDelegate(typeof(Func<Vector3?>));
                cameraLocked = () => cameraReader().HasValue;
                Type keys = assembly.GetType("PerspectiveShift.DefsOf", true);
                forward = (KeyBindingDef)AccessTools.Field(keys, "PS_MoveForward").GetValue(null);
                back = (KeyBindingDef)AccessTools.Field(keys, "PS_MoveBack").GetValue(null);
                left = (KeyBindingDef)AccessTools.Field(keys, "PS_MoveLeft").GetValue(null);
                right = (KeyBindingDef)AccessTools.Field(keys, "PS_MoveRight").GetValue(null);
                Type compatibility = assembly.GetType("PerspectiveShift.ModCompatibility", true);
                originalRunAndGun = AccessTools.MethodDelegate<Func<Pawn, bool>>(
                    AccessTools.Method(compatibility, "IsRunAndGunActiveFor", new[] { typeof(Pawn) }));

                MethodInfo jobs = External(assembly, "JobGivers_DisableForAvatar_Patch", "Prefix");
                originalJobPrefix = AccessTools.MethodDelegate<JobPrefix>(jobs);
                ReplacePrefix(harmony, AccessTools.Method(typeof(JobGiver_ConfigurableHostilityResponse), "TryGiveJob"),
                    jobs, nameof(CounterattackPrefix), replaced);
                ReplacePrefix(harmony, AccessTools.Method(typeof(JobGiver_AIFightEnemy), "TryGiveJob"),
                    jobs, nameof(CounterattackPrefix), replaced);
                MethodInfo wait = External(assembly, "JobDriver_Wait_CheckForAutoAttack_Patch", "Prefix");
                originalWaitPrefix = AccessTools.MethodDelegate<WaitPrefix>(wait);
                ReplacePrefix(harmony, AccessTools.Method(typeof(JobDriver_Wait), "CheckForAutoAttack"),
                    wait, nameof(WaitAttackPrefix), replaced);
                MethodInfo fire = External(assembly, "Pawn_DraftController_FireAtWill_Patch", "Prefix");
                originalFirePrefix = AccessTools.MethodDelegate<FirePrefix>(fire);
                ReplacePrefix(harmony, AccessTools.PropertyGetter(typeof(Pawn_DraftController), "FireAtWill"),
                    fire, nameof(FireAtWillPrefix), replaced);
                MethodInfo jobOverride = External(assembly, "Pawn_JobTracker_CheckForJobOverride_Patch", "Prefix");
                originalOverridePrefix = AccessTools.MethodDelegate<OverridePrefix>(jobOverride);
                ReplacePrefix(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "CheckForJobOverride"),
                    jobOverride, nameof(JobOverridePrefix), replaced);

                Patch(harmony, AccessTools.Method(state, "SetAvatar"), postfix: nameof(AvatarChanged));
                Patch(harmony, AccessTools.Method(state, "ClearAvatar"), postfix: nameof(AvatarChanged));
                Patch(harmony, AccessTools.Method(typeof(Game), "Dispose"), prefix: nameof(ClearSession));
                Patch(harmony, AccessTools.Method(avatar, "ExposeData"), postfix: nameof(ExposeAvatar));
                Patch(harmony, AccessTools.Method(avatar, "HandleSelectorClick"), prefix: nameof(SelectorClickPrefix));
                Patch(harmony, AccessTools.Method(avatar, "HandleFiring"), prefix: nameof(FiringPrefix));
                Patch(harmony, AccessTools.Method(avatar, "UpdatePhysics"), prefix: nameof(PhysicsPrefix), transpiler: nameof(MovementCalls));
                Patch(harmony, AccessTools.Method(avatar, "ProcessMovement"), prefix: nameof(ProcessMovementPrefix), transpiler: nameof(MovementCalls));
                Patch(harmony, AccessTools.Method(avatar, "HandleCombatStance"), prefix: nameof(CombatStancePrefix));
                Patch(harmony, AccessTools.Method(avatar, "PreventJobExpiry"), prefix: nameof(JobExpiryPrefix));
                ApplyRender(harmony, assembly);
                ApplyMotion(harmony, assembly);
                Enabled = true;
                AvatarChanged();
            }
            catch (Exception exception)
            {
                Enabled = false;
                harmony.UnpatchAll(HarmonyId);
                // Reinstall only the PS bindings removed by this adapter; never touch another owner's patches.
                foreach (var binding in replaced)
                    new Harmony(binding.Value.owner).Patch(binding.Key, prefix: CopyPatch(binding.Value));
                ClearSession();
                Log.Error("[RimKata] Perspective Shift integration failed.\n" + exception);
            }
        }

        private static MethodInfo External(Assembly assembly, string type, string method)
            => AccessTools.Method(assembly.GetType("PerspectiveShift." + type, true), method)
                ?? throw new MissingMethodException(type, method);

        private static Func<object, bool> AvatarPredicate(Type avatar, string name)
        {
            MethodInfo method = AccessTools.Method(avatar, name, Type.EmptyTypes);
            if (method?.ReturnType != typeof(bool) || method.IsStatic) throw new MissingMethodException(avatar.FullName, name);
            var wrapper = new DynamicMethod("RimKataPS" + name, typeof(bool), new[] { typeof(object) }, typeof(RimKataPerspectiveShiftCompat), true);
            ILGenerator il = wrapper.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, avatar);
            il.Emit(OpCodes.Call, method);
            il.Emit(OpCodes.Ret);
            return (Func<object, bool>)wrapper.CreateDelegate(typeof(Func<object, bool>));
        }

        private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(RimKataPerspectiveShiftCompat), name);
        private static void Patch(Harmony harmony, MethodBase method, string prefix = null, string postfix = null, string transpiler = null)
        {
            if (method == null) throw new MissingMethodException("Perspective Shift adapter target missing.");
            RimKataStartupPatches.Patch(harmony, method, prefix: prefix == null ? null : Hook(prefix),
                postfix: postfix == null ? null : Hook(postfix), transpiler: transpiler == null ? null : Hook(transpiler));
        }

        private static HarmonyMethod CopyPatch(Patch patch) => new HarmonyMethod(patch.PatchMethod)
            { priority = patch.priority, before = patch.before, after = patch.after };

        private static void ReplacePrefix(Harmony harmony, MethodBase target, MethodInfo original,
            string replacement, List<KeyValuePair<MethodBase, Patch>> replaced)
        {
            Patch binding = Harmony.GetPatchInfo(target)?.Prefixes.FirstOrDefault(p => p.PatchMethod == original
                && p.owner == "PerspectiveShiftMod");
            if (binding == null) throw new InvalidOperationException("Perspective Shift binding missing: " + target);
            var hook = Hook(replacement);
            hook.priority = binding.priority;
            hook.before = binding.before;
            hook.after = binding.after;
            RimKataStartupPatches.Patch(harmony, target, prefix: hook);
            harmony.Unpatch(target, original);
            replaced.Add(new KeyValuePair<MethodBase, Patch>(target, binding));
        }

        private static bool CounterattackPrefix(Pawn pawn, ref Job __result)
        {
            if (IsControlled(pawn) && !pawn.InMentalState && !pawn.Drafted)
            {
                if (MayAutoFire(pawn)) return true;
                __result = null;
                return false;
            }
            return originalJobPrefix(pawn, ref __result);
        }

        private static bool WaitAttackPrefix(JobDriver_Wait __instance)
            => IsControlled(__instance.pawn) && !__instance.pawn.InMentalState
                ? MayAutoFire(__instance.pawn)
                    && !__instance.pawn.Drafted
                : originalWaitPrefix(__instance);

        private static bool FireAtWillPrefix(Pawn_DraftController __instance, ref bool __result)
        {
            if (!IsControlled(__instance.pawn)) return originalFirePrefix(__instance, ref __result);
            __result = MayAutoFire(__instance.pawn);
            return false;
        }

        private static bool JobOverridePrefix(Pawn_JobTracker __instance, Pawn ___pawn)
            => MayAutoFire(___pawn) && !___pawn.Drafted
                && !___pawn.InMentalState ? true : originalOverridePrefix(__instance);

        private static void AvatarChanged() => Attach(avatarField.GetValue(null), true);

        private static void Attach(object avatar, bool reset)
        {
            if (ReferenceEquals(avatar, controlledAvatar) && sessionGame == Current.Game) return;
            if (manualFireActive && ControlledPawn != null) EndManualFire(ControlledPawn);
            if (reset) { ResetCombatInput(); ResetMotion(); ResetPlayerGizmos(); OffenseStopped = false; }
            else ResetManualFireInput();
            controlledAvatar = avatar;
            ControlledPawn = avatar == null ? null : avatarPawn(avatar);
            sessionGame = Current.Game;
            ManualMovement = previousMovement = waitForFireRelease = false;
        }

        private static void ClearSession()
        {
            if (manualFireActive && ControlledPawn != null) EndManualFire(ControlledPawn);
            controlledAvatar = null;
            ControlledPawn = null;
            sessionGame = null;
            ManualMovement = previousMovement = OffenseStopped = waitForFireRelease = false;
            ResetCombatInput();
            ResetMotion();
            ResetPlayerGizmos();
        }

        private static void ExposeAvatar(object __instance)
        {
            // These are input preferences for this avatar, not another weapon/target save registry.
            Scribe_Values.Look(ref OffenseStopped, "RimKataPSOffenseStopped", false);
            ExposeCombatInput();
            ExposeMotionInput();
            if (Scribe.mode == LoadSaveMode.PostLoadInit) Attach(__instance, false);
        }

        internal static void SyncAvatarPosition(Pawn pawn)
        {
            if (controlledAvatar == null || pawn != ControlledPawn) return;
            physicsPosition(controlledAvatar) = null;
            moveInput(controlledAvatar) = Vector3.zero;
            wasMoving(controlledAvatar) = false;
        }

        private static bool InputAvailable(object avatar)
        {
            Pawn pawn = ControlledPawn;
            return ReferenceEquals(avatar, controlledAvatar) && IsControlled(pawn)
                && pawn.Spawned && !pawn.Downed && !pawn.InMentalState && !passedOut(avatar)
                && pawn.Map == Find.CurrentMap && Find.TickManager?.Paused == false
                && !Find.Targeter.IsTargeting && !cameraLocked() && !mouseOverUI(avatar) && !mouseOverColonists(avatar);
        }

        private static bool SelectorClickPrefix(object __instance, ref bool __result)
        {
            if (Event.current?.type != EventType.MouseDown || !InputAvailable(__instance)) return true;
            Pawn pawn = ControlledPawn;
            if (pawn.Drafted && Event.current.button == 0 && OwnsFireInput(pawn))
            {
                // PS otherwise treats a click over a pawn as selection instead of firing.
                if (FiringPrefix(__instance)) return true;
                Event.current.Use();
                __result = true;
                return false;
            }
            if (Event.current.button != 1) return true;
            // Keep PS's inventory shortcut and orders for other selected pawns intact.
            Thing carried = pawn.carryTracker?.CarriedThing;
            if (carried != null && carried is not Pawn && carried is not Corpse) return true;
            foreach (Pawn selected in Find.Selector.SelectedPawns)
                if (selected != pawn) return true;
            IntVec3 cell = UI.MouseCell();
            if (!cell.InBounds(pawn.Map)) return true;
            if (Event.current.shift || Event.current.control || Event.current.alt || Event.current.command)
                return true;
            Thing target = null;
            bool occupied = false;
            foreach (Thing thing in cell.GetThingList(pawn.Map))
            {
                if (thing is Pawn clickedPawn && TryShowPlayerSubdueMenu(pawn, clickedPawn))
                {
                    Event.current.Use();
                    __result = true;
                    return false;
                }
                if (IsCombatTarget(pawn, thing)) { target = thing; break; }
                if (thing is Pawn || thing.def.category == ThingCategory.Building || thing.def.category == ThingCategory.Item)
                    occupied = true;
            }
            if (target != null)
            {
                EndManualFire(pawn);
                if (!CombatTarget(pawn, target)) return true;
                manualInputDrafted = pawn.Drafted;
                manualFireBlocked = false;
            }
            else
            {
                if (occupied) return true;
                EndManualFire(pawn);
                StopCombat(pawn);
                EndCombatJob(pawn);
            }
            Event.current.Use();
            __result = true;
            return false;
        }

        private static bool FiringPrefix(object __instance)
        {
            Pawn pawn = ControlledPawn;
            if (!ReferenceEquals(controlledAvatar, __instance) || !OwnsFireInput(pawn)) return true;
            // Once RimKata owns this input, a temporary rejection must not fire the primary through PS.
            if (!InputAvailable(__instance) || abilityJob(__instance) || !pawn.Drafted
                || waitForFireRelease || pawn.stances?.stunner?.Stunned == true
                || RimKataBreachUtility.BlocksAttacks(pawn) || MotionTarget(pawn) != null
                || pawn.CurJobDef == JobDefOf.AttackMelee) return false;
            // Actual full-body actions keep their own lock; RimKata's mobile aim is deliberately allowed.
            if (pawn.stances?.curStance is Stance_Busy && !(pawn.stances.curStance is Stance_RimKataAim)) return false;
            IntVec3 cell = UI.MouseCell();
            if (!cell.InBounds(pawn.Map)) return false;
            Thing target = null;
            foreach (Thing thing in cell.GetThingList(pawn.Map))
            {
                if (thing is Pawn && thing != pawn) { target = thing; break; }
                if (target == null && (thing.def.category == ThingCategory.Building || thing.def.category == ThingCategory.Item)) target = thing;
            }
            // Both slots keep their own attack type, even at Touch distance.
            CombatFire(pawn, target == null ? new LocalTargetInfo(cell) : new LocalTargetInfo(target));
            return false;
        }

        private static bool OwnsFireInput(Pawn pawn)
        {
            if (!IsControlled(pawn)) return false;
            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            if (primary != null && RimKataEquipmentUtility.IsWeaponEnabled(primary.def)) return true;
            ThingWithComps secondary = RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn, primary, true)
                ? RimKataWeaponSlotUtility.SecondaryWeapon(pawn) : null;
            return secondary != null && RimKataEquipmentUtility.IsWeaponEnabled(secondary.def);
        }

        internal static void NotifyGizmoOrder(Pawn pawn)
        {
            if (!IsControlled(pawn)) return;
            EndManualFire(pawn);
            manualInputDrafted = pawn.Drafted;
            manualFireBlocked = OffenseStopped = false;
            waitForFireRelease = true;
        }

        private static void PhysicsPrefix(object __instance)
        {
            if (!ReferenceEquals(controlledAvatar, __instance)) Attach(__instance, true);
            Pawn pawn = ControlledPawn;
            if (!IsControlled(pawn)) return;
            bool held = Input.GetMouseButton(0);
            UpdateManualFireInput(pawn, held);
            if (!held) waitForFireRelease = false;
            if (pawn.Map != Find.CurrentMap || pawn.Downed || pawn.InMentalState) return;
            bool input = !WorldRendererUtility.WorldSelected && !passedOut(__instance) && !cameraLocked()
                && !Find.WindowStack.WindowsPreventCameraMotion && GUI.GetNameOfFocusedControl() == ""
                && (forward.IsDown || back.IsDown || left.IsDown || right.IsDown);
            ManualMovement = input;
            if (input && (!previousMovement || OwnsMovement(pawn)))
            {
                // Read raw key intent before PS suppresses movement for a busy stance or takeover job.
                MotionInput(pawn);
                if (!pawn.Drafted && !HasFocus(pawn))
                {
                    CancelForMovement(pawn);
                    EndCombatJob(pawn);
                }
            }
            previousMovement = input;
        }

        private static bool ProcessMovementPrefix(object __instance)
        {
            Pawn pawn = ControlledPawn;
            if (!ReferenceEquals(controlledAvatar, __instance) || !IsControlled(pawn) || ManualMovement || !OwnsMovement(pawn)) return true;
            SyncAvatarPosition(pawn);
            return false;
        }

        private static void EndCombatJob(Pawn pawn)
        {
            if (MotionTarget(pawn) != null) MotionInput(pawn);
            if (pawn.CurJobDef != RimKataDefOf.RimKata_Attack) return;
            pawn.pather?.StopDead();
            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            SyncAvatarPosition(pawn);
        }

        private static bool CombatStancePrefix(object __instance)
            => !ReferenceEquals(controlledAvatar, __instance) || !IsControlled(ControlledPawn);

        private static bool JobExpiryPrefix(object __instance)
            => !ReferenceEquals(controlledAvatar, __instance) || !IsControlled(ControlledPawn)
                || (ControlledPawn.CurJobDef != RimKataDefOf.RimKata_Attack && !OwnsMovement(ControlledPawn));

        private static bool MayMoveAndFire(Pawn pawn) => IsControlled(pawn) && (pawn.Drafted || HasFocus(pawn))
            && RimKataTargetAccess.SettingsFor(pawn)?.movingFireEnabled != false
                ? true : originalRunAndGun(pawn);

        private static void EndMovementJob(Pawn_JobTracker tracker, JobCondition condition, bool startNewJob, bool canReturnToPool)
        {
            Pawn pawn = ControlledPawn;
            if (pawn?.jobs == tracker && IsControlled(pawn) && !pawn.Drafted && HasFocus(pawn)
                && (pawn.CurJobDef == RimKataDefOf.RimKata_Attack || pawn.CurJobDef == JobDefOf.AttackStatic)) return;
            tracker.EndCurrentJob(condition, startNewJob, canReturnToPool);
        }

        private static IEnumerable<CodeInstruction> MovementCalls(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            MethodInfo original = originalRunAndGun.Method;
            MethodInfo endJob = AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob));
            var codes = new List<CodeInstruction>(instructions);
            int count = 0, jobCalls = 0;
            foreach (CodeInstruction code in codes)
                if (code.Calls(original))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(RimKataPerspectiveShiftCompat), nameof(MayMoveAndFire));
                    count++;
                }
                else if (__originalMethod.Name == "ProcessMovement" && code.Calls(endJob))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(RimKataPerspectiveShiftCompat), nameof(EndMovementJob));
                    jobCalls++;
                }
            if (count != 1) throw new InvalidOperationException("Perspective Shift movement gate did not match.");
            if (__originalMethod.Name == "ProcessMovement" && jobCalls != 1)
                throw new InvalidOperationException("Perspective Shift movement job cancellation did not match.");
            return codes;
        }
    }
}
