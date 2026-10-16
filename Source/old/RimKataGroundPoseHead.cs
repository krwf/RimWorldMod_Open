using System.Collections.Concurrent;
using System.Collections.Generic;
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using RimWorld;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataGroundPoseHead
    {
        private sealed class Graph
        {
            internal PawnRenderNode root;
            internal PawnRenderNode head;
            internal PawnRenderNode body;
            internal readonly HashSet<PawnRenderNode> headNodes = new HashSet<PawnRenderNode>();
        }

        private sealed class Entry
        {
            internal Graph graph;
            internal Replacement replacement;
        }

        private sealed class Replacement
        {
            internal Pawn pawn;
            internal Graph graph;
            internal PawnDrawParms headParms;
            internal List<PawnGraphicDrawRequest> destination;
            internal readonly List<PawnGraphicDrawRequest> originals = new List<PawnGraphicDrawRequest>();
            internal readonly List<PawnGraphicDrawRequest> aimed = new List<PawnGraphicDrawRequest>();
        }

        internal readonly struct DrawContext
        {
            internal readonly HashSet<PawnRenderNode> headNodes;
            internal readonly PawnDrawParms headParms;

            internal DrawContext(HashSet<PawnRenderNode> headNodes, PawnDrawParms headParms)
            {
                this.headNodes = headNodes;
                this.headParms = headParms;
            }
        }

        private static readonly ConditionalWeakTable<Pawn, Entry> Entries =
            new ConditionalWeakTable<Pawn, Entry>();
        private static readonly ConcurrentDictionary<List<PawnGraphicDrawRequest>, Replacement> Pending =
            new ConcurrentDictionary<List<PawnGraphicDrawRequest>, Replacement>();
        private static int pendingCount;

        internal static bool Supports(Pawn pawn)
        {
            if (pawn?.health?.hediffSet?.HasHead != true) return false;
            PawnRenderTree tree = pawn?.Drawer?.renderer?.renderTree;
            if (tree == null) return false;
            if (tree.rootNode == null)
            {
                if (!UnityData.IsInMainThread) return false;
                tree.EnsureInitialized(PawnRenderFlags.Headgear | PawnRenderFlags.Clothes);
            }
            if (tree.rootNode == null) return false;

            Entry entry = Entries.GetValue(pawn, CreateEntry);
            Graph graph = Volatile.Read(ref entry.graph);
            if (graph?.root == tree.rootNode) return graph.head != null;
            // Unity resources must be initialized outside parallel rendering.
            if (!UnityData.IsInMainThread) return false;
            tree.EnsureInitialized(PawnRenderFlags.Headgear | PawnRenderFlags.Clothes);
            graph = new Graph { root = tree.rootNode };
            if (tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Head, out PawnRenderNode head)
                && tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Body, out PawnRenderNode body)
                && head != body && head?.PrimaryGraphic != null && body?.PrimaryGraphic != null
                && head.Props.useGraphic && body.Props.useGraphic)
            {
                graph.head = head;
                graph.body = body;
                CollectHeadNodes(head, graph.headNodes);
            }
            Volatile.Write(ref entry.graph, graph);
            return graph.head != null;
        }

        private static Entry CreateEntry(Pawn pawn) => new Entry();

        private static void CollectHeadNodes(PawnRenderNode node, HashSet<PawnRenderNode> nodes)
        {
            if (node == null || !nodes.Add(node) || node.children == null) return;
            for (int i = 0; i < node.children.Length; i++)
                CollectHeadNodes(node.children[i], nodes);
        }

        private static Graph CachedGraph(Pawn pawn, out Entry entry)
        {
            entry = null;
            if (pawn == null || !Entries.TryGetValue(pawn, out entry)) return null;
            Graph graph = Volatile.Read(ref entry.graph);
            return graph?.head != null && graph.root == pawn.Drawer.renderer.renderTree.rootNode
                ? graph : null;
        }

        internal static bool TryGetHeadMatrix(PawnDrawParms parms, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            Graph graph = CachedGraph(parms.pawn, out _);
            return graph != null && graph.head.tree.TryGetMatrix(graph.head, parms, out matrix);
        }

        internal static Vector3? Apply(PawnDrawParms parms, List<PawnGraphicDrawRequest> requests,
            Matrix4x4 bodyTransform, float progress,
            Vector3 weaponPivotOffset)
        {
            Graph graph = CachedGraph(parms.pawn, out Entry entry);
            if (graph == null || requests == null || parms.Portrait || progress <= 0f) return null;

            Matrix4x4 originalHead = default;
            bool foundHead = false;
            for (int i = 0; i < requests.Count; i++)
            {
                if (requests[i].node != graph.head) continue;
                originalHead = requests[i].preDrawnComputedMatrix;
                foundHead = true;
                break;
            }
            if (!foundHead) return null;

            PawnDrawParms headParms = parms;
            headParms.flipHead = false;
            if (!graph.head.tree.TryGetMatrix(graph.head, headParms, out Matrix4x4 aimedHead)) return null;

            Replacement replacement = entry.replacement ??= new Replacement { pawn = parms.pawn };
            replacement.aimed.Clear();
            graph.head.AppendRequests(headParms, replacement.aimed);
            if (replacement.aimed.Count == 0) return null;

            Vector3 bodyForward = bodyTransform.MultiplyVector(Vector3.forward);
            float bodyAngle = Mathf.Atan2(bodyForward.x, bodyForward.z) * Mathf.Rad2Deg;
            Vector3 neckPosition = Position(originalHead);
            Matrix4x4 headTransform = Matrix4x4.Translate(neckPosition)
                * Matrix4x4.Rotate(Quaternion.AngleAxis(bodyAngle, Vector3.up))
                * Matrix4x4.Translate(-Position(aimedHead));

            for (int i = 0; i < replacement.aimed.Count; i++)
            {
                PawnGraphicDrawRequest request = replacement.aimed[i];
                if (!graph.head.tree.TryGetMatrix(request.node, headParms, out Matrix4x4 matrix))
                    return null;
                request.preDrawnComputedMatrix = headTransform * matrix;
                replacement.aimed[i] = request;
            }

            replacement.originals.Clear();
            replacement.originals.AddRange(requests);
            replacement.graph = graph;
            replacement.headParms = headParms;
            replacement.destination = requests;
            if (Pending.TryAdd(requests, replacement)) Interlocked.Increment(ref pendingCount);

            int insertAt = requests.Count;
            for (int i = requests.Count - 1; i >= 0; i--)
            {
                if (!graph.headNodes.Contains(requests[i].node)) continue;
                insertAt = i;
                requests.RemoveAt(i);
            }
            requests.InsertRange(insertAt, replacement.aimed);
            return neckPosition + weaponPivotOffset;
        }

        private static Vector3 Position(Matrix4x4 matrix)
            => new Vector3(matrix.m03, matrix.m13, matrix.m23);

        internal static bool TryGetBodyMatrix(PawnDrawParms parms, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            Graph graph = CachedGraph(parms.pawn, out _);
            return graph?.body != null && graph.body.tree.TryGetMatrix(graph.body, parms, out matrix);
        }

        internal static void Restore(List<PawnGraphicDrawRequest> requests)
        {
            if (Volatile.Read(ref pendingCount) == 0 || requests == null
                || !Pending.TryRemove(requests, out Replacement replacement)) return;
            Interlocked.Decrement(ref pendingCount);
            // Vanilla caches this request list across frames, including after the pose ends.
            requests.Clear();
            requests.AddRange(replacement.originals);
            replacement.originals.Clear();
            replacement.destination = null;
        }

        internal static DrawContext ResolveDrawContext(PawnDrawParms parms,
            List<PawnGraphicDrawRequest> requests)
        {
            if (Volatile.Read(ref pendingCount) == 0 || parms.Portrait || parms.pawn == null
                || RimKataWorldRenderContext.BodyFor(parms.pawn)?.groundPose != true
                || !Entries.TryGetValue(parms.pawn, out Entry entry)) return default;
            Replacement replacement = entry.replacement;
            if (replacement?.destination == null || replacement.destination != requests) return default;
            return new DrawContext(replacement.graph.headNodes, replacement.headParms);
        }

        internal static bool TryGetDrawParms(PawnRenderNode node, PawnDrawParms parms,
            in DrawContext context, out PawnDrawParms headParms)
        {
            headParms = parms;
            // Carried derives weapon placement from these parms; only head nodes receive the substituted facing.
            if (context.headNodes == null || !context.headNodes.Contains(node)) return false;
            headParms = context.headParms;
            return true;
        }

        internal static PawnDrawParms DrawParms(PawnRenderNode node, PawnDrawParms parms,
            in DrawContext context)
            => TryGetDrawParms(node, parms, in context, out PawnDrawParms headParms) ? headParms : parms;

        internal static void Clear(Pawn pawn)
        {
            if (pawn == null || !Entries.TryGetValue(pawn, out Entry entry)) return;
            Replacement replacement = entry.replacement;
            if (replacement?.destination == null
                || !Pending.TryRemove(replacement.destination, out _)) return;
            Interlocked.Decrement(ref pendingCount);
            // A pose can end between render passes while the renderer still owns this list.
            replacement.graph.root.requestRecache = true;
            replacement.destination = null;
            replacement.originals.Clear();
        }

        internal static void ClearMap(Map map)
        {
            foreach (Replacement replacement in Pending.Values)
                if (replacement.pawn.Map == map || !replacement.pawn.Spawned)
                    Clear(replacement.pawn);
        }
    }

    [HarmonyPatch(typeof(PawnRenderTree), nameof(PawnRenderTree.Draw))]
    internal static class Patch_PawnRenderTree_RimKataHeadDraw
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            MethodBase original, ILGenerator generator)
        {
            LocalBuilder scope = generator.DeclareLocal(typeof(RimKataWorldRenderContext.Scope));
            return RimKataRenderHookIL.LivingOnly(WithHeadDraw(instructions, original, generator), generator, 1,
                new[] {
                    new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldloca, scope),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch_PawnRenderTree_RimKataHeadDraw), nameof(Begin)))
                }, new[] {
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataBreachRender), nameof(RimKataBreachRender.DrawDoor)))
                }, new[] {
                    new CodeInstruction(OpCodes.Ldloc, scope),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataWorldRenderContext), nameof(RimKataWorldRenderContext.End)))
                });
        }

        private static void Begin(PawnDrawParms parms, ref RimKataWorldRenderContext.Scope scope)
            => scope = RimKataWorldRenderContext.Begin(parms.pawn, parms.Portrait);

        private static IEnumerable<CodeInstruction> WithHeadDraw(IEnumerable<CodeInstruction> instructions,
            MethodBase original, ILGenerator generator)
        {
            int requestLocal = -1;
            foreach (LocalVariableInfo local in original.GetMethodBody().LocalVariables)
                if (local.LocalType == typeof(PawnGraphicDrawRequest)) requestLocal = local.LocalIndex;
            if (requestLocal < 0) throw new InvalidOperationException("Pawn draw request local was not found.");
            LocalBuilder drawContext = generator.DeclareLocal(typeof(RimKataGroundPoseHead.DrawContext));
            LocalBuilder aimedParms = generator.DeclareLocal(typeof(PawnDrawParms));
            FieldInfo requests = AccessTools.Field(typeof(PawnRenderTree), "drawRequests");
            FieldInfo node = AccessTools.Field(typeof(PawnGraphicDrawRequest), nameof(PawnGraphicDrawRequest.node));
            FieldInfo headNodes = AccessTools.Field(typeof(RimKataGroundPoseHead.DrawContext), "headNodes");
            MethodInfo capture = AccessTools.Method(typeof(RimKataGroundPoseHead), nameof(RimKataGroundPoseHead.ResolveDrawContext));
            MethodInfo resolve = AccessTools.Method(typeof(RimKataGroundPoseHead), nameof(RimKataGroundPoseHead.DrawParms));
            var originalCodes = new List<CodeInstruction>(instructions);
            var headCodes = new List<CodeInstruction>(originalCodes.Count);
            var labels = new Dictionary<Label, Label>();
            foreach (CodeInstruction code in originalCodes)
                foreach (Label label in code.labels)
                    labels[label] = generator.DefineLabel();
            foreach (CodeInstruction code in originalCodes)
            {
                var copy = new CodeInstruction(code);
                for (int i = 0; i < copy.labels.Count; i++) copy.labels[i] = labels[copy.labels[i]];
                if (copy.operand is Label branch) copy.operand = labels[branch];
                else if (copy.operand is Label[] branches)
                {
                    var targets = new Label[branches.Length];
                    for (int i = 0; i < targets.Length; i++) targets[i] = labels[branches[i]];
                    copy.operand = targets;
                }
                headCodes.Add(copy);
            }
            Label headDraw = generator.DefineLabel();
            headCodes[0].labels.Add(headDraw);
            Label originalDraw = generator.DefineLabel();
            originalCodes[0].labels.Add(originalDraw);
            yield return new CodeInstruction(OpCodes.Ldarga_S, (byte)1);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(PawnDrawParms), nameof(PawnDrawParms.dead)));
            yield return new CodeInstruction(OpCodes.Brtrue, originalDraw);
            yield return new CodeInstruction(OpCodes.Ldarg_1);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, requests);
            yield return new CodeInstruction(OpCodes.Call, capture);
            yield return new CodeInstruction(OpCodes.Stloc, drawContext);
            yield return new CodeInstruction(OpCodes.Ldloca, drawContext);
            yield return new CodeInstruction(OpCodes.Ldfld, headNodes);
            yield return new CodeInstruction(OpCodes.Brtrue, headDraw);
            foreach (CodeInstruction code in originalCodes) yield return code;
            bool insideDraw = false;
            foreach (CodeInstruction code in headCodes)
            {
                if (insideDraw && code.opcode == OpCodes.Ldarg_1)
                {
                    code.opcode = OpCodes.Ldloc;
                    code.operand = aimedParms;
                }
                yield return code;
                int storedLocal = code.opcode == OpCodes.Stloc_0 ? 0
                    : code.opcode == OpCodes.Stloc_1 ? 1
                    : code.opcode == OpCodes.Stloc_2 ? 2
                    : code.opcode == OpCodes.Stloc_3 ? 3
                    : code.opcode == OpCodes.Stloc || code.opcode == OpCodes.Stloc_S
                        ? code.operand is LocalBuilder local ? local.LocalIndex : Convert.ToInt32(code.operand)
                        : -1;
                if (storedLocal == requestLocal)
                {
                    insideDraw = true;
                    yield return new CodeInstruction(OpCodes.Ldloca, (short)requestLocal);
                    yield return new CodeInstruction(OpCodes.Ldfld, node);
                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Ldloca, drawContext);
                    yield return new CodeInstruction(OpCodes.Call, resolve);
                    yield return new CodeInstruction(OpCodes.Stloc, aimedParms);
                }
            }
        }
    }
}
