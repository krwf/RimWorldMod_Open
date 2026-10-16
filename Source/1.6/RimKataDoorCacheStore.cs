using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace KRWF.RimKata
{
    internal sealed class RimKataDoorCacheRecord
    {
        internal readonly string Key;
        internal readonly string MetadataXml;
        internal readonly string RenderXml;

        internal RimKataDoorCacheRecord(string key, string metadata, string render)
        {
            Key = key;
            MetadataXml = metadata;
            RenderXml = render;
        }
    }

    internal sealed class RimKataDoorCacheStore
    {
        internal const int SchemaVersion = 1;
        private readonly string directory;
        private readonly Dictionary<string, RimKataDoorCacheRecord> prepared =
            new Dictionary<string, RimKataDoorCacheRecord>(StringComparer.Ordinal);

        internal RimKataDoorCacheStore(string modRoot, string gameId, XmlDocument environment)
        {
            if (!Guid.TryParseExact(gameId, "N", out Guid parsedId))
                throw new ArgumentException("Invalid door cache game ID.", nameof(gameId));
            directory = Path.GetFullPath(Path.Combine(modRoot, "door", parsedId.ToString("N"), EnvironmentKey(environment)));
            Directory.CreateDirectory(directory);
            string manifest = Path.Combine(directory, "environment.xml");
            WriteAtomic(manifest, environment);
        }

        internal static string EnvironmentKey(XmlDocument environment)
        {
            Validate(environment, "RimKataDoorEnvironment");
            return Fingerprint(environment.DocumentElement.OuterXml, string.Empty);
        }

        internal RimKataDoorCacheRecord Prepare(XmlDocument metadata, XmlDocument render)
        {
            Validate(metadata, "RimKataDoorMetadata");
            Validate(render, "RimKataDoorRender");
            string first = metadata.DocumentElement.OuterXml;
            string second = render.DocumentElement.OuterXml;
            string key = Fingerprint(first, second);
            if (prepared.TryGetValue(key, out RimKataDoorCacheRecord ready)) return ready;
            if (!TryRead(key, out ready))
            {
                // Both hashes are checked because an interrupted write can leave mismatched documents.
                WriteAtomic(Path.Combine(directory, key + ".render.xml"), render);
                WriteAtomic(Path.Combine(directory, key + ".xml"), metadata);
                ready = new RimKataDoorCacheRecord(key, first, second);
            }
            prepared.Add(key, ready);
            return ready;
        }

        internal bool TryRead(string key, out RimKataDoorCacheRecord record)
        {
            record = null;
            if (key == null || key.Length != 64) return false;
            foreach (char c in key)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            try
            {
                XmlDocument metadata = Read(Path.Combine(directory, key + ".xml"));
                XmlDocument render = Read(Path.Combine(directory, key + ".render.xml"));
                Validate(metadata, "RimKataDoorMetadata");
                Validate(render, "RimKataDoorRender");
                string first = metadata.DocumentElement.OuterXml;
                string second = render.DocumentElement.OuterXml;
                if (Fingerprint(first, second) != key) return false;
                record = new RimKataDoorCacheRecord(key, first, second);
                return true;
            }
            catch (IOException) { return false; }
            catch (InvalidDataException) { return false; }
            catch (XmlException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static XmlDocument Document(string root)
        {
            var document = new XmlDocument { XmlResolver = null };
            XmlElement element = document.CreateElement(root);
            element.SetAttribute("schemaVersion", SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            document.AppendChild(element);
            return document;
        }

        private static void Validate(XmlDocument document, string root)
        {
            if (document?.DocumentElement?.Name != root
                || document.DocumentElement.GetAttribute("schemaVersion")
                    != SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidDataException("Unsupported RimKata door cache document.");
        }

        private static XmlDocument Read(string path)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = 1024 * 1024
            })) document.Load(reader);
            return document;
        }

        private static string Fingerprint(string metadata, string render)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(metadata + "\n" + render)))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void WriteAtomic(string destination, XmlDocument document)
        {
            // Appending to the full SHA filename can exceed the Windows/Mono path limit; atomic replacement also requires the same volume.
            string temporary = Path.Combine(Path.GetDirectoryName(destination),
                Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (XmlWriter writer = XmlWriter.Create(temporary, new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false), Indent = true
                })) document.Save(writer);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
