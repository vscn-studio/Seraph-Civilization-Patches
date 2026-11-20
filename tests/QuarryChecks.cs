using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

// Exercise real Stone Quarry serialization, player movement and vehicle inventories.
static class QuarryChecks
{
    private static EntityPlayer testPlayer = null!;
    private static IPlayerInventoryManager testManager = null!;
    private static IWorldPlayerData testData = null!;
    private static bool locked = false, reinforced = false;
    public static void Run(string root)
    {
        int checks = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL: " + message);
            checks++;
            Console.WriteLine("PASS: " + message);
        }
        Type mod = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem);
        Type quarryFix = mod.Assembly.GetType("SeraphCivilizationPatches.QuarryCarryCompatibility")!;
        var migrated = Newtonsoft.Json.JsonConvert.DeserializeObject<SeraphCivilizationPatches.QuarryCarryConfig>(
            "{\"Enabled\":false,\"SlowdownStartsAtBlocks\":12,\"ImmobileAtBlocks\":160,\"BackpackSlotAtBlocks\":40}")!;
        AccessTools.Method(typeof(SeraphCivilizationPatches.QuarryCarryConfig), "Normalize").Invoke(migrated, null);
        Check(!migrated.Enabled && migrated.SlowdownStartsAtBlocks == 12 && migrated.ImmobileAtBlocks == 160
            && migrated.BackpackSlotAtBlocks == 40 && migrated.MaximumCarryBlocks == 64
            && migrated.MaximumCarriedSlabs == 1 && migrated.RestrictedActionsAtBlocks == 16,
            "partial carry configuration preserves custom values and fills new defaults");
        var invalid = new SeraphCivilizationPatches.QuarryCarryConfig
        {
            SlowdownStartsAtBlocks = -1, ImmobileAtBlocks = -1, BackpackSlotAtBlocks = 0,
            MaximumCarryBlocks = 0, MaximumCarriedSlabs = 0, RestrictedActionsAtBlocks = int.MaxValue
        };
        AccessTools.Method(typeof(SeraphCivilizationPatches.QuarryCarryConfig), "Normalize").Invoke(invalid, null);
        Check(invalid.SlowdownStartsAtBlocks == 0 && invalid.ImmobileAtBlocks == 1 && invalid.BackpackSlotAtBlocks == 1
            && invalid.MaximumCarryBlocks == 1 && invalid.MaximumCarriedSlabs == 1 && invalid.RestrictedActionsAtBlocks == 1,
            "invalid carry thresholds normalize to safe positive limits");
        bool enabled = true, access = true;
        EnumAppSide side = EnumAppSide.Server;
        var config = new TreeAttribute();
        var shovel = new Item { ItemId = 2, Code = new("game:shovel-iron"), Tool = EnumTool.Shovel };
        var rock = new Block { BlockId = 3, Code = new("game:rock-granite"), MaxStackSize = 64 };
        var slab = new Block { BlockId = 4, Code = new("stonequarry:stoneslab-small-north"), MaxStackSize = 16 };
        var air = new Block { BlockId = 0, Code = new("game:air") };
        Block selectedBlock = air;
        BlockEntity? selectedEntity = null;
        var drops = new List<EntityItem>();
        var hotbar = new InventoryGeneric(2, "hotbar-test", null!);
        var backpack = new InventoryGeneric(4, "backpack-test", null!);
        var chest = new InventoryGeneric(1, "chest-test", null!);
        backpack[0] = new ItemSlotBackpack(backpack);
        var inventories = new Dictionary<string, IInventory> { [hotbar.InventoryID] = hotbar, [backpack.InventoryID] = backpack, [chest.InventoryID] = chest };
        ItemSlot held = hotbar[0];
        ItemSlot mouse = new DummySlot();
        var manager = Proxy.Create<IPlayerInventoryManager>((m, a) => m.Name switch
        {
            "get_ActiveHotbarSlot" => held, "get_MouseItemSlot" => mouse,
            "get_Inventories" => inventories,
            "GetOwnInventory" => (a![0] as string) switch { "hotbar" => hotbar, "backpack" => backpack, _ => null },
            _ => null
        });
        var claims = Proxy.Create<ILandClaimAPI>((m, _) => m.Name == "TryAccess" ? access : null);
        var blocks = Proxy.Create<IBlockAccessor>((m, a) => m.Name switch
        {
            "GetBlock" => selectedBlock,
            "GetBlockEntity" => selectedEntity,
            "SetBlock" => SetBlock((int)a![0]!),
            _ => null
        });
        EntityPlayer player = null!;
        EnumGameMode mode = EnumGameMode.Survival;
        var data = Proxy.Create<IWorldPlayerData>((m, _) => m.Name == "get_CurrentGameMode" ? mode : null);
        Type ownerType = Assembly.Load("VintagestoryLib").GetType("Vintagestory.Server.ServerPlayer")!;
        var owner = (IPlayer)RuntimeHelpers.GetUninitializedObject(ownerType);
        long now = 0;
        var logger = Proxy.Create<ILogger>((_, _) => null);
        ICoreAPI api = null!;
        IWorldAccessor world = null!;
        world = Proxy.Create<IWorldAccessor>((m, a) => m.Name switch
        {
            "get_Side" => side, "get_Config" => config, "get_Claims" => claims, "get_Logger" => logger, "get_Api" => api,
            "get_BlockAccessor" => blocks, "PlayerByUid" => owner, "get_ElapsedMilliseconds" => now,
            "GetItem" => a![0] is int id ? shovel : shovel,
            "GetBlock" => a![0] is int id && id == 3 ? rock : slab,
            "SpawnItemEntity" => Spawn((ItemStack)a![0]!),
            _ => null
        });
        var reinforcement = new ModSystemBlockReinforcement();
        var loader = Proxy.Create<IModLoader>((m, _) => m.Name switch
        {
            "IsModEnabled" => enabled,
            "GetModSystem" => m.IsGenericMethod && m.GetGenericArguments()[0] == typeof(ModSystemBlockReinforcement) ? reinforcement : null,
            _ => null
        });
        var registry = Proxy.Create<IClassRegistryAPI>((_, _) => null);
        api = Proxy.Create<ICoreAPI>((m, _) => m.Name switch
        {
            "get_World" => world, "get_Side" => side, "get_ModLoader" => loader, "get_Logger" => logger,
            "get_ClassRegistry" => registry, _ => null
        });
        player = new EntityPlayer { Api = api, World = world };
        testPlayer = player; testManager = manager; testData = data;
        foreach (var col in new CollectibleObject[] { shovel, rock, slab })
            AccessTools.Field(typeof(CollectibleObject), "api").SetValue(col, api);
        foreach (InventoryBase inv in new[] { hotbar, backpack, chest }) inv.Api = api;
        var harmony = new Harmony("vsscpmod.tests.quarry");
        IDisposable? quarryOwner = null, second = null;
        try
        {
            harmony.Patch(AccessTools.PropertyGetter(ownerType, "Entity"), prefix: Hook(nameof(PlayerEntity)));
            harmony.Patch(AccessTools.PropertyGetter(ownerType, "InventoryManager"), prefix: Hook(nameof(PlayerInventory)));
            harmony.Patch(AccessTools.PropertyGetter(ownerType, "WorldData"), prefix: Hook(nameof(PlayerData)));
            harmony.Patch(AccessTools.Method(typeof(ModSystemBlockReinforcement), "IsLockedForInteract"), prefix: Hook(nameof(Locked)));
            harmony.Patch(AccessTools.Method(typeof(ModSystemBlockReinforcement), "IsReinforced"), prefix: Hook(nameof(Reinforced)));
            // No native networking or chunk meshes exist in this test environment.
            harmony.Patch(AccessTools.Method(typeof(BlockEntity), nameof(BlockEntity.MarkDirty)), prefix: Hook(nameof(Skip)));
            enabled = false;
            Check(Create(quarryFix) == null, "optional integrations skip when dependencies are absent");
            enabled = true;
            quarryOwner = Create(quarryFix);
            Check(quarryOwner != null, "quarry integration install against Vintage Story 1.22.7");

            // Use Stone Quarry's actual inventory serializer, not a hand-made approximation.
            var quarry = Assembly.LoadFrom(Path.Combine(root, "tests/dependencies/quarry/StoneQuarry.dll"));
            Type slabInventoryType = quarry.GetType("StoneQuarry.StoneSlabInventory")!;
            var slabInventory = (InventoryBase)Activator.CreateInstance(slabInventoryType, api, null, 2)!;
            ItemStack Slab(int blocks, int outer = 1)
            {
                var result = new ItemStack(slab, outer);
                slabInventory[0].Itemstack = new ItemStack(rock, blocks);
                slabInventory[1].Itemstack = null;
                slabInventory.ToTreeAttributes(result.Attributes);
                return result;
            }
            long Count(ItemStack value) => (long)AccessTools.Method(quarryFix, "CountBlocks").Invoke(null, [value])!;
            foreach (int blocksInSlab in new[] { 0, 31, 32, 64, 216 })
            {
                ItemStack value = Slab(blocksInSlab, 2);
                Check(Count(value) == blocksInSlab * 2, $"actual quarry serializer: {blocksInSlab} rocks x two slabs counted exactly once");
                var source = new DummySlot(value);
                Check(hotbar[1].CanTakeFrom(source) == (blocksInSlab < 32), "threshold controls normal slot admission at " + blocksInSlab);
                Check(backpack[0].CanTakeFrom(source) == (blocksInSlab >= 32), "threshold controls backpack slot admission at " + blocksInSlab);
            }
            var mixed = Slab(10);
            slabInventory[1].Itemstack = new ItemStack(rock, 22);
            slabInventory.ToTreeAttributes(mixed.Attributes);
            Check(Count(mixed) == 32 && mixed.Collectible.GetStorageFlags(mixed) == EnumItemStorageFlags.Backpack,
                "mixed quarry inventory slots sum to backpack threshold");
            var preset = new ItemStack(slab);
            preset.Attributes.SetInt("preset", 12);
            Check(Count(preset) == 0, "render-only creative preset has no invented rock load");
            config.SetInt("vsscpmodQuarryCarryBackpack", 64);
            Check((mixed.Collectible.GetStorageFlags(mixed) & EnumItemStorageFlags.General) != 0, "server backpack threshold controls admission");
            config.SetInt("vsscpmodQuarryCarryBackpack", 32);
            QuarryContainerRecoveryChecks.Run(api, owner, Slab);
            QuarryDragChecks.Run(root, api, owner, Slab);
            QuarryShipChecks.Run(root, api, world, slab, rock, Slab, value => side = value);
            var transferSource = new DummySlot(Slab(32, 2));
            Check(transferSource.TryPutInto(world, backpack[0], 2) == 1 && backpack[0].StackSize == 1 && transferSource.StackSize == 1,
                "native transfer fits exactly one heavy slab in a backpack slot");
            Reset();
            held.Itemstack = Slab(64);
            backpack[0].Itemstack = Slab(32);
            mouse.Itemstack = Slab(32);
            chest[0].Itemstack = Slab(216);
            Check((long)AccessTools.Method(quarryFix, "CountCarried").Invoke(null, [manager])! == 128,
                "burden includes own inventories and cursor, excludes opened chest");
            mouse = held;
            Check((long)AccessTools.Method(quarryFix, "CountCarried").Invoke(null, [manager])! == 96, "mouse alias is never double counted");
            mouse = new DummySlot();

            // Keep native EntityPlayer dispatch and the real compatibility postfix;
            // replace only the base terrain/physics calculation with a buffed value.
            harmony.Patch(AccessTools.Method(typeof(EntityPlayer), nameof(EntityPlayer.GetWalkSpeedMultiplier)), prefix: Hook(nameof(BaseSpeed)));
            backpack[0].Itemstack = null;
            foreach (int quantity in new[] { 0, 15, 16, 31, 32, 40, 48, 63, 64, 128, 216, 224, 500, 0 })
            {
                held.Itemstack = Slab(quantity); now += 101;
                double expected = quantity < 16 ? 2 : quantity <= 48 ? 1 : quantity >= 64 ? 0 : 2d * (64 - quantity) / 32;
                Check(Math.Abs(player.GetWalkSpeedMultiplier() - expected) < 1e-9,
                    $"short-haul movement at {quantity} rocks preserves buffs, caps heavy walking, and restores after unloading");
            }
            held.Itemstack = Slab(1, 2); now += 101;
            Check(player.GetWalkSpeedMultiplier() == 0, "two nearly empty slabs cannot bypass the one-slab carry limit");
            held.Itemstack = Slab(1); backpack[0].Itemstack = Slab(1); now += 101;
            Check(player.GetWalkSpeedMultiplier() == 0, "separate backpack slots cannot bypass the one-slab carry limit");
            backpack[0].Itemstack = null;
            var portable = new ItemStack(shovel);
            portable.Attributes.GetOrAddTreeAttribute("inventory").GetOrAddTreeAttribute("slots").SetItemstack("0", Slab(32));
            held.Itemstack = portable; now += 101;
            Check(player.GetWalkSpeedMultiplier() == 1, "nested portable container includes its slab contents");
            portable.StackSize = 2; now += 101;
            Check(player.GetWalkSpeedMultiplier() == 0, "stacked portable containers multiply actual slab contents");
            portable.StackSize = 1; held.Itemstack = null;
            backpack[0].Itemstack = portable; hotbar[1].Itemstack = Slab(32); now += 101;
            Check(player.GetWalkSpeedMultiplier() == 1, "equipped bag snapshot is not counted again with live slots");
            backpack[0].Itemstack = null; hotbar[1].Itemstack = null;
            var carried = player.WatchedAttributes.GetOrAddTreeAttribute("carryon:Carried").GetOrAddTreeAttribute("Hands");
            carried.SetItemstack("Stack", Slab(32));
            carried["Data"] = Slab(48).Attributes.Clone();
            now += 101;
            Check(player.GetWalkSpeedMultiplier() == 1, "Carry On Quarry BE snapshot is counted once instead of stale Stack contents");
            held.Itemstack = Slab(1); now += 101;
            Check(player.GetWalkSpeedMultiplier() == 0, "Carry On and personal inventory share one slab count");
            held.Itemstack = null; carried.SetItemstack("Stack", new ItemStack(shovel));
            carried["Data"] = portable.Attributes.Clone(); now += 101;
            Check(player.GetWalkSpeedMultiplier() == 1, "Carry On carried container includes nested slabs");
            player.WatchedAttributes.RemoveAttribute("carryon:Carried");

            var mount = new EntityAgent { World = world };
            typeof(Entity).GetProperty("Properties")!.SetValue(mount, new EntityProperties
            {
                Server = new EntityServerProperties([], new()) { Behaviors = [] },
                Client = new EntityClientProperties([], new()) { Behaviors = [] }
            });
            typeof(Entity).GetProperty("Properties")!.SetValue(player, new EntityProperties
            {
                Code = new AssetLocation("game:player"),
                Server = new EntityServerProperties([], new()) { Behaviors = [] },
                Client = new EntityClientProperties([], new()) { Behaviors = [] }
            });
            player.WatchedAttributes.SetString("playerUID", "quarry-carry-test");
            player.Code = new AssetLocation("game:player");
            // Only replace the audit-log display name; native unmount and seat sync remain real.
            harmony.Patch(AccessTools.Method(typeof(Entity), nameof(Entity.GetName)), prefix: Hook(nameof(AuditName)));
            var supplier = Proxy.Create<IMountable>((m, _) => m.Name == "get_OnEntity" ? mount : null);
            var seat = Proxy.Create<IMountableSeat>((m, _) => m.Name switch
            {
                "get_MountSupplier" => supplier, "CanUnmount" => true, _ => null
            });
            foreach (string mountCode in new[] { "cartwrightscaravan:cart-basiccart-classic-oak", "shipwright:boat-catboat", "equusferus:horse-male" })
            {
                mount.Code = new(mountCode); held.Itemstack = Slab(16);
                Check(!player.TryMount(seat), "fresh same-tick carried load blocks boarding " + mountCode);
            }
            var stationary = Proxy.Create<IMountableSeat>((_, _) => null);
            object[] mountArgs = [player, stationary, false];
            Check((bool)AccessTools.Method(quarryFix, "BeforeMount").Invoke(null, mountArgs)!,
                "block seats and beds without transport entity keep native mounting rules");
            held.Itemstack = Slab(15);
            mountArgs = [player, seat, false];
            Check((bool)AccessTools.Method(quarryFix, "BeforeMount").Invoke(null, mountArgs)!, "small personal load may board");
            held.Itemstack = Slab(16); now += 101;
            player.Controls.Jump = player.ServerControls.Jump = true;
            player.Controls.Sprint = player.ServerControls.Sprint = true;
            player.Controls.CtrlKey = true; player.Controls.Gliding = true;
            AccessTools.Method(quarryFix, "BeforeTick").Invoke(null, [player]);
            Check(!player.Controls.Jump && !player.ServerControls.Jump && !player.Controls.Sprint && !player.ServerControls.Sprint
                && !player.Controls.Gliding && player.Controls.CtrlKey, "heavy controls disable jump/sprint/glide while preserving interaction Ctrl");
            player.Swimming = true; player.Controls.Jump = true;
            AccessTools.Method(quarryFix, "BeforeTick").Invoke(null, [player]);
            Check(player.Controls.Jump, "heavy swimmer retains ascent to reach the surface");
            player.Swimming = false; player.Controls.Jump = player.Controls.Sprint = true;
            AccessTools.Method(quarryFix, "BeforePhysics").Invoke(null, [new EntityBehaviorPlayerPhysics(player)]);
            Check(!player.Controls.Jump && !player.Controls.Sprint, "physics boundary rechecks refreshed movement input");
            AccessTools.Property(typeof(EntityAgent), "MountedOn").SetValue(player, seat);
            AccessTools.Method(quarryFix, "BeforeTick").Invoke(null, [player]);
            Check(player.MountedOn == null && held.StackSize == 1 && Count(held.Itemstack!) == 16,
                "taking slab after boarding dismounts via native flow and keeps the complete inventory");
            held.Itemstack = null; now += 101;
            mountArgs = [player, seat, false];
            Check(player.GetWalkSpeedMultiplier() == 2 && (bool)AccessTools.Method(quarryFix, "BeforeMount").Invoke(null, mountArgs)!,
                "unloading restores walking and transport boarding");
            config.SetInt("vsscpmodQuarryCarryMaximumBlocks", 80); config.SetInt("vsscpmodQuarryCarryMaximumSlabs", 2);
            held.Itemstack = Slab(32, 2); now += 101;
            Check(Math.Abs(player.GetWalkSpeedMultiplier() - 2d / 3) < 1e-9, "server carry thresholds can be customized");
            config.SetInt("vsscpmodQuarryCarryMaximumBlocks", 64); config.SetInt("vsscpmodQuarryCarryMaximumSlabs", 1);
            held.Itemstack = Slab(224); now += 101;
            config.SetBool("vsscpmodQuarryCarryEnabled", false);
            Check(player.GetWalkSpeedMultiplier() == 2 && held.Itemstack.Collectible.GetStorageFlags(held.Itemstack) != EnumItemStorageFlags.Backpack,
                "configuration disables movement and backpack restrictions together");
            config.SetBool("vsscpmodQuarryCarryEnabled", true);
            AccessTools.Property(typeof(EntityAgent), "MountedOn").SetValue(player, seat);
            Check(player.GetWalkSpeedMultiplier() == 0, "mounted players do not bypass cumulative burden");
            AccessTools.Property(typeof(EntityAgent), "MountedOn").SetValue(player, null);
            mode = EnumGameMode.Creative;
            Check(player.GetWalkSpeedMultiplier() == 2, "creative movement remains unrestricted");
            mode = EnumGameMode.Survival;
            second = Create(quarryFix); quarryOwner!.Dispose(); quarryOwner = null;
            Check(player.GetWalkSpeedMultiplier() == 0, "second owner survives first owner's disposal");
            second!.Dispose(); second = null;
            Check(player.GetWalkSpeedMultiplier() == 2, "last owner disposal removes quarry hooks");
            Console.WriteLine($"Quarry: {checks} checks passed.");
        }
        finally
        {
            quarryOwner?.Dispose(); second?.Dispose(); harmony.UnpatchAll(harmony.Id);
        }

        IDisposable? Create(Type type) => (IDisposable?)AccessTools.Method(type, "TryCreate").Invoke(null, [api]);
        object? SetBlock(int id) { if (id == 0) { selectedBlock = air; selectedEntity = null; } return null; }
        EntityItem Spawn(ItemStack value)
        {
            var entity = new EntityItem { Itemstack = value.Clone(), World = world };
            drops.Add(entity);
            return entity;
        }
        void Reset()
        {
            held.Itemstack = new ItemStack(shovel); hotbar[1].Itemstack = null;
            mouse.Itemstack = null; backpack[0].Itemstack = null;
            selectedBlock = rock; selectedEntity = null; drops.Clear(); access = true;
        }
    }
    static HarmonyMethod Hook(string name) => new(typeof(QuarryChecks), name);
    static bool Skip() => false;
    static bool PlayerEntity(ref EntityPlayer __result) { __result = testPlayer; return false; }
    static bool PlayerInventory(ref IPlayerInventoryManager __result) { __result = testManager; return false; }
    static bool PlayerData(ref IWorldPlayerData __result) { __result = testData; return false; }
    static bool BaseSpeed(ref double __result) { __result = 2; return false; }
    static bool AuditName(ref string __result) { __result = "quarry-carry-test"; return false; }
    static bool Locked(ref bool __result) { __result = locked; return false; }
    static bool Reinforced(ref bool __result) { __result = reinforced; return false; }
}
