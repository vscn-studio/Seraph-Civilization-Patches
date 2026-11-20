using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

static class RknDurabilityChecks
{
    private sealed class ProbeItem : Item { public void Init(ICoreAPI core) => api = core; }
    private sealed class RepairSlot(ItemStack stack) : DummySlot(stack)
    {
        public int DirtyCount;
        public override void MarkDirty() => DirtyCount++;
    }

    public static void CheckCommand(Type compatibility, object instance,
        System.Func<IPlayerInventoryManager, IPlayer> playerFactory, Action<bool, string> check)
    {
        OnCommandDelegate? handler = null;
        bool requiresPlayer = false;
        string? privilege = null, commandName = null;
        IChatCommand command = null!;
        command = Proxy.Create<IChatCommand>((m, a) =>
        {
            if (m.Name == "HandleWith") handler = (OnCommandDelegate)a![0]!;
            if (m.Name == "RequiresPlayer") requiresPlayer = true;
            if (m.Name == "RequiresPrivilege") privilege = (string)a![0]!;
            return command;
        });
        var commands = Proxy.Create<IChatCommandApi>((m, a) =>
        {
            if (m.Name == "GetOrCreate") commandName = (string)a![0]!;
            return command;
        });
        var server = Proxy.Create<ICoreServerAPI>((m, _) => m.Name == "get_ChatCommands" ? commands : null);
        compatibility.GetMethod("RegisterRecoveryCommand")!.Invoke(instance, [server]);
        check(commandName == "vsscpmod" && requiresPlayer && privilege == Privilege.chat && handler != null,
            "recovery command targets the calling player and is available without admin privileges");
        Lang.AvailableLanguages["en"] = Proxy.Create<ITranslationService>((m, a) => m.Name switch
        {
            "HasTranslation" => true, "Get" => a![0], _ => null
        });
        Lang.ChangeLanguage("en");
        var item = new ProbeItem { Code = new("diagnosis:ordinary"), ItemId = 2, Durability = 0, MaxStackSize = 64 };
        var stack = new ItemStack(item, 16); stack.Attributes.SetInt("durability", 1);
        var slot = new RepairSlot(stack);
        var manager = Proxy.Create<IPlayerInventoryManager>((m, _) => m.Name == "get_ActiveHotbarSlot" ? slot : null);
        var player = playerFactory(manager);
        var caller = new Caller();
        AccessTools.Field(typeof(Caller), "player").SetValue(caller, player);
        var args = new TextCommandCallingArgs { LanguageCode = "en", Caller = caller };
        check(handler!(args).Status == EnumCommandStatus.Success && slot.DirtyCount == 1
            && !stack.Attributes.HasAttribute("durability") && slot.StackSize == 16,
            "recovery command repairs the held stack and marks its slot for server synchronization");
        check(handler(args).Status == EnumCommandStatus.Error && slot.DirtyCount == 1,
            "recovery command leaves an already normal stack untouched");
        slot.Itemstack = null;
        check(handler(args).Status == EnumCommandStatus.Error && slot.DirtyCount == 1,
            "recovery command handles an empty hand without changing inventory");
    }

    public static void Run(Assembly rkn, Type compatibility, bool patched, Action<bool, string> check)
    {
        Type serviceType = rkn.GetType("RKN.Crafting.RecipeService", true)!;
        Type inputType = rkn.GetType("RKN.Crafting.RecipeInputSlots", true)!;
        Type wrapperType = rkn.GetType("RKN.Crafting.GridRecipeWrapper", true)!;
        Type systemType = rkn.GetType("RKN.Crafting.RknCraftingModSystem", true)!;
        object system = Activator.CreateInstance(systemType)!;
        systemType.GetProperty("ServerConfig")!.SetValue(system,
            Activator.CreateInstance(systemType.GetProperty("ServerConfig")!.PropertyType));
        var loader = Proxy.Create<IModLoader>((m, _) => m.Name == "GetModSystem" ? system : null);
        var world = Proxy.Create<IWorldAccessor>((_, _) => null);
        var registry = Proxy.Create<IClassRegistryAPI>((_, _) => null);
        var api = Proxy.Create<ICoreAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_ClassRegistry" => registry, "get_ModLoader" => loader, _ => null
        });
        var material = new ProbeItem { Code = new("game:stick"), ItemId = 1, Durability = 0, MaxStackSize = 64 };
        var output = new ProbeItem { Code = new("diagnosis:ordinary"), ItemId = 2, Durability = 0, MaxStackSize = 64 };
        var axe = new ProbeItem { Code = new("game:axe-copper"), ItemId = 3, Durability = 100, Tool = EnumTool.Axe };
        var saw = new ProbeItem { Code = new("game:saw-copper"), ItemId = 4, Durability = 100, Tool = EnumTool.Saw };
        foreach (var item in new[] { material, output, axe, saw }) item.Init(api);
        object service = RuntimeHelpers.GetUninitializedObject(serviceType);
        AccessTools.Field(serviceType, "api").SetValue(service, api);
        var recipe = new GridRecipe
        {
            AverageDurability = true,
            Output = new CraftingRecipeIngredient { ResolvedItemStack = new ItemStack(output) },
            ResolvedIngredients = []
        };
        object wrapper = RuntimeHelpers.GetUninitializedObject(wrapperType);
        AccessTools.Field(wrapperType, "RecipeWithTools").SetValue(wrapper, recipe);
        AccessTools.Field(wrapperType, "RecipeWithoutTools").SetValue(wrapper, recipe);
        var tools = new List<CraftingRecipeIngredient>();
        AccessTools.Field(wrapperType, "ToolIngredients").SetValue(wrapper, tools);
        MethodInfo grid = AccessTools.Method(serviceType, "GetIngredientGridWithTools");
        MethodInfo create = AccessTools.Method(serviceType, "CreateResult");
        ItemStack Worn(Item item) { var stack = new ItemStack(item); stack.Attributes.SetInt("durability", 50); return stack; }
        var materials = Enumerable.Range(0, 9).Select(_ => (ItemSlot)new DummySlot()).ToArray();
        materials[0].Itemstack = new ItemStack(material);
        ItemStack Generate(ItemSlot? main, ItemSlot? off, out ItemSlot[] inputs)
        {
            object input = Activator.CreateInstance(inputType, [materials, null, main, off])!;
            inputs = (ItemSlot[])grid.Invoke(service, [wrapper, input])!;
            object result = create.Invoke(service, [wrapper, null, input, null, true])!;
            return (ItemStack)result.GetType().GetProperty("SelectionItemStack")!.GetValue(result)!;
        }
        foreach (string scenario in new[] { "empty hands", "full tool", "worn main hand", "worn off hand" })
        {
            var held = new DummySlot(scenario == "full tool" ? new ItemStack(axe) : Worn(axe));
            ItemStack stack = Generate(scenario is "full tool" or "worn main hand" ? held : null,
                scenario == "worn off hand" ? held : null, out var inputs);
            bool buggy = !patched && scenario.StartsWith("worn");
            check(output.ShouldDisplayItemDamage(stack) == buggy,
                $"{(patched ? "fixed" : "original")} RKN {scenario}: damage bar {(buggy ? "reproduced" : "absent")}");
            check((output.GetMergableQuantity(new ItemStack(output), stack, EnumMergePriority.DirectMerge) > 0) == !buggy,
                $"{(patched ? "fixed" : "original")} RKN {scenario}: stack merge {(buggy ? "blocked" : "allowed")}");
            if (patched) check(inputs.Length == 9 && !stack.Attributes.HasAttribute("durability"),
                "unrelated hand excluded without inventing durability: " + scenario);
        }
        if (!patched) return;

        // A matching required tool remains available to mod crafting hooks;
        // the unrelated second hand cannot affect a durable output either.
        var required = new CraftingRecipeIngredient
        {
            Type = EnumItemClass.Item, Code = axe.Code, IsTool = true,
            ResolvedItemStack = new ItemStack(axe), Quantity = 1
        };
        tools.Add(required);
        recipe.ResolvedIngredients = [required];
        output.Durability = 100;
        var requiredSlot = new DummySlot(Worn(axe));
        foreach (bool requiredInMain in new[] { true, false })
        {
            var unrelatedSlot = new DummySlot(Worn(saw));
            var stack = Generate(requiredInMain ? requiredSlot : unrelatedSlot,
                requiredInMain ? unrelatedSlot : requiredSlot, out var inputs);
            check(inputs.Length == 10 && ReferenceEquals(inputs[9], requiredSlot),
                "keeps the actual required tool slot in " + (requiredInMain ? "main hand" : "off hand"));
            check(output.GetRemainingDurability(stack) == 100 && !stack.Attributes.HasAttribute("durability"),
                "required tool wear and unrelated hand do not reduce output tool durability");
        }
        // A worn item consumed as material must still transfer its real wear.
        tools.Clear(); recipe.ResolvedIngredients = [];
        materials[0].Itemstack = Worn(axe);
        var inherited = Generate(null, new DummySlot(Worn(saw)), out var materialInputs);
        check(ReferenceEquals(materialInputs[0], materials[0]) && output.GetRemainingDurability(inherited) == 50,
            "consumed worn materials retain vanilla durability inheritance");

        MethodInfo repair = AccessTools.Method(compatibility, "RepairPhantomDurability");
        bool Repair(ItemStack? stack) => (bool)repair.Invoke(null, [stack])!;
        output.Durability = 0;
        var broken = new ItemStack(output, 32);
        broken.Attributes.SetInt("durability", 1);
        broken.Attributes.SetString("custom-data", "preserved");
        var contents = new TreeAttribute(); contents.SetInt("count", 7);
        broken.Attributes["contents"] = contents;
        var normal = broken.Clone(); normal.Attributes.RemoveAttribute("durability");
        check(Repair(broken) && broken.StackSize == 32 && broken.Attributes.GetString("custom-data") == "preserved"
            && ReferenceEquals(broken.Attributes["contents"], contents), "repair preserves stack quantity and all other attributes");
        check(!output.ShouldDisplayItemDamage(broken)
            && output.GetMergableQuantity(normal, broken, EnumMergePriority.DirectMerge) == 32,
            "repaired existing stack loses its bar and merges with an otherwise identical stack");
        check(!Repair(broken) && !Repair(null), "repair is idempotent and accepts an empty hand");
        foreach (int durability in new[] { 0, 2, -1 })
        {
            var other = new ItemStack(output); other.Attributes.SetInt("durability", durability);
            check(!Repair(other) && other.Attributes.GetInt("durability") == durability,
                "repair does not change other durability signatures: " + durability);
        }
        var realTool = Worn(axe); realTool.Attributes.SetInt("durability", 1);
        check(!Repair(realTool) && realTool.Attributes.GetInt("durability") == 1, "repair never resets real tool wear");
        output.Durability = 100;
        var realDurable = new ItemStack(output); realDurable.Attributes.SetInt("durability", 1);
        check(!Repair(realDurable), "repair preserves durable non-tool items");
    }
}
