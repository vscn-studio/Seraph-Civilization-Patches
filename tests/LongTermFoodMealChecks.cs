using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json.Linq;
using HarmonyLib;
using SeraphCivilizationPatches;
using Vintagestory.API.Common;

static class LongTermFoodMealChecks
{
    public static void Run(string game, string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var data = new Dictionary<string, byte[]>();
        foreach (string recipe in new[] { "jam", "meatystew", "porridge" })
        {
            using var stream = zip.GetEntry($"assets/game/patches/{recipe}.json")!.Open();
            using var buffer = new MemoryStream(); stream.CopyTo(buffer);
            data[$"game:patches/{recipe}.json"] = buffer.ToArray();
        }
        foreach (string shape in new[] { "jam", "meaty-stew", "porridge" })
            data[$"game:shapes/block/food/meal/{shape}.json"] = File.ReadAllBytes(Path.Combine(game, $"assets/survival/shapes/block/food/meal/{shape}.json"));
        var original = data.ToDictionary(p => p.Key, p => p.Value.ToArray());
        bool enabled = false;
        EnumAppSide side = EnumAppSide.Server;
        var assetsByCode = data.Keys.ToDictionary(code => code, code => Proxy.Create<IAsset>((m, a) =>
        {
            switch (m.Name)
            {
                case "ToText": return Encoding.UTF8.GetString(data[code]);
                case "get_Data": return data[code];
                case "set_Data": data[code] = (byte[])a![0]!; return null;
                default: return null;
            }
        }));
        var assets = Proxy.Create<IAssetManager>((m, a) => m.Name == "TryGet" ? assetsByCode.GetValueOrDefault(a![0]!.ToString()!) : null);
        var mods = Proxy.Create<IModLoader>((m, a) => m.Name == "IsModEnabled" && enabled && (string)a![0]! == "longtermfood");
        var warnings = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) => { if (m.Name == "Warning") warnings.Add(string.Join(" ", a ?? [])); return null; });
        var api = Proxy.Create<ICoreAPI>((m, _) => m.Name switch
        { "get_Assets" => assets, "get_ModLoader" => mods, "get_Logger" => logger, "get_Side" => side, _ => null });
        int count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        var system = new LongTermFoodMealCompatibilitySystem();
        system.AssetsLoaded(api);
        Check(data.All(p => p.Value.SequenceEqual(original[p.Key])), "Absent mod must leave assets intact");
        enabled = true;
        system.AssetsLoaded(api);
        Check(data.Where(p => p.Key.Contains(":shapes/")).All(p => p.Value.SequenceEqual(original[p.Key])), "Server must only prepare recipes");
        var serverResult = data.ToDictionary(p => p.Key, p => p.Value.ToArray());
        side = EnumAppSide.Client; system.AssetsLoaded(api);
        Check(data.Where(p => p.Key.Contains(":patches/")).All(p => p.Value.SequenceEqual(serverResult[p.Key])), "Client must not modify recipe sources");
        Check(warnings.Count == 0, "Compatibility must not log warnings");

        // Use the actual engine selector, including its parent/* semantics.
        var tessType = Assembly.LoadFrom(Path.Combine(game, "VintagestoryLib.dll")).GetType("Vintagestory.Client.NoObf.ShapeTesselator", true)!;
        var tess = RuntimeHelpers.GetUninitializedObject(tessType);
        var match = AccessTools.Method(tessType, "SelectiveMatch");
        List<JObject> Select(JArray elements, string[]? selectors)
        {
            var result = new List<JObject>();
            foreach (JObject element in elements)
            {
                object?[] args = [(string)element["name"]!, selectors, null];
                if (!(bool)match.Invoke(tess, args)!) continue;
                result.Add(element);
                if (element["children"] is JArray children) result.AddRange(Select(children, (string[]?)args[2]));
            }
            return result;
        }
        bool Visible(JObject shape, string selector) => Select((JArray)shape["elements"]!, [selector])
            .Any(e => e["faces"] is JObject faces && faces.Properties().Any(f => f.Value["enabled"]?.Value<bool>() != false));
        int repaired = 0, broken = 0;
        foreach (string recipe in new[] { "jam", "meatystew", "porridge" })
        {
            string key = $"game:patches/{recipe}.json";
            var before = JArray.Parse(Encoding.UTF8.GetString(original[key]));
            var after = JArray.Parse(Encoding.UTF8.GetString(data[key]));
            string shapeKey = $"game:shapes/block/food/meal/{(recipe == "meatystew" ? "meaty-stew" : recipe)}.json";
            var oldShape = JObject.Parse(Encoding.UTF8.GetString(original[shapeKey]));
            var shape = JObject.Parse(Encoding.UTF8.GetString(data[shapeKey]));
            foreach (var selector in before.Descendants().OfType<JProperty>().Where(p => p.Name == "shapeElement"))
                if (!Visible(oldShape, selector.Value.Value<string>()!)) broken++;
            foreach (var selector in after.Descendants().OfType<JProperty>().Where(p => p.Name == "shapeElement"))
            {
                Check(Visible(shape, selector.Value.Value<string>()!), recipe + ": visible geometry for " + selector.Value);
                if (!JToken.DeepEquals(before.SelectToken(selector.Path), selector.Value)) repaired++;
            }
            foreach (var p in before.Descendants().OfType<JProperty>().Where(p => p.Name is "shapeElement" or "textureMapping").ToArray()) p.Remove();
            foreach (var p in after.Descendants().OfType<JProperty>().Where(p => p.Name is "shapeElement" or "textureMapping").ToArray()) p.Remove();
            Check(JToken.DeepEquals(before, after), "All non-render recipe fields preserved");
        }
        // Two topping/fruit paths still select the parent topping in the engine;
        // canonicalize them too, but do not count them as missing geometry.
        Check(broken == 20 && repaired == 22, $"Expected 20 invisible and 22 corrected entries, got {broken}/{repaired}");
        string porridgeKey = "game:shapes/block/food/meal/porridge.json";
        var oldPorridge = JObject.Parse(Encoding.UTF8.GetString(original[porridgeKey]));
        var newPorridge = JObject.Parse(Encoding.UTF8.GetString(data[porridgeKey]));
        string Geometry(JObject e) { var clone = (JObject)e.DeepClone(); clone.Remove("children"); return clone.ToString(); }
        foreach (string selector in new[] { "bowl/grain/*", "bowl/fruit 1/*", "bowl/fruit 2/*", "bowl/vegetable 1/*", "bowl/vegetable 2/*", "bowl/topping" })
            Check(Select((JArray)oldPorridge["elements"]!, [selector]).Select(Geometry).SequenceEqual(
                Select((JArray)newPorridge["elements"]!, [selector]).Select(Geometry)), "Vanilla porridge geometry preserved");
        var once = data.ToDictionary(p => p.Key, p => p.Value.ToArray());
        system.AssetsLoaded(api); side = EnumAppSide.Server; system.AssetsLoaded(api);
        Check(data.All(p => p.Value.SequenceEqual(once[p.Key])), "Repeated preparation must be idempotent");
        Console.WriteLine($"{count} Long-term food checks passed; 20 invisible entries reproduced, 22 rendering entries corrected against engine selection.");
    }
}
