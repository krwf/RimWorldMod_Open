using System;
using System.Linq.Expressions;
using HarmonyLib;
using UnityEngine;
using Verse;
using static KRWF.RimKata.RimKataMeleeAnimationCompat;

namespace KRWF.RimKata
{
    // Optional MA-only submission. Use its cutout evaluator with a separate
    // weapon override and property block; the primary's objects are read-only.
    internal static class RimKataMeleeAnimationWeaponDraw
    {
        private delegate void Split(object renderer, int index, ref Matrix4x4 matrix,
            MaterialPropertyBlock properties, object weaponOverride, int pass, ref int passes, ref Texture2D texture);
        private static Split split;
        private static Func<object> createOverride;
        private static Action<object, Frame> configureOverride;
        private static Func<object, int, bool> hasSplit;
        private static Func<object, int, Material> material;
        private static Func<object, int, Color> color;
        private static int mainTex;

        internal static void BindApi(Type renderer, Type snapshot, Type ov)
        {
            var r = Expression.Parameter(typeof(object), "renderer");
            var index = Expression.Parameter(typeof(int), "index");
            var ss = Expression.Variable(snapshot, "snapshot");
            Expression self = Expression.Convert(r, renderer);
            Expression load = Expression.Assign(ss, Expression.ArrayIndex(
                Expression.Field(self, AccessTools.Field(renderer, "snapshots")), index));
            hasSplit = Expression.Lambda<Func<object, int, bool>>(Expression.Block(new[] { ss }, load,
                Expression.AndAlso(Expression.NotEqual(Expression.Convert(Expression.Field(ss, "SplitDrawMode"), typeof(int)), Expression.Constant(0)),
                    Expression.NotEqual(Expression.Property(ss, "SplitDrawPivot"), Expression.Constant(null, AccessTools.Property(snapshot, "SplitDrawPivot").PropertyType)))), r, index).Compile();
            color = Expression.Lambda<Func<object, int, Color>>(Expression.Block(new[] { ss }, load,
                Expression.Property(ss, "FinalColor")), r, index).Compile();
            var force = Expression.Variable(typeof(bool), "force");
            material = Expression.Lambda<Func<object, int, Material>>(Expression.Block(new[] { ss, force }, load,
                Expression.Call(self, AccessTools.Method(renderer, "GetMaterialFor"), ss, Expression.Constant(false), force)), r, index).Compile();
            var matrix = Expression.Parameter(typeof(Matrix4x4).MakeByRefType(), "matrix");
            var pb = Expression.Parameter(typeof(MaterialPropertyBlock), "pb");
            var w = Expression.Parameter(typeof(object), "weaponOverride");
            var pass = Expression.Parameter(typeof(int), "pass");
            var passes = Expression.Parameter(typeof(int).MakeByRefType(), "passes");
            var tex = Expression.Parameter(typeof(Texture2D).MakeByRefType(), "texture");
            split = Expression.Lambda<Split>(Expression.Block(new[] { ss }, load,
                Expression.Call(self, AccessTools.Method(renderer, "ConfigureSplitDraw"), ss, matrix, pb,
                    Expression.Convert(w, ov), pass, passes, tex)), r, index, matrix, pb, w, pass, passes, tex).Compile();
            createOverride = Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(ov), typeof(object))).Compile();
            var frame = Expression.Parameter(typeof(Frame), "frame");
            Expression value = Expression.Convert(w, ov);
            configureOverride = Expression.Lambda<Action<object, Frame>>(Expression.Block(
                Expression.Assign(Expression.Field(value, "Material"), Expression.Field(frame, "Material")),
                Expression.Assign(Expression.Field(value, "Weapon"), Expression.Property(frame, "Secondary")),
                Expression.Assign(Expression.Field(value, "LocalOffset"), Expression.Field(frame, "Offset")),
                Expression.Assign(Expression.Field(value, "LocalRotation"), Expression.Field(frame, "Angle")),
                Expression.Assign(Expression.Field(value, "LocalScaleFactor"), Expression.New(typeof(Vector2).GetConstructor(new[] { typeof(float), typeof(float) }),
                    Expression.Field(Expression.Field(frame, "Scale"), "x"), Expression.Field(Expression.Field(frame, "Scale"), "z"))),
                Expression.Assign(Expression.Field(value, "FlipX"), Expression.Field(frame, "FlipX")),
                Expression.Assign(Expression.Field(value, "FlipY"), Expression.Field(frame, "FlipY"))), w, frame).Compile();
        }

        internal static void Draw(Frame frame, object renderer, int item, Mesh mesh,
            Matrix4x4 matrix, int layer, Camera camera)
        {
            if (!hasSplit(renderer, item))
            {
                DrawMirrored(frame, mesh, matrix, frame.Material, layer, camera, 0, null, renderer);
                return;
            }
            if (frame.SecondaryOverride == null) frame.SecondaryOverride = createOverride();
            if (frame.WeaponProperties == null) frame.WeaponProperties = new MaterialPropertyBlock();
            if (mainTex == 0) mainTex = Shader.PropertyToID("_MainTex");
            configureOverride(frame.SecondaryOverride, frame);
            Material cutout = material(renderer, item);
            Texture2D texture = frame.Material.mainTexture as Texture2D;
            Color tint = color(renderer, item);
            Texture mask = frame.Material.HasProperty(ShaderPropertyIDs.MaskTex)
                ? frame.Material.GetTexture(ShaderPropertyIDs.MaskTex) : null;
            int passes = 1;
            for (int pass = 0; pass < passes; pass++)
            {
                MaterialPropertyBlock properties = frame.WeaponProperties;
                properties.Clear();
                properties.SetColor(ShaderPropertyIDs.Color, mask != null ? tint : tint * frame.Secondary.DrawColor);
                if (mask != null)
                {
                    properties.SetTexture(ShaderPropertyIDs.MaskTex, mask);
                    properties.SetColor(ShaderPropertyIDs.ColorTwo, frame.Secondary.DrawColor);
                }
                split(renderer, item, ref matrix, properties, frame.SecondaryOverride, pass, ref passes, ref texture);
                properties.SetTexture(mainTex, texture);
                DrawMirrored(frame, mesh, matrix, cutout, layer, camera, 0, properties, renderer);
            }
        }
    }
}
