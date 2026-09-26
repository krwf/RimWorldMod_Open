using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    // Replace only native warmup construction. Keeping the original cast and
    // WarmupComplete methods lets weapon mods retain their own aim setup.
    [HarmonyPatch]
    internal static class Patch_Verb_RimKataCrawlWarmup
    {
        private static readonly Type[] CastSignature =
        {
            typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(bool),
            typeof(bool), typeof(bool), typeof(bool)
        };

        private static readonly ConstructorInfo WarmupConstructor = AccessTools.Constructor(
            typeof(Stance_Warmup), new[] { typeof(int), typeof(LocalTargetInfo), typeof(Verb) });

        private static readonly MethodInfo WarmupFactory = AccessTools.Method(
            typeof(Patch_Verb_RimKataCrawlWarmup), nameof(CreateWarmup));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            var methods = new HashSet<MethodBase>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;
                IEnumerable<Type> types;
                try
                {
                    types = AccessTools.GetTypesFromAssembly(assembly);
                }
                catch
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type == null || !typeof(Verb).IsAssignableFrom(type))
                        continue;
                    MethodInfo cast = DeclaredMethod(type, nameof(Verb.TryStartCastOn),
                        CastSignature, typeof(bool));
                    if (cast != null && methods.Add(cast))
                        yield return cast;
                    MethodInfo warmup = DeclaredMethod(type, nameof(Verb.WarmupComplete),
                        Type.EmptyTypes, typeof(void));
                    if (warmup != null && methods.Add(warmup))
                        yield return warmup;
                }
            }
        }

        private static MethodInfo DeclaredMethod(Type type, string name, Type[] signature, Type returnType)
        {
            MethodInfo method = type.GetMethod(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, signature, null);
            return method != null && !method.IsAbstract && !method.ContainsGenericParameters
                && method.ReturnType == returnType && method.GetMethodBody() != null ? method : null;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            if (WarmupConstructor == null || WarmupFactory == null)
                throw new InvalidOperationException("RimKata crawling warmup construction API does not match.");
            foreach (CodeInstruction code in instructions)
            {
                if (code.opcode == OpCodes.Newobj && Equals(code.operand, WarmupConstructor))
                {
                    // The factory consumes the same three arguments and returns
                    // Stance_Warmup, preserving labels, exception blocks and callers.
                    code.opcode = OpCodes.Call;
                    code.operand = WarmupFactory;
                }
                yield return code;
            }
        }

        private static Stance_Warmup CreateWarmup(int ticks, LocalTargetInfo target, Verb verb)
        {
            return RimKataCrawlFireUtility.IsCrawlVerb(verb)
                ? new Stance_RimKataCrawlWarmup(ticks, target, verb)
                : new Stance_Warmup(ticks, target, verb);
        }
    }
}
