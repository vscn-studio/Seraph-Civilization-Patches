using System.Collections;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

internal sealed class ToolsmithCraftingCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.toolsmith-crafting";
    private readonly Harmony harmony = new(HarmonyId);
    private readonly ICoreAPI api;
    private readonly Type handleType;
    private readonly FieldInfo stats;
    private readonly MethodInfo validHandle;
    private readonly Dictionary<string, FieldInfo> tables;
    private readonly FieldInfo? recipeWithTools;
    private readonly MethodInfo? ingredientGrid;
    private readonly HashSet<string> reported = [];
    private static readonly Dictionary<EnumAppSide, ToolsmithCraftingCompatibility> instances = [];

    private ToolsmithCraftingCompatibility(ICoreAPI api, Type handleType)
    {
        this.api = api;
        this.handleType = handleType;
        Assembly toolsmith = handleType.Assembly;
        stats = AccessTools.Field(toolsmith.GetType("Toolsmith.ToolsmithModSystem"), "Stats")
            ?? throw new MissingFieldException("Toolsmith Stats");
        validHandle = AccessTools.Method(toolsmith.GetType("Toolsmith.ToolTinkering.TinkeringUtility"), "IsValidHandle")
            ?? throw new MissingMethodException("Toolsmith IsValidHandle");
        tables = new[] { "BaseHandleParts", "GripParts", "GripStats", "TreatmentParts", "TreatmentStats" }
            .ToDictionary(name => name, name => AccessTools.Field(stats.FieldType, name)
                ?? throw new MissingFieldException(stats.FieldType.FullName, name));
        if (api.ModLoader.IsModEnabled("rkncrafting"))
        {
            Type service = AccessTools.TypeByName("RKN.Crafting.RecipeService")
                ?? throw new TypeLoadException("RKN.Crafting.RecipeService");
            recipeWithTools = AccessTools.Field(service.Assembly.GetType("RKN.Crafting.GridRecipeWrapper"), "RecipeWithTools")
                ?? throw new MissingFieldException("RKN RecipeWithTools");
            ingredientGrid = AccessTools.Method(service, "GetIngredientGridWithTools")
                ?? throw new MissingMethodException("RKN GetIngredientGridWithTools");
        }
    }

    public static ToolsmithCraftingCompatibility? TryCreate(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("toolsmith")) return null;
        Type? handle = AccessTools.TypeByName("Toolsmith.ToolTinkering.Behaviors.CollectibleBehaviorToolHandle");
        if (handle == null) return null;
        ToolsmithCraftingCompatibility? owner = null;
        try
        {
            owner = new(api, handle);
            if (instances.Count == 0)
            {
                owner.harmony.Patch(AccessTools.Method(handle, "OnCreatedByCrafting"),
                    prefix: new HarmonyMethod(typeof(ToolsmithCraftingCompatibility), nameof(FilterHandleInputs)));
                if (owner.ingredientGrid != null)
                    owner.harmony.Patch(AccessTools.Method(owner.ingredientGrid.DeclaringType, "MatchesRecipe"),
                        postfix: new HarmonyMethod(typeof(ToolsmithCraftingCompatibility), nameof(ValidateRknHandle)));
            }
            instances[api.Side] = owner;
            api.Logger.Notification("[vsscpmod] Toolsmith handle ingredient filtering and RKN recipe validation enabled.");
            return owner;
        }
        catch (Exception exception)
        {
            if (instances.Count == 0) owner?.harmony.UnpatchAll(HarmonyId);
            api.Logger.Warning("[vsscpmod] Toolsmith crafting compatibility unavailable: {0}", exception.GetBaseException().Message);
            return null;
        }
    }

    private static ToolsmithCraftingCompatibility? Current =>
        instances.GetValueOrDefault(EnumAppSide.Client) ?? instances.GetValueOrDefault(EnumAppSide.Server);

    private static bool Ignored(ItemSlot? slot) => slot?.Itemstack?.Collectible?.Code == null
        || slot.Itemstack.Collectible.Code.Equals("toolsmith:sandpaper")
        || slot.Itemstack.Collectible.Code.Equals("game:firewood");

    private static void FilterHandleInputs(ref ItemSlot[] allInputslots)
    {
        // Toolsmith 1.2.19 uses != sandpaper || != firewood, which is always
        // true. Filter only this callback's argument, never the recipe or live
        // inventory: consumption, tool wear and the other behaviors stay intact.
        allInputslots = allInputslots.Where(slot => !Ignored(slot)).ToArray();
    }

    private static void ValidateRknHandle(object __instance, object __0, object __1, ref bool __result)
    {
        var owner = Current;
        if (!__result || owner?.recipeWithTools?.GetValue(__1) is not GridRecipe recipe
            || recipe.Output?.ResolvedItemStack is not ItemStack output
            || !output.Collectible.CollectibleBehaviors.Any(owner.handleType.IsInstanceOfType)) return;

        var slots = (ItemSlot[])owner.ingredientGrid!.Invoke(__instance, [__1, __0])!;
        if (owner.SafeHandleRecipe(slots, output, out string reason)) return;
        __result = false;
        string key = $"{recipe.Name}|{output.Collectible.Code}|{reason}";
        if (owner.reported.Add(key))
            owner.api.Logger.Warning("[vsscpmod] Skipped unsafe Toolsmith RKN recipe {0} -> {1}: {2}. Inputs: {3}",
                recipe.Name, output.Collectible.Code, reason,
                string.Join(", ", slots.Where(s => s?.Itemstack != null).Select(s => s.Itemstack!.Collectible.Code)));
    }

    private bool SafeHandleRecipe(ItemSlot[] slots, ItemStack output, out string reason)
    {
        // Mirror Toolsmith 1.2.19's branch selection, then check the config
        // entries it dereferences. RKN previews run the real crafting callback
        // with an ordinary slot, so DummySlot's preview bypass does not apply.
        reason = "missing handle definition";
        ItemStack? tool = null, beam = null, handle = null, other = null;
        foreach (ItemSlot slot in slots)
        {
            if (Ignored(slot)) continue;
            ItemStack stack = slot.Itemstack!;
            if (stack.Collectible.Tool.HasValue) tool = stack;
            else if (stack.Collectible.Code.FirstCodePart() == "supportbeam") beam = stack;
            else if ((bool)validHandle.Invoke(null, [stack])!) handle = stack;
            else other = stack;
        }
        if (tool != null && beam != null)
            return output.Collectible.Code.Equals("game:stick") || output.Collectible.Code.Equals("game:bone")
                || Lookup("BaseHandleParts", output.Collectible.Code.Path) != null;
        if (handle == null || other == null) return true;
        if (Lookup("BaseHandleParts", output.Collectible.Code.Path) == null) return false;
        object? grip = Lookup("GripParts", other.Collectible.Code.Path);
        if (grip != null)
        {
            reason = "duplicate grip or missing grip stats";
            return !handle.Attributes.HasAttribute("toolHandleGripTag")
                && Lookup("GripStats", TextField(grip, "gripStatTag")) != null;
        }
        reason = "duplicate treatment or grip prevents treatment";
        if (handle.Attributes.HasAttribute("toolHandleGripTag")
            || handle.Attributes.HasAttribute("toolHandleTreatmentTag")) return false;
        ItemStack? treatment = other.Block is ILiquidInterface liquid ? liquid.GetContent(other) : other;
        reason = "empty liquid container or missing treatment definition/stats";
        object? part = treatment == null ? null : Lookup("TreatmentParts", treatment.Collectible.Code.Path);
        return part != null && Lookup("TreatmentStats", TextField(part, "treatmentStatTag")) != null
            && Lookup("BaseHandleParts", handle.Collectible.Code.Path) != null;
    }

    private object? Lookup(string table, string? key) => key != null && stats.GetValue(null) is object value
        && tables[table].GetValue(value) is IDictionary map && map.Contains(key) ? map[key] : null;

    private static string? TextField(object value, string name) =>
        AccessTools.Field(value.GetType(), name)?.GetValue(value) as string;

    public void Dispose()
    {
        if (!ReferenceEquals(instances.GetValueOrDefault(api.Side), this)) return;
        instances.Remove(api.Side);
        if (instances.Count == 0) harmony.UnpatchAll(HarmonyId);
    }
}
