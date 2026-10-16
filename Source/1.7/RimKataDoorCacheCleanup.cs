using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace KRWF.RimKata
{
    internal static class RimKataDoorCacheCleanup
    {
        private sealed class SaveReference
        {
            internal string GameId;
            internal string EnvironmentKey;
        }

        private sealed class SaveSnapshot
        {
            internal long Length;
            internal DateTime Modified;
            internal List<SaveReference> References;
        }

        private static Dictionary<string, SaveSnapshot> snapshots =
            new Dictionary<string, SaveSnapshot>(StringComparer.OrdinalIgnoreCase);

        internal static int Prune(string cacheRoot, string savesDirectory,
            string activeGameId, string activeEnvironmentKey)
        {
            cacheRoot = Path.GetFullPath(cacheRoot);
            if (!Directory.Exists(cacheRoot) || IsLink(cacheRoot)) return 0;
            string[] saves = Directory.GetFiles(savesDirectory, "*.rws", SearchOption.TopDirectoryOnly);
            var keepAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keepPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var next = new Dictionary<string, SaveSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in saves)
            {
                if (!string.Equals(Path.GetExtension(path), ".rws", StringComparison.OrdinalIgnoreCase)) continue;
                string fullPath = Path.GetFullPath(path);
                var info = new FileInfo(fullPath);
                if (!snapshots.TryGetValue(fullPath, out SaveSnapshot snapshot)
                    || snapshot.Length != info.Length || snapshot.Modified != info.LastWriteTimeUtc)
                {
                    snapshot = new SaveSnapshot { Length = info.Length, Modified = info.LastWriteTimeUtc,
                        References = ReadReferences(fullPath) };
                    info.Refresh();
                    if (!info.Exists || info.Length != snapshot.Length || info.LastWriteTimeUtc != snapshot.Modified)
                        throw new IOException("A save changed while checking door cache references.");
                }
                next.Add(fullPath, snapshot);
                foreach (SaveReference reference in snapshot.References)
                    Keep(reference.GameId, reference.EnvironmentKey, keepAll, keepPairs);
            }
            // An unreadable save must abort cleanup rather than make its cache appear orphaned.
            Keep(activeGameId, activeEnvironmentKey, keepAll, keepPairs);
            snapshots = next;
            int removed = 0;
            foreach (string gameDirectory in Directory.GetDirectories(cacheRoot))
            {
                string gameId = Path.GetFileName(gameDirectory);
                if (!Guid.TryParseExact(gameId, "N", out _) || !IsDirectChild(cacheRoot, gameDirectory)
                    || IsLink(gameDirectory) || keepAll.Contains(gameId)) continue;
                foreach (string environmentDirectory in Directory.GetDirectories(gameDirectory))
                {
                    string key = Path.GetFileName(environmentDirectory);
                    if (!IsHash(key) || !IsDirectChild(gameDirectory, environmentDirectory)
                        || IsLink(environmentDirectory) || keepPairs.Contains(gameId + "/" + key)) continue;
                    foreach (string file in Directory.GetFiles(environmentDirectory))
                        if (IsDirectChild(environmentDirectory, file) && !IsLink(file) && IsCacheFile(Path.GetFileName(file)))
                            File.Delete(file);
                    if (RemoveEmpty(environmentDirectory)) removed++;
                }
                RemoveEmpty(gameDirectory);
            }
            return removed;
        }

        private static void Keep(string gameId, string environmentKey,
            HashSet<string> keepAll, HashSet<string> keepPairs)
        {
            if (!Guid.TryParseExact(gameId, "N", out Guid id))
                throw new InvalidDataException("Invalid door cache game ID in save references.");
            if (string.IsNullOrEmpty(environmentKey)) keepAll.Add(id.ToString("N"));
            else
            {
                if (!IsHash(environmentKey)) throw new InvalidDataException("Invalid door cache environment in save references.");
                keepPairs.Add(id.ToString("N") + "/" + environmentKey);
            }
        }

        private static List<SaveReference> ReadReferences(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                IgnoreWhitespace = true, IgnoreComments = true
            }))
            {
                reader.MoveToContent();
                reader.ReadStartElement("savegame");
                while (reader.MoveToContent() == XmlNodeType.Element)
                {
                    if (reader.Name != "game") { reader.Skip(); continue; }
                    reader.ReadStartElement("game");
                    while (reader.MoveToContent() == XmlNodeType.Element)
                    {
                        if (reader.Name == "components") return ReadComponents(reader);
                        reader.Skip();
                    }
                    break;
                }
                throw new InvalidDataException("Save game components were not readable: " + Path.GetFileName(path));
            }
        }

        private static List<SaveReference> ReadComponents(XmlReader reader)
        {
            var references = new List<SaveReference>();
            if (reader.IsEmptyElement) return references;
            reader.ReadStartElement("components");
            while (reader.MoveToContent() == XmlNodeType.Element)
            {
                string type = reader.GetAttribute("Class") ?? string.Empty;
                int separator = type.IndexOf(',');
                if (separator >= 0) type = type.Substring(0, separator).Trim();
                if (reader.Name == "li" && type == "KRWF.RimKata.RimKataDoorCache")
                {
                    var component = new XmlDocument { XmlResolver = null };
                    using (XmlReader subtree = reader.ReadSubtree()) component.Load(subtree);
                    XmlElement root = component.DocumentElement;
                    string gameId = root["gameId"]?.InnerText;
                    string environmentKey = root["environmentKey"]?.InnerText;
                    if (!Guid.TryParseExact(gameId, "N", out _)
                        || (environmentKey != null && !IsHash(environmentKey)))
                        throw new InvalidDataException("Invalid door cache identity in a save.");
                    references.Add(new SaveReference { GameId = gameId, EnvironmentKey = environmentKey });
                }
                reader.Skip();
            }
            reader.ReadEndElement();
            // Components precede map contents in the save XML.
            return references;
        }

        private static bool IsHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f') && !(c >= 'A' && c <= 'F')) return false;
            return true;
        }

        private static bool IsCacheFile(string name)
        {
            if (name == "environment.xml") return true;
            if (name.EndsWith(".render.xml", StringComparison.Ordinal))
                return IsHash(name.Substring(0, name.Length - ".render.xml".Length));
            return name.EndsWith(".xml", StringComparison.Ordinal)
                && IsHash(name.Substring(0, name.Length - ".xml".Length));
        }

        private static bool IsDirectChild(string parent, string child) =>
            string.Equals(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetDirectoryName(Path.GetFullPath(child)), StringComparison.OrdinalIgnoreCase);

        private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        private static bool RemoveEmpty(string directory)
        {
            if (Directory.GetFileSystemEntries(directory).Length != 0) return false;
            Directory.Delete(directory, false);
            return true;
        }
    }
}
