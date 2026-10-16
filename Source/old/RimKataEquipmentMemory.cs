using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Verse;

namespace KRWF.RimKata
{
    // Accessed by startup preparation and the refresh window on the main thread.
    // Keys are XML attributes, never filesystem paths supplied by other mods.
    internal static class RimKataEquipmentMemory
    {
        private const string SchemaVersion = "1";
        private const long MaximumFileBytes = 32L * 1024L * 1024L;
        private const int MaximumEntries = 100000;
        private static string storageDirectory;
        private static string temporaryDirectory;
        private static bool warned;
        private static View current = new View(false);

        internal static View Current => current;

        internal static void ConfigureRoot(string root)
        {
            using (RimKataStartupDiagnostics.Measure("cache_read", "equipment_memory")) Configure(root);
        }

        private static void Configure(string root)
        {
            storageDirectory = null;
            temporaryDirectory = null;
            current = new View(false);
            try
            {
                if (string.IsNullOrEmpty(root)) throw new ArgumentException("Missing mod root.");
                string directory = Path.Combine(Path.GetFullPath(root), "equipment-memory");
                string temporary = Path.Combine(directory, "temp");
                EnsureDirectory(directory);
                EnsureDirectory(temporary);
                storageDirectory = directory;
                temporaryDirectory = temporary;
            }
            catch (Exception exception)
            {
                Warn(exception);
                return;
            }

            try
            {
                string path = Path.Combine(storageDirectory, "cache.xml");
                RimKataStartupDiagnostics.Detail("cache_file", path);
                if (File.Exists(path))
                {
                    current = Read(path);
                    RimKataStartupDiagnostics.Count("cache_file_loaded");
                }
                else RimKataStartupDiagnostics.Count("cache_file_missing");
            }
            catch (Exception exception)
            {
                // A malformed cache must not retain a partially loaded view.
                current = new View(false);
                RimKataStartupDiagnostics.Count("cache_file_rejected");
                Warn(exception);
            }
        }

        internal static View BeginRefresh()
        {
            return current.Copy(true);
        }

        // Startup writes are collected in memory and flushed after preparation.
        internal static bool Flush()
        {
            if (!current.dirty)
            {
                RimKataStartupDiagnostics.Count("cache_write_skipped");
                return true;
            }
            using (RimKataStartupDiagnostics.Measure("cache_write", "equipment_memory")) return FlushChanged();
        }

        private static bool FlushChanged()
        {
            if (storageDirectory == null) return false;
            string candidate = null;
            try
            {
                candidate = CreateCandidatePath();
                Write(current, candidate);
                ReplaceCache(candidate);
                current.dirty = false;
                RimKataStartupDiagnostics.Count("cache_file_written");
                return true;
            }
            catch (Exception exception)
            {
                Warn(exception);
                return false;
            }
            finally
            {
                DeleteFile(candidate);
            }
        }

        internal static string Fingerprint(string value)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                StringBuilder text = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) text.Append(hash[i].ToString("x2"));
                return text.ToString();
            }
        }

        internal static XmlElement NewData()
        {
            XmlDocument document = NewDocument();
            XmlElement data = document.CreateElement("data");
            document.AppendChild(data);
            return data;
        }

        internal sealed class View : IDisposable
        {
            private readonly Dictionary<string, Dictionary<string, Entry>> categories =
                new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal);
            private string stagedDirectory;
            private string stagedPath;
            private string stagedWritePath;
            private bool staged;
            private bool disposed;
            private bool committed;
            internal bool dirty;

            internal View(bool forceRefresh)
            {
                ForceRefresh = forceRefresh;
            }

            internal bool ForceRefresh { get; }

            internal bool TryRead(string category, string key, string fingerprint, out XmlElement data)
            {
                EnsureOpen();
                data = null;
                if (ForceRefresh || category == null || key == null || fingerprint == null) return false;
                if (!categories.TryGetValue(category, out Dictionary<string, Entry> entries)
                    || !entries.TryGetValue(key, out Entry entry)
                    || !string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal)) return false;
                // Discovery readers consume this XML without mutating it; staging copies remain separate.
                data = entry.Data;
                return true;
            }

            internal void Store(string category, string key, string fingerprint, XmlElement data)
            {
                EnsureOpen();
                ValidateKey(category, key, fingerprint);
                if (data == null) throw new ArgumentNullException(nameof(data));
                if (categories.TryGetValue(category, out Dictionary<string, Entry> previous)
                    && previous.TryGetValue(key, out Entry known) && known.Fingerprint == fingerprint
                    && known.Data.OuterXml == data.OuterXml)
                {
                    RimKataStartupDiagnostics.Count("cache_records_unchanged");
                    return;
                }
                if (!categories.TryGetValue(category, out Dictionary<string, Entry> entries))
                {
                    entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                    categories.Add(category, entries);
                }
                entries[key] = new Entry(fingerprint, CopyData(data));
                dirty = true;
                RimKataStartupDiagnostics.Count("cache_records_changed");
                staged = false;
            }

            internal void Restore(string category, string key, string fingerprint, XmlElement data)
            {
                if (!categories.TryGetValue(category, out Dictionary<string, Entry> entries))
                    categories.Add(category, entries = new Dictionary<string, Entry>(StringComparer.Ordinal));
                entries.Add(key, new Entry(fingerprint, CopyData(data)));
            }

            internal void Remove(string category, string key)
            {
                EnsureOpen();
                if (category == null || key == null
                    || !categories.TryGetValue(category, out Dictionary<string, Entry> entries)
                    || !entries.Remove(key)) return;
                if (entries.Count == 0) categories.Remove(category);
                dirty = true;
                staged = false;
            }

            // Called when refresh preparation is complete, before the user confirms.
            internal bool Stage()
            {
                EnsureOpen();
                if (staged) return true;
                if (temporaryDirectory == null) return false;
                try
                {
                    if (stagedDirectory == null)
                    {
                        EnsureDirectory(temporaryDirectory);
                        string directory = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N"));
                        if (Directory.Exists(directory) || File.Exists(directory))
                            throw new IOException("Refresh directory already exists.");
                        Directory.CreateDirectory(directory);
                        stagedDirectory = directory;
                        stagedPath = Path.Combine(directory, "cache.xml");
                        stagedWritePath = Path.Combine(directory, "cache.new.xml");
                    }
                    EnsureOwnedStageDirectory();
                    DeleteFile(stagedWritePath);
                    Write(this, stagedWritePath);
                    if (File.Exists(stagedPath)) File.Replace(stagedWritePath, stagedPath, null);
                    else File.Move(stagedWritePath, stagedPath);
                    staged = true;
                    return true;
                }
                catch (Exception exception)
                {
                    Warn(exception);
                    return false;
                }
            }

            // A failed confirmation leaves both the active view and the original file intact.
            internal bool Commit()
            {
                EnsureOpen();
                bool persisted = false;
                string candidate = null;
                try
                {
                    if (Stage())
                    {
                        EnsureOwnedStageDirectory();
                        candidate = CreateCandidatePath();
                        File.Copy(stagedPath, candidate, false);
                        ReplaceCache(candidate);
                        persisted = true;
                    }
                }
                catch (Exception exception)
                {
                    Warn(exception);
                }
                finally
                {
                    DeleteFile(candidate);
                }

                if (!persisted) return false;
                View replacement = Copy(false);
                replacement.dirty = false;
                current = replacement;
                committed = true;
                CleanupStage();
                return persisted;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                CleanupStage();
            }

            internal View Copy(bool forceRefresh)
            {
                View result = new View(forceRefresh);
                foreach (KeyValuePair<string, Dictionary<string, Entry>> category in categories)
                {
                    Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                    result.categories.Add(category.Key, entries);
                    foreach (KeyValuePair<string, Entry> entry in category.Value)
                        entries.Add(entry.Key, new Entry(entry.Value.Fingerprint, CopyData(entry.Value.Data)));
                }
                result.dirty = dirty;
                return result;
            }

            internal void WriteEntries(XmlWriter writer)
            {
                // Stable output makes a refresh inspectable without dictionary ordering noise.
                List<string> categoryNames = new List<string>(categories.Keys);
                categoryNames.Sort(StringComparer.Ordinal);
                int count = 0;
                foreach (string category in categoryNames)
                {
                    Dictionary<string, Entry> entries = categories[category];
                    List<string> keys = new List<string>(entries.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    foreach (string key in keys)
                    {
                        if (++count > MaximumEntries) throw new InvalidDataException("Too many cache entries.");
                        Entry entry = entries[key];
                        writer.WriteStartElement("entry");
                        writer.WriteAttributeString("category", category);
                        writer.WriteAttributeString("key", key);
                        writer.WriteAttributeString("fingerprint", entry.Fingerprint);
                        entry.Data.WriteTo(writer);
                        writer.WriteEndElement();
                    }
                }
            }

            internal bool Contains(string category, string key)
            {
                return categories.TryGetValue(category, out Dictionary<string, Entry> entries)
                    && entries.ContainsKey(key);
            }

            private void EnsureOpen()
            {
                if (disposed) throw new ObjectDisposedException(nameof(View));
                if (committed) throw new InvalidOperationException("The refresh was already committed.");
            }

            private void EnsureOwnedStageDirectory()
            {
                if (temporaryDirectory == null || stagedDirectory == null
                    || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(stagedDirectory)),
                        Path.GetFullPath(temporaryDirectory), StringComparison.OrdinalIgnoreCase)
                    || !Guid.TryParseExact(Path.GetFileName(stagedDirectory), "N", out _))
                    throw new IOException("Invalid refresh directory.");
                RejectReparsePoint(temporaryDirectory);
                RejectReparsePoint(stagedDirectory);
            }

            private void CleanupStage()
            {
                if (stagedDirectory == null) return;
                try
                {
                    EnsureOwnedStageDirectory();
                    // Only remove this view's two files; never recursively delete a directory.
                    DeleteFile(stagedWritePath);
                    DeleteFile(stagedPath);
                    if (Directory.Exists(stagedDirectory)) Directory.Delete(stagedDirectory, false);
                }
                catch (Exception exception)
                {
                    Warn(exception);
                }
                stagedDirectory = null;
                stagedPath = null;
                stagedWritePath = null;
                staged = false;
            }
        }

        private sealed class Entry
        {
            internal readonly string Fingerprint;
            internal readonly XmlElement Data;

            internal Entry(string fingerprint, XmlElement data)
            {
                Fingerprint = fingerprint;
                Data = data;
            }
        }

        private static View Read(string path)
        {
            RejectReparsePoint(path);
            XmlDocument document = NewDocument();
            XmlReaderSettings settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumFileBytes,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaximumFileBytes)
                    throw new InvalidDataException("Invalid cache size.");
                using (XmlReader reader = XmlReader.Create(stream, settings)) document.Load(reader);
            }
            XmlElement root = document.DocumentElement;
            if (root == null || root.Name != "equipmentMemory" || root.NamespaceURI.Length != 0
                || root.GetAttribute("version") != SchemaVersion)
                throw new InvalidDataException("Unsupported cache schema.");

            View result = new View(false);
            int count = 0;
            foreach (XmlNode node in root.ChildNodes)
            {
                XmlElement entry = node as XmlElement;
                if (entry == null || entry.Name != "entry" || entry.NamespaceURI.Length != 0
                    || ++count > MaximumEntries || entry.ChildNodes.Count != 1
                    || !(entry.FirstChild is XmlElement data))
                    throw new InvalidDataException("Invalid cache entry.");
                string category = entry.GetAttribute("category");
                string key = entry.GetAttribute("key");
                string fingerprint = entry.GetAttribute("fingerprint");
                ValidateKey(category, key, fingerprint);
                if (result.Contains(category, key)) throw new InvalidDataException("Duplicate cache entry.");
                result.Restore(category, key, fingerprint, data);
            }
            RimKataStartupDiagnostics.Count("cache_records_loaded", count);
            return result;
        }

        private static void Write(View view, string path)
        {
            XmlWriterSettings settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                CloseOutput = false
            };
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (XmlWriter writer = XmlWriter.Create(stream, settings))
                {
                    writer.WriteStartDocument();
                    writer.WriteStartElement("equipmentMemory");
                    writer.WriteAttributeString("version", SchemaVersion);
                    view.WriteEntries(writer);
                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                }
                if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Cache is too large.");
                stream.Flush(true);
            }
        }

        private static string CreateCandidatePath()
        {
            if (storageDirectory == null) throw new IOException("Cache storage is unavailable.");
            EnsureDirectory(storageDirectory);
            return Path.Combine(storageDirectory, "cache." + Guid.NewGuid().ToString("N") + ".tmp");
        }

        private static void ReplaceCache(string candidate)
        {
            string destination = Path.Combine(storageDirectory, "cache.xml");
            RejectReparsePoint(destination);
            // Candidate and destination share a directory and volume. No delete-first fallback.
            if (File.Exists(destination)) File.Replace(candidate, destination, null);
            else File.Move(candidate, destination);
        }

        private static void EnsureDirectory(string path)
        {
            Directory.CreateDirectory(path);
            RejectReparsePoint(path);
        }

        private static void RejectReparsePoint(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Cache paths cannot be links.");
        }

        private static XmlDocument NewDocument()
        {
            return new XmlDocument { XmlResolver = null };
        }

        private static XmlElement CopyData(XmlElement data)
        {
            XmlDocument document = NewDocument();
            XmlElement copy = (XmlElement)document.ImportNode(data, true);
            document.AppendChild(copy);
            return copy;
        }

        private static void ValidateKey(string category, string key, string fingerprint)
        {
            if (string.IsNullOrEmpty(category) || category.Length > 256
                || string.IsNullOrEmpty(key) || key.Length > 65536
                || string.IsNullOrEmpty(fingerprint) || fingerprint.Length > 1024)
                throw new ArgumentException("Invalid equipment memory key.");
        }

        private static void DeleteFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception exception)
            {
                Warn(exception);
            }
        }

        private static void Warn(Exception exception)
        {
            if (warned) return;
            warned = true;
            Log.Warning("[RimKata] Equipment memory storage could not be used ("
                + exception.GetType().Name + "). Runtime equipment discovery will continue.");
        }
    }
}
