using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataDoorCacheEnvironment
    {
        // Capture only when a game's store is first needed. Use already loaded
        // metadata/assemblies; do not crawl mod folders or read their DLL files.
        internal static XmlDocument Capture()
        {
            XmlDocument document = RimKataDoorCacheStore.Document("RimKataDoorEnvironment");
            XmlElement root = document.DocumentElement;
            root.SetAttribute("gameVersion", VersionControl.CurrentVersionStringWithRev);
            root.AppendChild(AssemblyElement(document, typeof(Game).Assembly));
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                XmlElement element = document.CreateElement("mod");
                element.SetAttribute("packageId", mod.PackageId);
                element.SetAttribute("loadOrder", mod.loadOrder.ToString(CultureInfo.InvariantCulture));
                element.SetAttribute("version", mod.ModMetaData?.ModVersion ?? string.Empty);
                if (mod.assemblies?.loadedAssemblies != null)
                {
                    foreach (Assembly assembly in mod.assemblies.loadedAssemblies
                        .OrderBy(value => value.FullName, StringComparer.Ordinal))
                        element.AppendChild(AssemblyElement(document, assembly));
                }
                root.AppendChild(element);
            }
            return document;
        }

        private static XmlElement AssemblyElement(XmlDocument document, Assembly assembly)
        {
            XmlElement element = document.CreateElement("assembly");
            element.SetAttribute("name", assembly.FullName);
            element.SetAttribute("moduleId", assembly.ManifestModule.ModuleVersionId.ToString("D"));
            return element;
        }
    }
}
