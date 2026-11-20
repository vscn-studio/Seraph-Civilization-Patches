using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

static class QuarryContainerRecoveryChecks
{
    public static void Run(ICoreAPI originalApi, IPlayer player, System.Func<int, int, ItemStack> slab)
    {
        int checks = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL: " + message);
            checks++;
            Console.WriteLine("PASS: " + message);
        }
        var tasks = new Queue<Action>();
        var drops = new List<(ItemStack Stack, BlockPos Pos)>();
        var settings = originalApi.World.Config;
        EnumAppSide side = EnumAppSide.Server;
        BlockEntity? live = null;
        bool failSpawn = false, throwSpawn = false;
        int modified = 0;
        var chunk = Proxy.Create<IWorldChunk>((m, _) => { if (m.Name == "MarkModified") modified++; return null; });
        var accessor = Proxy.Create<IBlockAccessor>((m, _) => m.Name switch {
            "GetBlockEntity" => live, "GetChunkAtBlockPos" => chunk, _ => null });
        var events = Proxy.Create<IEventAPI>((m, a) => {
            if (m.Name == "EnqueueMainThreadTask") tasks.Enqueue((Action)a![0]!);
            return m.Name == "RegisterGameTickListener" ? 1L : null;
        });
        var network = Proxy.Create<IInventoryNetworkUtil>((_, _) => null);
        var registry = Proxy.Create<IClassRegistryAPI>((m, _) => m.Name == "CreateInvNetworkUtil" ? network : null);
        ICoreAPI api = null!;
        var world = Proxy.Create<IWorldAccessor>((m, a) => {
            if (m.Name == "get_Api") return api;
            if (m.Name == "get_Side") return side;
            if (m.Name == "get_BlockAccessor") return accessor;
            if (m.Name == "SpawnItemEntity")
            {
                if (throwSpawn) throw new InvalidOperationException("test spawn failure");
                if (failSpawn) return null;
                drops.Add((((ItemStack)a![0]!).Clone(), ((BlockPos)a[1]!).Copy()));
                return new EntityItem { Itemstack = (ItemStack)a[0]! };
            }
            return m.Invoke(originalApi.World, a);
        });
        api = Proxy.Create<ICoreAPI>((m, a) => m.Name switch {
            "get_World" => world, "get_Side" => side, "get_Event" => events,
            "get_ClassRegistry" => registry, _ => m.Invoke(originalApi, a) });
        void Flush() { while (tasks.TryDequeue(out var task)) task(); }
        TreeAttribute BinaryRoundtrip(TreeAttribute tree)
        {
            using var stream = new MemoryStream();
            tree.ToBytes(new BinaryWriter(stream));
            stream.Position = 0;
            var restored = new TreeAttribute();
            restored.FromBytes(new BinaryReader(stream));
            return restored;
        }
        var inventoryField = AccessTools.Field(typeof(BlockEntityGenericContainer), "inventory");
        BlockEntityGenericContainer Chest(params ItemStack?[] stacks)
        {
            var source = new InventoryGeneric(stacks.Length, "chest-source", api);
            for (int i = 0; i < stacks.Length; i++) source[i].Itemstack = stacks[i]?.Clone();
            var data = new TreeAttribute();
            var invData = new TreeAttribute(); source.ToTreeAttributes(invData);
            data["inventory"] = invData;
            data.SetInt("posx", 10); data.SetInt("posy", 20 + BlockPos.DimensionBoundary); data.SetInt("posz", 30);
            var chest = new BlockEntityGenericContainer { Block = new Block { Code = new("game:chest-normal") } };
            inventoryField.SetValue(chest, new InventoryGeneric(stacks.Length, null!, null!));
            chest.FromTreeAttributes(BinaryRoundtrip(data), world); // Real vanilla load before Initialize.
            live = chest;
            chest.Initialize(api);
            chest.Inventory.DirtySlots.Clear();
            return chest;
        }
        try
        {
            var heavy = slab(64, 3);
            heavy.Attributes.SetString("custom-data", "legacy-cargo");
            heavy.Attributes.GetOrAddTreeAttribute("preset").SetString("0", "game:rock-granite");
            var chest = Chest(heavy, slab(31, 2), slab(0, 1), new ItemStack(new Block { BlockId = 3, Code = new("game:rock-granite") }));
            Check(tasks.Count == 1 && drops.Count == 0 && !chest.Inventory[0].Empty,
                "legacy chest load defers recovery until complete initialization");
            chest.OnBlockPlaced(); chest.OnBlockPlaced();
            Check(tasks.Count == 1, "repeated container callbacks coalesce before recovery");
            Flush();
            Check(chest.Inventory[0].Empty && chest.Inventory.Skip(1).All(s => !s.Empty) && drops.Count == 1,
                "loaded native chest ejects heavy slabs only; light slabs, presets and other goods stay");
            Check(drops[0].Stack.StackSize == 3 && drops[0].Stack.Attributes.Equals(world, heavy.Attributes),
                "whole legacy stack retains nested Quarry rocks, preset and custom attributes");
            Check(drops[0].Pos.InternalY > BlockPos.DimensionBoundary && drops[0].Pos.Equals(chest.Pos.UpCopy()),
                "recovered slab spawns above its container in the original dimension");
            Check(chest.Inventory.DirtySlots.Contains(0) && modified > 0,
                "recovery dirties inventory synchronization and native container chunk persistence");
            chest.OnBlockPlaced(); Flush(); chest.Inventory.Open(player);
            Check(drops.Count == 1, "repeated load/open paths never duplicate recovered slabs");
            var saved = new TreeAttribute(); chest.ToTreeAttributes(saved);
            var reloaded = Chest(null, null, null, null);
            reloaded.FromTreeAttributes(BinaryRoundtrip(saved), world); Flush();
            Check(reloaded.Inventory[0].Empty && drops.Count == 1, "native save/reload retains the cleared legacy slot");

            drops.Clear(); chest = Chest((ItemStack?)null); Flush();
            chest.Inventory[0].Itemstack = heavy.Clone();
            saved = new TreeAttribute(); chest.ToTreeAttributes(saved);
            chest.Inventory[0].Itemstack = null;
            chest.FromTreeAttributes(BinaryRoundtrip(saved), world);
            Check(drops.Count == 0 && !chest.Inventory[0].Empty && tasks.Count == 1,
                "restoring an already initialized container defers until derived inventory restoration finishes");
            Flush();
            Check(chest.Inventory[0].Empty && drops.Count == 1,
                "initialized native container restoration recovers the restored heavy slab once");

            drops.Clear();
            chest = Chest(slab(32, 1), slab(31, 16)); Flush();
            Check(chest.Inventory[0].Empty && chest.Inventory[1].StackSize == 16 && drops.Count == 1,
                "threshold applies per slab, not the total number of rocks in a stack");
            settings.SetInt("vsscpmodQuarryCarryBackpack", 64);
            drops.Clear(); chest = Chest(slab(32, 1), slab(64, 1)); Flush();
            Check(!chest.Inventory[0].Empty && chest.Inventory[1].Empty && drops.Count == 1,
                "legacy recovery follows the current server backpack threshold");
            settings.SetInt("vsscpmodQuarryCarryBackpack", 32);

            drops.Clear(); chest = Chest(heavy);
            chest.Inventory[0] = new ItemSlotBackpack(chest.Inventory) { Itemstack = heavy.Clone() };
            Flush();
            Check(!chest.Inventory[0].Empty && drops.Count == 0, "legal backpack-capable container slots are preserved");
            settings.SetBool("vsscpmodQuarryCarryEnabled", false);
            chest = Chest(heavy); Flush();
            Check(!chest.Inventory[0].Empty && drops.Count == 0, "disabled Quarry storage restrictions do not eject old cargo");
            settings.SetBool("vsscpmodQuarryCarryEnabled", true);

            foreach (bool throws in new[] { false, true })
            {
                failSpawn = !throws; throwSpawn = throws;
                chest = Chest(heavy); Flush();
                Check(chest.Inventory[0].Itemstack!.Attributes.Equals(world, heavy.Attributes)
                    && chest.Inventory[0].StackSize == 3 && drops.Count == 0,
                    "failed drop preserves the complete source stack: throws=" + throws);
                failSpawn = throwSpawn = false;
                chest.Inventory.Open(player);
                Check(chest.Inventory[0].Empty && drops.Count == 1, "native inventory opening retries a failed recovery once");
                drops.Clear();
            }
            chest = Chest(heavy); live = null; Flush();
            Check(!chest.Inventory[0].Empty && drops.Count == 0, "unloaded or removed containers cannot spawn ghost drops");
            chest = Chest(heavy); live = new EmptyEntity(); Flush();
            Check(!chest.Inventory[0].Empty && drops.Count == 0, "replaced block entities cannot eject stale inventory");

            var unbound = new InventoryGeneric(1, "hotbar-recovery", api) { Pos = chest.Pos };
            unbound[0].Itemstack = heavy.Clone(); unbound.Open(player);
            Check(!unbound[0].Empty && drops.Count == 0, "player and vehicle inventories are never recovered through an unrelated block position");

            side = EnumAppSide.Client;
            chest = Chest(heavy); chest.Inventory.Open(player); Flush();
            Check(!chest.Inventory[0].Empty && drops.Count == 0, "clients never eject or clear legacy slabs");
        }
        finally
        {
            settings.SetInt("vsscpmodQuarryCarryBackpack", 32);
            settings.SetBool("vsscpmodQuarryCarryEnabled", true);
        }
        Console.WriteLine($"Quarry container recovery: {checks} checks passed.");
    }
    sealed class EmptyEntity : BlockEntity { }
}
