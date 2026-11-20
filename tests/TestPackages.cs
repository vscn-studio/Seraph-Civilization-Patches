using System.IO.Compression;
using System.Text.Json;

static class TestPackages
{
    internal static string Find(string directory, string expected)
    {
        var ids = new Dictionary<string, string> {
            ["Komet-3.0.0-pre.3.zip"] = "komet",
            ["Primitive Survival-5.1.4.zip"] = "primitivesurvival",
            ["Fields of Salt-1.2.5.zip"] = "fieldsofsalt",
            ["Shipwright_ Vessels of Distant Shores-1.5.0.zip"] = "shipwright",
            ["Envelopes-3.9.1.zip"] = "envelopes",
            ["HeraldryCapes-v2.0.0.zip"] = "capes",
            ["From Golden Combs-2.1.0.zip"] = "fromgoldencombs"
        };
        foreach (var file in Directory.EnumerateFiles(directory, "*.zip"))
        {
            using var zip = ZipFile.OpenRead(file);
            if (zip.GetEntry("modinfo.json") is not { } entry) continue;
            using var reader = new StreamReader(entry.Open());
            using var doc = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.EnumerateObject().Any(p => p.Name.Equals("modid", StringComparison.OrdinalIgnoreCase)
                && p.Value.GetString() == ids[expected])) return file;
        }
        throw new FileNotFoundException("Required test mod is missing: " + ids[expected]);
    }
}
