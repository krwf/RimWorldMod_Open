using System;
using System.Collections.Generic;
using Verse;

namespace KRWF.RimKata
{
    public sealed class RimKataTargetRule : IExposable
    {
        public string key;
        public bool enabled;
        public string profileId;

        public RimKataTargetRule Copy()
        {
            return new RimKataTargetRule { key = key, enabled = enabled, profileId = profileId };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key");
            Scribe_Values.Look(ref enabled, "enabled", false);
            Scribe_Values.Look(ref profileId, "profileId");
        }
    }

    internal static class RimKataTargetAccess
    {
        private sealed class Binding
        {
            internal RimKataStoredProfile profile;
        }

        private static Dictionary<RimKataTargetEntry, Binding> bindings =
            new Dictionary<RimKataTargetEntry, Binding>();
        private static Dictionary<string, string> enabledProfiles = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool inactiveInterceptionEnabled;
        internal static int Revision { get; private set; }

        internal static bool AnyExplosiveInterceptionEnabled =>
            RimKataMod.Settings?.explosiveInterceptionEnabled != false || inactiveInterceptionEnabled;

        internal static bool IsEnabled(Pawn pawn)
        {
            if (bindings.Count == 0) return false;
            RimKataTargetEntry target = RimKataTargetCatalog.Resolve(pawn);
            return target != null && bindings.ContainsKey(target);
        }

        internal static RimKataSettings SettingsFor(Pawn pawn)
        {
            if (bindings.Count == 0) return RimKataMod.Settings;
            RimKataTargetEntry target = RimKataTargetCatalog.Resolve(pawn);
            if (target != null && bindings.TryGetValue(target, out Binding binding)
                && binding.profile != null && !ReferenceEquals(binding.profile, RimKataMod.Profiles?.Current))
            {
                return binding.profile.RuntimeSettings;
            }
            return RimKataMod.Settings;
        }

        // Called on startup and settings events only. Combat reads use Def-keyed lookup and cached values.
        internal static void Rebuild()
        {
            RimKataSettings settings = RimKataMod.Settings;
            if (settings == null) return;
            IReadOnlyList<RimKataTargetEntry> targets = RimKataTargetCatalog.Entries;
            if (!settings.targetAccessInitialized)
            {
                if (settings.accessRestrictionsDisabled)
                {
                    var existing = new HashSet<string>(StringComparer.Ordinal);
                    foreach (RimKataTargetRule rule in settings.targetAccessRules) existing.Add(rule.key);
                    foreach (RimKataTargetEntry target in targets)
                    {
                        if (existing.Add(target.Key))
                            settings.targetAccessRules.Add(new RimKataTargetRule
                            {
                                key = target.Key,
                                enabled = true,
                                profileId = settings.ActiveProfileId
                            });
                    }
                }
                settings.accessRestrictionsDisabled = false;
                settings.targetAccessInitialized = true;
            }

            var profiles = new Dictionary<string, RimKataStoredProfile>(StringComparer.Ordinal);
            if (RimKataMod.Profiles?.IsInitialized == true)
            {
                foreach (RimKataStoredProfile profile in RimKataMod.Profiles.Profiles)
                    profiles[profile.Id] = profile;
            }
            var rules = new Dictionary<string, RimKataTargetRule>(StringComparer.Ordinal);
            foreach (RimKataTargetRule rule in settings.targetAccessRules)
            {
                if (rule != null && !string.IsNullOrEmpty(rule.key)) rules[rule.key] = rule;
            }
            var nextBindings = new Dictionary<RimKataTargetEntry, Binding>();
            var nextEnabled = new Dictionary<string, string>(StringComparer.Ordinal);
            bool anyInactiveInterception = false;
            foreach (RimKataTargetEntry target in targets)
            {
                if (!rules.TryGetValue(target.Key, out RimKataTargetRule rule) || !rule.enabled) continue;
                profiles.TryGetValue(rule.profileId ?? string.Empty, out RimKataStoredProfile profile);
                nextBindings[target] = new Binding { profile = profile };
                nextEnabled[target.Key] = rule.profileId;
                if (profile != null && !ReferenceEquals(profile, RimKataMod.Profiles.Current)
                    && profile.RuntimeSettings.explosiveInterceptionEnabled)
                    anyInactiveInterception = true;
            }
            bool changed = nextEnabled.Count != enabledProfiles.Count;
            if (!changed)
            {
                foreach (KeyValuePair<string, string> rule in nextEnabled)
                {
                    if (!enabledProfiles.TryGetValue(rule.Key, out string previous)
                        || !string.Equals(previous, rule.Value, StringComparison.Ordinal))
                    {
                        changed = true;
                        break;
                    }
                }
            }
            bindings = nextBindings;
            enabledProfiles = nextEnabled;
            inactiveInterceptionEnabled = anyInactiveInterception;
            if (changed) unchecked { Revision++; }
        }
    }
}
