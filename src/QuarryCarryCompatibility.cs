using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

public sealed class QuarryCarryConfig
{
    public bool Enabled { get; set; } = true;
    public int SlowdownStartsAtBlocks { get; set; } = 32;
    public int ImmobileAtBlocks { get; set; } = 224;
    public int BackpackSlotAtBlocks { get; set; } = 32;
    public int MaximumCarryBlocks { get; set; } = 64;
    public int MaximumCarriedSlabs { get; set; } = 1;
    public int RestrictedActionsAtBlocks { get; set; } = 16;

    internal void Normalize()
    {
        SlowdownStartsAtBlocks = Math.Clamp(SlowdownStartsAtBlocks, 0, 1_000_000);
        ImmobileAtBlocks = Math.Clamp(ImmobileAtBlocks, SlowdownStartsAtBlocks + 1, 2_000_000);
        BackpackSlotAtBlocks = Math.Clamp(BackpackSlotAtBlocks, 1, 1_000_000);
        MaximumCarryBlocks = Math.Clamp(MaximumCarryBlocks, 1, 2_000_000);
        MaximumCarriedSlabs = Math.Clamp(MaximumCarriedSlabs, 1, 1_000_000);
        RestrictedActionsAtBlocks = Math.Clamp(RestrictedActionsAtBlocks, 1, MaximumCarryBlocks);
    }
}

internal sealed class QuarryCarryCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.quarry-carry";
    private const string ConfigKey = "vsscpmodQuarryCarry";
    private static readonly HashSet<QuarryCarryCompatibility> owners = [];
    internal static bool Active => owners.Count > 0;
    private static readonly Harmony harmony = new(HarmonyId);
    private static readonly ConditionalWeakTable<EntityPlayer, LoadCache> cache = new();
    private sealed class LoadCache
    {
        public long CheckedAt = long.MinValue;
        public long Blocks;
        public long Slabs;
    }

    public static QuarryCarryCompatibility? TryCreate(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("stonequarryrepckfipil") && !api.ModLoader.IsModEnabled("stonequarry")) return null;
        if (api.Side == EnumAppSide.Server)
        {
            QuarryCarryConfig config;
            try { config = api.LoadModConfig<QuarryCarryConfig>("vsscpmod-quarry-carry.json") ?? new(); }
            catch (Exception e)
            {
                api.Logger.Warning("[vsscpmod] Invalid quarry carry config; using defaults: {0}", e.Message);
                config = new();
            }
            config.Normalize();
            api.StoreModConfig(config, "vsscpmod-quarry-carry.json");
            // World config is sent to clients by the game; clients cannot choose lighter limits.
            api.World.Config.SetBool(ConfigKey + "Enabled", config.Enabled);
            api.World.Config.SetInt(ConfigKey + "Start", config.SlowdownStartsAtBlocks);
            api.World.Config.SetInt(ConfigKey + "Stop", config.ImmobileAtBlocks);
            api.World.Config.SetInt(ConfigKey + "Backpack", config.BackpackSlotAtBlocks);
            api.World.Config.SetInt(ConfigKey + "MaximumBlocks", config.MaximumCarryBlocks);
            api.World.Config.SetInt(ConfigKey + "MaximumSlabs", config.MaximumCarriedSlabs);
            api.World.Config.SetInt(ConfigKey + "Actions", config.RestrictedActionsAtBlocks);
        }
        if (owners.Count == 0)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(EntityPlayer), nameof(EntityPlayer.GetWalkSpeedMultiplier)),
                    postfix: new HarmonyMethod(typeof(QuarryCarryCompatibility), nameof(Speed)) { priority = Priority.Last });
                harmony.Patch(AccessTools.Method(typeof(EntityAgent), nameof(EntityAgent.TryMount), [typeof(IMountableSeat)]),
                    prefix: new HarmonyMethod(typeof(QuarryCarryCompatibility), nameof(BeforeMount)));
                harmony.Patch(AccessTools.Method(typeof(EntityPlayer), nameof(EntityPlayer.OnGameTick)),
                    prefix: new HarmonyMethod(typeof(QuarryCarryCompatibility), nameof(BeforeTick)));
                // Also enforce at the physics boundary: client input can be refreshed after the entity tick.
                harmony.Patch(AccessTools.Method(typeof(EntityBehaviorPlayerPhysics), nameof(EntityBehaviorPlayerPhysics.SetPlayerControls)),
                    prefix: new HarmonyMethod(typeof(QuarryCarryCompatibility), nameof(BeforePhysics)));
                harmony.Patch(AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.GetHeldItemInfo)),
                    postfix: new HarmonyMethod(typeof(QuarryCarryCompatibility), nameof(Info)));
                harmony.Patch(AccessTools.Method(typeof(CollectibleObject), nameof(CollectibleObject.GetStorageFlags)),
                    postfix: new HarmonyMethod(typeof(QuarryCarryCompatibility), nameof(StorageFlags)));
                foreach (string method in new[] { nameof(BlockEntity.Initialize), nameof(BlockEntity.FromTreeAttributes), nameof(BlockEntity.OnBlockPlaced) })
                    harmony.Patch(AccessTools.Method(typeof(BlockEntity), method),
                        postfix: new HarmonyMethod(typeof(QuarryContainerRecovery), nameof(QuarryContainerRecovery.Queue)));
                harmony.Patch(AccessTools.Method(typeof(InventoryBase), nameof(InventoryBase.Open)),
                    prefix: new HarmonyMethod(typeof(QuarryContainerRecovery), nameof(QuarryContainerRecovery.BeforeOpen)));
            }
            catch (Exception e)
            {
                harmony.UnpatchAll(HarmonyId);
                api.Logger.Warning("[vsscpmod] Quarry carry compatibility unavailable: {0}", e.GetBaseException().Message);
                return null;
            }
        }
        var owner = new QuarryCarryCompatibility();
        owners.Add(owner);
        api.Logger.Notification("[vsscpmod] Quarry short-haul limits active: cumulative load, slab count, jumping, sprinting and vehicle boarding.");
        return owner;
    }

    internal static long CountBlocks(ItemStack? stack)
    {
        if (stack?.Collectible?.Code is not { Domain: "stonequarry" } code
            || !code.Path.StartsWith("stoneslab-", StringComparison.Ordinal) || stack.StackSize <= 0) return 0;
        ITreeAttribute? slots = stack.Attributes.GetTreeAttribute("slots");
        long count = 0;
        if (slots != null)
        {
            foreach (var entry in slots)
                if (entry.Value is ItemstackAttribute { value: { } content })
                    count = Math.Min(int.MaxValue, count + Math.Max(0, content.StackSize));
        }
        // Creative presets contain only a rendering sample and no stored rocks.
        return count * stack.StackSize;
    }

    internal static double SpeedFactor(long blocks, int start, int stop) =>
        blocks <= start ? 1 : blocks >= stop ? 0 : (double)(stop - blocks) / (stop - start);

    private static void StorageFlags(ItemStack itemstack, ICoreAPI ___api, ref EnumItemStorageFlags __result)
    {
        var config = ___api?.World.Config;
        if (config == null || !config.GetBool(ConfigKey + "Enabled", true)) return;
        long blocks = CountBlocks(itemstack) / Math.Max(1, itemstack.StackSize);
        if (blocks >= config.GetInt(ConfigKey + "Backpack", 32)) __result = EnumItemStorageFlags.Backpack;
    }

    internal static long CountCarried(IPlayerInventoryManager manager) => ReadLoad(manager).Blocks;

    private static LoadCache ReadLoad(IPlayerInventoryManager manager, IWorldAccessor? world = null)
    {
        var load = new LoadCache();
        var seen = new HashSet<ItemSlot>(ReferenceEqualityComparer.Instance);
        // GetOwnInventory excludes opened chests, carts and other external inventories.
        foreach (string name in new[] { "hotbar", "backpack", "craftinggrid", "character", "mouse" })
        {
            IInventory? inventory = manager.GetOwnInventory(name);
            if (inventory == null) continue;
            foreach (ItemSlot slot in inventory)
                if (seen.Add(slot)) AddStack(load, slot.Itemstack, world, 0, slot is ItemSlotBackpack);
        }
        if (manager.MouseItemSlot is { } mouse && seen.Add(mouse))
            AddStack(load, mouse.Itemstack, world, 0);
        return load;
    }

    private static void AddStack(LoadCache load, ItemStack? stack, IWorldAccessor? world, int depth, bool equippedBag = false, long multiplier = 1)
    {
        if (stack == null || stack.StackSize <= 0 || depth > 16) return;
        if (stack.Collectible == null && world != null)
        {
            stack = stack.Clone();
            if (!stack.ResolveBlockOrItem(world)) return;
        }
        long blocks = CountBlocks(stack);
        if (blocks > 0)
        {
            load.Blocks = Math.Min(int.MaxValue, load.Blocks + Math.Min(int.MaxValue, blocks) * multiplier);
            load.Slabs = Math.Min(int.MaxValue, load.Slabs + stack.StackSize * multiplier);
            return;
        }
        // Equipped bags expose their contents as live ItemSlotBagContent slots.
        // Do not count the serialized snapshot a second time. Other portable
        // containers keep their actual inventory in item attributes.
        if (!equippedBag) AddTree(load, stack.Attributes, world, depth + 1, Math.Min(int.MaxValue, multiplier * stack.StackSize));
    }

    private static void AddTree(LoadCache load, ITreeAttribute? tree, IWorldAccessor? world, int depth, long multiplier = 1)
    {
        if (tree == null || depth > 16) return;
        foreach (var entry in tree)
        {
            if (entry.Key is "preset" or "carryonbackup") continue;
            if (entry.Value is ItemstackAttribute item) AddStack(load, item.value, world, depth + 1, multiplier: multiplier);
            else if (entry.Value is ITreeAttribute child) AddTree(load, child, world, depth + 1, multiplier);
        }
    }

    private static void AddCarryOn(LoadCache load, EntityPlayer player)
    {
        // Carry On 2.0 / CarryOnLib 1.0 watched format; Data is the live BE
        // snapshot, Stack may contain a stale duplicate of the same inventory.
        var root = player.WatchedAttributes.GetTreeAttribute("carryon:Carried");
        if (root == null) return;
        foreach (var entry in root)
            if (entry.Value is ITreeAttribute carried) AddCarried(load, carried, player.World, 0);
    }

    private static void AddCarried(LoadCache load, ITreeAttribute carried, IWorldAccessor world, int depth)
    {
        if (depth > 16) return;
        ItemStack? stack = carried.GetItemstack("Stack");
        if (stack != null && stack.Collectible == null)
        {
            stack = stack.Clone();
            stack.ResolveBlockOrItem(world);
        }
        var data = carried.GetTreeAttribute("Data");
        if (stack?.Collectible?.Code is { Domain: "stonequarry" } code && code.Path.StartsWith("stoneslab-", StringComparison.Ordinal)
            && data?.GetTreeAttribute("slots") is { } slots)
        {
            // Quarry stores rock stacks directly in the BE root slots.
            long rocks = 0;
            foreach (var entry in slots)
                if (entry.Value is ItemstackAttribute { value: { } rock }) rocks += Math.Max(0, rock.StackSize);
            if (rocks > 0)
            {
                load.Blocks = Math.Min(int.MaxValue, load.Blocks + rocks * Math.Max(1, stack.StackSize));
                load.Slabs = Math.Min(int.MaxValue, load.Slabs + Math.Max(1, stack.StackSize));
            }
        }
        else if (data != null) AddTree(load, data, world, 0);
        else AddStack(load, stack, world, 0);
        if (carried.GetTreeAttribute("Children") is { } children)
            foreach (var child in children)
                if (child.Value is ITreeAttribute tree) AddCarried(load, tree, world, depth + 1);
    }

    private static bool Applies(EntityPlayer player) => player.Player?.InventoryManager != null
        && player.Player.WorldData.CurrentGameMode == EnumGameMode.Survival
        && player.World.Config.GetBool(ConfigKey + "Enabled", true);

    private static LoadCache GetLoad(EntityPlayer player, bool fresh = false)
    {
        LoadCache load = cache.GetOrCreateValue(player);
        long now = player.World.ElapsedMilliseconds;
        if (fresh || load.CheckedAt == long.MinValue || now < load.CheckedAt || now - load.CheckedAt >= 100)
        {
            LoadCache found = ReadLoad(player.Player.InventoryManager, player.World);
            AddCarryOn(found, player);
            load.Blocks = found.Blocks;
            load.Slabs = found.Slabs;
            load.CheckedAt = now;
        }
        return load;
    }

    private static bool Restricted(EntityPlayer player, LoadCache load) =>
        load.Blocks >= player.World.Config.GetInt(ConfigKey + "Actions", 16)
        || load.Slabs > player.World.Config.GetInt(ConfigKey + "MaximumSlabs", 1);

    private static void Speed(EntityPlayer __instance, ref double __result)
    {
        if (!Applies(__instance)) return;
        LoadCache load = GetLoad(__instance);
        int start = __instance.World.Config.GetInt(ConfigKey + "Start", 32);
        int stop = Math.Max(1, Math.Min(__instance.World.Config.GetInt(ConfigKey + "Stop", 224),
            __instance.World.Config.GetInt(ConfigKey + "MaximumBlocks", 64)));
        start = Math.Min(start, stop - 1);
        double factor = SpeedFactor(load.Blocks, start, stop);
        if (Restricted(__instance, load)) factor = Math.Min(factor, 0.5);
        if (load.Slabs > __instance.World.Config.GetInt(ConfigKey + "MaximumSlabs", 1)) factor = 0;
        __result *= factor;
    }

    private static bool TransportSeat(IMountableSeat seat) => seat.MountSupplier?.OnEntity != null;

    private static bool BeforeMount(EntityAgent __instance, IMountableSeat onmount, ref bool __result)
    {
        if (__instance is not EntityPlayer player || !Applies(player) || !TransportSeat(onmount)
            || !Restricted(player, GetLoad(player, true))) return true;
        if (player.Api is ICoreClientAPI client)
            client.TriggerIngameError(player, "quarrycarrymount", Lang.Get("vsscpmod:quarry-carry-mount-blocked"));
        __result = false;
        return false;
    }

    private static void BeforeTick(EntityPlayer __instance)
    {
        if (!Applies(__instance) || !Restricted(__instance, GetLoad(__instance))) return;
        LimitControls(__instance);
        // Covers picking up a slab after boarding and restoring a mounted login.
        // Keep inventories intact; the native unmount flow syncs the seat state.
        if (__instance.World.Side == EnumAppSide.Server && __instance.MountedOn is { } seat && TransportSeat(seat))
            __instance.TryUnmount();
    }

    private static void BeforePhysics(EntityBehaviorPlayerPhysics __instance)
    {
        if (__instance.entity is EntityPlayer player && Applies(player) && Restricted(player, GetLoad(player))) LimitControls(player);
    }

    private static void LimitControls(EntityPlayer player)
    {
        foreach (EntityControls controls in new[] { player.Controls, player.ServerControls })
        {
            controls.Sprint = false;
            // Preserve swimming ascent; a jump prohibition must not trap players underwater.
            if (!player.Swimming && !player.FeetInLiquid) controls.Jump = false;
            controls.Gliding = false;
        }
    }

    private static void Info(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world)
    {
        long blocks = CountBlocks(inSlot.Itemstack);
        if (blocks == 0 || !world.Config.GetBool(ConfigKey + "Enabled", true)) return;
        dsc.AppendLine(Lang.Get("vsscpmod:quarry-carry-help", blocks,
            Math.Min(world.Config.GetInt(ConfigKey + "Stop", 224), world.Config.GetInt(ConfigKey + "MaximumBlocks", 64))));
        dsc.AppendLine(Lang.Get("vsscpmod:quarry-backpack-help", world.Config.GetInt(ConfigKey + "Backpack", 32)));
        dsc.AppendLine(Lang.Get("vsscpmod:quarry-carry-actions-help", world.Config.GetInt(ConfigKey + "Actions", 16),
            world.Config.GetInt(ConfigKey + "MaximumSlabs", 1)));
    }

    public void Dispose()
    {
        if (!owners.Remove(this) || owners.Count != 0) return;
        harmony.UnpatchAll(HarmonyId);
        cache.Clear();
    }
}
