using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.ServerMods.NoObf;
using SeraphCivilizationPatches;

static class SeaweedPatchChecks
{
    // Ashes 1.5.1's installed patch, before compatibility preparation.
    private const string Original = """
        [{"op":"addmerge","path":"/drops/-","value":{"type":"item","code":"ashes:plantdebris-seaweed","quantity":{"avg":1,"var":1}},"file":"game:blocktypes/plant/aquatic/seaweed.json","side":"Server"}]
        """;

    public static void Run(string root)
    {
        int count = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            count++;
        }

        byte[] patchBytes = Encoding.UTF8.GetBytes(Original);
        byte[] blockBytes = Encoding.UTF8.GetBytes("{}");
        IAsset Asset(string code, Func<byte[]> read, Action<byte[]> write) => Proxy.Create<IAsset>((m, a) => m.Name switch
        {
            "get_Location" => new AssetLocation(code),
            "get_Data" => read(),
            "set_Data" => SetBytes((byte[])a![0]!, write),
            "ToText" => Encoding.UTF8.GetString(read()),
            "ToObject" => JsonConvert.DeserializeObject(Encoding.UTF8.GetString(read()), m.GetGenericArguments()[0]),
            _ => null
        });
        var patchAsset = Asset("ashes:patches/survival-blocktypes-plant-aquatic-seaweed.json", () => patchBytes, b => patchBytes = b);
        var blockAsset = Asset("game:blocktypes/plant/aquatic/seaweed.json", () => blockBytes, b => blockBytes = b);
        bool enabled = true;
        EnumAppSide side = EnumAppSide.Server;
        var errors = new List<object?[]>();
        var logger = Proxy.Create<ILogger>((m, a) => { if (m.Name == "Error") errors.Add(a!); return null; });
        var assets = Proxy.Create<IAssetManager>((m, a) => m.Name == "TryGet" ?
            ((AssetLocation)a![0]!).Domain == "ashes" ? patchAsset : blockAsset : null);
        var mods = Proxy.Create<IModLoader>((m, _) => m.Name == "IsModEnabled" ? enabled : null);
        var world = Proxy.Create<IServerWorldAccessor>((m, _) => m.Name == "get_Logger" ? logger : null);
        var api = Proxy.Create<ICoreServerAPI>((m, _) => m.Name switch
        {
            "get_Assets" => assets, "get_ModLoader" => mods, "get_Logger" => logger,
            "get_World" => world, "get_Side" => side, _ => null
        });
        var system = new SeaweedPatchCompatibilitySystem();
        var loader = new ModJsonPatchLoader();
        typeof(ModJsonPatchLoader).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(loader, api);
        Check(system.ExecuteOrder() < loader.ExecuteOrder(), "preparation precedes vanilla patch loading");
        Check(!system.ShouldLoad(EnumAppSide.Client) && system.ShouldLoad(EnumAppSide.Server), "server only");
        enabled = false;
        system.AssetsLoaded(api);
        Check(Encoding.UTF8.GetString(patchBytes) == Original, "absent Ashes skipped");
        enabled = true;
        side = EnumAppSide.Client;
        system.AssetsLoaded(api);
        Check(Encoding.UTF8.GetString(patchBytes) == Original, "client assets unchanged");
        side = EnumAppSide.Server;

        void Apply(string json, string source, ref int errorCount)
        {
            int applied = 0, missing = 0;
            JsonPatch[] patches = JsonConvert.DeserializeObject<JsonPatch[]>(json)!;
            for (int i = 0; i < patches.Length; i++)
                loader.ApplyPatch(i, new AssetLocation(source), patches[i], ref applied, ref missing, ref errorCount);
            if (missing != 0) throw new Exception("Missing test asset");
        }

        int failures = 0;
        Apply(Original, "ashes:patches/survival-blocktypes-plant-aquatic-seaweed.json", ref failures);
        Check(failures == 1 && errors.Count != 0, "unmodified Ashes reproduces missing drops error");
        errors.Clear();
        system.AssetsLoaded(api);
        string safeAshes = Encoding.UTF8.GetString(patchBytes);
        system.AssetsLoaded(api);
        Check(Encoding.UTF8.GetString(patchBytes) == safeAshes, "preparation is idempotent");

        string fixes = new JArray(JArray.Parse(File.ReadAllText(Path.Combine(root,
            "assets/vsscpmod/patches/compatibility/valley-of-ashes-long-term-food.json")))
            .Where(p => (string?)p["file"] == "game:blocktypes/plant/aquatic/seaweed.json")).ToString();
        string wild = """[{"op":"remove","path":"/drops","file":"game:blocktypes/plant/aquatic/seaweed.json","side":"Server"}]""";
        string food = """[{"op":"replace","path":"/drops","value":[{"type":"block","code":"seaweed-section","quantity":{"avg":1,"var":0},"tool":"knife"},{"type":"block","code":"seaweed-section","quantity":{"avg":1}}],"file":"game:blocktypes/plant/aquatic/seaweed.json","side":"Server"}]""";
        var groups = new Dictionary<string, string> { ["ashes"] = safeAshes, ["wildfarmingfork"] = wild, ["vsscpmod"] = fixes };
        var resolver = typeof(RegistryObjectType).GetMethod("solveByType", BindingFlags.Static | BindingFlags.NonPublic)!;
        // Long-term Food loads first in the pack. Exercise every ordering of
        // Ashes, Wild Farming and our patch, plus both orders without Wild Farming.
        var orders = new List<string[]>();
        foreach (string first in groups.Keys)
            foreach (string second in groups.Keys.Where(k => k != first))
                orders.Add([first, second, groups.Keys.Single(k => k != first && k != second)]);
        orders.Add(["ashes", "vsscpmod"]);
        orders.Add(["vsscpmod", "ashes"]);
        foreach (string[] order in orders)
        {
            blockBytes = Encoding.UTF8.GetBytes("{\"drops\":[]}");
            failures = 0;
            Apply(food, "game:patches/seaweed.json", ref failures);
            foreach (string group in order) Apply(groups[group], group + ":patches/seaweed.json", ref failures);
            Check(failures == 0 && errors.Count == 0, "no patch errors: " + string.Join(",", order));
            foreach (string part in new[] { "top", "section" })
            {
                var resolved = JObject.Parse(Encoding.UTF8.GetString(blockBytes));
                resolver.Invoke(null, [resolved, "seaweed-" + part, new Vintagestory.API.Datastructures.OrderedDictionary<string, string>()]);
                var drops = (JArray)resolved["drops"]!;
                Check(drops.Count == 3 && drops.Count(d => (string?)d["code"] == "ashes:plantdebris-seaweed") == 1
                    && drops.Count(d => (string?)d["code"] == "seaweed-section") == 2,
                    "preserve food and one debris drop: " + part);
            }
        }
        // Ashes alone must retain existing drops when no Long-term Food fix runs.
        blockBytes = Encoding.UTF8.GetBytes("{\"drops\":[{\"code\":\"other:existing\"}]}");
        failures = 0;
        Apply(safeAshes, "ashes:patches/seaweed.json", ref failures);
        Check(failures == 0 && JArray.Parse(JObject.Parse(Encoding.UTF8.GetString(blockBytes))["drops"]!.ToString()).Count == 2,
            "standalone Ashes preserves existing drops");
        Console.WriteLine($"{count} seaweed patch checks passed.");
    }

    private static object? SetBytes(byte[] bytes, Action<byte[]> write) { write(bytes); return null; }
}
