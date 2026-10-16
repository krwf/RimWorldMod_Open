using System;
using System.Collections.Generic;
using Verse;

namespace KRWF.RimKata
{
    public sealed class RimKataTargetRule : IExposable
    {
        public string key;
        public bool enabledFriendly;
        public bool enabledHostile;
        public string profileIdFriendly;
        public string profileIdHostile;

        public RimKataTargetRule Copy()
        {
            return new RimKataTargetRule
            {
                key = key,
                enabledFriendly = enabledFriendly,
                enabledHostile = enabledHostile,
                profileIdFriendly = profileIdFriendly,
                profileIdHostile = profileIdHostile
            };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key");
            bool legacyEnabled = false;
            string legacyProfileId = null;
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Scribe_Values.Look(ref legacyEnabled, "enabled", false);
                Scribe_Values.Look(ref legacyProfileId, "profileId");
            }
            Scribe_Values.Look(ref enabledFriendly, "enabledFriendly", legacyEnabled);
            Scribe_Values.Look(ref enabledHostile, "enabledHostile", legacyEnabled);
            Scribe_Values.Look(ref profileIdFriendly, "profileIdFriendly", legacyProfileId);
            Scribe_Values.Look(ref profileIdHostile, "profileIdHostile", legacyProfileId);
        }
    }

    internal static class RimKataTargetAccess
    {
        private sealed class Binding
        {
            internal bool enabledFriendly;
            internal bool enabledHostile;
            internal RimKataStoredProfile friendlyProfile;
            internal RimKataStoredProfile hostileProfile;

            internal bool EnabledFor(Pawn pawn) => enabledFriendly == enabledHostile
                ? enabledFriendly : RimKataEligibility.IsHostileToPlayerFaction(pawn)
                    ? enabledHostile : enabledFriendly;

            internal RimKataStoredProfile ProfileFor(Pawn pawn)
            {
                if (enabledFriendly && enabledHostile && ReferenceEquals(friendlyProfile, hostileProfile))
                    return friendlyProfile;
                return RimKataEligibility.IsHostileToPlayerFaction(pawn)
                    ? enabledHostile ? hostileProfile : null
                    : enabledFriendly ? friendlyProfile : null;
            }
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
            return target != null && bindings.TryGetValue(target, out Binding binding) && binding.EnabledFor(pawn);
        }

        internal static RimKataSettings SettingsFor(Pawn pawn)
        {
            if (bindings.Count == 0) return RimKataMod.Settings;
            RimKataTargetEntry target = RimKataTargetCatalog.Resolve(pawn);
            if (target != null && bindings.TryGetValue(target, out Binding binding))
            {
                RimKataStoredProfile profile = binding.ProfileFor(pawn);
                if (profile != null && !ReferenceEquals(profile, RimKataMod.Profiles?.Current))
                    return profile.RuntimeSettings;
            }
            return RimKataMod.Settings;
        }

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
                                enabledFriendly = true,
                                enabledHostile = true,
                                profileIdFriendly = settings.ActiveProfileId,
                                profileIdHostile = settings.ActiveProfileId
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
                if (!rules.TryGetValue(target.Key, out RimKataTargetRule rule)
                    || (!rule.enabledFriendly && !rule.enabledHostile)) continue;
                profiles.TryGetValue(rule.profileIdFriendly ?? string.Empty, out RimKataStoredProfile friendlyProfile);
                profiles.TryGetValue(rule.profileIdHostile ?? string.Empty, out RimKataStoredProfile hostileProfile);
                nextBindings[target] = new Binding
                {
                    enabledFriendly = rule.enabledFriendly,
                    enabledHostile = rule.enabledHostile,
                    friendlyProfile = friendlyProfile,
                    hostileProfile = hostileProfile
                };
                if (rule.enabledFriendly)
                {
                    nextEnabled[target.Key + ":friendly"] = rule.profileIdFriendly;
                    if (friendlyProfile != null && !ReferenceEquals(friendlyProfile, RimKataMod.Profiles.Current)
                        && friendlyProfile.RuntimeSettings.explosiveInterceptionEnabled)
                        anyInactiveInterception = true;
                }
                if (rule.enabledHostile)
                {
                    nextEnabled[target.Key + ":hostile"] = rule.profileIdHostile;
                    if (hostileProfile != null && !ReferenceEquals(hostileProfile, RimKataMod.Profiles.Current)
                        && hostileProfile.RuntimeSettings.explosiveInterceptionEnabled)
                        anyInactiveInterception = true;
                }
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
