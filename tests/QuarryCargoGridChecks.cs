using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
using SeraphCivilizationPatches;

static class QuarryCargoGridChecks
{
    static string assetRoot = "";
    static string loadedModel = "";
    public static void Run(string root, ICoreAPI originalApi, IWorldAccessor originalWorld,
        System.Func<int, int, ItemStack> makeSlab, Action<EnumAppSide> setSide, Action<bool, string> check)
    {
        assetRoot = root;
        var sizes = new[] { "small", "medium", "large", "huge", "giant" };
        var blocks = sizes.Select((size, i) => new Block { BlockId = 100 + i,
            Code = new("stonequarry:stoneslab-" + size + "-north"), MaxStackSize = 16 }).ToArray();
        var drops = new List<ItemStack>();
        var extraBlocks = new Dictionary<int, Block>();
        ICoreAPI api = null!;
        bool quarryEnabled = true;
        var loader = Proxy.Create<IModLoader>((m, a) => m.Name == "IsModEnabled" && (string)a![0]! == "stonequarryrepckfipil"
            ? quarryEnabled : m.Invoke(originalApi.ModLoader, a));
        IWorldAccessor world = Proxy.Create<IWorldAccessor>((m, a) =>
        {
            if (m.Name == "get_Api") return api;
            if (m.Name == "get_Rand") return new Random(1);
            if (m.Name == "SpawnItemEntity") { drops.Add(((ItemStack)a![0]!).Clone()); return null; }
            if (m.Name == "GetBlock")
            {
                if (a![0] is int extraId && extraBlocks.TryGetValue(extraId, out var extra)) return extra;
                var block = a![0] is int id ? blocks.FirstOrDefault(b => b.Id == id)
                    : blocks.FirstOrDefault(b => b.Code.Equals(a[0]));
                if (block != null) return block;
            }
            return m.Invoke(originalWorld, a);
        });
        api = Proxy.Create<ICoreAPI>((m, a) => m.Name == "get_World" ? world : m.Name == "get_ModLoader" ? loader : m.Invoke(originalApi, a));
        foreach (var block in blocks)
        {
            AccessTools.Field(typeof(CollectibleObject), "api").SetValue(block, api);
            var behavior = new QuarryShipCargoBehavior(block);
            behavior.OnLoaded(api); block.CollectibleBehaviors = [behavior];
        }
        ItemStack Slab(string size, int count = 1) => new(blocks[Array.IndexOf(sizes, size)], count)
        { Attributes = makeSlab(64, 1).Attributes.Clone() };
        var attach = AccessTools.Method(typeof(EntityBehaviorAttachable), "TryAttach");
        var detach = AccessTools.Method(typeof(EntityBehaviorAttachable), "TryRemoveAttachment");
        var configField = AccessTools.Field(typeof(EntityBehaviorAttachable), "wearableSlots");
        var cartJson = JObject.Parse(File.ReadAllText(Path.Combine(root,
            "tests/dependencies/cartwrights/assets/cartwrightscaravan/entities/nonliving/cart.json")));
        var configs = new Dictionary<string, JArray>();
        var cartSeats = new Dictionary<string, SeatConfig[]>();
        foreach (string type in new[] { "basic", "slim" })
        {
            configs["cartwrightscaravan:cart-" + type + "cart-classic-oak"] = (JArray)cartJson["behaviorConfigs"]!["rideableaccessories"]!["wearableSlotsByType"]!["*" + type + "*"]!;
            cartSeats["cartwrightscaravan:cart-" + type + "cart-classic-oak"] = cartJson["behaviorConfigs"]!["rideable"]!["seatsByType"]![type == "slim" ? "*slim*" : "*"]!.ToObject<SeatConfig[]>()!;
        }
        foreach (string type in new[] { "coracle", "reedlongboat", "sailedreedboat", "punter", "catboat", "drakkar", "largejunker" })
        {
            var json = JObject.Parse(File.ReadAllText(Path.Combine(root,
                "tests/dependencies/shipwright/assets/shipwright/entities/nonliving/" + type + ".json")));
            var config = json["behaviorConfigs"]!["rideableaccessories"] ?? json["behaviorConfigs"]!["attachable"]!;
            configs["shipwright:boat-" + type + "-oak"] = (JArray)config["wearableSlots"]!;
        }
        EntityBehaviorAttachable Create(string code, SyncedTreeAttribute? saved = null)
        {
            var entity = new EntityAgent { Api = api, World = world, Code = new(code) };
            AccessTools.Property(typeof(Entity), "Properties").SetValue(entity, new EntityProperties { Server = new([], new()), Client = new([], new()) });
            if (saved != null) entity.WatchedAttributes = saved;
            var cargo = new EntityBehaviorAttachable(entity);
            cargo.Initialize(entity.Properties, new JsonObject(new JObject {
                ["dropContentsOnDeath"] = true, ["wearableSlots"] = configs[code].DeepClone() }));
            entity.Properties.Server.Behaviors.Add(cargo);
            entity.Properties.Client.Behaviors.Add(cargo);
            var selection = new EntityBehaviorSelectionBoxes(entity);
            entity.Properties.Server.Behaviors.Add(selection);
            entity.Properties.Client.Behaviors.Add(selection);
            return cargo;
        }
        bool Attach(EntityBehaviorAttachable cargo, ItemStack stack, int index) => (bool)attach.Invoke(cargo, [new DummySlot(stack), index, null])!;
        void Clear(EntityBehaviorAttachable cargo) { foreach (var slot in cargo.Inventory) slot.Itemstack = null; }
        var backpack = new InventoryGeneric(1, "backpack-grid", api);
        backpack[0] = new ItemSlotBackpack(backpack);
        var carrier = new Carrier(world, backpack[0]);
        void CheckSeats(EntityBehaviorAttachable cargo, string code, WearableSlotConfig[] slots, int[] occupied)
        {
            if (!cartSeats.TryGetValue(code, out var seats)) return;
            var seatable = new EntityBehaviorSeatable(cargo.entity) { SeatConfigs = seats, Seats = [] };
            foreach (var config in seats)
            {
                bool blocked = occupied.Any(i => slots[i].AttachmentPointCode == config.SelectionBox || slots[i].AttachmentPointCode == config.APName);
                bool mountConsulted = false;
                var seat = Proxy.Create<IMountableSeat>((m, _) => m.Name switch {
                    "get_Config" => config, "get_Entity" => cargo.entity,
                    "CanMount" => ConsultMount(), _ => null });
                bool ConsultMount() { mountConsulted = true; return false; }
                foreach (var side in new[] { EnumAppSide.Server, EnumAppSide.Client })
                {
                    setSide(side);
                    check(seatable.CanSitOn(seat) == !blocked,
                        code + " " + side + " actual " + config.APName + "/" + config.SelectionBox + " seat respects slab footprint");
                    var selection = cargo.entity.GetBehavior<EntityBehaviorSelectionBoxes>()!;
                    selection.selectionBoxes = [new AttachmentPointAndPose { AttachPoint = new AttachmentPoint { Code = config.SelectionBox } }];
                    var help = SeatableInteractionHelp.GetOrCreateInteractionHelp(api, seatable, [seat], 0);
                    check(blocked ? help == null : help?.Any(h => h.ActionLangCode == "seatableentity-mount") == true,
                        code + " " + side + " native hover help hides mounting only for slab-covered seats");
                    mountConsulted = false;
                    check(!carrier.TryMount(seat) && mountConsulted == !blocked,
                        code + " " + side + " direct mount rejects occupied cells before native mount, keeps free seats and grip checks");
                }
            }
            setSide(EnumAppSide.Server);
        }
        var patch = new Harmony("vsscpmod.tests.cargogrid");
        patch.Patch(AccessTools.Method(typeof(Shape), "TryGet", [typeof(ICoreAPI), typeof(AssetLocation)]),
            prefix: new HarmonyMethod(typeof(QuarryCargoGridChecks), nameof(LoadShape)));
        try
        {
            foreach (var (code, _) in configs)
            {
                var cargo = Create(code);
                var slots = (WearableSlotConfig[])configField.GetValue(cargo)!;
                int[] indices = Enumerable.Range(0, slots.Length).Where(i => cargo.Inventory[i].GetType().Name == "QuarryShipCargoSlot").ToArray();
                bool basic = code.Contains("basiccart"), junker = code.Contains("largejunker"), coracle = code.Contains("coracle");
                string tag = code.Split(':')[1];
                foreach (string size in sizes)
                {
                    int expected = size == "small" ? 1 : size == "medium" ? 2 : 4;
                    bool allowed = size == "small" || size == "medium" && !coracle || basic || junker;
                    foreach (int index in indices)
                    {
                        Clear(cargo);
                        var source = new DummySlot(Slab(size, 2));
                        var original = source.Itemstack!.Attributes.Clone();
                        bool success = (bool)attach.Invoke(cargo, [source, index, null])!;
                        check(success == allowed && source.StackSize == (allowed ? 1 : 2), tag + " " + size + " footprint admission at " + slots[index].Code);
                        if (!allowed) { check(cargo.Inventory.Empty, "rejected size leaves no cargo or reservations"); continue; }
                        int[] reserved = indices.Where(i => cargo.GetSlotIndexFromSelectionBoxIndex(i) == index).ToArray();
                        check(reserved.Length == expected && cargo.Inventory.Count(s => !s.Empty) == 1,
                            tag + " " + size + " reserves " + expected + " cells with exactly one real stack");
                        CheckSeats(cargo, code, slots, reserved);
                        check(cargo.Inventory[index].Itemstack!.Attributes.Equals(world, original)
                            && cargo.Inventory[index].GetRemainingSlotSpace(source.Itemstack) == 0,
                            "cargo attributes unchanged and a second slab cannot merge");
                        foreach (int other in reserved.Where(i => i != index))
                            check(!cargo.Inventory[other].CanTakeFrom(source), "direct inventory insertion cannot bypass reserved cell");

                        string parent = slots[index].StepParentTo[""].ElementName;
                        var shape = new Shape { Elements = [new ShapeElement { Name = parent, From = [0, 0, 0], To = [16, 0, 16] }] };
                        string[] deleted = [];
                        cargo.addGearToShape(shape, cargo.Inventory[index].Itemstack, IAttachableToEntity.FromCollectible(blocks[Array.IndexOf(sizes, size)]),
                            slots[index].Code, tag, ref deleted, slots[index].StepParentTo);
                        var children = shape.Elements[0].Children!;
                        int layers = size == "huge" ? 2 : size == "giant" ? 3 : 1;
                        check(loadedModel.Contains("/grid/") && children.Length == expected * layers + 2,
                            tag + " native renderer loads grid model for " + size + " anchor " + index);
                        check(children.Max(e => e.To![1]) == layers * 16 - 1,
                            size + " model height matches stone layers");
                        double width = children.Max(e => e.To![0]) - children.Min(e => e.From![0]);
                        double depth = children.Max(e => e.To![2]) - children.Min(e => e.From![2]);
                        check(width * depth == (expected == 1 ? 14 * 14 : expected == 2 ? 14 * 30 : 30 * 30),
                            "native model covers the reserved cell footprint");
                        if (size == "medium") check(basic || junker ? width == 14 && depth == 30 : width == 30 && depth == 14,
                            "medium model follows the continuous row or the cart width");

                        // Reload into an entirely new entity, using actual binary watched attributes.
                        cargo.ToBytes(false);
                        using var bytes = new MemoryStream();
                        cargo.entity.WatchedAttributes.ToBytes(new BinaryWriter(bytes));
                        var saved = new SyncedTreeAttribute();
                        saved.FromBytes(new BinaryReader(new MemoryStream(bytes.ToArray())));
                        var reloaded = Create(code, saved);
                        check(reserved.All(i => reloaded.GetSlotIndexFromSelectionBoxIndex(i) == index)
                            && reloaded.Inventory[index].Itemstack!.Attributes.Equals(world, original),
                            "fresh entity binary reload reconstructs footprint and full slab contents");
                        Clear(reloaded); reloaded.FromBytes(true);
                        check(reserved.All(i => reloaded.GetSlotIndexFromSelectionBoxIndex(i) == index)
                            && reloaded.Inventory.Count(s => !s.Empty) == 1,
                            "native client inventory refresh reconstructs reservations without placeholder items");
                        CheckSeats(reloaded, code, slots, reserved);
                        int click = reserved.Last();
                        backpack[0].Itemstack = Slab("small");
                        check(!(bool)detach.Invoke(reloaded, [carrier, click])! && !reloaded.Inventory[index].Empty,
                            "full backpack leaves entire footprint reserved");
                        backpack[0].Itemstack = null;
                        check((bool)detach.Invoke(reloaded, [carrier, click])! && reloaded.Inventory.Empty
                            && backpack[0].Itemstack!.Attributes.Equals(world, original)
                            && reserved.All(i => reloaded.GetSlotIndexFromSelectionBoxIndex(i) == i),
                            "clicking any occupied cell unloads one intact slab and frees its whole footprint");
                        CheckSeats(reloaded, code, slots, []);
                    }
                }
                Clear(cargo);
                int first = indices.First();
                // Legacy loads rejected by new size rules must remain available for removal.
                cargo.Inventory[first].Itemstack = Slab("giant");
                cargo.ToBytes(false); cargo.FromBytes(false);
                backpack[0].Itemstack = null;
                check((bool)detach.Invoke(cargo, [carrier, first])! && backpack[0].Itemstack!.Block.Code.Path.Contains("giant"),
                    tag + " preserves and unloads legacy giant cargo even when new loading is forbidden");
                var ownable = new EntityBehaviorOwnable(cargo.entity);
                cargo.entity.Properties.Server.Behaviors.Add(ownable);
                cargo.entity.Properties.Client.Behaviors.Add(ownable);
                var denied = Slab("small", 2);
                check(!Attach(cargo, denied, first) && denied.StackSize == 2 && cargo.Inventory.Empty,
                    tag + " native ownership check still rejects unauthorized loading");
                cargo.Inventory[first].Itemstack = Slab("small");
                check(!(bool)detach.Invoke(cargo, [carrier, first])! && !cargo.Inventory[first].Empty,
                    tag + " native ownership check still rejects unauthorized unloading");
                cargo.entity.Properties.Server.Behaviors.Remove(ownable);
                cargo.entity.Properties.Client.Behaviors.Remove(ownable);
                if (!coracle)
                {
                    Clear(cargo);
                    check(Attach(cargo, Slab("medium"), first), tag + " medium loads for collision checks");
                    int neighbor = indices.First(i => i != first && cargo.GetSlotIndexFromSelectionBoxIndex(i) == first);
                    var chest = new Block { BlockId = 110, Code = new("game:chest-normal"), MaxStackSize = 1, Attributes = new JsonObject(JObject.Parse(
                        "{\"attachableToEntity\":{\"categoryCode\":\"" + (code.StartsWith("shipwright") ? "chest" : "storage") + "\"}}")) };
                    AccessTools.Field(typeof(CollectibleObject), "api").SetValue(chest, api);
                    extraBlocks[chest.Id] = chest;
                    var chestSource = new DummySlot(new ItemStack(chest));
                    check(!cargo.Inventory[neighbor].CanHold(chestSource) && !Attach(cargo, chestSource.Itemstack!, neighbor),
                        tag + " ordinary cargo cannot enter a slab reservation");
                    var seatConfig = new SeatConfig { APName = "Rider2", SelectionBox = slots[neighbor].AttachmentPointCode };
                    var seatable = new EntityBehaviorSeatable(cargo.entity) { SeatConfigs = [seatConfig], Seats = [] };
                    var emptySeat = Proxy.Create<IMountableSeat>((m, _) => m.Name == "get_Config" ? seatConfig : null);
                    check(!seatable.CanSitOn(emptySeat), tag + " native seating rejects a reserved secondary cargo cell");
                    var passenger = new EntityAgent();
                    seatable.Seats = [Proxy.Create<IMountableSeat>((m, _) => m.Name switch {
                        "get_Config" => seatConfig, "get_Passenger" => passenger, _ => null })];
                    cargo.entity.Properties.Server.Behaviors.Add(seatable);
                    cargo.entity.Properties.Client.Behaviors.Add(seatable);
                    check(!(bool)detach.Invoke(cargo, [carrier, first])!, tag + " unloading checks passengers across the whole footprint");
                    Clear(cargo);
                    check(!Attach(cargo, Slab("medium"), first), tag + " slab cannot overlap a passenger in a secondary cell");
                    cargo.entity.Properties.Server.Behaviors.Remove(seatable);
                    cargo.entity.Properties.Client.Behaviors.Remove(seatable);
                    Clear(cargo); cargo.Inventory[neighbor].Itemstack = chestSource.Itemstack;
                    setSide(EnumAppSide.Client);
                    var rejected = Slab("medium", 2);
                    check(!Attach(cargo, rejected, first) && rejected.StackSize == 2 && cargo.Inventory[first].Empty,
                        tag + " client rejects slab overlapping ordinary cargo without mutation");
                    setSide(EnumAppSide.Server);
                    check(!Attach(cargo, Slab("medium"), first), tag + " server rejects slab overlapping ordinary cargo");
                    // Two real legacy stacks are individually accessible, never silently removed.
                    cargo.Inventory[first].Itemstack = Slab("medium");
                    cargo.ToBytes(false); cargo.FromBytes(false);
                    check(cargo.GetSlotIndexFromSelectionBoxIndex(neighbor) == neighbor && cargo.Inventory.Count(s => !s.Empty) == 2,
                        tag + " overlapping old cargo retains both real items and individual hit targets");
                    Clear(cargo); Attach(cargo, Slab("medium"), first);
                    drops.Clear(); cargo.OnEntityDeath(new DamageSource());
                    check(drops.Count == 1 && drops[0].StackSize == 1 && cargo.Inventory.Empty,
                        tag + " native death drop emits one real slab, no reserved-cell duplicates");
                    Clear(cargo);
                    // Empty capacity is insufficient when those cells are separated by other cargo.
                    foreach (int i in indices) cargo.Inventory[i].Itemstack = chestSource.Itemstack!.Clone();
                    int Index(string parent) => Array.FindIndex(slots, s => s.StepParentTo?.GetValueOrDefault("")?.ElementName == parent);
                    string[] corners = basic ? ["RightStorage1", "RightStorage3", "LeftStorage1", "LeftStorage3"]
                        : junker ? ["Storage5", "Storage1", "Storage6", "Storage2"] : [];
                    foreach (string parent in corners) cargo.Inventory[Index(parent)].Itemstack = null;
                    if (corners.Length > 0) check(indices.All(i => !Attach(cargo, Slab("large"), i)),
                        tag + " four separated empty cells cannot substitute for a continuous 2x2 area");
                    if (basic)
                    {
                        Clear(cargo);
                        foreach (string parent in new[] { "RightStorage1", "RightStorage2", "RightStorage3" })
                            check(Attach(cargo, Slab("medium"), Index(parent)), "basic cart carries three medium slabs across its width");
                        check(indices.All(i => !Attach(cargo, Slab("small"), i)), "three medium slabs fill all six cart cells");
                        Clear(cargo);
                        var longBlock = new Block { BlockId = 111, Code = new("game:longcargo"), MaxStackSize = 1, Attributes = new JsonObject(JObject.Parse(
                            "{\"attachableToEntity\":{\"categoryCode\":\"storage\",\"requiresBehindSlots\":2}}")) };
                        AccessTools.Field(typeof(CollectibleObject), "api").SetValue(longBlock, api);
                        extraBlocks[longBlock.Id] = longBlock;
                        cargo.Inventory[Index("RightStorage3")].Itemstack = Slab("small");
                        check(!Attach(cargo, new ItemStack(longBlock), Index("RightStorage1")),
                            "long normal cargo checks all required behind cells against slabs");
                        Clear(cargo);
                        check(Attach(cargo, new ItemStack(longBlock), Index("RightStorage1")), "native cart loads long ordinary cargo into empty row");
                        check(!Attach(cargo, Slab("medium"), Index("LeftStorage3")),
                            "slab checks the full reservation of existing long normal cargo");
                        check(Attach(cargo, Slab("small"), Index("LeftStorage3")),
                            "cargo next to a long normal load remains usable");
                    }
                }
            }
            foreach (bool enabled in new[] { true, false })
            {
                quarryEnabled = enabled;
                VehicleCargoChecks.Run(api, world, configs.Keys.ToArray(), Create, setSide, check,
                    block => extraBlocks[block.Id] = block, drops, enabled);
            }
        }
        finally { setSide(EnumAppSide.Server); patch.UnpatchAll(patch.Id); }
    }
    static bool LoadShape(AssetLocation shapePath, ref Shape __result)
    {
        loadedModel = shapePath.Path;
        __result = JsonConvert.DeserializeObject<Shape>(File.ReadAllText(Path.Combine(assetRoot, "assets", shapePath.Domain, shapePath.Path)))!;
        return false;
    }
    sealed class Carrier(IWorldAccessor world, ItemSlot target) : EntityAgent
    {
        public override string GetName() => "grid test carrier";
        public override bool TryGiveItemStack(ItemStack stack) => new DummySlot(stack).TryPutInto(world, target, stack.StackSize) > 0;
    }
}
