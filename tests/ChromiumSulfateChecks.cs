using System.IO.Compression;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

static class ChromiumSulfateChecks
{
    public static void Run(string game, string mods, string? kometDll = null)
    {
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }
        Item Liquid(int id, string code)
        {
            var item = (Item)Activator.CreateInstance(typeof(CookingRecipe).Assembly.GetType("Vintagestory.GameContent.ItemLiquidPortion")!)!;
            item.ItemId = id;
            item.Code = new(code);
            item.Attributes = JsonObject.FromJson("{waterTightContainerProps:{containable:true,itemsPerLitre:100}}");
            return item;
        }
        byte[] ReadModEntry(string pattern, string entryName)
        {
            using var zip = ZipFile.OpenRead(Directory.GetFiles(mods, pattern).Single());
            using var stream = zip.GetEntry(entryName)!.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        var registry = new RecipeRegistrySystem();
        var recipes = JsonConvert.DeserializeObject<CookingRecipe[]>(File.ReadAllText(Path.Combine(game, "assets/survival/recipes/cooking/sulfate.json")))!;
        registry.CookingRecipes.AddRange(recipes);
        bool canvasEnabled = false;
        var loader = Proxy.Create<IModLoader>((m, a) => m.Name switch
        {
            "GetModSystem" => registry,
            "IsModEnabled" => canvasEnabled && (string)a![0]! == "vintagecanvas",
            _ => null
        });
        var logger = Proxy.Create<ILogger>((m, a) =>
        {
            if (m.Name == "Warning") throw new Exception("Unexpected warning: " + a![0]);
            return null;
        });
        var calendar = Proxy.Create<IGameCalendar>((_, _) => 0d);
        var cache = new Dictionary<string, object>();
        var blocks = new Dictionary<string, Block>();
        var items = new Dictionary<string, Item>();
        var random = new Random(42);
        ICoreAPI api = null!;
        var world = Proxy.Create<IWorldAccessor>((m, a) => m.Name switch
        {
            "get_Api" => api, "get_Calendar" => calendar, "get_Side" => EnumAppSide.Server, "get_Rand" => random,
            "GetBlock" => blocks.GetValueOrDefault(a![0]!.ToString()!),
            "GetItem" => items.GetValueOrDefault(a![0]!.ToString()!), _ => null
        });
        api = Proxy.Create<ICoreAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_ModLoader" => loader, "get_Logger" => logger,
            "get_ObjectCache" => cache, "get_Side" => EnumAppSide.Server, _ => null
        });
        var acid = Liquid(1, "game:acid-full-sulfuric");
        var mineral = new Item { ItemId = 2, Code = new("game:crushed-chromite") };
        var powdered = new Item { ItemId = 3, Code = new("em:powdered-ore-chromite") };
        var sulfate = Liquid(4, "game:sulfate-full-chromite");
        var recipe = recipes.Single(r => r.Code == "sulfate-full-chromite");
        foreach (var ingredient in recipe.Ingredients!)
        {
            ingredient.world = world;
            foreach (var stack in ingredient.ValidStacks)
                stack.ResolvedItemstack = new ItemStack(stack.Code.Path.Contains("acid") ? acid : mineral);
        }
        recipe.CooksInto!.ResolvedItemstack = new ItemStack(sulfate, 100);
        var pot = new BlockCookingContainer
        {
            BlockId = 1, Code = new("game:dirtyclaypot-blue-empty"), MaxServingSize = 6,
            Attributes = JsonObject.FromJson("{isDirtyPot:true,servingCapacity:6,maxTemperature:200,cookingContainerSlots:4,dirtiedBlockCode:'game:dirtyclaypot-blue-cooked'}"),
            Variant = new(new Dictionary<string, string> { ["color"] = "blue", ["type"] = "empty" })
        };
        var cooked = new BlockCookedContainer { BlockId = 2, Code = new("game:dirtyclaypot-blue-cooked"), Attributes = JsonObject.FromJson("{}") };
        blocks[cooked.Code.ToString()] = cooked;
        foreach (var item in new[] { acid, mineral, powdered, sulfate }) items[item.Code.ToString()] = item;
        foreach (var collectible in new CollectibleObject[] { acid, mineral, powdered, sulfate, pot, cooked })
            AccessTools.Field(typeof(CollectibleObject), "api").SetValue(collectible, api);

        var firepit = new BlockEntityFirepit { Api = api, furnaceTemperature = 230 };
        var inventory = (InventorySmelting)firepit.Inventory;
        inventory.Api = api;
        void Fill(int servings = 6)
        {
            inventory[1].Itemstack = new ItemStack(pot);
            inventory[3].Itemstack = new ItemStack(acid, servings * 100);
            inventory[4].Itemstack = new ItemStack(acid, servings * 100);
            inventory[5].Itemstack = new ItemStack(mineral, servings);
            inventory[6].Itemstack = new ItemStack(mineral, servings);
            firepit.InputStackTemp = 20;
            firepit.inputStackCookingTime = 0;
        }
        float Temperature() => pot.GetMeltingPoint(world, inventory, firepit.inputSlot);
        var fixType = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly.GetType("SeraphCivilizationPatches.ChromiumSulfateCookingCompatibility")!;
        IDisposable? Install() => (IDisposable?)fixType.GetMethod("TryCreate")!.Invoke(null, [api]);
        Fill();
        Check(Temperature() == 100 && firepit.canSmeltInput(), "vanilla valid 6 L sulfate cooks at 100 C");
        Check(Install() == null, "optional fix skips worlds without VintageCanvas");

        var harmony = new Harmony("vsscpmod.tests.chromium-sulfate.dependencies");
        IDisposable? fix = null;
        try
        {
            var komet = (kometDll == null ? Assembly.Load(ReadModEntry("Komet-*.zip", "Komet.dll"))
                : Assembly.LoadFrom(kometDll)).GetType("Komet.World.CookingMatch")!;
            komet.GetMethod("Install", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [harmony, null, 5130506542444273785UL]);
            Check((bool)komet.GetProperty("Rewritten")!.GetValue(null)!, "actual Komet cooking optimization installed");
            Check(Temperature() == 100 && firepit.canSmeltInput(), "Komet alone preserve sulfate cooking");

            var patch = JArray.Parse(System.Text.Encoding.UTF8.GetString(ReadModEntry("VintageCanvas-*.zip", "assets/vintagecanvas/patches/combustiblerecipes.json")));
            mineral.CombustibleProps = patch[0]["value"]!["*-chromite"]!.ToObject<CombustibleProperties>();
            canvasEnabled = true;
            Check(Temperature() == 1000 && firepit.canSmeltInput(), "actual VintageCanvas patch reproduces valid preview but 1000 C requirement");
            for (int i = 0; i < 6000; i++) firepit.heatInput(.1f);
            Check(firepit.InputStackTemp == 200 && firepit.inputStackCookingTime == 0,
                "230 C fire and 200 C dirty pot remain at zero progress for 600 simulated seconds");
            fix = Install();
            Check(fix != null && Temperature() == 100, "production fix restores valid sulfate requirement to 100 C");

            for (int servings = 1; servings <= 6; servings++)
            {
                Fill(servings);
                Check(Temperature() == 100 && firepit.canSmeltInput(), $"valid {servings} L recipe accepted");
                for (int i = 0; i < 6000 && firepit.inputStackCookingTime <= firepit.maxCookingTime(); i++) firepit.heatInput(.1f);
                Check(firepit.inputStackCookingTime > firepit.maxCookingTime() && firepit.InputStackTemp <= 200,
                    $"{servings} L progresses to completion under original pot limit");
                pot.DoSmelt(world, inventory, firepit.inputSlot, firepit.outputSlot);
                Check(firepit.inputStack?.Collectible == cooked && inventory[3].Itemstack?.Collectible == sulfate
                    && inventory[3].StackSize == servings * 100 && inventory[4].Empty && inventory[5].Empty && inventory[6].Empty,
                    $"{servings} L output and complete ingredient consumption verified by game DoSmelt");
            }
            Fill();
            inventory[6].Itemstack!.StackSize = 5;
            Check(Temperature() == 1000 && !firepit.canSmeltInput(), "unequal quantities are not made cookable");
            Fill(7);
            Check(Temperature() == 1000 && !firepit.canSmeltInput(), "over-capacity recipe is not overridden");
            Fill();
            inventory[5].Itemstack = new ItemStack(powdered, 6);
            inventory[6].Itemstack = new ItemStack(powdered, 6);
            Check(!firepit.canSmeltInput(), "unaccepted powdered ore is not granted a recipe");
            Fill();
            recipe.Code = "another-recipe";
            Check(Temperature() == 1000, "unrelated cooking recipe keeps original required temperature");
            recipe.Code = "sulfate-full-chromite";
            recipe.CooksInto.ResolvedItemstack = new ItemStack(acid, 100);
            Check(Temperature() == 1000, "different output with reused recipe code is not overridden");
            recipe.CooksInto.ResolvedItemstack = new ItemStack(sulfate, 100);
            Check(mineral.GetMeltingPoint(world, inventory, new DummySlot(new ItemStack(mineral))) == 1000
                && mineral.CombustibleProps!.SmeltedStack.Code.ToString() == "vintagecanvas:pigment-viridian",
                "direct chromite pigment smelting retains 1000 C and viridian output");
            using (var replacement = Install())
            {
                fix!.Dispose();
                Check(replacement != null && Temperature() == 100, "replacement survives disposal of previous owner");
            }
            Check(Temperature() == 1000, "dispose restores original cooking behavior");
        }
        finally
        {
            fix?.Dispose();
            harmony.UnpatchAll(harmony.Id);
        }
        Console.WriteLine($"Chromium sulfate checks passed: {checks}");
    }
}
