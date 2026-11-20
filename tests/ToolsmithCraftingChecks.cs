using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

static class ToolsmithCraftingChecks
{
    private sealed class ProbeItem : Item
    {
        public void Init(ICoreAPI core) => api = core;
        public override void SetTransitionState(ItemStack stack, EnumTransitionType type, float hours) { }
    }
    private sealed class LiquidBlock : Block, ILiquidInterface
    {
        public bool AllowHeldLiquidTransfer => true;
        public float CapacityLitres => 10;
        public float TransferSizeLitres => 1;
        public float GetCurrentLitres(ItemStack stack) => 1;
        public float GetCurrentLitres(BlockPos pos) => 1;
        public bool IsFull(ItemStack stack) => false;
        public bool IsFull(BlockPos pos) => false;
        public WaterTightContainableProps? GetContentProps(ItemStack stack) => null;
        public WaterTightContainableProps? GetContentProps(BlockPos pos) => null;
        public ItemStack? GetContent(ItemStack stack) => stack.Attributes.GetItemstack("test-content");
        public ItemStack? GetContent(BlockPos pos) => null;
    }

    public static void Run(string root)
    {
        int count = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new Exception("FAIL: " + message);
            Console.WriteLine("PASS: " + message); count++;
        }
        var toolsmith = Assembly.LoadFrom(Path.Combine(root, "tests/dependencies/toolsmith-audit/Toolsmith.dll"));
        var rkn = Assembly.LoadFrom(Path.Combine(root, "tests/dependencies/inventory-diagnosis/rkncrafting_1.4.0/RknCrafting.dll"));
        Type handleType = toolsmith.GetType("Toolsmith.ToolTinkering.Behaviors.CollectibleBehaviorToolHandle", true)!;
        Type systemType = toolsmith.GetType("Toolsmith.ToolsmithModSystem", true)!;
        var statsField = AccessTools.Field(systemType, "Stats");
        var apiField = AccessTools.Field(systemType, "Sapi");
        object? oldApi = apiField.GetValue(null);
        object? oldStats = statsField.GetValue(null);
        object stats = Activator.CreateInstance(statsField.FieldType)!;
        void Add(string table, string type, string key, params (string, object)[] fields)
        {
            object entry = Activator.CreateInstance(toolsmith.GetType("Toolsmith.Config." + type, true)!)!;
            foreach (var (field, value) in fields) AccessTools.Field(entry.GetType(), field).SetValue(entry, value);
            ((IDictionary)AccessTools.Field(stats.GetType(), table).GetValue(stats)!)[key] = entry;
        }
        Add("BaseHandleParts", "HandlePartDefines", "handle", ("handleShapePath", "toolsmith:shapes/item/parts/handles/regular/handle"),
            ("handleStatTag", "handle"), ("dryingTimeMult", 1f));
        Add("TreatmentParts", "TreatmentPartDefines", "fat", ("treatmentStatTag", "fat"), ("dryingHours", 12));
        Add("TreatmentStats", "TreatmentStatDefines", "fat", ("id", "fat"));
        Add("GripParts", "GripPartDefines", "twine", ("gripStatTag", "twine"),
            ("gripShapePath", "fabric"), ("gripTextureOverride", "game:item/resource/string"));
        Add("GripStats", "GripStatDefines", "twine", ("id", "twine"));
        statsField.SetValue(null, stats);
        var warnings = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) => {
            if (m.Name == "Warning") warnings.Add(a is [string format, object[] args] ? string.Format(format, args) : string.Join(" ", a ?? []));
            return null;
        });
        Type rknSystemType = rkn.GetType("RKN.Crafting.RknCraftingModSystem", true)!;
        object rknSystem = Activator.CreateInstance(rknSystemType)!;
        rknSystemType.GetProperty("ServerConfig")!.SetValue(rknSystem,
            Activator.CreateInstance(rknSystemType.GetProperty("ServerConfig")!.PropertyType));
        var enabled = new HashSet<string> { "toolsmith", "rkncrafting" };
        var loader = Proxy.Create<IModLoader>((m, a) => m.Name switch { "IsModEnabled" => enabled.Contains((string)a![0]!), "GetModSystem" => rknSystem, _ => null });
        var events = Proxy.Create<IEventAPI>((m, _) => m.Name == "TriggerMatchesRecipe" ? true : null);
        var world = Proxy.Create<IWorldAccessor>((_, _) => null);
        var registry = Proxy.Create<IClassRegistryAPI>((_, _) => null);
        var assets = Proxy.Create<IAssetManager>((m, _) => m.Name == "Exists" ? false : null);
        ICoreAPI Api(EnumAppSide side) => Proxy.Create<ICoreServerAPI>((m, _) => m.Name switch
        {
            "get_Side" => side, "get_ModLoader" => loader, "get_Logger" => logger, "get_Event" => events,
            "get_World" => world, "get_ClassRegistry" => registry, "get_Assets" => assets, _ => null
        });
        ICoreAPI api = Api(EnumAppSide.Server);
        apiField.SetValue(null, api);
        ProbeItem Item(string code, int id)
        {
            var item = new ProbeItem { Code = new(code), ItemId = id, MaxStackSize = 64 };
            item.Init(api); return item;
        }
        var handle = Item("toolsmith:handle", 1);
        handle.CollectibleBehaviors = [(CollectibleBehavior)Activator.CreateInstance(handleType, [handle])!];
        var fat = Item("game:fat", 2); var invalid = Item("game:stone-granite", 3);
        var sandpaper = Item("toolsmith:sandpaper", 4); var firewood = Item("game:firewood", 5);
        var twine = Item("game:twine", 6);
        var knife = Item("game:knife-flint", 7); knife.Tool = EnumTool.Knife;
        var beam = Item("game:supportbeam-oak", 8);
        var container = new LiquidBlock { Code = new("game:woodbucket"), BlockId = 1 };
        AccessTools.Field(typeof(CollectibleObject), "api").SetValue(container, api);
        Type serviceType = rkn.GetType("RKN.Crafting.RecipeService", true)!;
        Type inputType = rkn.GetType("RKN.Crafting.RecipeInputSlots", true)!;
        Type wrapperType = rkn.GetType("RKN.Crafting.GridRecipeWrapper", true)!;
        object service = RuntimeHelpers.GetUninitializedObject(serviceType);
        AccessTools.Field(serviceType, "api").SetValue(service, api);
        MethodInfo match = AccessTools.Method(serviceType, "MatchesRecipe");
        MethodInfo create = AccessTools.Method(serviceType, "CreateResult");
        var recipe = new GridRecipe { Shapeless = true, Width = 2, Height = 1, AverageDurability = false,
            Name = new("test:handle-treatment"), Output = new() { ResolvedItemStack = new ItemStack(handle) } };
        var slots = Enumerable.Range(0, 9).Select(_ => (ItemSlot)new DummySlot()).ToArray();
        object wrapper = RuntimeHelpers.GetUninitializedObject(wrapperType);
        AccessTools.Field(wrapperType, "RecipeWithTools").SetValue(wrapper, recipe);
        AccessTools.Field(wrapperType, "RecipeWithoutTools").SetValue(wrapper, recipe);
        AccessTools.Field(wrapperType, "ToolIngredients").SetValue(wrapper, new List<CraftingRecipeIngredient>());
        object input = Activator.CreateInstance(inputType, [slots, null, null, null])!;
        void Materials(params ItemStack[] stacks)
        {
            for (int i = 0; i < slots.Length; i++) slots[i].Itemstack = i < stacks.Length ? stacks[i] : null;
            recipe.ResolvedIngredients = stacks.Select(s => new CraftingRecipeIngredient
                { Type = s.Class, Code = s.Collectible.Code, Quantity = 1, ResolvedItemStack = s.Clone() }).ToArray();
        }
        void Inputs(ItemStack other)
        {
            var source = new ItemStack(handle); source.Attributes.SetString("custom-data", "preserved");
            Materials(source, other);
        }
        bool Matches() => (bool)match.Invoke(service, [input, wrapper, true, null])!;
        ItemStack Generate()
        {
            object result = create.Invoke(service, [wrapper, null, input, null, true])!;
            return (ItemStack)result.GetType().GetProperty("SelectionItemStack")!.GetValue(result)!;
        }
        Type compatibility = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly
            .GetType("SeraphCivilizationPatches.ToolsmithCraftingCompatibility", true)!;
        IDisposable? server = null, client = null, replacement = null, rknFix = null, toolsmithOnly = null;
        try
        {
            Inputs(new ItemStack(invalid));
            Check(Matches(), "original RKN accepts a recipe with an unconfigured treatment");
            bool crashed = false;
            try { Generate(); }
            catch (TargetInvocationException e) when (e.InnerException is NullReferenceException n
                && n.StackTrace!.Contains("CollectibleBehaviorToolHandle.OnCreatedByCrafting")) { crashed = true; }
            Check(crashed, "real Toolsmith 1.2.19 + RKN 1.4.0 reproduce the reported null reference");
            Type existingRkn = compatibility.Assembly.GetType("SeraphCivilizationPatches.RknCraftingCompatibility", true)!;
            rknFix = (IDisposable?)existingRkn.GetMethod("TryCreate")!.Invoke(null, [api]);
            Check(rknFix != null, "existing RKN durability patch installs alongside new compatibility");
            server = (IDisposable?)compatibility.GetMethod("TryCreate")!.Invoke(null, [api]);
            Check(server != null && !warnings.Any(w => w.Contains("unavailable")), "Toolsmith compatibility installs");
            Check(!Matches(), "unsafe recipe rejected before output generation");
            Check(slots[0].StackSize == 1 && slots[1].StackSize == 1
                && !slots[0].Itemstack!.Attributes.HasAttribute("modularMultiPartRenderData"), "rejection does not consume or modify inputs");
            int warningCount = warnings.Count;
            Matches(); Check(warnings.Count == warningCount, "repeated hover does not repeat diagnostic warnings");
            foreach (var other in new[] { new ItemStack(fat), new ItemStack(twine) })
            {
                Inputs(other); Check(Matches(), "valid treatment/grip recipe remains available: " + other.Collectible.Code);
                var result = Generate();
                Check(result.Attributes.GetString(other.Collectible == fat ? "toolHandleTreatmentTag" : "toolHandleGripTag")
                    == (other.Collectible == fat ? "fat" : "twine"), "real callback generates treatment/grip attributes");
                Check(result.Attributes.GetString("custom-data") == "preserved"
                    && !slots[0].Itemstack!.Attributes.HasAttribute("toolHandleGripTag")
                    && !slots[0].Itemstack!.Attributes.HasAttribute("toolHandleTreatmentTag"), "copies handle data without modifying source");
            }
            Inputs(new ItemStack(twine)); slots[0].Itemstack!.Attributes.SetString("toolHandleGripTag", "twine");
            Check(!Matches(), "duplicate grip cannot create disposable air output");
            Materials(new ItemStack(knife), new ItemStack(beam), new ItemStack(sandpaper));
            Check(Matches() && Generate().Attributes.GetString("toolHandleTag") == "handle",
                "knife, beam and sandpaper still generate original wood handle stats");
            Materials(new ItemStack(knife), new ItemStack(firewood));
            Check(Matches() && Generate().Collectible == handle, "knife and firewood crude handle recipe remains available");
            Inputs(new ItemStack(fat));
            var held = new DummySlot(new ItemStack(invalid));
            input = Activator.CreateInstance(inputType, [slots, null, held, null])!;
            Check(Matches() && Generate().Attributes.GetString("toolHandleTreatmentTag") == "fat",
                "existing RKN filter prevents unrelated held item from becoming a treatment");
            Check(held.Itemstack?.Collectible == invalid && held.StackSize == 1, "held item remains untouched");
            input = Activator.CreateInstance(inputType, [slots, null, null, null])!;
            Inputs(new ItemStack(fat)); slots[0].Itemstack!.Attributes.SetString("toolHandleTreatmentTag", "fat");
            Check(!Matches(), "duplicate treatment cannot create disposable air output");
            Inputs(new ItemStack(container)); Check(!Matches(), "empty liquid container is rejected");
            slots[1].Itemstack!.Attributes.SetItemstack("test-content", new ItemStack(invalid));
            Check(!Matches(), "unconfigured liquid contents are rejected");
            slots[1].Itemstack!.Attributes.SetItemstack("test-content", new ItemStack(fat));
            Check(Matches() && Generate().Attributes.GetString("toolHandleTreatmentTag") == "fat", "configured liquid contents use original Toolsmith callback");
            foreach (var excluded in new[] { sandpaper, firewood })
            {
                Inputs(new ItemStack(excluded));
                Check(Matches() && Generate().Attributes.GetString("custom-data") == "preserved",
                    "ignored material no longer becomes a treatment: " + excluded.Code);
                Check(slots[1].Itemstack!.Collectible == excluded && slots[1].StackSize == 1, "filter preserves live material slot");
            }
            Inputs(new ItemStack(fat));
            ((IDictionary)AccessTools.Field(stats.GetType(), "TreatmentStats").GetValue(stats)!).Clear();
            Check(!Matches(), "missing treatment stats are rejected");
            client = (IDisposable?)compatibility.GetMethod("TryCreate")!.Invoke(null, [Api(EnumAppSide.Client)]);
            replacement = (IDisposable?)compatibility.GetMethod("TryCreate")!.Invoke(null, [Api(EnumAppSide.Client)]);
            client!.Dispose(); server!.Dispose();
            Check(!Matches(), "replacement remains active after old client and integrated server disposal");
            replacement!.Dispose();
            Check(Harmony.GetPatchInfo(match)?.Owners.Contains("vsscpmod.toolsmith-crafting") != true,
                "last disposal restores original RKN matching");
            Check(Matches(), "original matching restored after cleanup");
            enabled.Remove("rkncrafting");
            toolsmithOnly = (IDisposable?)compatibility.GetMethod("TryCreate")!.Invoke(null, [api]);
            Inputs(new ItemStack(sandpaper));
            Check(toolsmithOnly != null && Generate().Attributes.GetString("custom-data") == "preserved",
                "Toolsmith ingredient correction also installs without RKN enabled");
            Check(Harmony.GetPatchInfo(match)?.Owners.Contains("vsscpmod.toolsmith-crafting") != true,
                "disabled RKN receives no new matching patch");
            toolsmithOnly!.Dispose(); enabled.Remove("toolsmith");
            Check(compatibility.GetMethod("TryCreate")!.Invoke(null, [api]) == null,
                "disabled Toolsmith installs no compatibility");
            Console.WriteLine($"{count} Toolsmith crafting checks passed.");
        }
        finally {
            toolsmithOnly?.Dispose(); replacement?.Dispose(); client?.Dispose(); server?.Dispose(); rknFix?.Dispose();
            statsField.SetValue(null, oldStats); apiField.SetValue(null, oldApi);
        }
    }
}
