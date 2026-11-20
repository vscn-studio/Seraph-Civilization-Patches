using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

static class PieBarrelTextureChecks
{
    public static void Run(string game)
    {
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }
        var items = new Dictionary<int, Item>();
        var blocks = new Dictionary<int, Block>();
        var world = Proxy.Create<IClientWorldAccessor>((m, a) => m.Name switch
        {
            "GetItem" => a![0] is int id ? items.GetValueOrDefault(id) : items.Values.FirstOrDefault(i => i.Code.Equals(a[0])),
            "GetBlock" => a![0] is int id ? blocks.GetValueOrDefault(id) : blocks.Values.FirstOrDefault(b => b.Code.Equals(a[0])),
            _ => null
        });
        string AssetFile(AssetLocation location) => new[] { "survival", "game", "creative" }
            .Select(folder => Path.Combine(game, "assets", folder, location.Path)).FirstOrDefault(File.Exists)
            ?? Path.Combine(game, "assets/survival", location.Path);
        var assets = Proxy.Create<IAssetManager>((m, a) => m.Name == "Exists"
            ? ((AssetLocation)a![0]!).Domain == "game" && File.Exists(AssetFile((AssetLocation)a[0]!)) : null);
        AssetLocation? requested = null;
        var atlasPosition = new TextureAtlasPosition();
        var atlas = Proxy.Create<IBlockTextureAtlasAPI>((m, a) =>
        {
            if (m.Name == "GetOrInsertTexture")
            {
                requested = ((AssetLocation)a![0]!).Clone();
                a[1] = 17;
                a[2] = atlasPosition;
                return true;
            }
            return null;
        });
        var logger = Proxy.Create<ILogger>((m, a) =>
        {
            if (m.Name == "Warning") throw new Exception("Unexpected patch warning: " + a![0]);
            return null;
        });
        var api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_Assets" => assets, "get_Logger" => logger, "get_BlockTextureAtlas" => atlas,
            _ => null
        });
        Item Ingredient(string code, string? texture = null)
        {
            var item = new Item { ItemId = items.Count + 1, Code = new AssetLocation(code) };
            if (texture != null) item.Attributes = JsonObject.FromJson("{inPieProperties:{texture:'" + texture + "',partType:'Crust'}}");
            items.Add(item.Id, item);
            return item;
        }
        ItemStack Pie(string state, Item? dough, int count = 1)
        {
            var block = new BlockPie
            {
                BlockId = blocks.Count + 1, Code = new AssetLocation("game:pie-" + state), MaxStackSize = 32,
                Shape = new CompositeShape { Base = new AssetLocation("game:block/basic/invisible") },
                Textures = new Dictionary<string, CompositeTexture> { ["all"] = new(new AssetLocation("game:unknown")) },
                Attributes = JsonObject.FromJson("{}")
            };
            blocks.Add(block.Id, block);
            var stack = new ItemStack(block, count);
            block.SetContents(stack, [dough == null ? null! : new ItemStack(dough, 2), null!, null!, null!, null!, null!]);
            stack.Attributes.SetInt("pieSize", 4);
            return stack;
        }
        void Surface(ItemStack stack, string expected, bool actualPng = true)
        {
            byte[] before = stack.ToBytes();
            var source = Lookup(api, stack, out float fill);
            requested = null;
            Check(ReferenceEquals(source["surface"], atlasPosition) && requested?.Equals(new AssetLocation(expected)) == true,
                stack.Collectible.Code + " selects " + expected + " through the real barrel and atlas lookup");
            Check(Math.Abs(fill - Math.Min(.75f, .7f * stack.StackSize / 32)) < .00001f && before.SequenceEqual(stack.ToBytes()),
                "surface keeps vanilla fill height and serialized food data");
            if (actualPng)
            {
                var file = AssetFile(requested!.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"));
                Check(File.ReadAllBytes(file).AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                    "selected texture exists as a PNG in the actual game assets");
            }
        }
        var spelt = Ingredient("game:dough-spelt", "block/food/pie/spelt{bakeLevel}");
        var sample = Pie("perfect", spelt);
        Surface(sample, "game:unknown", actualPng: false);

        Type type = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly.GetType("SeraphCivilizationPatches.PieBarrelTextureCompatibility")!;
        IDisposable? owner = null;
        IDisposable? replacement = null;
        try
        {
            owner = (IDisposable?)type.GetMethod("TryCreate")!.Invoke(null, [api]);
            Check(owner != null, "client compatibility installs against the actual game DLL");
            var doughJson = JObject.Parse(File.ReadAllText(Path.Combine(game, "assets/survival/itemtypes/food/dough.json")));
            var template = doughJson["attributes"]!["inPieProperties"]!.ToString();
            string[] states = ["raw", "partbaked", "perfect", "charred"];
            foreach (string grain in doughJson["variantgroups"]![0]!["states"]!.Values<string>().Select(g => g!))
            {
                var dough = Ingredient("game:dough-" + grain);
                dough.Attributes = JsonObject.FromJson("{inPieProperties:" + template.Replace("{type}", grain) + "}");
                for (int stage = 0; stage < states.Length; stage++)
                {
                    var pie = Pie(states[stage], dough, stage + 1);
                    pie.Attributes.SetInt("bakeLevel", stage);
                    Surface(pie, "game:block/food/pie/" + grain + (stage + 1));
                }
            }
            Surface(sample, "game:block/food/pie/spelt3"); // Missing bakeLevel uses the block's state.
            Surface(Pie("raw", null), "game:block/food/pie/spelt1");
            Surface(Pie("perfect", Ingredient("mod:dough", "missing/crust{bakelevel}")), "game:block/food/pie/spelt3");
            Surface(Pie("partbaked", Ingredient("mod:dough-explicit", "game:block/food/pie/rye{bakelevel}")), "game:block/food/pie/rye2");
            Surface(Pie("charred", Ingredient("game:rot")), "game:block/wood/barrel/rot");
            var open = Pie("perfect", spelt);
            open.Attributes.SetString("topCrustType", "none");
            open.Attributes.SetInt("pieSize", 1);
            Surface(open, "game:block/food/pie/spelt3");

            var creative = Pie("raw", null);
            creative.Attributes.RemoveAttribute("contents");
            var content = new TreeAttribute();
            content.SetString("type", "item"); content.SetString("code", "game:dough-spelt"); content.SetInt("stackSize", 2);
            creative.Attributes["ucontents"] = new TreeArrayAttribute([content]);
            Surface(creative, "game:block/food/pie/spelt1");

            var overridden = Pie("raw", spelt);
            overridden.Block.Attributes = JsonObject.FromJson("{inContainerTexture:{base:'game:block/food/pie/rye4'}}");
            Surface(overridden, "game:block/food/pie/rye4");
            var itemStack = new ItemStack(Ingredient("game:ordinary"));
            itemStack.Item.Textures = new Dictionary<string, CompositeTexture> { ["all"] = new(new AssetLocation("game:block/wood/barrel/rot")) };
            var ordinary = Lookup(api, itemStack, out float ordinaryFill);
            requested = null;
            _ = ordinary["surface"];
            Check(requested?.Equals(new AssetLocation("game:block/wood/barrel/rot")) == true && ordinaryFill > 0, "non-pie contents use the original barrel path");
            Check(Lookup(api, null!, out float emptyFill) == null && emptyFill == 0, "empty barrels retain the original behavior");

            replacement = (IDisposable?)type.GetMethod("TryCreate")!.Invoke(null, [api]);
            owner!.Dispose();
            Surface(sample, "game:block/food/pie/spelt3");
            replacement!.Dispose();
            Surface(sample, "game:unknown", actualPng: false);
            Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(BlockBarrel), nameof(BlockBarrel.getContentTexture)))?.Owners
                .Contains("vsscpmod.pie-barrel-texture") != true, "disposing the client removes the patch");
        }
        finally { replacement?.Dispose(); owner?.Dispose(); }
        Console.WriteLine($"{checks} pie barrel texture checks passed.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ITexPositionSource Lookup(ICoreClientAPI api, ItemStack stack, out float fill) => BlockBarrel.getContentTexture(api, stack, out fill);
}
