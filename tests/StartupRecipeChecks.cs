using System.IO.Compression;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.ServerMods.NoObf;
using SeraphCivilizationPatches;

static class StartupRecipeChecks
{
    public static void Run(string root, string modsPath)
    {
        int count = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            count++;
            Console.WriteLine("PASS: " + name);
        }
        string Read(string package, string path)
        {
            using var zip = ZipFile.OpenRead(TestPackages.Find(modsPath, package));
            using var reader = new StreamReader(zip.GetEntry(path)!.Open());
            return reader.ReadToEnd();
        }
        const string capes = "HeraldryCapes-v2.0.0.zip", combs = "From Golden Combs-2.1.0.zip";
        const string patchCode = "capes:patches/disable.json", capeCode = "capes:recipes/grid/cape-to-banner.json";
        const string barrelCode = "fromgoldencombs:recipes/barrel/langstrothparts.json";
        var originalPatch = Read(capes, "assets/capes/patches/disable.json");
        var originalCape = Read(capes, "assets/capes/recipes/grid/cape-to-banner.json");
        var originalBarrel = Read(combs, "assets/fromgoldencombs/recipes/barrel/langstrothparts.json");
        var data = new Dictionary<string, byte[]> {
            [patchCode] = Encoding.UTF8.GetBytes(originalPatch),
            [capeCode] = Encoding.UTF8.GetBytes(originalCape),
            [barrelCode] = Encoding.UTF8.GetBytes(originalBarrel)
        };
        var assetObjects = data.Keys.ToDictionary(code => code, code => Proxy.Create<IAsset>((m, a) => {
            switch (m.Name)
            {
                case "get_Location": return new AssetLocation(code);
                case "get_Data": return data[code];
                case "set_Data": data[code] = (byte[])a![0]!; return null;
                case "ToText": return Encoding.UTF8.GetString(data[code]);
                case "ToObject": return JsonConvert.DeserializeObject(Encoding.UTF8.GetString(data[code]), m.GetGenericArguments()[0]);
                default: return null;
            }
        }));
        bool banners = false, capeEnabled = true, combsEnabled = true;
        var errors = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) => { if (m.Name == "Error") errors.Add(string.Join(" ", a ?? [])); return null; });
        var assets = Proxy.Create<IAssetManager>((m, a) => m.Name == "TryGet" ? assetObjects.GetValueOrDefault(a![0]!.ToString()!) : null);
        var mods = Proxy.Create<IModLoader>((m, a) => m.Name == "IsModEnabled" ? a![0] switch {
            "capes" => capeEnabled, "heraldrybanners" => banners, "fromgoldencombs" => combsEnabled, _ => false
        } : null);
        string[] woods = ["oak", "pine", "aged", "veryaged"];
        var items = new List<Item> { new() { Code = new AssetLocation("game:beeswax"), ItemId = 1 } };
        foreach (string wood in woods)
        {
            items.Add(new Item { Code = new AssetLocation("game:plank-" + wood), ItemId = items.Count + 1 });
            if (wood != "veryaged") items.Add(new Item { Code = new AssetLocation("fromgoldencombs:langstrothside-" + wood), ItemId = items.Count + 1 });
        }
        var fastSearch = new System.Collections.Generic.OrderedDictionary<IRecipeIngredientBase, List<IRecipeBase>>();
        var world = Proxy.Create<IServerWorldAccessor>((m, a) => m.Name switch {
            "get_Logger" => logger, "get_Items" => items,
            "get_FastSearchRecipesByIngredient" => fastSearch,
            "GetItem" => items.FirstOrDefault(i => i.Code.Equals(a![0])), _ => null
        });
        var api = Proxy.Create<ICoreServerAPI>((m, _) => m.Name switch {
            "get_Assets" => assets, "get_ModLoader" => mods, "get_Logger" => logger,
            "get_World" => world, "get_Side" => EnumAppSide.Server, _ => null
        });
        var loader = new ModJsonPatchLoader();
        typeof(ModJsonPatchLoader).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(loader, api);
        (int applied, int failed) Apply(string json, string source)
        {
            int applied = 0, missing = 0, failed = 0;
            var patches = JsonConvert.DeserializeObject<JsonPatch[]>(json)!;
            for (int i = 0; i < patches.Length; i++)
            {
                // ApplyPatches filters dependencies before calling ApplyPatch.
                if (patches[i].DependsOn?.Any(d => !(mods.IsModEnabled(d.modid) ^ d.invert)) == true) continue;
                loader.ApplyPatch(i, new AssetLocation(source), patches[i], ref applied, ref missing, ref failed);
            }
            Check(missing == 0, source + " all target assets exist");
            return (applied, failed);
        }
        Check(Apply(originalPatch, patchCode).failed == 2 && errors.Count == 2, "installed cape patches reproduce both invalid array paths");
        errors.Clear();
        var system = new CapeRecipePatchCompatibilitySystem();
        Check(system.ExecuteOrder() < loader.ExecuteOrder() && !system.ShouldLoad(EnumAppSide.Client), "cape repair runs on server before patch loader");
        capeEnabled = false;
        system.AssetsLoaded(api);
        Check(Encoding.UTF8.GetString(data[patchCode]) == originalPatch, "absent cape mod skipped");
        capeEnabled = true;
        system.AssetsLoaded(api);
        string safePatch = Encoding.UTF8.GetString(data[patchCode]);
        system.AssetsLoaded(api);
        Check(Encoding.UTF8.GetString(data[patchCode]) == safePatch, "cape preparation idempotent");
        var before = JArray.Parse(originalPatch);
        var after = JArray.Parse(safePatch);
        for (int i = 0; i < after.Count; i++) before[i]["path"] = after[i]["path"];
        Check(JToken.DeepEquals(before, after), "all cape patch conditions and fields preserved");
        Check(Apply(safePatch, patchCode) == (2, 0) && errors.Count == 0, "fixed cape patches apply without errors");
        Check(JArray.Parse(Encoding.UTF8.GetString(data[capeCode])).All(r => (bool?)r["enabled"] == false), "missing banners disables only two conversion recipes");
        banners = true;
        data[capeCode] = Encoding.UTF8.GetBytes(originalCape);
        Check(Apply(safePatch, patchCode) == (0, 0) && Encoding.UTF8.GetString(data[capeCode]) == originalCape, "installed banners preserve both conversion recipes");

        BarrelRecipe[] Expand(string json)
        {
            RecipeBase.CollectiblePreSearchResultsCache.Clear();
            fastSearch.Clear();
            return JsonConvert.DeserializeObject<BarrelRecipe[]>(json)!.SelectMany(r => {
                r.Name = new AssetLocation(barrelCode);
                r.OnParsed(world);
                return r.GenerateRecipesForAllIngredientCombinations(world).Cast<BarrelRecipe>();
            }).ToArray();
        }
        var unpatched = Expand(originalBarrel);
        Check(unpatched.Length == 4 && unpatched.Count(r => !r.Resolve(world, "RecipeLoader")) == 1,
            "installed barrel recipe reproduces exactly one unresolved veryaged output");
        errors.Clear();
        string fixCode = "vsscpmod:patches/compatibility/fromgoldencombs-barrel-side.json";
        string fix = File.ReadAllText(Path.Combine(root, "assets/vsscpmod/patches/compatibility/fromgoldencombs-barrel-side.json"));
        combsEnabled = false;
        Check(Apply(fix, fixCode) == (0, 0), "absent bee mod skipped");
        combsEnabled = true;
        Check(Apply(fix, fixCode) == (1, 0), "barrel skipVariants patch applied");
        var fixedRecipes = Expand(Encoding.UTF8.GetString(data[barrelCode]));
        Check(fixedRecipes.Length == 3 && fixedRecipes.All(r => r.Resolve(world, "RecipeLoader")) && errors.Count == 0,
            "oak, pine and aged barrel recipes resolve; unsupported veryaged excluded");
        Check(fixedRecipes.All(r => r.SealHours == 48 && r.Output!.StackSize == 4 && r.Ingredients![0].Quantity == 1 && r.Ingredients[1].Quantity == 4),
            "wax, plank quantities, duration and output amounts preserved");
        Console.WriteLine($"Startup recipes: {count} checks passed.");
    }
}
