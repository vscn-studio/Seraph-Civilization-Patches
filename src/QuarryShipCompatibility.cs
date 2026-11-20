using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

// Uses vanilla attachment transfers and serialization; no dependency on vehicle/Quarry DLLs.
public sealed class QuarryShipCargoBehavior(Block block) : BlockBehavior(block), IAttachableToEntity
{
    internal const string Category = "vsscpmodquarryslab";
    private ICoreAPI api = null!;
    public override void OnLoaded(ICoreAPI api) { base.OnLoaded(api); this.api = api; }
    public int RequiresBehindSlots { get; set; }
    internal static bool IsSlab(ItemStack? stack) => stack?.Block?.Code is { Domain: "stonequarry" } code
        && code.Path.StartsWith("stoneslab-", StringComparison.Ordinal);
    internal static bool IsShip(Entity entity) => entity.Code is { Domain: "shipwright" } code
        && code.Path.StartsWith("boat-", StringComparison.Ordinal);
    internal static bool IsCart(Entity entity) => entity.Code is { Domain: "cartwrightscaravan" } code
        && code.Path.StartsWith("cart-", StringComparison.Ordinal);
    public bool IsAttachable(Entity toEntity, ItemStack itemStack) => IsSlab(itemStack)
        && ((IsShip(toEntity) && api.ModLoader.IsModEnabled("shipwright"))
            || (IsCart(toEntity) && api.ModLoader.IsModEnabled("cartwrightscaravan")));
    public string GetCategoryCode(ItemStack stack) => Category;
    public CompositeShape GetAttachedShape(ItemStack stack, string slotCode) => new()
    {
        Base = new AssetLocation("vsscpmod:quarrycargo/" + Size(stack))
    };
    internal static string Size(ItemStack stack) => stack.Block.Variant?["size"] ?? stack.Block.Code.Path.Split('-')[1];
    public string[]? GetDisableElements(ItemStack stack) => null;
    public string[]? GetKeepElements(ItemStack stack) => null;
    public string GetTexturePrefixCode(ItemStack stack)
    {
        // Boats need not useSlotPrefix. Different rock mixtures must never share texture keys.
        var preset = stack.Attributes.GetTreeAttribute("preset");
        string key = stack.Collectible.Code + "|" + string.Join("|", Enumerable.Range(0, 12)
            .Select(i => preset?.GetString(i.ToString(), "") ?? ""));
        return "vsscpmodquarry" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + "-";
    }
    public void CollectTextures(ItemStack stack, Shape shape, string texturePrefixCode, Dictionary<string, CompositeTexture> intoDict)
    {
        shape.Textures ??= new();
        CompositeTexture? wood = null;
        stack.Block.Textures?.TryGetValue("wood", out wood);
        shape.Textures["wood"] = wood?.Baked?.BakedName ?? wood?.Base
            ?? new AssetLocation("game:block/wood/planks/generic");
        var preset = stack.Attributes.GetTreeAttribute("preset");
        for (int i = 0; i < 12; i++)
        {
            string? code = preset?.GetString(i.ToString());
            Block? rock = string.IsNullOrWhiteSpace(code) ? null : api.World.GetBlock(new AssetLocation(code));
            CompositeTexture? texture = rock?.Textures?.Values.FirstOrDefault();
            shape.Textures["stone" + i] = texture?.Baked?.BakedName ?? texture?.Base
                ?? new AssetLocation("game:block/transparent");
        }
    }
}

internal sealed class QuarryShipCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.quarry-ship";
    private static readonly Harmony harmony = new(HarmonyId);
    private static readonly HashSet<QuarryShipCompatibility> owners = [];
    internal static QuarryShipCompatibility? TryCreate(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("shipwright") && !api.ModLoader.IsModEnabled("cartwrightscaravan")) return null;
        if (owners.Count == 0)
        {
            harmony.Patch(AccessTools.Method(typeof(EntityBehaviorAttachable), nameof(EntityBehaviorAttachable.Initialize)),
                postfix: new HarmonyMethod(typeof(QuarryShipCompatibility), nameof(InitializeSlots)));
            harmony.Patch(AccessTools.Method(typeof(EntityBehaviorAttachable), nameof(EntityBehaviorAttachable.GetSlotIndexFromSelectionBoxIndex)),
                postfix: new HarmonyMethod(typeof(QuarryCargoLayout), nameof(QuarryCargoLayout.ResolveSelection)));
            harmony.Patch(AccessTools.Method(typeof(EntityBehaviorAttachable), nameof(EntityBehaviorAttachable.GetSlotConfigFromAPName)),
                postfix: new HarmonyMethod(typeof(QuarryCargoLayout), nameof(QuarryCargoLayout.ResolveAttachmentPoint)));
            harmony.Patch(AccessTools.Method(typeof(EntityBehaviorAttachable), "TryAttach"),
                prefix: new HarmonyMethod(typeof(QuarryCargoLayout), nameof(QuarryCargoLayout.BeforeAttach)),
                transpiler: new HarmonyMethod(typeof(QuarryCargoLayout), nameof(QuarryCargoLayout.AccurateRearOccupancy)));
            harmony.Patch(AccessTools.Method(typeof(EntityBehaviorAttachable), "TryRemoveAttachment"),
                prefix: new HarmonyMethod(typeof(QuarryCargoLayout), nameof(QuarryCargoLayout.BeforeRemove)));
            harmony.Patch(AccessTools.Method(typeof(EntityBehaviorSeatable), nameof(EntityBehaviorSeatable.CanSitOn)),
                prefix: new HarmonyMethod(typeof(QuarryCargoLayout), nameof(QuarryCargoLayout.BeforeSit)));
            harmony.Patch(AccessTools.Method(typeof(EntityAgent), nameof(EntityAgent.TryMount)),
                prefix: new HarmonyMethod(typeof(QuarryCargoLayout), nameof(QuarryCargoLayout.BeforeMount)));
            var render = typeof(EntityBehaviorContainer).GetMethods().Single(m => m.Name == "addGearToShape" && !m.IsStatic);
            harmony.Patch(render, prefix: new HarmonyMethod(typeof(QuarryCargoLayout), nameof(QuarryCargoLayout.BeforeShape)));
        }
        var owner = new QuarryShipCompatibility();
        owners.Add(owner);
        return owner;
    }
    private static void InitializeSlots(EntityBehaviorAttachable __instance, WearableSlotConfig[] ___wearableSlots)
    {
        var entity = __instance.entity;
        string category;
        if (QuarryShipCargoBehavior.IsShip(entity) && entity.Api.ModLoader.IsModEnabled("shipwright")) category = "chest";
        else if (QuarryShipCargoBehavior.IsCart(entity) && entity.Api.ModLoader.IsModEnabled("cartwrightscaravan")) category = "storage";
        else return;
        InventoryBase inventory = __instance.Inventory;
        for (int i = 0; i < inventory.Count; i++)
            if (inventory[i] is ItemSlotWearable slot)
            {
                // Both TryAttach and the inventory validate categories. Add a slab-only category
                // instead of allowing all boat chests in cart storage points (or vice versa).
                bool slabs = entity.Api.ModLoader.IsModEnabled("stonequarryrepckfipil") && slot.canHoldWearableCodes.Contains(category);
                string[] categories = slabs ? slot.canHoldWearableCodes.Append(QuarryShipCargoBehavior.Category).Distinct().ToArray()
                    : slot.canHoldWearableCodes;
                ___wearableSlots[i].ForCategoryCodes = categories;
                ItemSlotWearable replacement = slabs ? new QuarryShipCargoSlot(inventory, categories, __instance, i)
                    : new VehicleCargoSlot(inventory, categories, __instance, i);
                replacement.Itemstack = slot.Itemstack;
                replacement.StorageType = slot.StorageType;
                inventory[i] = replacement;
            }
        QuarryCargoLayout.Register(__instance, ___wearableSlots);
    }
    public void Dispose()
    {
        if (owners.Remove(this) && owners.Count == 0) harmony.UnpatchAll(HarmonyId);
    }
}

// Validate all adapted attachment points, including those that do not accept slabs.
internal class VehicleCargoSlot(InventoryBase inventory, string[] categories, EntityBehaviorAttachable behavior, int index) : ItemSlotWearable(inventory, categories)
{
    public override bool CanHold(ItemSlot source) => ValidateAdmission(source, () => base.CanHold(source));
    public override bool CanTakeFrom(ItemSlot source, EnumMergePriority priority = EnumMergePriority.AutoMerge) =>
        ValidateAdmission(source, () => base.CanTakeFrom(source, priority));
    protected virtual bool ValidateAdmission(ItemSlot? source, Func<bool> check)
    {
        if (source?.Itemstack == null || QuarryCargoLayout.TryGet(behavior, out var layout) && !layout.CanLoad(index, source.Itemstack)) return false;
        return check();
    }
}

// Keep native categories/locks/merge checks; admit backpack-only slabs only here.
internal sealed class QuarryShipCargoSlot(InventoryBase inventory, string[] categories, EntityBehaviorAttachable behavior, int index)
    : VehicleCargoSlot(inventory, categories, behavior, index)
{
    public override int GetRemainingSlotSpace(ItemStack stack) => QuarryShipCargoBehavior.IsSlab(stack)
        ? Math.Min(base.GetRemainingSlotSpace(stack), Math.Max(0, 1 - StackSize)) : base.GetRemainingSlotSpace(stack);
    protected override bool ValidateAdmission(ItemSlot? source, Func<bool> check)
    {
        var previous = StorageType;
        try
        {
            if (QuarryShipCargoBehavior.IsSlab(source?.Itemstack)) StorageType |= EnumItemStorageFlags.Backpack;
            return base.ValidateAdmission(source, check);
        }
        finally { StorageType = previous; }
    }
}
