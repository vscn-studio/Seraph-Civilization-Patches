using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

static class VehicleCargoChecks
{
    public static void Run(ICoreAPI api, IWorldAccessor world, string[] codes,
        System.Func<string, SyncedTreeAttribute?, EntityBehaviorAttachable> create,
        Action<EnumAppSide> side, Action<bool, string> check, Action<Block> register,
        List<ItemStack> drops, bool quarryEnabled)
    {
        var configField = AccessTools.Field(typeof(EntityBehaviorAttachable), "wearableSlots");
        var attach = AccessTools.Method(typeof(EntityBehaviorAttachable), "TryAttach");
        var detach = AccessTools.Method(typeof(EntityBehaviorAttachable), "TryRemoveAttachment");
        int blockId = 200;
        ItemStack Cargo(string category, int behind = 0, JObject? extra = null)
        {
            var attributes = new JObject { ["attachableToEntity"] = new JObject {
                ["categoryCode"] = category, ["requiresBehindSlots"] = behind } };
            if (extra != null) attributes.Merge(extra);
            var block = new Block { BlockId = blockId++, Code = new("game:cargo-test-" + blockId),
                MaxStackSize = 1, Attributes = new JsonObject(attributes) };
            AccessTools.Field(typeof(CollectibleObject), "api").SetValue(block, api);
            register(block);
            var stack = new ItemStack(block);
            stack.Attributes.SetString("cargo-content", "preserved");
            return stack;
        }
        void Clear(EntityBehaviorAttachable cargo) { foreach (var slot in cargo.Inventory) slot.Itemstack = null; }
        bool Attach(EntityBehaviorAttachable cargo, ItemStack stack, int index) =>
            (bool)attach.Invoke(cargo, [new DummySlot(stack), index, null])!;
        void Seating(EntityBehaviorAttachable cargo, WearableSlotConfig[] slots, int[] blocked, string tag)
        {
            var seatable = new EntityBehaviorSeatable(cargo.entity) { Seats = [], SeatConfigs = [] };
            foreach (int i in Enumerable.Range(0, slots.Length))
            {
                foreach (bool sameName in new[] { false, true })
                {
                    var config = new SeatConfig { APName = sameName ? slots[i].AttachmentPointCode : "Rider-" + i,
                        SelectionBox = slots[i].AttachmentPointCode };
                    bool reachedNative = false;
                    bool Consult() { reachedNative = true; return false; }
                    var seat = Proxy.Create<IMountableSeat>((m, _) => m.Name switch {
                        "get_Config" => config, "get_Entity" => cargo.entity, "CanMount" => Consult(), _ => null });
                    var selection = cargo.entity.GetBehavior<EntityBehaviorSelectionBoxes>()!;
                    selection.selectionBoxes = [new() { AttachPoint = new() { Code = config.SelectionBox } }];
                    foreach (var appSide in new[] { EnumAppSide.Client, EnumAppSide.Server })
                    {
                        side(appSide);
                        bool denied = blocked.Contains(i);
                        check(seatable.CanSitOn(seat) == !denied, tag + " " + appSide + " seat/AP mapping " + i + "/" + sameName);
                        check(SeatableInteractionHelp.GetOrCreateInteractionHelp(api, seatable, [seat], 0) == null == denied,
                            tag + " native hover help follows cargo occupancy");
                        reachedNative = false;
                        check(!new EntityAgent().TryMount(seat) && reachedNative == !denied, tag + " direct mounting follows cargo occupancy");
                    }
                }
            }
            side(EnumAppSide.Server);
        }
        // Unrelated entities must retain their native slots and reverse occupancy path.
        var nativeEntity = new EntityAgent { Api = api, World = world, Code = new("game:cargo-test") };
        AccessTools.Property(typeof(Entity), "Properties").SetValue(nativeEntity,
            new EntityProperties { Server = new([], new()), Client = new([], new()) });
        var native = new EntityBehaviorAttachable(nativeEntity);
        native.Initialize(nativeEntity.Properties, new JsonObject(JObject.Parse("""
            {wearableSlots:[
              {code:'a',forCategoryCodes:['storage'],attachmentPointCode:'A',behindSlots:['b','c']},
              {code:'b',forCategoryCodes:['storage'],attachmentPointCode:'B'},
              {code:'c',forCategoryCodes:['storage'],attachmentPointCode:'C'}]}
            """)));
        check(native.Inventory.All(s => s.GetType() == typeof(ItemSlotWearable))
            && Attach(native, Cargo("storage", 1), 0) && !Attach(native, Cargo("storage"), 2),
            "unrelated entities retain native slots and reverse rear occupancy");
        foreach (string code in codes)
        {
            var cargo = create(code, null);
            var slots = (WearableSlotConfig[])configField.GetValue(cargo)!;
            string tag = code + " quarry=" + quarryEnabled;
            string category = code.StartsWith("shipwright:") ? "chest" : "storage";
            int[] indices = Enumerable.Range(0, slots.Length).Where(i => slots[i].ForCategoryCodes.Contains(category)).ToArray();
            check(indices.All(i => slots[i].ForCategoryCodes.Contains("vsscpmodquarryslab") == quarryEnabled),
                tag + " ordinary compatibility works without enabling Quarry or adding unwanted categories");
            var ownable = new EntityBehaviorOwnable(cargo.entity);
            cargo.entity.Properties.Server.Behaviors.Add(ownable);
            cargo.entity.Properties.Client.Behaviors.Add(ownable);
            check(!Attach(cargo, Cargo(category), indices[0]) && cargo.Inventory.Empty,
                tag + " ordinary cargo retains native ownership restrictions");
            cargo.entity.Properties.Server.Behaviors.Remove(ownable);
            cargo.entity.Properties.Client.Behaviors.Remove(ownable);
            foreach (int point in Enumerable.Range(0, slots.Length).Except(indices).Where(i => slots[i].ForCategoryCodes.Length > 0))
            {
                Clear(cargo);
                string pointCategory = slots[point].ForCategoryCodes.First();
                check(Attach(cargo, Cargo(pointCategory), point), tag + " non-storage attachment keeps native category admission");
                Seating(cargo, slots, [point], tag + " non-storage attachment");
                Clear(cargo);
                check(Attach(cargo, Cargo(pointCategory, extra: JObject.Parse("{isSaddle:true}")), point),
                    tag + " non-storage saddle loads through native transfer");
                Seating(cargo, slots, [], tag + " non-storage saddle");
            }
            foreach (int anchor in indices)
            {
                Clear(cargo);
                var chest = Cargo(category);
                check(Attach(cargo, chest.Clone(), anchor), tag + " native single-cell cargo load");
                Seating(cargo, slots, [anchor], tag + " ordinary chest at " + anchor);
                cargo.ToBytes(false);
                var saved = new SyncedTreeAttribute();
                using var buffer = new MemoryStream();
                cargo.entity.WatchedAttributes.ToBytes(new BinaryWriter(buffer));
                saved.FromBytes(new BinaryReader(new MemoryStream(buffer.ToArray())));
                var reloaded = create(code, saved);
                check(reloaded.Inventory[anchor].Itemstack!.Attributes.Equals(world, chest.Attributes), tag + " normal cargo binary reload retains content");
                Seating(reloaded, slots, [anchor], tag + " reloaded chest");
                var receiver = new Receiver();
                check((bool)detach.Invoke(cargo, [receiver, anchor])! && cargo.Inventory.Empty
                    && receiver.Received?.Attributes.Equals(world, chest.Attributes) == true, tag + " native unload keeps normal cargo content");
                Seating(cargo, slots, [], tag + " unloaded chest");

                // Use the same seat exceptions as vanilla, with slot-specific maps scoped to this anchor.
                foreach (JObject attributes in new[] {
                    JObject.Parse("{isSaddle:true}"),
                    JObject.Parse("{attachableToEntity:{seatConfig:{controllable:false}}}"),
                    new JObject { ["attachableToEntity"] = new JObject { ["seatConfigBySlotCode"] = new JObject { [slots[anchor].Code] = new JObject() } } } })
                {
                    cargo.Inventory[anchor].Itemstack = Cargo(category, extra: attributes);
                    Seating(cargo, slots, [], tag + " native seat-providing item");
                }
                cargo.Inventory[anchor].Itemstack = Cargo(category, extra: JObject.Parse("{attachableToEntity:{seatConfigBySlotCode:{unrelated:{}}}}"));
                Seating(cargo, slots, [anchor], tag + " unrelated slot seat config does not make cargo sit-able");
                Clear(cargo);

                var seatConfig = new SeatConfig { APName = "Rider", SelectionBox = slots[anchor].AttachmentPointCode };
                var seatable = new EntityBehaviorSeatable(cargo.entity) { SeatConfigs = [seatConfig], Seats = [
                    Proxy.Create<IMountableSeat>((m, _) => m.Name switch { "get_Config" => seatConfig, "get_Passenger" => new EntityAgent(), _ => null })] };
                cargo.entity.Properties.Server.Behaviors.Add(seatable);
                cargo.entity.Properties.Client.Behaviors.Add(seatable);
                foreach (var appSide in new[] { EnumAppSide.Client, EnumAppSide.Server })
                {
                    side(appSide);
                    var source = new DummySlot(chest.Clone());
                    check(!Attach(cargo, source.Itemstack!, anchor) && !cargo.Inventory[anchor].CanHold(source)
                        && !cargo.Inventory[anchor].CanTakeFrom(source) && cargo.Inventory.Empty && source.StackSize == 1,
                        tag + " passenger blocks normal attachment and direct inventory insertion on " + appSide);
                }
                side(EnumAppSide.Server);
                cargo.entity.Properties.Server.Behaviors.Remove(seatable);
                cargo.entity.Properties.Client.Behaviors.Remove(seatable);
            }
            // The real Cartwright layouts declare rows with up to two rear cells.
            foreach (int anchor in indices.Where(i => (slots[i].BehindSlots?.Length ?? 0) > 0))
            {
                int[] rear = slots[anchor].BehindSlots.Select(c => Array.FindIndex(slots, s => s.Code == c)).ToArray();
                for (int count = 1; count <= rear.Length; count++)
                {
                    Clear(cargo);
                    var longCargo = Cargo(category, count);
                    int[] footprint = [anchor, ..rear.Take(count)];
                    check(Attach(cargo, longCargo.Clone(), anchor), tag + " native long cargo loads");
                    check(footprint.All(i => cargo.GetSlotIndexFromSelectionBoxIndex(i) == anchor), tag + " all long cargo cells resolve to one owner");
                    Seating(cargo, slots, footprint, tag + " long cargo rear count=" + count);
                    cargo.Inventory[anchor].Itemstack = Cargo(category, count,
                        JObject.Parse("{attachableToEntity:{seatConfig:{controllable:false}}}"));
                    Seating(cargo, slots, footprint.Skip(1).ToArray(), tag + " long seat allows anchor only");
                    cargo.Inventory[anchor].Itemstack = longCargo.Clone();
                    foreach (int target in footprint.Skip(1))
                        check(!Attach(cargo, Cargo(category), target) && !cargo.Inventory[target].CanTakeFrom(new DummySlot(Cargo(category))),
                            tag + " rear reservation rejects other normal cargo");
                    if (count < rear.Length)
                        check(Attach(cargo, Cargo(category), rear[count]), tag + " unused declared rear cells remain loadable");
                    Clear(cargo);
                    check(Attach(cargo, longCargo.Clone(), anchor), tag + " reload long cargo for unload");
                    cargo.ToBytes(false); cargo.FromBytes(true);
                    var receiver = new Receiver();
                    check((bool)detach.Invoke(cargo, [receiver, footprint.Last()])! && cargo.Inventory.Empty
                        && receiver.Received?.Attributes.Equals(world, longCargo.Attributes) == true,
                        tag + " reserved rear click unloads one complete long item after sync");

                    foreach (int target in footprint)
                    {
                        Clear(cargo);
                        cargo.Inventory[target].Itemstack = Cargo(category);
                        foreach (var appSide in new[] { EnumAppSide.Client, EnumAppSide.Server })
                        {
                            side(appSide);
                            check(!Attach(cargo, longCargo.Clone(), anchor) && cargo.Inventory.Count(s => !s.Empty) == 1,
                                tag + " long cargo checks every cell for ordinary cargo on " + appSide);
                        }
                        side(EnumAppSide.Server);
                        Clear(cargo);
                        var seatConfig = new SeatConfig { APName = "Rider", SelectionBox = slots[target].AttachmentPointCode };
                        var seatable = new EntityBehaviorSeatable(cargo.entity) { SeatConfigs = [seatConfig], Seats = [
                            Proxy.Create<IMountableSeat>((m, _) => m.Name switch { "get_Config" => seatConfig, "get_Passenger" => new EntityAgent(), _ => null })] };
                        cargo.entity.Properties.Server.Behaviors.Add(seatable);
                        cargo.entity.Properties.Client.Behaviors.Add(seatable);
                        foreach (var appSide in new[] { EnumAppSide.Client, EnumAppSide.Server })
                        {
                            side(appSide);
                            var source = new DummySlot(longCargo.Clone());
                            check(!Attach(cargo, source.Itemstack!, anchor) && !cargo.Inventory[anchor].CanTakeFrom(source),
                                tag + " long cargo checks every passenger on " + appSide);
                        }
                        side(EnumAppSide.Server);
                        cargo.Inventory[anchor].Itemstack = longCargo.Clone();
                        check(!(bool)detach.Invoke(cargo, [new Receiver(), anchor])!, tag + " cannot unload long cargo under a legacy passenger");
                        cargo.entity.Properties.Server.Behaviors.Remove(seatable);
                        cargo.entity.Properties.Client.Behaviors.Remove(seatable);
                    }
                    Clear(cargo);
                    cargo.Inventory[anchor].Itemstack = longCargo.Clone();
                    cargo.Inventory[rear[0]].Itemstack = Cargo(category, extra: JObject.Parse("{isSaddle:true}"));
                    check(cargo.GetSlotIndexFromSelectionBoxIndex(rear[0]) == rear[0], tag + " legacy overlap keeps each real cargo accessible");
                    Seating(cargo, slots, footprint, tag + " overlapping legacy saddle does not hide long cargo");
                    Clear(cargo);
                    cargo.Inventory[anchor].Itemstack = longCargo.Clone();
                    drops.Clear(); cargo.OnEntityDeath(new DamageSource());
                    check(drops.Count == 1 && drops[0].Attributes.Equals(world, longCargo.Attributes) && cargo.Inventory.Empty,
                        tag + " long cargo drops once without reservation duplicates");
                }
            }
        }
    }
    sealed class Receiver : EntityAgent
    {
        public ItemStack? Received;
        public override string GetName() => "cargo receiver";
        public override bool TryGiveItemStack(ItemStack stack) { Received = stack.Clone(); return true; }
    }
}
