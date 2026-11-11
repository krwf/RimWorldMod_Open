using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace KRWF.RimKata
{
    internal static class RimKataMultiplayerSettings
    {
        internal static string ProfileKey(RimKataSettingsProfile profile)
        {
            string values = string.Join("\n", profile.CopyEntries()
                .Where(e => !LocalDisplay(e.Substring(0, e.IndexOf('='))))
                .OrderBy(e => e, StringComparer.Ordinal));
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(values))).Replace("-", "");
        }

        // Together has no host ModSettings transport. Compare effective values at
        // session entry, without writing files, running analysis, or hashing profile IDs.
        internal static string Fingerprint()
        {
            if (RimKataMod.Settings == null || RimKataMod.Profiles?.IsInitialized != true)
                throw new InvalidOperationException("RimKata settings are not initialized.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                WriteFields(writer, RimKataMod.Settings);
                writer.Flush();
                using (SHA256 hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "");
            }
        }

        private static bool LocalDisplay(string name) =>
            name == nameof(RimKataSettings.showDebugGizmo)
            || name == nameof(RimKataSettings.showRangedWeaponCooldown)
            || name == nameof(RimKataSettings.showMeleeWeaponAimTime)
            || name == nameof(RimKataSettings.showFocusedAttackLine)
            || name == nameof(RimKataSettings.smoothAimTransition);

        private static void WriteFields(BinaryWriter writer, object value)
        {
            FieldInfo[] fields = value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance
                | BindingFlags.DeclaredOnly).Where(f => !LocalDisplay(f.Name))
                .OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
            writer.Write(fields.Length);
            foreach (FieldInfo field in fields)
            {
                writer.Write(field.Name);
                if (value is RimKataTargetRule
                    && (field.Name == nameof(RimKataTargetRule.profileIdFriendly)
                        || field.Name == nameof(RimKataTargetRule.profileIdHostile)))
                {
                    string id = (string)field.GetValue(value);
                    RimKataStoredProfile profile = RimKataMod.Profiles.Profiles.FirstOrDefault(p => p.Id == id);
                    var entries = (profile?.Values ?? RimKataSettingsProfile.Capture(RimKataMod.Settings))
                        .CopyEntries().Where(e => !LocalDisplay(e.Substring(0, e.IndexOf('='))));
                    Write(writer, entries.ToArray());
                }
                else Write(writer, field.GetValue(value));
            }
        }

        private static void Write(BinaryWriter writer, object value)
        {
            writer.Write(value != null);
            if (value == null) return;
            Type type = value.GetType();
            if (value is string text) { writer.Write("text"); writer.Write(text); return; }
            if (type.IsPrimitive || type.IsEnum || value is decimal)
            {
                writer.Write(type.FullName);
                writer.Write(value is float single ? single.ToString("R", CultureInfo.InvariantCulture)
                    : value is double number ? number.ToString("R", CultureInfo.InvariantCulture)
                    : Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is IEnumerable items)
            {
                // These settings collections are Def/rule sets, not runtime priority queues.
                var rows = new System.Collections.Generic.List<string>();
                foreach (object item in items)
                    using (var stream = new MemoryStream())
                    using (var nested = new BinaryWriter(stream, Encoding.UTF8))
                    {
                        Write(nested, item);
                        nested.Flush();
                        rows.Add(Convert.ToBase64String(stream.ToArray()));
                    }
                rows.Sort(StringComparer.Ordinal);
                writer.Write("set"); writer.Write(rows.Count);
                foreach (string row in rows) writer.Write(row);
                return;
            }
            if (type != typeof(RimKataTargetRule) && type != typeof(RimKataStrengthRule)
                && type != typeof(RimKataGeneProbabilityRule) && type != typeof(RimKataSkillActivationRule)
                && type != typeof(RimKataStatActivationRule) && type != typeof(RimKataTraitActivationRule))
                throw new NotSupportedException("Unknown RimKata multiplayer setting: " + type.FullName);
            writer.Write(type.FullName);
            WriteFields(writer, value);
        }
    }
}
