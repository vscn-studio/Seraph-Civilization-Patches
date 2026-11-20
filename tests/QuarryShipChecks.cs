using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;
using SeraphCivilizationPatches;

static class QuarryShipChecks
{
    public static void Run(string root, ICoreAPI api, IWorldAccessor world, Block slab, Block rock,
        System.Func<int, int, ItemStack> makeSlab, Action<EnumAppSide> setSide)
    {
        int count = 0;
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
        var behavior = new QuarryShipCargoBehavior(slab);
        behavior.OnLoaded(api);
        var oldBehaviors = slab.CollectibleBehaviors;
        slab.CollectibleBehaviors = [behavior];
        var patcher = new Harmony("vsscpmod.tests.quarryship");
        Type fix = typeof(SeraphCivilizationPatchesSystem).Assembly.GetType("SeraphCivilizationPatches.QuarryShipCompatibility")!;
        IDisposable? owner = null;
        try
        {
            foreach (string[] enabled in new[] { Array.Empty<string>(), new[] { "stonequarryrepckfipil" } })
            {
                var loader = Proxy.Create<IModLoader>((m, args) => m.Name == "IsModEnabled" ? enabled.Contains((string)args![0]!) : null);
                var withoutDependency = Proxy.Create<ICoreAPI>((m, _) => m.Name == "get_ModLoader" ? loader : null);
                Check(AccessTools.Method(fix, "TryCreate").Invoke(null, [withoutDependency]) == null,
                    "cargo integration skips incomplete mod set: " + string.Join(",", enabled));
            }
            foreach (string vehicleMod in new[] { "shipwright", "cartwrightscaravan" })
            {
                var vehicleOnlyLoader = Proxy.Create<IModLoader>((m, args) => m.Name == "IsModEnabled"
                    ? (string)args![0]! == vehicleMod : null);
                var vehicleOnlyApi = Proxy.Create<ICoreAPI>((m, _) => m.Name == "get_ModLoader" ? vehicleOnlyLoader : null);
                using (var vehicleOnlyOwner = (IDisposable?)AccessTools.Method(fix, "TryCreate").Invoke(null, [vehicleOnlyApi]))
                    Check(vehicleOnlyOwner != null, vehicleMod + " occupancy fixes install without Quarry");
                var loader = Proxy.Create<IModLoader>((m, args) => m.Name == "IsModEnabled"
                    ? (string)args![0]! is "stonequarryrepckfipil" || (string)args[0]! == vehicleMod : null);
                var singleVehicleApi = Proxy.Create<ICoreAPI>((m, _) => m.Name == "get_ModLoader" ? loader : null);
                using var singleOwner = (IDisposable?)AccessTools.Method(fix, "TryCreate").Invoke(null, [singleVehicleApi]);
                Check(singleOwner != null, vehicleMod + " cargo support installs without the other vehicle mod");
                var isolatedBehavior = new QuarryShipCargoBehavior(slab);
                isolatedBehavior.OnLoaded(singleVehicleApi);
                Check(isolatedBehavior.IsAttachable(new EntityAgent { Code = new("cartwrightscaravan:cart-basiccart-classic-oak") }, makeSlab(64, 1))
                        == (vehicleMod == "cartwrightscaravan"), "slab attachment respects the enabled vehicle mod: " + vehicleMod);
            }
            owner = (IDisposable)AccessTools.Method(fix, "TryCreate").Invoke(null, [api])!;
            Check(owner != null, "optional Shipwright/Quarry integration installs");
            // The test has no animated renderer: the hitbox resolver supplies its slot index.
            patcher.Patch(AccessTools.Method(typeof(EntityBehaviorAttachable), "GetSlotIndexFromSelectionBoxIndex"),
                prefix: new HarmonyMethod(typeof(QuarryShipChecks), nameof(SelectionIndex)));
            var ship = new EntityAgent { Api = api, World = world, Code = new("shipwright:boat-coracle-oak") };
            AccessTools.Property(typeof(Entity), "Properties").SetValue(ship,
                new EntityProperties { Server = new([], new()), Client = new([], new()) });
            var cargo = new EntityBehaviorAttachable(ship);
            var config = new JsonObject(JObject.Parse("""
                {"wearableSlots":[
                  {"code":"cargo","forCategoryCodes":["chest","basket"],"attachmentPointCode":"Cargo"},
                  {"code":"small","forCategoryCodes":["basket","lantern"],"attachmentPointCode":"Small"}
                ]}
                """));
            cargo.Initialize(ship.Properties, config);
            Check(IAttachableToEntity.FromCollectible(slab) == behavior, "native collectible interface finds slab block behavior");
            Check(cargo.Inventory[0].GetType().Name == "QuarryShipCargoSlot" && cargo.Inventory[1].GetType().Name == "VehicleCargoSlot",
                "only chest-capable ship slots receive heavy slab support");
            var source = new DummySlot(makeSlab(64, 2));
            source.Itemstack!.Attributes.SetString("custom-data", "preserved");
            source.Itemstack.Attributes.GetOrAddTreeAttribute("preset").SetString("0", "game:rock-granite");
            var original = source.Itemstack.Attributes.Clone();
            var attach = AccessTools.Method(typeof(EntityBehaviorAttachable), "TryAttach");
            bool Attach(int index) => (bool)attach.Invoke(cargo, [source, index, null])!;
            Check(!Attach(1) && source.StackSize == 2, "basket-only cargo points reject slabs without consuming anything");
            setSide(EnumAppSide.Client);
            Check(Attach(0) && source.StackSize == 2 && cargo.Inventory[0].Empty, "client attachment validates without moving or duplicating cargo");
            setSide(EnumAppSide.Server);
            Check(Attach(0) && source.StackSize == 1 && cargo.Inventory[0].StackSize == 1,
                "native server attachment transfers exactly one heavy slab");
            Check(cargo.Inventory[0].Itemstack!.Attributes.Equals(world, original), "native attachment preserves all nested slab attributes");
            Check(!Attach(0) && source.StackSize == 1, "occupied cargo point rejects a second slab");
            Check(!new InventoryGeneric(1, "chest-normal", api)[0].CanTakeFrom(source), "ordinary chests continue to reject heavy slabs");
            var flags = cargo.Inventory[0].StorageType;
            Check(cargo.Inventory[0].CanHold(source) && cargo.Inventory[0].StorageType == flags,
                "heavy slab admission restores storage flags after native checks");
            cargo.Inventory.PutLocked = true;
            Check(!cargo.Inventory[0].CanHold(source), "ship slot retains native inventory locks");
            cargo.Inventory.PutLocked = false;
            cargo.ToBytes(false);
            byte[] saved;
            using (var stream = new MemoryStream())
            {
                ship.WatchedAttributes.ToBytes(new BinaryWriter(stream));
                saved = stream.ToArray();
            }
            cargo.Inventory[0].Itemstack = null;
            ship.WatchedAttributes.FromBytes(new BinaryReader(new MemoryStream(saved)));
            cargo.FromBytes(false);
            Check(cargo.Inventory[0].Itemstack!.Attributes.Equals(world, original), "native boat binary save/reload restores complete rock inventory and custom data");
            var backpack = new InventoryGeneric(1, "backpack-ship-test", api);
            backpack[0] = new ItemSlotBackpack(backpack);
            var carrier = new Carrier(world, backpack[0]);
            var detach = AccessTools.Method(typeof(EntityBehaviorAttachable), "TryRemoveAttachment");
            Check((bool)detach.Invoke(cargo, [carrier, 0])! && cargo.Inventory[0].Empty && backpack[0].StackSize == 1,
                "native unloading returns a heavy slab to an empty backpack slot");
            Check(backpack[0].Itemstack!.Attributes.Equals(world, original), "unloaded slab preserves rock counts and preset");
            var beType = System.Reflection.Assembly.LoadFrom(Path.Combine(root, "tests/dependencies/quarry/StoneQuarry.dll"))
                .GetType("StoneQuarry.BEStoneSlab")!;
            var be = (BlockEntity)Activator.CreateInstance(beType)!;
            be.Api = api;
            be.Pos = new Vintagestory.API.MathTools.BlockPos(0, 1, 0);
            AccessTools.Method(beType, "ContentFromAttributes").Invoke(be, [backpack[0].Itemstack!.Attributes.Clone(), world]);
            var restored = (InventoryBase)AccessTools.Property(beType, "Inventory").GetValue(be)!;
            Check(restored.Sum(slot => slot.StackSize) == 64, "actual Stone Quarry block entity restores all 64 rocks after unloading");
            Check(Attach(0), "cargo point can be reused after unloading");
            Check(!(bool)detach.Invoke(cargo, [carrier, 0])! && !cargo.Inventory[0].Empty,
                "full backpack leaves cargo attached without losing or dropping the slab");
            Check(!behavior.IsAttachable(new EntityAgent { Code = new("game:elk-male") }, backpack[0].Itemstack!),
                "attachment behavior rejects unrelated entities");
            Check(!behavior.IsAttachable(new EntityAgent { Code = new("cartwrightscaravan:sled-basicsled-oak") }, backpack[0].Itemstack!),
                "cart support does not add sled cargo points");
            var assetPatch = JArray.Parse(File.ReadAllText(Path.Combine(root,
                "assets/vsscpmod/patches/compatibility/quarry-ship-cargo.json")));
            Check(assetPatch.Count == 1 && assetPatch[0]!["dependsOn"]!.Count() == 1
                && (string?)assetPatch[0]!["dependsOn"]![0]!["modid"] == "stonequarryrepckfipil",
                "one slab behavior is loaded with Quarry even when Shipwright is absent");

            // Use Cartwrights 1.9.1's actual categories, neighboring slots and step parents.
            var cartAsset = JObject.Parse(File.ReadAllText(Path.Combine(root,
                "tests/dependencies/cartwrights/assets/cartwrightscaravan/entities/nonliving/cart.json")));
            foreach (string cartType in new[] { "basic", "slim" })
            {
                var slotsJson = (JArray)cartAsset["behaviorConfigs"]!["rideableaccessories"]!["wearableSlotsByType"]!["*" + cartType + "*"]!;
                string unchangedConfig = slotsJson.ToString();
                var cart = new EntityAgent { Api = api, World = world, Code = new("cartwrightscaravan:cart-" + cartType + "cart-classic-oak") };
                AccessTools.Property(typeof(Entity), "Properties").SetValue(cart,
                    new EntityProperties { Server = new([], new()), Client = new([], new()) });
                var cartCargo = new EntityBehaviorRideableAccessories(cart);
                cartCargo.Initialize(cart.Properties, new JsonObject(new JObject { ["wearableSlots"] = slotsJson.DeepClone() }));
                var configs = (WearableSlotConfig[])AccessTools.Field(typeof(EntityBehaviorAttachable), "wearableSlots").GetValue(cartCargo)!;
                int[] cargoIndices = Enumerable.Range(0, configs.Length)
                    .Where(i => configs[i].ForCategoryCodes.Contains("storage")).ToArray();
                Check(cargoIndices.Length == (cartType == "basic" ? 6 : 3)
                    && cargoIndices.All(i => cartCargo.Inventory[i].GetType().Name == "QuarryShipCargoSlot"),
                    cartType + " cart adapts exactly its original storage points");
                Check(Enumerable.Range(0, configs.Length).Where(i => !cargoIndices.Contains(i))
                    .All(i => cartCargo.Inventory[i].GetType().Name == "VehicleCargoSlot"
                        && !configs[i].ForCategoryCodes.Contains(behavior.GetCategoryCode(source.Itemstack!))),
                    cartType + " cart grip, canopy and decorative points retain their categories without slab support");
                Check(slotsJson.ToString() == unchangedConfig, cartType + " cart does not mutate shared asset configuration");
                var chestBlock = new Block { Code = new("game:chest-normal"),
                    Attributes = new JsonObject(JObject.Parse("""{"attachableToEntity":{"categoryCode":"storage"}}""")) };
                Check(cartCargo.Inventory[cargoIndices[0]].CanHold(new DummySlot(new ItemStack(chestBlock))),
                    cartType + " cart retains ordinary storage cargo admission");
                var cartSource = new DummySlot(makeSlab(64, 8));
                cartSource.Itemstack!.Attributes.SetString("custom-data", "cart-preserved");
                var cartOriginal = cartSource.Itemstack.Attributes.Clone();
                bool CartAttach(int i) => (bool)attach.Invoke(cartCargo, [cartSource, i, null])!;
                Check(!CartAttach(0) && cartSource.StackSize == 8, cartType + " cart rejects slab on driver grip");
                setSide(EnumAppSide.Client);
                Check(CartAttach(cargoIndices[0]) && cartSource.StackSize == 8 && cartCargo.Inventory.Empty,
                    cartType + " client attachment does not modify inventory");
                setSide(EnumAppSide.Server);
                foreach (int i in cargoIndices)
                    Check(CartAttach(i) && cartCargo.Inventory[i].StackSize == 1
                        && cartCargo.Inventory[i].Itemstack!.Attributes.Equals(world, cartOriginal),
                        cartType + " cart loads one heavy slab with full attributes into " + configs[i].Code);
                Check(!CartAttach(cargoIndices[0]) && cartSource.StackSize == 8 - cargoIndices.Length,
                    cartType + " occupied cargo rejects another slab without consuming it");
                cartCargo.Inventory.PutLocked = true;
                Check(!cartCargo.Inventory[cargoIndices[0]].CanHold(cartSource), cartType + " cart keeps native inventory locks");
                cartCargo.Inventory.PutLocked = false;
                cartCargo.ToBytes(false);
                using var stream = new MemoryStream();
                cart.WatchedAttributes.ToBytes(new BinaryWriter(stream));
                foreach (int i in cargoIndices) cartCargo.Inventory[i].Itemstack = null;
                cart.WatchedAttributes.FromBytes(new BinaryReader(new MemoryStream(stream.ToArray())));
                cartCargo.FromBytes(false);
                Check(cargoIndices.All(i => cartCargo.Inventory[i].Itemstack!.Attributes.Equals(world, cartOriginal)),
                    cartType + " cart binary save/reload preserves every loaded slab");
                backpack[0].Itemstack = null;
                Check((bool)detach.Invoke(cartCargo, [carrier, cargoIndices[0]])!
                    && cartCargo.Inventory[cargoIndices[0]].Empty
                    && backpack[0].Itemstack!.Attributes.Equals(world, cartOriginal),
                    cartType + " native unloading returns intact heavy slab to backpack");
                Check(CartAttach(cargoIndices[0]) && !(bool)detach.Invoke(cartCargo, [carrier, cargoIndices[0]])!
                    && !cartCargo.Inventory[cargoIndices[0]].Empty,
                    cartType + " full backpack leaves slab attached");
                foreach (string size in new[] { "small", "medium", "large", "huge", "giant" })
                {
                    var gear = Newtonsoft.Json.JsonConvert.DeserializeObject<Shape>(File.ReadAllText(Path.Combine(root,
                        "assets/vsscpmod/shapes/quarrycargo", size + ".json")))!;
                    gear.SubclassForStepParenting(behavior.GetTexturePrefixCode(cartSource.Itemstack!));
                    gear.ResolveReferences(api.Logger, size);
                    EntityBehaviorContainer.applyStepParentOverrides(configs[cargoIndices[0]].StepParentTo, gear);
                    string parentName = configs[cargoIndices[0]].StepParentTo[""].ElementName;
                    var parent = new Shape { Elements = [new ShapeElement { Name = parentName, From = [0, 0, 0], To = [0, 0, 0] }] };
                    Check(parent.StepParentShape(gear, size, cartType, api.Logger, (_, _) => { }),
                        cartType + " cart " + size + " pallet attaches to original storage parent");
                }
            }
            QuarryCargoGridChecks.Run(root, api, world, makeSlab, setSide, Check);
            var a = makeSlab(64, 1); a.Attributes = original.Clone();
            var b = a.Clone(); b.Attributes.GetTreeAttribute("preset").SetString("0", "game:rock-basalt");
            Check(behavior.GetTexturePrefixCode(a) != behavior.GetTexturePrefixCode(b), "different rock cargo uses isolated texture prefixes");
            Check(behavior.GetTexturePrefixCode(a) == behavior.GetTexturePrefixCode(a.Clone()), "texture prefix survives clone/save without unstable hashes");
            foreach (string size in new[] { "small", "medium", "large", "huge", "giant" })
            {
                string json = File.ReadAllText(Path.Combine(root, "assets/vsscpmod/shapes/quarrycargo", size + ".json"));
                var model = JObject.Parse(json);
                int expected = size switch { "small" => 1, "medium" => 2, "large" => 4, "huge" => 8, _ => 12 };
                Check(model["elements"]!.Count() == expected + 2, size + " cargo model has one segment per quarry render piece and two pallet runners");
                var gear = Newtonsoft.Json.JsonConvert.DeserializeObject<Shape>(json)!;
                string prefix = behavior.GetTexturePrefixCode(a);
                gear.SubclassForStepParenting(prefix);
                gear.ResolveReferences(api.Logger, size);
                behavior.CollectTextures(a, gear, prefix, new());
                EntityBehaviorContainer.applyStepParentOverrides(new() { [""] = new() { ElementName = "CargoParent" } }, gear);
                var parent = new Shape { Elements = [new ShapeElement { Name = "CargoParent", From = [0, 0, 0], To = [0, 0, 0] }] };
                Check(parent.StepParentShape(gear, size, "boat", api.Logger, (_, _) => { }) && parent.Elements[0].Children!.Length == expected + 2,
                    size + " model attaches through native ship step-parent overrides");
            }
            var shape = new Shape();
            var granite = new Block { Textures = new Dictionary<string, CompositeTexture> { ["all"] = new(new AssetLocation("game:block/stone/rock/granite1")) } };
            var basalt = new Block { Textures = new Dictionary<string, CompositeTexture> { ["all"] = new(new AssetLocation("game:block/stone/rock/basalt1")) } };
            var textureWorld = Proxy.Create<IWorldAccessor>((m, args) => m.Name == "GetBlock"
                ? (args![0]!.ToString() == "game:rock-granite" ? granite : basalt) : null);
            behavior.OnLoaded(Proxy.Create<ICoreAPI>((m, _) => m.Name == "get_World" ? textureWorld : null));
            behavior.CollectTextures(a, shape, behavior.GetTexturePrefixCode(a), new());
            Check(shape.Textures["stone0"].ToString() == "game:block/stone/rock/granite1", "cargo resolves actual granite texture from slab preset");
            Check(shape.Textures["stone1"].ToString() == "game:block/transparent", "empty/missing preset pieces remain transparent");
            behavior.CollectTextures(b, shape, behavior.GetTexturePrefixCode(b), new());
            Check(shape.Textures["stone0"].ToString() == "game:block/stone/rock/basalt1", "second cargo resolves basalt without reusing granite texture");
            Console.WriteLine($"Quarry / Shipwright / Cartwright: {count} checks passed.");
        }
        finally { setSide(EnumAppSide.Server); slab.CollectibleBehaviors = oldBehaviors; owner?.Dispose(); patcher.UnpatchAll(patcher.Id); }
    }
    static bool SelectionIndex(int boxIndex, ref int __result) { __result = boxIndex; return false; }
    sealed class Carrier(IWorldAccessor world, ItemSlot target) : EntityAgent
    {
        public override string GetName() => "test carrier";
        public override bool TryGiveItemStack(ItemStack stack) => new DummySlot(stack).TryPutInto(world, target, stack.StackSize) > 0;
    }
}
