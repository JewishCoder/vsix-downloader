using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace VsixDownloader;

internal static class VsixValidator
{
    public static void Validate(string path, PackageBuild expected)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var manifest = zip.GetEntry("extension.vsixmanifest");
            if (manifest is null || manifest.Length > 1024 * 1024 || zip.GetEntry("extension/package.json") is null)
                throw new InvalidDataException("В архиве не найден манифест VSIX или package.json расширения.");

            using var stream = manifest.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 1024 * 1024
            });
            var root = XDocument.Load(reader).Root;
            if (root is null || root.Name.LocalName != "PackageManifest")
                throw new InvalidDataException("Некорректный манифест VSIX.");

            var ns = root.Name.Namespace;
            var identity = root.Element(ns + "Metadata")?.Element(ns + "Identity");
            var id = $"{(string?)identity?.Attribute("Publisher")}.{(string?)identity?.Attribute("Id")}";
            var version = (string?)identity?.Attribute("Version");
            var platform = (string?)identity?.Attribute("TargetPlatform");
            if (string.IsNullOrWhiteSpace(platform) || platform == "universal")
                platform = null;

            if (!string.Equals(id, expected.ExtensionId, StringComparison.OrdinalIgnoreCase) ||
                version != expected.Version || platform != (expected.TargetPlatform == "universal" ? null : expected.TargetPlatform))
                throw new InvalidDataException("ID, версия или платформа в манифесте VSIX не совпадают с выбранным пакетом.");
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("Не удалось прочитать манифест VSIX.", ex);
        }
    }
}
