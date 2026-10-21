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
        private const string SchemaVersion = "2";
        private const long MaximumFileBytes = 32L * 1024L * 1024L;
        private const int MaximumEntries = 100000;
        private static string storageDirectory;
        private static string temporaryDirectory;
        private static string analysisDirectory;
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
            analysisDirectory = null;
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
                analysisDirectory = Path.Combine(directory, "analysis");
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
                current.PublishPayloads();
                Write(current, candidate);
                ReplaceCache(candidate);
                current.dirty = false;
                current.CompletePayloadCommit();
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
            private readonly Dictionary<string, string> stagedPayloads =
                new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> retiredPayloads =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                // Analysis payloads are loaded only for this reader; the view retains no XML graph.
                try
                {
                    data = entry.Data ?? ReadPayload(entry.PayloadPath, entry.Payload);
                    return true;
                }
                catch (Exception exception)
                {
                    Warn(exception);
                    Remove(category, key);
                    return false;
                }
            }

            internal void Store(string category, string key, string fingerprint, XmlElement data)
            {
                EnsureOpen();
                ValidateKey(category, key, fingerprint);
                if (data == null) throw new ArgumentNullException(nameof(data));
                if (IsAnalysis(category))
                {
                    try { StorePayload(category, key, fingerprint, data); }
                    catch (Exception exception)
                    {
                        // Disk caching is optional; callers keep using their discovered runtime data.
                        Warn(exception);
                        Remove(category, key);
                    }
                    return;
                }
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
                if (IsAnalysis(category))
                {
                    // Legacy inline records are migrated one at a time, without retaining their XML.
                    Store(category, key, fingerprint, data);
                    return;
                }
                if (!categories.TryGetValue(category, out Dictionary<string, Entry> entries))
                    categories.Add(category, entries = new Dictionary<string, Entry>(StringComparer.Ordinal));
                entries.Add(key, new Entry(fingerprint, CopyData(data)));
            }

            internal void RestorePayload(string category, string key, string fingerprint, string payload, string file = null)
            {
                if (!IsAnalysis(category) || !IsPayloadName(payload))
                    throw new InvalidDataException("Invalid analysis payload reference.");
                if (!categories.TryGetValue(category, out Dictionary<string, Entry> entries))
                    categories.Add(category, entries = new Dictionary<string, Entry>(StringComparer.Ordinal));
                entries.Add(key, new Entry(fingerprint, payload, PayloadPath(payload, file)));
            }

            private void StorePayload(string category, string key, string fingerprint, XmlElement data)
            {
                string payload = Fingerprint(data.OuterXml);
                if (categories.TryGetValue(category, out Dictionary<string, Entry> entries)
                    && entries.TryGetValue(key, out Entry known) && known.Fingerprint == fingerprint
                    && known.Payload == payload && File.Exists(known.PayloadPath)
                    && Path.GetFileNameWithoutExtension(known.PayloadPath).Length < 64)
                {
                    RimKataStartupDiagnostics.Count("cache_records_unchanged");
                    return;
                }
                EnsureStageDirectory();
                if (!stagedPayloads.TryGetValue(payload, out string path))
                {
                    path = CreatePayloadStagePath(payload);
                    // Remember the file before writing so a failed write is also discarded on cancel.
                    stagedPayloads.Add(payload, path);
                    try { WritePayload(data, path); }
                    catch
                    {
                        stagedPayloads.Remove(payload);
                        DeleteFile(path);
                        throw;
                    }
                }
                if (entries == null)
                    categories.Add(category, entries = new Dictionary<string, Entry>(StringComparer.Ordinal));
                if (entries.TryGetValue(key, out Entry replaced)) RetirePayload(replaced);
                entries[key] = new Entry(fingerprint, payload, path);
                dirty = true;
                staged = false;
                RimKataStartupDiagnostics.Count("cache_records_changed");
            }

            internal void Remove(string category, string key)
            {
                EnsureOpen();
                if (category == null || key == null
                    || !categories.TryGetValue(category, out Dictionary<string, Entry> entries)
                    || !entries.TryGetValue(key, out Entry removed)) return;
                entries.Remove(key);
                RetirePayload(removed);
                if (entries.Count == 0) categories.Remove(category);
                dirty = true;
                staged = false;
            }

            private void RetirePayload(Entry entry)
            {
                if (entry.Payload == null || analysisDirectory == null
                    || !string.Equals(Path.GetDirectoryName(entry.PayloadPath), analysisDirectory,
                        StringComparison.OrdinalIgnoreCase)) return;
                retiredPayloads[entry.PayloadPath] = entry.Payload;
            }

            private string CreatePayloadStagePath(string payload)
            {
                for (int length = 16; length <= payload.Length; length += 8)
                {
                    string file = payload.Substring(0, length);
                    string path = Path.Combine(stagedDirectory, file + ".xml");
                    if (File.Exists(path) || Directory.Exists(path)) continue;
                    string destination = PayloadPath(payload, file);
                    if (Directory.Exists(destination)) continue;
                    if (File.Exists(destination))
                    {
                        try { ReadPayload(destination, payload); }
                        catch { continue; } // A prefix collision must never overwrite another payload.
                    }
                    return path;
                }
                throw new IOException("No collision-free analysis filename is available.");
            }

            // Called when refresh preparation is complete, before the user confirms.
            internal bool Stage()
            {
                EnsureOpen();
                if (staged) return true;
                if (temporaryDirectory == null) return false;
                try
                {
                    EnsureStageDirectory();
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
                    PublishPayloads();
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
                CompletePayloadCommit();
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
                try
                {
                    foreach (KeyValuePair<string, Dictionary<string, Entry>> category in categories)
                    {
                        Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                        result.categories.Add(category.Key, entries);
                        foreach (KeyValuePair<string, Entry> entry in category.Value)
                        {
                            Entry source = entry.Value;
                            if (source.Data != null)
                                entries.Add(entry.Key, new Entry(source.Fingerprint, CopyData(source.Data)));
                            else
                            {
                                string path = source.PayloadPath;
                                if (stagedPayloads.TryGetValue(source.Payload, out string stagedSource)
                                    && source.PayloadPath == stagedSource)
                                {
                                    result.EnsureStageDirectory();
                                    if (!result.stagedPayloads.TryGetValue(source.Payload, out path))
                                    {
                                        path = Path.Combine(result.stagedDirectory, Path.GetFileName(source.PayloadPath));
                                        result.stagedPayloads.Add(source.Payload, path);
                                        File.Copy(source.PayloadPath, path, false);
                                    }
                                }
                                entries.Add(entry.Key, new Entry(source.Fingerprint, source.Payload, path));
                            }
                        }
                    }
                    foreach (KeyValuePair<string, string> retired in retiredPayloads)
                        result.retiredPayloads.Add(retired.Key, retired.Value);
                    result.dirty = dirty;
                    return result;
                }
                catch
                {
                    result.Dispose();
                    throw;
                }
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
                        if (entry.Payload != null)
                        {
                            writer.WriteAttributeString("payload", entry.Payload);
                            string file = Path.GetFileNameWithoutExtension(entry.PayloadPath);
                            if (file != entry.Payload) writer.WriteAttributeString("file", file);
                        }
                        else entry.Data.WriteTo(writer);
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

            private void EnsureStageDirectory()
            {
                if (stagedDirectory != null) { EnsureOwnedStageDirectory(); return; }
                if (temporaryDirectory == null) throw new IOException("Cache storage is unavailable.");
                EnsureDirectory(temporaryDirectory);
                string directory = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N"));
                if (Directory.Exists(directory) || File.Exists(directory))
                    throw new IOException("Refresh directory already exists.");
                Directory.CreateDirectory(directory);
                stagedDirectory = directory;
                stagedPath = Path.Combine(directory, "cache.xml");
                stagedWritePath = Path.Combine(directory, "cache.new.xml");
            }

            internal void PublishPayloads()
            {
                if (stagedPayloads.Count == 0) return;
                EnsureOwnedStageDirectory();
                HashSet<string> published = new HashSet<string>(StringComparer.Ordinal);
                HashSet<string> failed = new HashSet<string>(StringComparer.Ordinal);
                List<KeyValuePair<string, string>> rejected = new List<KeyValuePair<string, string>>();
                foreach (KeyValuePair<string, Dictionary<string, Entry>> category in categories)
                    foreach (KeyValuePair<string, Entry> item in category.Value)
                    {
                        Entry entry = item.Value;
                        if (entry.Payload == null || !stagedPayloads.TryGetValue(entry.Payload, out string source)
                            || entry.PayloadPath != source) continue;
                        if (published.Add(entry.Payload))
                        {
                            try { PublishPayload(source, entry.Payload); }
                            catch (Exception exception)
                            {
                                Warn(exception);
                                failed.Add(entry.Payload);
                                RimKataStartupDiagnostics.Count("cache_payload_rejected");
                            }
                        }
                        if (failed.Contains(entry.Payload))
                            rejected.Add(new KeyValuePair<string, string>(category.Key, item.Key));
                    }
                // Build the index only after excluding payloads that could not be published.
                foreach (KeyValuePair<string, string> entry in rejected) Remove(entry.Key, entry.Value);
            }

            internal void CompletePayloadCommit()
            {
                foreach (Dictionary<string, Entry> entries in categories.Values)
                    foreach (Entry entry in entries.Values)
                        if (entry.Payload != null && stagedPayloads.TryGetValue(entry.Payload, out string source)
                            && entry.PayloadPath == source)
                            entry.PayloadPath = PayloadPath(entry.Payload, Path.GetFileNameWithoutExtension(source));
                CleanupStage();
                DeleteRetiredPayloads();
            }

            private void DeleteRetiredPayloads()
            {
                if (retiredPayloads.Count == 0) return;
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Dictionary<string, Entry> entries in categories.Values)
                    foreach (Entry entry in entries.Values)
                        if (entry.Payload != null) referenced.Add(entry.PayloadPath);
                foreach (KeyValuePair<string, string> retired in retiredPayloads)
                {
                    if (referenced.Contains(retired.Key)) continue;
                    try
                    {
                        string owned = PayloadPath(retired.Value, Path.GetFileNameWithoutExtension(retired.Key));
                        if (!string.Equals(owned, retired.Key, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Invalid retired analysis path.");
                        RejectReparsePoint(owned);
                        File.Delete(owned);
                    }
                    catch (DirectoryNotFoundException) { }
                    catch (Exception exception) { Warn(exception); }
                }
                retiredPayloads.Clear();
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
                    // Only remove files created by this view; never recursively delete a directory.
                    foreach (string path in stagedPayloads.Values) DeleteFile(path);
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
                stagedPayloads.Clear();
            }
        }

        private sealed class Entry
        {
            internal readonly string Fingerprint;
            internal readonly XmlElement Data;
            internal readonly string Payload;
            internal string PayloadPath;

            internal Entry(string fingerprint, XmlElement data)
            {
                Fingerprint = fingerprint;
                Data = data;
            }

            internal Entry(string fingerprint, string payload, string path)
            {
                Fingerprint = fingerprint;
                Payload = payload;
                PayloadPath = path;
            }
        }

        private static View Read(string path)
        {
            RejectReparsePoint(path);
            View result = new View(false);
            int count = 0;
            try
            {
                using (FileStream stream = OpenCache(path))
                using (XmlReader reader = XmlReader.Create(stream, ReaderSettings()))
                {
                    if (reader.MoveToContent() != XmlNodeType.Element || reader.Name != "equipmentMemory"
                        || reader.NamespaceURI.Length != 0 || (reader.GetAttribute("version") != SchemaVersion
                            && reader.GetAttribute("version") != "1"))
                        throw new InvalidDataException("Unsupported cache schema.");
                    bool empty = reader.IsEmptyElement;
                    reader.ReadStartElement();
                    while (!empty && reader.MoveToContent() != XmlNodeType.EndElement)
                    {
                        if (reader.NodeType != XmlNodeType.Element || reader.Name != "entry"
                            || reader.NamespaceURI.Length != 0 || ++count > MaximumEntries)
                            throw new InvalidDataException("Invalid cache entry.");
                        // Do not construct a document containing every weapon's analysis at startup.
                        XmlDocument document = NewDocument();
                        using (XmlReader item = reader.ReadSubtree()) document.Load(item);
                        XmlElement entry = document.DocumentElement;
                        string category = entry.GetAttribute("category");
                        string key = entry.GetAttribute("key");
                        string fingerprint = entry.GetAttribute("fingerprint");
                        ValidateKey(category, key, fingerprint);
                        if (result.Contains(category, key)) throw new InvalidDataException("Duplicate cache entry.");
                        if (entry.HasAttribute("payload") && entry.ChildNodes.Count == 0)
                        {
                            try
                            {
                                result.RestorePayload(category, key, fingerprint, entry.GetAttribute("payload"),
                                    entry.HasAttribute("file") ? entry.GetAttribute("file") : null);
                            }
                            catch (Exception exception) when (IsAnalysis(category))
                            {
                                Warn(exception);
                                result.dirty = true;
                                RimKataStartupDiagnostics.Count("cache_payload_rejected");
                            }
                        }
                        else if (!entry.HasAttribute("payload") && entry.ChildNodes.Count == 1
                            && entry.FirstChild is XmlElement data)
                            result.Restore(category, key, fingerprint, data);
                        else throw new InvalidDataException("Invalid cache entry.");
                        reader.Skip();
                    }
                    if (!empty) reader.ReadEndElement();
                    if (reader.MoveToContent() != XmlNodeType.None)
                        throw new InvalidDataException("Invalid cache document.");
                }
            }
            catch
            {
                result.Dispose();
                throw;
            }
            RimKataStartupDiagnostics.Count("cache_records_loaded", count);
            return result;
        }

        private static bool IsAnalysis(string category)
            => category == "weapon_execution" || category == "weapon_effects";

        private static bool IsPayloadName(string name)
        {
            if (name == null || name.Length != 64) return false;
            for (int i = 0; i < name.Length; i++)
                if (!((name[i] >= '0' && name[i] <= '9') || (name[i] >= 'a' && name[i] <= 'f'))) return false;
            return true;
        }

        private static string PayloadPath(string payload, string file = null)
        {
            file = file ?? payload;
            if (analysisDirectory == null || !IsPayloadName(payload) || file.Length < 16
                || file.Length > 64 || file.Length % 8 != 0 || !payload.StartsWith(file, StringComparison.Ordinal))
                throw new IOException("Invalid analysis storage path.");
            RejectReparsePoint(analysisDirectory);
            return Path.Combine(analysisDirectory, file + ".xml");
        }

        private static XmlReaderSettings ReaderSettings() => new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumFileBytes,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };

        private static FileStream OpenCache(string path)
        {
            RejectReparsePoint(path);
            FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 0 && stream.Length <= MaximumFileBytes) return stream;
            stream.Dispose();
            throw new InvalidDataException("Invalid cache size.");
        }

        private static XmlElement ReadPayload(string path, string payload)
        {
            XmlDocument document = NewDocument();
            using (FileStream stream = OpenCache(path))
            using (XmlReader reader = XmlReader.Create(stream, ReaderSettings())) document.Load(reader);
            XmlElement data = document.DocumentElement;
            if (data == null || Fingerprint(data.OuterXml) != payload)
                throw new InvalidDataException("Invalid analysis payload.");
            return data;
        }

        private static void WritePayload(XmlElement data, string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings
                    { Encoding = new UTF8Encoding(false), CloseOutput = false })) data.WriteTo(writer);
                if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Analysis payload is too large.");
                stream.Flush(true);
            }
        }

        private static void PublishPayload(string source, string payload)
        {
            EnsureDirectory(analysisDirectory);
            string file = Path.GetFileNameWithoutExtension(source);
            string destination = PayloadPath(payload, file);
            if (File.Exists(destination))
            {
                ReadPayload(destination, payload); // Recheck before publishing, including concurrent changes.
                return;
            }
            string candidate = Path.Combine(analysisDirectory, file + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(source, candidate, false);
                RejectReparsePoint(destination);
                File.Move(candidate, destination);
            }
            finally { DeleteFile(candidate); }
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
