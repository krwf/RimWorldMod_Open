using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace KRWF.RimKata
{
    // Harmony installation callbacks only, never a combat/render check.
    // Share failures across patches that discover the same external override.
    internal static class RimKataExternalPatchGuard
    {
        private static readonly HashSet<MethodBase> failedMethods = new HashSet<MethodBase>();

        internal static bool Prepare(MethodBase original)
            => original == null || !failedMethods.Contains(original);

        internal static Exception Cleanup(MethodBase original, Exception exception)
        {
            // Harmony also calls Cleanup for the whole class, without an original.
            // Keep core/registration errors visible through the bootstrap handler.
            if (exception == null || original == null
                || original.Module.Assembly == typeof(Verb).Assembly
                || original.Module.Assembly == typeof(RimKataExternalPatchGuard).Assembly)
                return exception;

            if (failedMethods.Add(original))
            {
                Exception root = exception.GetBaseException();
                string message = string.IsNullOrEmpty(root.Message) ? "<no message>"
                    : root.Message.Replace('\r', ' ').Replace('\n', ' ');
                Log.Warning("[RimKata] Skipped external method patches for "
                    + (original.DeclaringType?.FullName ?? "<unknown>") + "::" + original.Name
                    + ": " + root.GetType().Name + ": " + message
                    + ". Remaining patch registration will continue.");
            }

            // Suppress this installation failure so Harmony continues with the
            // next target in this class. This does not suppress runtime errors.
            return null;
        }
    }
}
