using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataExternalPatchGuard
    {
        private static readonly HashSet<MethodBase> failedMethods = new HashSet<MethodBase>();

        internal static bool Prepare(MethodBase original)
            => original == null || !failedMethods.Contains(original);

        internal static Exception Cleanup(MethodBase original, Exception exception)
        {
            // Harmony also calls Cleanup for the whole class, without an original method.
            if (exception == null || original == null
                || original.Module.Assembly == typeof(Verb).Assembly
                || original.Module.Assembly == typeof(RimKataExternalPatchGuard).Assembly)
                return exception;

            if (failedMethods.Add(original))
            {
                Exception root = exception.GetBaseException();
                string message = string.IsNullOrEmpty(root.Message) ? "<no message>"
                    : root.Message.Replace('\r', ' ').Replace('\n', ' ');
                Log.Warning("[RimKata] Skipped " + RelatedWeapons(original)
                    + ". This message is not an error. External method patches were skipped for "
                    + (original.DeclaringType?.FullName ?? "<unknown>") + "::" + original.Name
                    + ": " + root.GetType().Name + ": " + message
                    + ". Remaining patch registration will continue.");
            }

            // Harmony can continue installing other targets; runtime errors do not reach this hook.
            return null;
        }

        private static string RelatedWeapons(MethodBase original)
        {
            var weapons = new List<string>();
            Type declaringType = original.DeclaringType;
            if (declaringType != null)
            {
                foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (!def.IsWeapon) continue;
                    List<VerbProperties> verbs = def.Verbs;
                    if (verbs == null) continue;
                    foreach (VerbProperties verb in verbs)
                    {
                        if (verb?.verbClass == null || !declaringType.IsAssignableFrom(verb.verbClass))
                            continue;
                        string label = string.IsNullOrEmpty(def.label) ? def.defName : def.label;
                        weapons.Add("'" + label.Replace('\r', ' ').Replace('\n', ' ')
                            + "[" + def.defName + "]'");
                        break;
                    }
                }
            }
            return weapons.Count == 0
                ? "an external method with no matching weapon definition"
                : string.Join(", ", weapons);
        }
    }
}
