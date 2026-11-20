using System.IO.Compression;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.ServerMods.NoObf;

static class StartupAssetChecks
{
    private sealed class TestMod : Mod
    {
        public TestMod(string id) => typeof(Mod).GetProperty(nameof(Info))!.SetValue(this, new ModInfo { ModID = id });
    }

    public static void Run(string root, string game, string modsPath)
    {
        int count = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            count++;
            Console.WriteLine("PASS: " + name);
        }
        string Read(string package, string asset)
        {
            using var zip = ZipFile.OpenRead(TestPackages.Find(modsPath, package));
            using var reader = new StreamReader(zip.GetEntry(asset)!.Open());
            return reader.ReadToEnd();
        }
        const string salt = "Fields of Salt-1.2.5.zip", ship = "Shipwright_ Vessels of Distant Shores-1.5.0.zip", envelopes = "Envelopes-3.9.1.zip";
        var originals = new Dictionary<string, string> {
            ["fieldsofsalt:recipes/grid/connector.json"] = Read(salt, "assets/fieldsofsalt/recipes/grid/connector.json"),
            ["shipwright:shapes/entity/drakkar.json"] = Read(ship, "assets/shipwright/shapes/entity/drakkar.json"),
            ["envelopes:itemtypes/signetring.json"] = Read(envelopes, "assets/envelopes/itemtypes/signetring.json")
        };
        string[] patchNames = ["fieldsofsalt-connector-rock", "shipwright-drakkar-faces", "envelopes-signetring-engraving", "envelopes-signetring-preview"];
        var patches = patchNames.ToDictionary(n => "vsscpmod:patches/compatibility/" + n + ".json",
            n => File.ReadAllText(Path.Combine(root, "assets/vsscpmod/patches/compatibility/" + n + ".json")));
        var messages = new List<(string level, string text)>();
        var logger = Proxy.Create<ILogger>((m, a) => {
            if (a?.FirstOrDefault() is string s) messages.Add((m.Name, s));
            return null;
        });
        IAsset Asset(string code, Func<byte[]> read, Action<byte[]> write) => Proxy.Create<IAsset>((m, a) => m.Name switch {
            "get_Location" => new AssetLocation(code), "get_Data" => read(),
            "set_Data" => Write((byte[])a![0]!, write), "ToText" => Encoding.UTF8.GetString(read()),
            "ToObject" => JsonConvert.DeserializeObject(Encoding.UTF8.GetString(read()), m.GetGenericArguments()[0]), _ => null
        });
        Dictionary<string, string> Apply(EnumAppSide side, bool enabled)
        {
            messages.Clear();
            var data = originals.ToDictionary(k => k.Key, k => Encoding.UTF8.GetBytes(k.Value));
            var assetsByCode = data.Keys.ToDictionary(k => k, k => Asset(k, () => data[k], b => data[k] = b));
            var patchAssets = patches.Select(k => Asset(k.Key, () => Encoding.UTF8.GetBytes(k.Value), _ => { })).ToList();
            var assets = Proxy.Create<IAssetManager>((m, a) => m.Name switch {
                "GetMany" => patchAssets,
                "TryGet" => a![0] is AssetLocation loc &&
                    (loc.Path.StartsWith("shapes/") ? side == EnumAppSide.Client : side == EnumAppSide.Server)
                    ? assetsByCode.GetValueOrDefault(loc.ToString()) : null, _ => null
            });
            var mods = Proxy.Create<IModLoader>((m, _) => m.Name == "get_Mods"
                ? (IEnumerable<Mod>)(enabled ? new Mod[] { new TestMod("fieldsofsalt"), new TestMod("shipwright"), new TestMod("envelopes") } : []) : null);
            var world = Proxy.Create<IWorldAccessor>((m, _) => m.Name switch {
                "get_Logger" => logger, "get_Config" => new TreeAttribute(), _ => null
            });
            var api = Proxy.Create<ICoreAPI>((m, _) => m.Name switch {
                "get_Assets" => assets, "get_Logger" => logger, "get_ModLoader" => mods,
                "get_World" => world, "get_Side" => side, _ => null
            });
            new ModJsonPatchLoader().AssetsLoaded(api);
            Check(!messages.Any(m => m.level == "Error"), $"actual asset loader: {side}, mods={enabled}, no errors");
            return data.ToDictionary(k => k.Key, k => Encoding.UTF8.GetString(k.Value));
        }
        foreach (var side in new[] { EnumAppSide.Client, EnumAppSide.Server })
            Check(Apply(side, false).All(k => k.Value == originals[k.Key]), $"{side}: optional mods absent, assets untouched");
        var server = Apply(EnumAppSide.Server, true);
        var client = Apply(EnumAppSide.Client, true);
        Check(server["shipwright:shapes/entity/drakkar.json"] == originals["shipwright:shapes/entity/drakkar.json"] &&
            client.Where(k => !k.Key.StartsWith("shipwright:")).All(k => k.Value == originals[k.Key]), "patches run only on the asset-owning side");

        // Expand and resolve the actual grid recipe against every supported rock,
        // plus the extra polished obsidian that triggered the installed error.
        var connector = JObject.Parse(Read(salt, "assets/fieldsofsalt/blocktypes/connector.json"));
        var rocks = JObject.Parse(File.ReadAllText(Path.Combine(game, "assets/survival/worldproperties/block/rock.json")))["variants"]!
            .Select(r => (string)r["Code"]!).Concat(connector["variantgroups"]![0]!["states"]!.Select(r => r.Value<string>()!)).Distinct().ToArray();
        var blocks = new List<Block>();
        foreach (string rock in rocks.Append("obsidian"))
        {
            blocks.Add(new Block { BlockId = blocks.Count + 1, Code = new AssetLocation("game:rockpolished-" + rock) });
            if (rock != "obsidian") blocks.Add(new Block { BlockId = blocks.Count + 1, Code = new AssetLocation("fieldsofsalt:connector-" + rock + "-ns") });
        }
        var items = new[] { "plank-oak", "chisel-copper", "hammer-copper", "saw-copper" }
            .Select((code, i) => new Item { ItemId = i + 1, Code = new AssetLocation("game:" + code) }).ToList();
        var fastSearch = new System.Collections.Generic.OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>>();
        var recipeWorld = Proxy.Create<IWorldAccessor>((m, a) => m.Name switch {
            "get_Items" => items, "get_Blocks" => blocks, "get_Logger" => logger,
            "get_FastSearchRecipesByIngredient" => fastSearch,
            "GetItem" => items.FirstOrDefault(i => i.Code.Equals(a![0])),
            "GetBlock" => blocks.FirstOrDefault(b => b.Code.Equals(a![0])), _ => null
        });
        GridRecipe[] Expand(string json)
        {
            RecipeBase.CollectiblePreSearchResultsCache.Clear(); fastSearch.Clear();
            var recipe = JsonUtil.ToObject<GridRecipe>(JObject.Parse(json), "fieldsofsalt")!;
            recipe.Name = new AssetLocation("fieldsofsalt:connector"); recipe.OnParsed(recipeWorld);
            return recipe.GenerateRecipesForAllIngredientCombinations(recipeWorld).Cast<GridRecipe>().ToArray();
        }
        string recipeCode = "fieldsofsalt:recipes/grid/connector.json";
        var oldRecipes = Expand(originals[recipeCode]);
        Check(oldRecipes.Length == rocks.Length + 1 && oldRecipes.Count(r => !r.Resolve(recipeWorld, "RecipeLoader")) == 1,
            "installed connector recipe reproduces exactly one missing obsidian output");
        var recipes = Expand(server[recipeCode]);
        Check(recipes.Length == rocks.Length && recipes.All(r => r.Resolve(recipeWorld, "RecipeLoader")), "all supported rock connector recipes resolve after patch");
        var expectedRecipe = JObject.Parse(originals[recipeCode]); expectedRecipe["ingredients"]!["R"]!["skipVariants"] = new JArray("obsidian");
        Check(JToken.DeepEquals(expectedRecipe, JObject.Parse(server[recipeCode])), "recipe pattern, tools, costs and output quantities preserved");

        string shipCode = "shipwright:shapes/entity/drakkar.json";
        var before = JObject.Parse(originals[shipCode]); var after = JObject.Parse(client[shipCode]);
        IEnumerable<JObject> Elements(JToken elements)
        {
            foreach (JObject e in elements)
            {
                yield return e;
                if (e["children"] != null) foreach (var child in Elements(e["children"]!)) yield return child;
            }
        }
        var bad = Elements(before["elements"]!).Single(e => (string?)e["name"] == "FlooR119");
        var fixedElement = Elements(after["elements"]!).Single(e => (string?)e["name"] == "FlooR119");
        string[] faces = ["north", "south", "west", "up"];
        Check(faces.All(f => (bool?)bad["faces"]![f]!["enabled"] != false && (string?)bad["faces"]![f]!["texture"] == "#null"), "four undefined drakkar faces reproduce original model error");
        Check(faces.All(f => (bool?)fixedElement["faces"]![f]!["enabled"] == false), "only the four unintended faces are disabled");
        foreach (string f in faces) fixedElement["faces"]![f]!.Value<JObject>()!.Remove("enabled");
        Check(JToken.DeepEquals(before, after), "all other ship geometry, UVs, sail states and water mask remain intact");
        var shipItem = JObject.Parse(Read(ship, "assets/shipwright/itemtypes/boats/drakkar.json"));
        var ignored = shipItem["shapeByType"]!["*"]!["ignoreElements"]!.Values<string>().ToHashSet();
        var textureKeys = shipItem["texturesByType"]!["*"]!.Value<JObject>()!.Properties().Select(p => p.Name).ToHashSet();
        textureKeys.UnionWith(((JObject?)before["textures"] ?? new JObject()).Properties().Select(p => p.Name));
        IEnumerable<string> Missing(JToken elements)
        {
            foreach (JObject e in elements)
            {
                if (ignored.Contains((string?)e["name"])) continue;
                foreach (JProperty face in ((JObject?)e["faces"] ?? new JObject()).Properties())
                    if ((bool?)face.Value["enabled"] != false && face.Value["texture"] is JValue t && !textureKeys.Contains(t.ToString().TrimStart('#')))
                        yield return e["name"] + "/" + face.Name;
                if (e["children"] != null) foreach (string child in Missing(e["children"]!)) yield return child;
            }
        }
        var oldMissing = Missing(before["elements"]!).ToArray();
        var newMissing = Missing(JObject.Parse(client[shipCode])["elements"]!).ToArray();
        Check(oldMissing.Length == 4 && newMissing.Length == 0, "rendered ship item has no undefined texture references after patch");

        // Use the real Envelopes shape generator and real texture source lookup.
        string ringCode = "envelopes:itemtypes/signetring.json";
        var oldRing = JObject.Parse(originals[ringCode]); var ring = JObject.Parse(server[ringCode]);
        var ringShapeText = Read(envelopes, "assets/envelopes/shapes/item/signetring.json");
        var ringShape = JObject.Parse(ringShapeText);
        int engravingFaces = Elements(ringShape["elements"]!).SelectMany(e => ((JObject?)e["faces"] ?? new JObject()).Properties())
            .Count(f => (string?)f.Value["texture"] == "#engraving");
        Check(oldRing["textures"]!["engraving"] == null && engravingFaces == 6, "original ring leaves six engraving faces undefined");
        var expectedRing = (JObject)oldRing.DeepClone(); expectedRing["textures"]!["engraving"] = JObject.Parse("{base:'game:block/metal/ingot/gold'}");
        expectedRing["guiTransform"]!["translation"] = JObject.Parse("{x:0,y:0,z:0}");
        expectedRing["guiTransform"]!["origin"] = JObject.Parse("{x:0,y:-0.02890625,z:0.009375}");
        Check(JToken.DeepEquals(expectedRing, ring), "ring fixes change only engraving fallback and GUI position/pivot");
        foreach (string metal in ring["variantgroups"]![0]!["states"]!.Values<string>()!)
            Check(File.Exists(Path.Combine(game, "assets/survival/textures/block/metal/ingot/" + metal + ".png")), "ring body metal exists: " + metal);
        Check(File.Exists(Path.Combine(game, "assets/survival/textures/block/metal/ingot/gold.png")), "default engraving texture is an existing game asset");
        using var envelopeZip = ZipFile.OpenRead(TestPackages.Find(modsPath, envelopes));
        using var envelopeDll = envelopeZip.GetEntry("Envelopes.dll")!.Open();
        using var assemblyBytes = new MemoryStream();
        envelopeDll.CopyTo(assemblyBytes);
        var assembly = Assembly.Load(assemblyBytes.ToArray());
        var behaviorType = assembly.GetType("Envelopes.Behaviors.RenderStampEmblem", true)!;
        var item = new Item { Code = new AssetLocation("envelopes:signetring-copper"), ItemId = 1,
            Shape = new CompositeShape { Base = new AssetLocation("envelopes:item/signetring") },
            Textures = JsonConvert.DeserializeObject<Dictionary<string, CompositeTexture>>(ring["textures"]!.ToString())! };
        item.Textures["metal"].Base = new AssetLocation("game:block/metal/ingot/copper");
        var behavior = Activator.CreateInstance(behaviorType, item)!;
        ((CollectibleBehavior)behavior).Initialize(new JsonObject(JObject.Parse("{faceUp:true}")));
        var stack = new ItemStack(item); stack.Attributes.SetString("StampBodyMetal", "copper");
        var shapeAsset = Asset("envelopes:shapes/item/signetring.json", () => Encoding.UTF8.GetBytes(ringShapeText), _ => { });
        var clientAssets = Proxy.Create<IAssetManager>((m, _) => m.Name == "TryGet" ? shapeAsset : null);
        AssetLocation? requested = null;
        var position = new TextureAtlasPosition();
        var atlas = Proxy.Create<IBlockTextureAtlasAPI>((m, a) => {
            if (m.Name != "GetOrInsertTexture") return null;
            requested = a![0] is CompositeTexture t ? t.Base : (AssetLocation)a[0]!;
            a[1] = 1; a[2] = position; return true;
        });
        bool envelopesEnabled = false;
        var clientMods = Proxy.Create<IModLoader>((m, _) => m.Name == "IsModEnabled" ? envelopesEnabled : null);
        var capi = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch {
            "get_Assets" => clientAssets, "get_BlockTextureAtlas" => atlas, "get_ModLoader" => clientMods, "get_Logger" => logger, _ => null
        });
        behaviorType.GetField("_api", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(behavior, capi);
        var compat = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly.GetType("SeraphCivilizationPatches.EnvelopeEngravingCompatibility")!;
        var create = compat.GetMethod("TryCreate")!;
        Check(create.Invoke(null, [capi]) == null, "Envelopes absent: runtime compatibility skipped");
        Shape Generate() => (Shape)behaviorType.GetMethod("GenShape", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(behavior, [capi, stack])!;
        var plainShape = Generate();
        int dimensions = (int)assembly.GetType("Envelopes.Gui.Constants", true)!.GetField("GridDimensions")!.GetValue(null)!;
        stack.Attributes.SetString("StampDesign", string.Concat(Enumerable.Range(0, dimensions * dimensions).Select(i => i % 2 == 0 ? '1' : '0')));
        EnvelopePreviewChecks.Run(game, [("static fallback", JsonConvert.DeserializeObject<Shape>(ringShapeText)!),
            ("default engraving", plainShape), ("custom engraving", Generate())], oldRing, ring, Check);
        stack.Attributes.RemoveAttribute("StampDesign");
        ShapeTextureSource Source(Shape shape, string name = "stampssealsource") => new(capi, shape, name, item.Textures, p => p);
        stack.Attributes.SetString("EngravingMetal", "silver");
        _ = Source(Generate())["engraving"];
        Check(requested!.Equals(new AssetLocation("game:block/metal/ingot/gold")), "static fallback alone would override the selected silver engraving");
        envelopesEnabled = true;
        using ((IDisposable)create.Invoke(null, [capi])!)
        {
            foreach (string? metal in new string?[] { null, "silver", "copper" })
            {
                if (metal == null) stack.Attributes.RemoveAttribute("EngravingMetal"); else stack.Attributes.SetString("EngravingMetal", metal);
                _ = Source(Generate())["engraving"];
                Check(requested!.Equals(new AssetLocation("game:block/metal/ingot/" + (metal ?? "gold"))), "dynamic engraving lookup: " + (metal ?? "default gold"));
            }
            _ = Source(Generate(), "unrelated-shape")["engraving"];
            Check(requested!.Equals(new AssetLocation("game:block/metal/ingot/gold")), "unrelated texture sources keep normal item precedence");
            Check(item.Textures["engraving"].Base.Equals(new AssetLocation("game:block/metal/ingot/gold")), "shared item fallback is not mutated by custom engravings");
        }
        _ = Source(Generate())["engraving"];
        Check(requested!.Equals(new AssetLocation("game:block/metal/ingot/gold")), "disposing compatibility restores original texture lookup");

        Console.WriteLine($"Startup assets: {count} checks passed.");
    }

    private static object? Write(byte[] data, Action<byte[]> write) { write(data); return null; }
}
