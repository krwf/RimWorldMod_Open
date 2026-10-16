using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace KRWF.RimKata
{
    internal static class RimKataWeaponDrawCapture
    {
        [ThreadStatic] private static CaptureScope active;
        [ThreadStatic] private static Stack<CaptureScope> scopePool;

        private static readonly Dictionary<Mesh, Mesh> mirroredMeshes = new Dictionary<Mesh, Mesh>();
        private static readonly InternalDrawMeshDelegate DrawInternal = CreateInternalDelegate();

        private delegate void InternalDrawMeshDelegate(Mesh mesh, int submeshIndex, Matrix4x4 matrix,
            Material material, int layer, Camera camera, MaterialPropertyBlock properties,
            ShadowCastingMode castShadows, bool receiveShadows, Transform probeAnchor,
            LightProbeUsage lightProbeUsage, LightProbeProxyVolume lightProbeProxyVolume);

        internal readonly struct DrawCommand
        {
            internal readonly Mesh Mesh;
            internal readonly Matrix4x4 Matrix;
            internal readonly Material Material;
            internal readonly int Layer;
            internal readonly Camera Camera;
            internal readonly int SubmeshIndex;
            internal readonly MaterialPropertyBlock Properties;
            internal readonly ShadowCastingMode CastShadows;
            internal readonly bool ReceiveShadows;
            internal readonly Transform ProbeAnchor;
            internal readonly LightProbeUsage LightProbeUsage;
            internal readonly LightProbeProxyVolume LightProbeProxyVolume;

            internal DrawCommand(Mesh mesh, Matrix4x4 matrix, Material material, int layer,
                Camera camera = null, int submeshIndex = 0, MaterialPropertyBlock properties = null,
                ShadowCastingMode castShadows = ShadowCastingMode.On, bool receiveShadows = true,
                Transform probeAnchor = null, LightProbeUsage lightProbeUsage = LightProbeUsage.BlendProbes,
                LightProbeProxyVolume lightProbeProxyVolume = null)
            {
                Mesh = mesh;
                Matrix = matrix;
                Material = material;
                Layer = layer;
                Camera = camera;
                SubmeshIndex = submeshIndex;
                Properties = properties;
                CastShadows = castShadows;
                ReceiveShadows = receiveShadows;
                ProbeAnchor = probeAnchor;
                LightProbeUsage = lightProbeUsage;
                LightProbeProxyVolume = lightProbeProxyVolume;
            }

            internal bool CanReplay => Mesh != null && Material != null && Properties == null
                && SubmeshIndex >= 0 && SubmeshIndex < Mesh.subMeshCount;

            internal void Submit(Mesh mesh, Matrix4x4 matrix,
                bool mirrorSecondaryDepth = false, bool adjustSecondaryHeight = false,
                Vector3 pawnPivot = default(Vector3), bool keepSecondaryHeight = false,
                float weaponAngleOffset = 0f, bool lowerSecondaryDepth = false, bool accessory = false,
                bool finalPose = false, float bodyAltitude = 0f)
            {
                if (mirrorSecondaryDepth)
                {
                    // Reflecting the fallen weapon layer would put one weapon behind the body.
                    matrix.m13 = RimKataGroundPoseRender.WeaponsAboveBody()
                        ? Matrix.m13 - 0.001f
                        : 2f * bodyAltitude - Matrix.m13;
                }
                else if (lowerSecondaryDepth)
                {
                    matrix.m13 = Matrix.m13 - 0.001f;
                }
                if (adjustSecondaryHeight && !keepSecondaryHeight)
                {
                    matrix.m23 = pawnPivot.z + (matrix.m23 - pawnPivot.z) * 0.5f;
                }
                if (weaponAngleOffset != 0f)
                {
                    Vector4 position = matrix.GetColumn(3);
                    matrix = Matrix4x4.Rotate(Quaternion.AngleAxis(weaponAngleOffset, Vector3.up))
                        * matrix;
                    matrix.SetColumn(3, position);
                }
                if (!finalPose)
                    matrix = accessory ? RimKataGroundPoseRender.TransformAccessory(matrix)
                        : RimKataGroundPoseRender.TransformEquipment(matrix);
                DrawInternal(mesh, SubmeshIndex, matrix, Material, Layer, Camera, Properties,
                    CastShadows, ReceiveShadows, ProbeAnchor, LightProbeUsage, LightProbeProxyVolume);
            }
        }

        // Captured commands become invalid when Dispose returns their storage to the pool.
        internal sealed class CaptureScope : IDisposable
        {
            private readonly List<DrawCommand> commands = new List<DrawCommand>(4);
            private readonly List<DrawCommand> accessories = new List<DrawCommand>(2);
            private readonly List<Mesh> replayMeshes = new List<Mesh>(4);
            private CaptureScope previous;
            private bool disposed = true;

            internal int Count => commands.Count;
            internal int AccessoryCount => accessories.Count;
            internal IReadOnlyList<DrawCommand> Commands => commands;

            internal void Enter()
            {
                previous = active;
                disposed = false;
                active = this;
            }

            internal void Record(DrawCommand command, bool accessory = false)
            {
                commands.Add(command);
                if (accessory) accessories.Add(command);
            }

            internal bool Replay(bool weaponsOnly = false)
            {
                if (disposed || commands.Count == 0) return false;
                bool replayed = false;
                for (int i = 0; i < commands.Count; i++)
                {
                    if (weaponsOnly && accessories.Contains(commands[i])) continue;
                    if (!commands[i].CanReplay) return false;
                }
                for (int i = 0; i < commands.Count; i++)
                {
                    DrawCommand command = commands[i];
                    if (weaponsOnly && accessories.Contains(command)) continue;
                    command.Submit(command.Mesh, command.Matrix, accessory: accessories.Contains(command));
                    replayed = true;
                }
                return replayed;
            }

            internal bool ReplayHeldWeapon(Vector3 position, float rotation,
                float maximumAltitude = float.PositiveInfinity)
            {
                if (disposed) return false;
                int first = -1;
                float bottom = float.PositiveInfinity;
                float top = float.NegativeInfinity;
                for (int i = 0; i < commands.Count; i++)
                {
                    if (accessories.Contains(commands[i])) continue;
                    if (!commands[i].CanReplay) return false;
                    if (first < 0) first = i;
                    bottom = Mathf.Min(bottom, commands[i].Matrix.m13);
                    top = Mathf.Max(top, commands[i].Matrix.m13);
                }
                if (first < 0) return false;
                Matrix4x4 reference = commands[first].Matrix;
                Vector3 origin = reference.GetColumn(3);
                float weaponBottom = position.y;
                float layerScale = 1f;
                if (top > bottom && weaponBottom + top - bottom > maximumAltitude)
                    layerScale = Mathf.Clamp01((maximumAltitude - weaponBottom) / (top - bottom));
                // Requested altitude is the multipart weapon bottom, not the first part origin.
                position.y += origin.y - bottom;
                // Ignore the planar mesh's zero Y scale when reading its angle.
                float sourceAngle = Mathf.Atan2(reference.m02, reference.m22) * Mathf.Rad2Deg;
                Matrix4x4 transform = Matrix4x4.Translate(position)
                    * Matrix4x4.Rotate(Quaternion.AngleAxis(rotation - sourceAngle, Vector3.up))
                    * Matrix4x4.Translate(-origin);
                for (int i = 0; i < commands.Count; i++)
                {
                    DrawCommand command = commands[i];
                    if (accessories.Contains(command)) continue;
                    Matrix4x4 matrix = transform * command.Matrix;
                    if (layerScale < 1f)
                        matrix.m13 = weaponBottom + (command.Matrix.m13 - bottom) * layerScale;
                    command.Submit(command.Mesh, matrix, finalPose: true);
                }
                return true;
            }

            internal bool ReplayMirrored(Vector3 pivot, float facingAngle, float bodyAltitude,
                float visualAngleOffset = 0f, bool sideFacingSecondary = false,
                bool keepSecondaryHeight = false, bool accessoriesOnly = false)
            {
                List<DrawCommand> selected = accessoriesOnly ? accessories : commands;
                if (disposed || selected.Count == 0) return false;
                replayMeshes.Clear();
                // MaterialPropertyBlock has no snapshot API; reused blocks cannot be replayed safely.
                for (int i = 0; i < selected.Count; i++)
                {
                    DrawCommand command = selected[i];
                    if (!command.CanReplay) return false;
                    Mesh replayMesh = command.Mesh;
                    if (!sideFacingSecondary && !TryGetMirroredMesh(command.Mesh, out replayMesh))
                        return false;
                    replayMeshes.Add(replayMesh);
                }

                Matrix4x4 reflection = sideFacingSecondary
                    ? Matrix4x4.identity
                    : WorldReflection(pivot, facingAngle);
                if (visualAngleOffset != 0f)
                {
                    Matrix4x4 rotation = Matrix4x4.Rotate(
                        Quaternion.AngleAxis(visualAngleOffset, Vector3.up));
                    rotation.m03 = pivot.x - rotation.m00 * pivot.x - rotation.m02 * pivot.z;
                    rotation.m23 = pivot.z - rotation.m20 * pivot.x - rotation.m22 * pivot.z;
                    reflection = rotation * reflection;
                }
                for (int i = 0; i < selected.Count; i++)
                {
                    DrawCommand command = selected[i];
                    Matrix4x4 matrix = reflection * command.Matrix;
                    if (!sideFacingSecondary)
                    {
                        // Cancel the mesh's local X reflection in the matrix to preserve winding parity.
                        matrix.m00 = -matrix.m00;
                        matrix.m10 = -matrix.m10;
                        matrix.m20 = -matrix.m20;
                        matrix.m30 = -matrix.m30;
                    }
                    command.Submit(replayMeshes[i], matrix, sideFacingSecondary,
                        adjustSecondaryHeight: sideFacingSecondary, pawnPivot: pivot,
                        keepSecondaryHeight: keepSecondaryHeight,
                        lowerSecondaryDepth: !sideFacingSecondary,
                        accessory: accessories.Contains(command), bodyAltitude: bodyAltitude);
                }
                return true;
            }

            public void Dispose()
            {
                if (disposed) return;
                if (!ReferenceEquals(active, this))
                    throw new InvalidOperationException("Weapon draw capture scopes must be disposed in nesting order.");
                active = previous;
                previous = null;
                disposed = true;
                commands.Clear();
                accessories.Clear();
                replayMeshes.Clear();
                scopePool ??= new Stack<CaptureScope>(2);
                if (scopePool.Count < 4) scopePool.Push(this);
            }
        }

        internal static CaptureScope Begin()
        {
            CaptureScope scope = scopePool?.Count > 0 ? scopePool.Pop() : new CaptureScope();
            scope.Enter();
            return scope;
        }

        internal static MethodInfo ReplacementFor(MethodInfo original)
        {
            if (original?.DeclaringType != typeof(Graphics)
                || (original.Name != nameof(Graphics.DrawMesh) && original.Name != "Internal_DrawMesh"))
                return null;
            ParameterInfo[] parameters = original.GetParameters();
            Type[] types = new Type[parameters.Length];
            for (int i = 0; i < parameters.Length; i++) types[i] = parameters[i].ParameterType;
            return typeof(RimKataWeaponDrawCapture).GetMethod(
                original.Name == "Internal_DrawMesh" ? nameof(DrawMeshInternal) : nameof(DrawMesh),
                BindingFlags.Public | BindingFlags.Static, null, types, null);
        }

        public static void DrawAccessoryMesh(Mesh mesh, Vector3 position, Quaternion rotation,
            Material material, int layer)
        {
            if (active != null)
                active.Record(new DrawCommand(mesh, Matrix4x4.TRS(position, rotation, Vector3.one),
                    material, layer), accessory: true);
            else
            {
                Matrix4x4 matrix = RimKataGroundPoseRender.TransformAccessory(
                    Matrix4x4.TRS(position, rotation, Vector3.one));
                Graphics.DrawMesh(mesh, matrix, material, layer);
                RimKataWeaponRenderProbe.NotifyMeshDraw();
            }
        }

        internal static void ClearMeshCache()
        {
            foreach (Mesh mirrored in mirroredMeshes.Values)
                if (mirrored != null) UnityEngine.Object.Destroy(mirrored);
            mirroredMeshes.Clear();
        }

        private static Matrix4x4 WorldReflection(Vector3 pivot, float facingAngle)
        {
            float radians = facingAngle * Mathf.Deg2Rad;
            float x = Mathf.Sin(radians);
            float z = Mathf.Cos(radians);
            Matrix4x4 result = Matrix4x4.identity;
            result.m00 = 2f * x * x - 1f;
            result.m02 = result.m20 = 2f * x * z;
            result.m22 = 2f * z * z - 1f;
            result.m03 = pivot.x - result.m00 * pivot.x - result.m02 * pivot.z;
            result.m23 = pivot.z - result.m20 * pivot.x - result.m22 * pivot.z;
            return result;
        }

        internal static bool TryGetMirroredMesh(Mesh source, out Mesh mirrored)
        {
            if (mirroredMeshes.TryGetValue(source, out mirrored)) return mirrored != null;
            if (!source.isReadable)
            {
                mirroredMeshes.Add(source, null);
                return false;
            }

            mirrored = null;
            try
            {
                mirrored = UnityEngine.Object.Instantiate(source);
                mirrored.name = source.name + " (RimKata mirrored)";
                Vector3[] vertices = source.vertices;
                for (int i = 0; i < vertices.Length; i++) vertices[i].x = -vertices[i].x;
                mirrored.vertices = vertices;

                Vector3[] normals = source.normals;
                for (int i = 0; i < normals.Length; i++) normals[i].x = -normals[i].x;
                if (normals.Length != 0) mirrored.normals = normals;
                Vector4[] tangents = source.tangents;
                for (int i = 0; i < tangents.Length; i++)
                {
                    tangents[i].x = -tangents[i].x;
                    tangents[i].w = -tangents[i].w;
                }
                if (tangents.Length != 0) mirrored.tangents = tangents;

                for (int submesh = 0; submesh < source.subMeshCount; submesh++)
                {
                    MeshTopology topology = source.GetTopology(submesh);
                    if (topology != MeshTopology.Triangles && topology != MeshTopology.Quads) continue;
                    int[] indices = source.GetIndices(submesh, false);
                    int stride = topology == MeshTopology.Triangles ? 3 : 4;
                    for (int i = 0; i < indices.Length; i += stride)
                    {
                        int swap = indices[i + 1];
                        indices[i + 1] = indices[i + stride - 1];
                        indices[i + stride - 1] = swap;
                    }
                    mirrored.SetIndices(indices, topology, submesh, false, checked((int)source.GetBaseVertex(submesh)));
                }
                Bounds bounds = source.bounds;
                Vector3 center = bounds.center;
                center.x = -center.x;
                bounds.center = center;
                mirrored.bounds = bounds;
                mirroredMeshes.Add(source, mirrored);
                return true;
            }
            catch (Exception)
            {
                if (mirrored != null) UnityEngine.Object.Destroy(mirrored);
                mirrored = null;
                mirroredMeshes[source] = null;
                return false;
            }
        }

        private static InternalDrawMeshDelegate CreateInternalDelegate()
        {
            MethodInfo original = typeof(Graphics).GetMethod("Internal_DrawMesh",
                BindingFlags.NonPublic | BindingFlags.Static);
            return (InternalDrawMeshDelegate)Delegate.CreateDelegate(typeof(InternalDrawMeshDelegate), original);
        }

        public static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer)
        {
            if (active == null && (RimKataBreachWeaponRender.Active || RimKataSubdueWeaponRender.Active || RimKataReactiveRender.Active)) return;
            if (active != null) active.Record(new DrawCommand(mesh, matrix, material, layer));
            else
            {
                matrix.m23 = RimKataWeaponRenderProbe.PlaceExternalPrimaryHeight(matrix.m23);
                matrix = RimKataGroundPoseRender.TransformExternalEquipment(matrix);
                Graphics.DrawMesh(mesh, matrix, material, layer);
                RimKataWeaponRenderProbe.NotifyMeshDraw();
            }
        }

        public static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer, Camera camera)
        {
            if (active == null && (RimKataBreachWeaponRender.Active || RimKataSubdueWeaponRender.Active || RimKataReactiveRender.Active)) return;
            if (active != null) active.Record(new DrawCommand(mesh, matrix, material, layer, camera));
            else
            {
                matrix.m23 = RimKataWeaponRenderProbe.PlaceExternalPrimaryHeight(matrix.m23);
                matrix = RimKataGroundPoseRender.TransformExternalEquipment(matrix);
                Graphics.DrawMesh(mesh, matrix, material, layer, camera);
                RimKataWeaponRenderProbe.NotifyMeshDraw();
            }
        }

        public static void DrawMesh(Mesh mesh, Vector3 position, Quaternion rotation, Material material, int layer)
        {
            if (active == null && (RimKataBreachWeaponRender.Active || RimKataSubdueWeaponRender.Active || RimKataReactiveRender.Active)) return;
            if (active != null)
                active.Record(new DrawCommand(mesh, Matrix4x4.TRS(position, rotation, Vector3.one), material, layer));
            else
            {
                position.z = RimKataWeaponRenderProbe.PlaceExternalPrimaryHeight(position.z);
                RimKataGroundPoseRender.TransformExternalEquipment(ref position, ref rotation);
                Graphics.DrawMesh(mesh, position, rotation, material, layer);
                RimKataWeaponRenderProbe.NotifyMeshDraw();
            }
        }

        public static void DrawMesh(Mesh mesh, Vector3 position, Quaternion rotation, Material material, int layer, Camera camera)
        {
            if (active == null && (RimKataBreachWeaponRender.Active || RimKataSubdueWeaponRender.Active || RimKataReactiveRender.Active)) return;
            if (active != null)
                active.Record(new DrawCommand(mesh, Matrix4x4.TRS(position, rotation, Vector3.one), material, layer, camera));
            else
            {
                position.z = RimKataWeaponRenderProbe.PlaceExternalPrimaryHeight(position.z);
                RimKataGroundPoseRender.TransformExternalEquipment(ref position, ref rotation);
                Graphics.DrawMesh(mesh, position, rotation, material, layer, camera);
                RimKataWeaponRenderProbe.NotifyMeshDraw();
            }
        }

        public static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer,
            Camera camera, int submeshIndex, MaterialPropertyBlock properties, ShadowCastingMode castShadows,
            bool receiveShadows, Transform probeAnchor, LightProbeUsage lightProbeUsage, LightProbeProxyVolume lightProbeProxyVolume)
        {
            if (active == null && (RimKataBreachWeaponRender.Active || RimKataSubdueWeaponRender.Active || RimKataReactiveRender.Active)) return;
            if (active != null)
                active.Record(new DrawCommand(mesh, matrix, material, layer, camera, submeshIndex,
                    properties, castShadows, receiveShadows, probeAnchor, lightProbeUsage, lightProbeProxyVolume));
            else
            {
                matrix.m23 = RimKataWeaponRenderProbe.PlaceExternalPrimaryHeight(matrix.m23);
                matrix = RimKataGroundPoseRender.TransformExternalEquipment(matrix);
                Graphics.DrawMesh(mesh, matrix, material, layer, camera, submeshIndex, properties,
                    castShadows, receiveShadows, probeAnchor, lightProbeUsage, lightProbeProxyVolume);
                RimKataWeaponRenderProbe.NotifyMeshDraw();
            }
        }

        public static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer,
            Camera camera, int submeshIndex, MaterialPropertyBlock properties, ShadowCastingMode castShadows,
            bool receiveShadows, Transform probeAnchor, LightProbeUsage lightProbeUsage)
        {
            if (active == null && (RimKataBreachWeaponRender.Active || RimKataSubdueWeaponRender.Active || RimKataReactiveRender.Active)) return;
            if (active != null)
                active.Record(new DrawCommand(mesh, matrix, material, layer, camera, submeshIndex,
                    properties, castShadows, receiveShadows, probeAnchor, lightProbeUsage));
            else
            {
                matrix.m23 = RimKataWeaponRenderProbe.PlaceExternalPrimaryHeight(matrix.m23);
                matrix = RimKataGroundPoseRender.TransformExternalEquipment(matrix);
                Graphics.DrawMesh(mesh, matrix, material, layer, camera, submeshIndex, properties,
                    castShadows, receiveShadows, probeAnchor, lightProbeUsage);
                RimKataWeaponRenderProbe.NotifyMeshDraw();
            }
        }

        public static void DrawMesh(Mesh mesh, Vector3 position, Quaternion rotation, Material material, int layer,
            Camera camera, int submeshIndex, MaterialPropertyBlock properties, ShadowCastingMode castShadows,
            bool receiveShadows, Transform probeAnchor, bool useLightProbes)
        {
            if (active == null && (RimKataBreachWeaponRender.Active || RimKataSubdueWeaponRender.Active || RimKataReactiveRender.Active)) return;
            if (active != null)
                active.Record(new DrawCommand(mesh, Matrix4x4.TRS(position, rotation, Vector3.one), material,
                    layer, camera, submeshIndex, properties, castShadows, receiveShadows, probeAnchor,
                    useLightProbes ? LightProbeUsage.BlendProbes : LightProbeUsage.Off));
            else
            {
                position.z = RimKataWeaponRenderProbe.PlaceExternalPrimaryHeight(position.z);
                RimKataGroundPoseRender.TransformExternalEquipment(ref position, ref rotation);
                Graphics.DrawMesh(mesh, position, rotation, material, layer, camera, submeshIndex, properties,
                    castShadows, receiveShadows, probeAnchor, useLightProbes);
                RimKataWeaponRenderProbe.NotifyMeshDraw();
            }
        }

        public static void DrawMeshInternal(Mesh mesh, int submeshIndex, Matrix4x4 matrix,
            Material material, int layer, Camera camera, MaterialPropertyBlock properties,
            ShadowCastingMode castShadows, bool receiveShadows, Transform probeAnchor,
            LightProbeUsage lightProbeUsage, LightProbeProxyVolume lightProbeProxyVolume)
        {
            if (active == null && (RimKataBreachWeaponRender.Active || RimKataSubdueWeaponRender.Active || RimKataReactiveRender.Active)) return;
            if (active != null)
                active.Record(new DrawCommand(mesh, matrix, material, layer, camera, submeshIndex,
                    properties, castShadows, receiveShadows, probeAnchor, lightProbeUsage, lightProbeProxyVolume));
            else
            {
                matrix.m23 = RimKataWeaponRenderProbe.PlaceExternalPrimaryHeight(matrix.m23);
                matrix = RimKataGroundPoseRender.TransformExternalEquipment(matrix);
                DrawInternal(mesh, submeshIndex, matrix, material, layer, camera, properties,
                    castShadows, receiveShadows, probeAnchor, lightProbeUsage, lightProbeProxyVolume);
                RimKataWeaponRenderProbe.NotifyMeshDraw();
            }
        }
    }
}
