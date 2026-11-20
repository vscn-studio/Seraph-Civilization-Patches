using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace SeraphCivilizationPatches;

/// <summary>
/// RKN keeps its crafting state alive when the active hotbar slot changes.
/// The normal block-interaction stop callback is not guaranteed to run for a
/// wheel switch, so close the state from the block entity tick as a fallback.
/// </summary>
internal sealed class RknCraftingCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.rkn-crafting";
    private readonly Harmony harmony = new(HarmonyId);
    private readonly ICoreAPI api;
    private readonly Type surfaceType;
    private readonly FieldInfo craftingParams;
    private readonly FieldInfo player;
    private readonly MethodInfo cancelCrafting;
    private readonly MethodInfo serverStopCrafting;
    private readonly PropertyInfo inputItems;
    private readonly FieldInfo toolIngredients;
    private readonly ConditionalWeakTable<object, Snapshot> snapshots = new();

    private bool loggedUnfinishedTooltipWarning;
    private bool disposed;
    private static readonly Dictionary<EnumAppSide, RknCraftingCompatibility> instances = [];

    private RknCraftingCompatibility(ICoreAPI api, Type surfaceType)
    {
        this.api = api;
        this.surfaceType = surfaceType;
        craftingParams = AccessTools.Field(surfaceType, "craftingParams")
            ?? throw new MissingMemberException(surfaceType.FullName, "craftingParams");
        Type paramsType = craftingParams.FieldType;
        player = AccessTools.Field(paramsType, "Player")
            ?? throw new MissingMemberException(paramsType.FullName, "Player");
        cancelCrafting = AccessTools.Method(surfaceType, "CancelCrafting")
            ?? throw new MissingMethodException(surfaceType.FullName, "CancelCrafting");
        serverStopCrafting = AccessTools.Method(surfaceType, "ServerStopCrafting")
            ?? throw new MissingMethodException(surfaceType.FullName, "ServerStopCrafting");
        Type inputType = surfaceType.Assembly.GetType("RKN.Crafting.RecipeInputSlots", true)!;
        inputItems = AccessTools.Property(inputType, "Items")
            ?? throw new MissingMemberException(inputType.FullName, "Items");
        Type wrapperType = surfaceType.Assembly.GetType("RKN.Crafting.GridRecipeWrapper", true)!;
        toolIngredients = AccessTools.Field(wrapperType, "ToolIngredients")
            ?? throw new MissingMemberException(wrapperType.FullName, "ToolIngredients");
    }

    public static RknCraftingCompatibility? TryCreate(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("rkncrafting")) return null;
        Type? type = AccessTools.TypeByName("RKN.Crafting.Entities.BlockEntityCraftingSurface");
        if (type == null) return null;

        RknCraftingCompatibility? compatibility = null;
        try
        {
            compatibility = new RknCraftingCompatibility(api, type);
            if (instances.Remove(api.Side, out var previous)) previous.disposed = true;
            if (instances.Count == 0)
            {
                compatibility.harmony.UnpatchAll(HarmonyId);
                compatibility.Install();
            }
            instances[api.Side] = compatibility;
            api.Logger.Notification("[vsscpmod] RKN crafting hotbar-switch recovery and unrelated-tool durability fix enabled.");
            return compatibility;
        }
        catch (Exception exception)
        {
            if (instances.Count == 0) compatibility?.harmony.UnpatchAll(HarmonyId);
            api.Logger.Warning("[vsscpmod] RKN crafting compatibility unavailable: {0}", exception.GetBaseException().Message);
            return null;
        }
    }

    private void Install()
    {
        // Resolve every target before installing anything. RKN uses both
        // RKN.Crafting and RknCrafting namespaces within the same assembly.
        Type? unfinishedCraftType = surfaceType.Assembly.GetType("RknCrafting.Entities.ItemUnfinishedCraft")
            ?? surfaceType.Assembly.GetType("RKN.Crafting.ItemUnfinishedCraft");
        MethodInfo getHeldItemInfo = RequireMethod(unfinishedCraftType, "GetHeldItemInfo",
            [typeof(ItemSlot), typeof(StringBuilder), typeof(IWorldAccessor), typeof(bool)]);
        MethodInfo getHeldItemName = RequireMethod(unfinishedCraftType, "GetHeldItemName", [typeof(ItemStack)]);
        MethodInfo start = RequireMethod(surfaceType, "StartCrafting", [typeof(IPlayer)]);
        MethodInfo clientStart = RequireMethod(surfaceType, "ClientStartedCrafting");
        MethodInfo tick = RequireMethod(surfaceType, "OnTick", [typeof(float)]);
        MethodInfo step = RequireMethod(surfaceType, "OnCraftingStep", [typeof(float), typeof(IPlayer)]);
        MethodInfo ingredientGrid = RequireMethod(surfaceType.Assembly.GetType("RKN.Crafting.RecipeService"),
            "GetIngredientGridWithTools");

        harmony.Patch(start,
            postfix: new HarmonyMethod(typeof(RknCraftingCompatibility), nameof(RememberStartedCrafting)));
        harmony.Patch(clientStart,
            postfix: new HarmonyMethod(typeof(RknCraftingCompatibility), nameof(RememberClientCrafting)));
        harmony.Patch(tick,
            prefix: new HarmonyMethod(typeof(RknCraftingCompatibility), nameof(CheckHotbarChange)));
        harmony.Patch(step,
            prefix: new HarmonyMethod(typeof(RknCraftingCompatibility), nameof(StopChangedCraftingStep)));
        harmony.Patch(ingredientGrid,
            postfix: new HarmonyMethod(typeof(RknCraftingCompatibility), nameof(FilterUnrelatedTools)));

        harmony.Patch(getHeldItemInfo,
            prefix: new HarmonyMethod(typeof(RknCraftingCompatibility), nameof(SafeUnfinishedInfoPrefix)),
            finalizer: new HarmonyMethod(typeof(RknCraftingCompatibility), nameof(SafeUnfinishedTooltipFinalizer)));
        harmony.Patch(getHeldItemName,
            prefix: new HarmonyMethod(typeof(RknCraftingCompatibility), nameof(SafeUnfinishedNamePrefix)),
            finalizer: new HarmonyMethod(typeof(RknCraftingCompatibility), nameof(SafeUnfinishedTooltipFinalizer)));
    }

    private static MethodInfo RequireMethod(Type? type, string name, Type[]? parameters = null) =>
        (type == null ? null : AccessTools.Method(type, name, parameters))
        ?? throw new MissingMethodException(type?.FullName ?? "RKN ItemUnfinishedCraft", name);

    private static RknCraftingCompatibility? For(object instance) =>
        instance is BlockEntity entity && instances.TryGetValue(entity.Api.Side, out var active) ? active : null;

    private static void RememberStartedCrafting(object __instance, IPlayer byPlayer, bool __result)
    {
        if (__result) For(__instance)?.Remember(__instance, byPlayer);
    }

    private static void RememberClientCrafting(object __instance, IPlayer byPlayer)
    {
        For(__instance)?.Remember(__instance, byPlayer);
    }

    private static void CheckHotbarChange(object __instance)
    {
        For(__instance)?.Check(__instance);
    }

    private static bool StopChangedCraftingStep(object __instance, IPlayer byPlayer)
    {
        return For(__instance)?.Check(__instance) ?? true;
    }

    private static bool SafeUnfinishedInfoPrefix(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world)
    {
        if (Current?.TryGetMissingUnfinishedTool(inSlot?.Itemstack, world, out string? missingTool) != true) return true;

        dsc.Append("Used tools: unavailable (").Append(missingTool).Append(')');
        Current.WarnUnfinishedTooltip();
        return false;
    }

    private static bool SafeUnfinishedNamePrefix(ItemStack itemStack, ref string __result)
    {
        if (Current?.HasInvalidUnfinishedOutput(itemStack) != true) return true;

        __result = "Unfinished craft (missing output)";
        Current.WarnUnfinishedTooltip();
        return false;
    }

    private static Exception? SafeUnfinishedTooltipFinalizer(Exception? __exception)
    {
        if (__exception is not (NullReferenceException or FormatException)) return __exception;

        Current?.WarnUnfinishedTooltip();
        return null;
    }

    private static RknCraftingCompatibility? Current =>
        instances.GetValueOrDefault(EnumAppSide.Client) ?? instances.GetValueOrDefault(EnumAppSide.Server);

    private static void FilterUnrelatedTools(object __0, object __1, ref ItemSlot[] __result)
    {
        var active = Current;
        if (active == null || active.inputItems.GetValue(__1) is not ItemSlot[] items
            || active.toolIngredients.GetValue(__0) is not List<CraftingRecipeIngredient> tools
            || __result.Length <= items.Length) return;

        // Preserve the entire material grid and the real tool slots. Only the
        // extra hands appended by RKN are candidates for removal. Passing an
        // unrelated worn tool lets vanilla average its wear into the output.
        __result = __result.Where((slot, index) => index < items.Length
            || (slot?.Itemstack is ItemStack stack && tools.Any(tool => tool.SatisfiesAsIngredient(stack))))
            .ToArray();
    }

    public void RegisterRecoveryCommand(ICoreServerAPI serverApi)
    {
        ModCommands.Root(serverApi).BeginSubCommand("fixstack")
            .WithDescription("Repair the RKN phantom durability on the stack in your active hand")
            .RequiresPrivilege(Privilege.chat).RequiresPlayer()
            .HandleWith(args =>
            {
                ItemSlot slot = args.Caller.Player.InventoryManager.ActiveHotbarSlot;
                if (!RepairPhantomDurability(slot.Itemstack))
                    return TextCommandResult.Error(Lang.GetL(args.LanguageCode, "vsscpmod:stack-repair-none"));
                slot.MarkDirty();
                return TextCommandResult.Success(Lang.GetL(args.LanguageCode, "vsscpmod:stack-repair-success"));
            }).EndSubCommand();
    }

    internal static bool RepairPhantomDurability(ItemStack? stack)
    {
        // Repair only the exact observed signature, on an explicitly selected
        // stack. Do not reset real tool wear, quality, contents or other data.
        if (stack?.Collectible is not CollectibleObject collectible || collectible.Tool != null
            || collectible.Durability != 0 || collectible.GetMaxDurability(stack) != 0
            || stack.Attributes["durability"] is not IntAttribute { value: 1 }) return false;
        stack.Attributes.RemoveAttribute("durability");
        return true;
    }

    private bool TryGetMissingUnfinishedTool(ItemStack? stack, IWorldAccessor world, out string? missingTool)
    {
        missingTool = null;
        try
        {
            ITreeAttribute? usedTools = stack?.Attributes?.GetTreeAttribute("usedTools");
            if (usedTools == null) return false;

            foreach (KeyValuePair<string, IAttribute> entry in usedTools)
            {
                string? code = usedTools.GetString(entry.Key);
                if (string.IsNullOrWhiteSpace(code))
                {
                    missingTool = "invalid tool record";
                    return true;
                }

                AssetLocation tool = new(code);
                if (world.GetItem(tool) != null) continue;

                missingTool = code;
                return true;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or NullReferenceException)
        {
            missingTool = "invalid tool record";
            return true;
        }

        return false;
    }

    private bool HasInvalidUnfinishedOutput(ItemStack? stack)
    {
        try
        {
            ItemStack? output = stack?.Attributes?.GetItemstack("output", null);
            if (output == null) return false;

            output.ResolveBlockOrItem(api.World);
            return output.Collectible == null;
        }
        catch (Exception exception) when (exception is ArgumentException or NullReferenceException)
        {
            return true;
        }
    }

    private void WarnUnfinishedTooltip()
    {
        if (loggedUnfinishedTooltipWarning) return;
        loggedUnfinishedTooltipWarning = true;
        api.Logger.Warning("[vsscpmod] RKN unfinished craft contains a missing or invalid recipe reference; its tooltip was safely degraded.");
    }

    private void Remember(object instance, IPlayer player)
    {
        if (player?.InventoryManager?.ActiveHotbarSlot == null) return;
        snapshots.Remove(instance);
        snapshots.Add(instance, Snapshot.Create(player));
    }

    private bool Check(object instance)
    {
        object? parameters = craftingParams.GetValue(instance);
        if (parameters == null)
        {
            snapshots.Remove(instance);
            return true;
        }

        if (!snapshots.TryGetValue(instance, out Snapshot? expected))
        {
            object? owner = player.GetValue(parameters);
            if (owner is IPlayer currentPlayer) Remember(instance, currentPlayer);
            return true;
        }

        object? ownerObject = player.GetValue(parameters);
        if (ownerObject is not IPlayer craftingPlayer || !expected.Changed(craftingPlayer)) return true;

        // The server method broadcasts the animation stop to the client. On
        // the client, CancelCrafting clears the local animation immediately.
        if (((BlockEntity)instance).Api.Side == EnumAppSide.Server)
            serverStopCrafting.Invoke(instance, null);
        else
            cancelCrafting.Invoke(instance, [craftingPlayer]);
        snapshots.Remove(instance);
        return false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!ReferenceEquals(instances.GetValueOrDefault(api.Side), this)) return;
        instances.Remove(api.Side);
        if (instances.Count == 0) harmony.UnpatchAll(HarmonyId);
    }

    private sealed record Snapshot(ItemSlot Slot, int CollectibleId)
    {
        public static Snapshot Create(IPlayer player)
        {
            ItemSlot slot = player.InventoryManager.ActiveHotbarSlot;
            return new Snapshot(slot, slot.Itemstack?.Collectible?.Id ?? 0);
        }

        public bool Changed(IPlayer player)
        {
            ItemSlot current = player.InventoryManager.ActiveHotbarSlot;
            return !ReferenceEquals(current, Slot)
                || (current.Itemstack?.Collectible?.Id ?? 0) != CollectibleId;
        }
    }
}
