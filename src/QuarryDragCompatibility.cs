using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

// Intercept the common use dispatcher before native chain or ground-storage
// interaction. No dependency on the optional restraints assembly.
internal sealed class QuarryDragCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.quarry-drag";
    private static readonly Harmony harmony = new(HarmonyId);
    private static readonly HashSet<QuarryDragCompatibility> owners = [];
    private static readonly ConditionalWeakTable<EntityAgent, Binding> bindings = new();
    private static readonly System.Reflection.MethodInfo? raySelection = AccessTools.Method(
        AccessTools.TypeByName("Vintagestory.Common.GameMain"), "RayTraceForSelection",
        [typeof(IWorldIntersectionSupplier), typeof(Ray), typeof(BlockSelection).MakeByRefType(),
            typeof(EntitySelection).MakeByRefType(), typeof(BlockFilter), typeof(EntityFilter)]);
    private sealed record Binding(ItemSlot Slot, ItemStack Material, BlockPos Position, int BlockId, long Started)
    {
        public bool Finished;
    }
    internal static QuarryDragCompatibility? TryCreate(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("stonequarryrepckfipil") && !api.ModLoader.IsModEnabled("stonequarry")) return null;
        if (owners.Count == 0)
        {
            foreach (var (method, prefix) in new[] {
                (nameof(CollectibleObject.OnHeldDropped), nameof(Drop)),
                (nameof(CollectibleObject.OnHeldUseStart), nameof(Start)),
                (nameof(CollectibleObject.OnHeldUseStep), nameof(Step)),
                (nameof(CollectibleObject.OnHeldUseStop), nameof(Stop)),
                (nameof(CollectibleObject.OnHeldUseCancel), nameof(Cancel)) })
                harmony.Patch(AccessTools.Method(typeof(CollectibleObject), method),
                    prefix: new HarmonyMethod(typeof(QuarryDragCompatibility), prefix));
            foreach (string method in new[] { nameof(ItemSlot.CanTakeFrom), nameof(ItemSlot.CanHold) })
                harmony.Patch(AccessTools.Method(typeof(ItemSlot), method),
                    prefix: new HarmonyMethod(typeof(QuarryDragCompatibility), nameof(GroundAdmission)));
            harmony.Patch(AccessTools.Method(typeof(Block), nameof(Block.GetPlacedBlockInteractionHelp)),
                postfix: new HarmonyMethod(typeof(QuarryDragCompatibility), nameof(Help)));
            harmony.Patch(AccessTools.Method(typeof(CollisionTester), nameof(CollisionTester.ApplyTerrainCollision)),
                postfix: new HarmonyMethod(typeof(QuarryDragCompatibility), nameof(SolidCollision)));
            if (raySelection != null) harmony.Patch(raySelection,
                postfix: new HarmonyMethod(typeof(QuarryDragCompatibility), nameof(SelectThroughGrass)));
        }
        var owner = new QuarryDragCompatibility();
        owners.Add(owner);
        return owner;
    }

    internal static bool FreeBinding(ItemStack? stack)
    {
        if (stack?.Collectible?.Code == null || stack.StackSize <= 0) return false;
        var code = stack.Collectible.Code;
        return code.Domain == "vsrestraints" && code.Path == "ironchain"
            && !new[] { "vschain-target", "vschain-token", "vschain-firstanchor" }.Any(stack.Attributes.HasAttribute);
    }

    internal static bool EmptyHands(EntityPlayer player) => player.RightHandItemSlot?.Empty != false && player.LeftHandItemSlot?.Empty != false;

    private static void SelectThroughGrass(object __instance, IWorldIntersectionSupplier supplier, Ray ray,
        ref BlockSelection blockSelection, ref EntitySelection entitySelection, BlockFilter? bfilter, EntityFilter? efilter)
    {
        if (blockSelection?.Position is not { } selected || supplier.GetBlock(selected) is not BlockPlant) return;
        // Grass uses a randomly offset selection box. It can protrude through
        // a load or tie its surface distance, in which case native picking keeps
        // the block and never sends the entity's Shift-right-click interaction.
        var tester = new AABBIntersectionTest(supplier); tester.LoadRayAndPos(ray);
        var loads = supplier.GetEntitiesAround(ray.origin, (float)ray.Length, (float)ray.Length,
            e => e is EntityQuarryDrag && e.Alive && (efilter == null || efilter(e)))
            .OfType<EntityQuarryDrag>().Where(e => CoveredPlant(e, selected, supplier.GetBlock(selected))
                && tester.RayIntersectsWithCuboid(e.SelectionBox, e.Pos.X, e.Pos.InternalY, e.Pos.Z)).ToArray();
        if (loads.Length == 0) return;
        BlockFilter filter = (p, b) => (bfilter == null || bfilter(p, b)) && !loads.Any(e => CoveredPlant(e, p, b));
        // Repeat native selection with only the covered plants excluded. This
        // retains range/entity filters and any intervening solid block. Recursion
        // ends because the selected covered plants are now filtered out.
        object?[] args = [supplier, ray, blockSelection, entitySelection, filter, efilter];
        raySelection!.Invoke(__instance, args);
        blockSelection = (BlockSelection)args[2]!;
        entitySelection = (EntitySelection)args[3]!;
    }

    private static bool CoveredPlant(EntityQuarryDrag load, BlockPos pos, Block block) => block is BlockPlant
        && load.Slab?.Block is { } slab && block.IsReplacableBy(slab)
        && pos.dimension == load.Pos.Dimension
        && new Cuboidd(-load.Width / 2, 0, -load.Depth / 2, load.Width / 2, load.Height, load.Depth / 2)
            .Translate(load.Pos.X, load.Pos.InternalY, load.Pos.Z).Contains(pos.X + .5, pos.InternalY + .5, pos.Z + .5);

    // A passive entity's terrain collision box does not stop other entities.
    // Clip native physics against moving slabs on BOTH client and server.
    private static void SolidCollision(Entity entity, EntityPos entityPos, ref Vec3d newPosition)
    {
        if (entity is not EntityAgent && entity is not EntityQuarryDrag || entity.CollisionBox == null
            || entity is EntityAgent { Controls.NoClip: true }) return;
        foreach (var other in entity.World.GetEntitiesAround(entityPos.XYZ, (float)(6 + entityPos.Motion.Length() * 3), 6))
        {
            if (other == entity || !other.Alive || other.CollisionBox == null || other.Pos.Dimension != entityPos.Dimension
                || other is not EntityQuarryDrag && (entity is not EntityQuarryDrag || other is not EntityAgent)
                || other is EntityAgent { Controls.NoClip: true }) continue;
            ClipSolid(entity, entityPos, newPosition, other.CollisionBox.ToDouble().Translate(other.Pos.XYZ));
        }
    }

    internal static void ClipSolid(Entity entity, EntityPos pos, Vec3d next, Cuboidd solid)
    {
        var box = entity.CollisionBox.ToDouble().Translate(pos.XYZ);
        double dx = next.X - pos.X, dy = next.Y - pos.Y, dz = next.Z - pos.Z;
        var direction = EnumPushDirection.None;
        double y = solid.pushOutY(box, dy, ref direction);
        if (direction != EnumPushDirection.None) entity.CollidedVertically = true;
        box.Translate(0, y, 0);
        double x = solid.pushOutX(box, dx, ref direction);
        if (direction != EnumPushDirection.None) entity.CollidedHorizontally = true;
        box.Translate(x, 0, 0);
        double z = solid.pushOutZ(box, dz, ref direction);
        if (direction != EnumPushDirection.None) entity.CollidedHorizontally = true;
        next.Set(pos.X + x, pos.Y + y, pos.Z + z);
    }

    private static void Help(Block __instance, IWorldAccessor world, ref WorldInteraction[] __result)
    {
        if (!QuarryShipCargoBehavior.IsSlab(new ItemStack(__instance))) return;
        var actions = new List<WorldInteraction>(__result ?? []);
        if (world.GetItem(new AssetLocation("vsrestraints:ironchain")) is { } chain)
            actions.Add(new() { ActionLangCode = "vsscpmod:quarry-drag-bind", MouseButton = EnumMouseButton.Right, Itemstacks = [new(chain)] });
        __result = actions.ToArray();
    }

    internal static bool ContainsSlab(ItemStack? stack, int depth = 0)
    {
        if (stack == null || depth > 16) return false;
        if (QuarryShipCargoBehavior.IsSlab(stack)) return true;
        return ContainsTree(stack.Attributes, depth);
    }
    private static bool GroundAdmission(ItemSlot __instance, ItemSlot sourceSlot, ref bool __result)
    {
        // Inventory packet transfers do not call OnHeldDropped. Reject before
        // removing anything from the source, including slabs inside containers.
        if (__instance.Inventory is not InventoryBasePlayer { ClassName: "ground" } ground
            || ground.Player?.Entity?.Alive != true || !ContainsSlab(sourceSlot.Itemstack)) return true;
        __result = false;
        Error(ground.Player.Entity, "quarry-no-drop");
        return false;
    }
    private static bool ContainsTree(Vintagestory.API.Datastructures.ITreeAttribute tree, int depth)
    {
        if (depth > 16) return false;
        foreach (var entry in tree)
        {
            if (entry.Key is "preset" or "carryonbackup") continue;
            if (entry.Value is Vintagestory.API.Datastructures.ItemstackAttribute item && ContainsSlab(item.value, depth + 1)) return true;
            if (entry.Value is Vintagestory.API.Datastructures.ITreeAttribute child && ContainsTree(child, depth + 1)) return true;
        }
        return false;
    }
    private static bool Drop(IWorldAccessor world, IPlayer byPlayer, ItemSlot slot, ref EnumHandling handling)
    {
        // Native death inventory drops must still work.
        if (byPlayer?.Entity?.Alive != true || !ContainsSlab(slot.Itemstack)) return true;
        handling = EnumHandling.PreventDefault;
        Error(byPlayer.Entity, "quarry-no-drop");
        return false;
    }
    internal static void Error(EntityAgent actor, string key)
    {
        if (actor.Api is ICoreClientAPI client) client.TriggerIngameError(actor, key, Lang.Get("vsscpmod:" + key));
        else if (actor is EntityPlayer { Player: Vintagestory.API.Server.IServerPlayer player })
            player.SendIngameError(key, Lang.Get("vsscpmod:" + key));
    }

    internal static BlockPos? SlabPosition(IWorldAccessor world, BlockSelection? selection)
    {
        if (selection?.Position == null) return null;
        BlockPos pos = selection.Position.Copy();
        Block block = world.BlockAccessor.GetBlock(pos);
        if (block is BlockMultiblock proxy) { pos.Add(proxy.OffsetInv); block = world.BlockAccessor.GetBlock(pos); }
        return QuarryShipCargoBehavior.IsSlab(new ItemStack(block)) ? pos : null;
    }

    internal static List<BlockPos> Cells(Block block, BlockPos controller)
    {
        var cells = new List<BlockPos>();
        if (block.GetBehavior<BlockBehaviorMultiblock>() is { } multi)
            multi.IterateOverEach(controller, pos => { cells.Add(pos.Copy()); return true; });
        else cells.Add(controller.Copy());
        return cells;
    }

    internal static bool Access(IWorldAccessor world, IPlayer player, IEnumerable<BlockPos> cells, bool removing)
    {
        var reinforcement = world.Api.ModLoader.GetModSystem<ModSystemBlockReinforcement>();
        foreach (BlockPos cell in cells)
        {
            if (world.BlockAccessor.GetChunkAtBlockPos(cell) == null
                || !world.Claims.TryAccess(player, cell, EnumBlockAccessFlags.BuildOrBreak)) return false;
            // Moving a reinforced slab would otherwise erase its reinforcement.
            if (removing && reinforcement?.IsReinforced(cell) == true) return false;
        }
        return true;
    }

    private static bool Start(ItemSlot slot, EntityAgent byEntity, BlockSelection? blockSel, EntitySelection? entitySel,
        EnumHandInteract useType, bool firstEvent, ref EnumHandHandling handling)
    {
        if (useType != EnumHandInteract.HeldItemInteract || byEntity is not EntityPlayer player
            || !FreeBinding(slot.Itemstack)) return true;
        if (entitySel?.Entity is EntityQuarryDrag drag)
        {
            handling = EnumHandHandling.PreventDefault;
            if (firstEvent) drag.OnInteract(player, slot, new Vec3d(), EnumInteractMode.Interact);
            return false;
        }
        BlockPos? pos = SlabPosition(player.World, blockSel);
        if (pos == null) return true;
        handling = EnumHandHandling.PreventDefault;
        if (!firstEvent) return false;
        bindings.Remove(player);
        if (!player.Alive || player.MountedOn != null || player.Pos.XYZ.DistanceTo(pos.ToVec3d().Add(.5, .5, .5)) > 6
            || !Access(player.World, player.Player, Cells(player.World.BlockAccessor.GetBlock(pos), pos), true))
        { Error(player, "quarry-drag-protected"); return false; }
        bindings.Add(player, new Binding(slot, slot.Itemstack!, pos, player.World.BlockAccessor.GetBlock(pos).Id, player.World.ElapsedMilliseconds));
        return false;
    }

    private static bool Step(float secondsPassed, ItemSlot slot, EntityAgent byEntity, BlockSelection? blockSel, ref EnumHandInteract __result)
    {
        if (!bindings.TryGetValue(byEntity, out var binding)) return true;
        __result = EnumHandInteract.None;
        if (binding.Finished) return false;
        if (!ReferenceEquals(slot, binding.Slot) || !ReferenceEquals(slot.Itemstack, binding.Material)
            || !FreeBinding(slot.Itemstack) || !byEntity.Alive || byEntity is not EntityPlayer player || player.MountedOn != null
            || SlabPosition(byEntity.World, blockSel) != binding.Position
            || player.World.BlockAccessor.GetBlock(binding.Position).Id != binding.BlockId
            || player.Pos.XYZ.DistanceTo(binding.Position.ToVec3d().Add(.5, .5, .5)) > 6)
        { binding.Finished = true; return false; }
        __result = EnumHandInteract.HeldItemInteract;
        player.StartAnimation("interactstatic");
        if (secondsPassed < 2 || player.World.ElapsedMilliseconds - binding.Started < 1800) return false;
        binding.Finished = true;
        __result = EnumHandInteract.None;
        if (player.World.Side == EnumAppSide.Server) Bind(player, slot, binding.Position);
        return false;
    }

    private static void Bind(EntityPlayer player, ItemSlot slot, BlockPos pos)
    {
        IWorldAccessor world = player.World;
        Block block = world.BlockAccessor.GetBlock(pos);
        if (!QuarryShipCargoBehavior.IsSlab(new ItemStack(block)) || !FreeBinding(slot.Itemstack)
            || !Access(world, player.Player, Cells(block, pos), true)) return;
        var stack = block.OnPickBlock(world, pos)?.Clone();
        if (!QuarryShipCargoBehavior.IsSlab(stack) || QuarryCarryCompatibility.CountBlocks(stack) <= 0) return;
        // Quarry's pick stack normalizes orientation to north. Keep this block's
        // orientation so its mesh and multiblock footprint do not rotate on binding.
        stack = new ItemStack(block) { Attributes = stack!.Attributes.Clone() };
        var properties = world.GetEntityType(new AssetLocation("vsscpmod", "quarrydrag"));
        if (properties == null) { Error(player, "quarry-drag-unavailable"); return; }
        var drag = (EntityQuarryDrag)world.ClassRegistry.CreateEntity(properties);
        drag.WatchedAttributes.SetItemstack("slab", stack);
        ItemStack material = slot.Itemstack!.Clone(); material.StackSize = 1;
        drag.WatchedAttributes.SetItemstack("binding", material);
        var cells = Cells(block, pos);
        drag.Pos.SetPos(cells.Min(p => p.X) + (cells.Max(p => p.X) - cells.Min(p => p.X) + 1) / 2d,
            cells.Min(p => p.Y), cells.Min(p => p.Z) + (cells.Max(p => p.Z) - cells.Min(p => p.Z) + 1) / 2d);
        drag.Pos.Dimension = pos.dimension;
        // Spawn first: a failure to create/initialize must leave the original slab intact.
        try { world.SpawnEntity(drag); }
        catch (Exception exception)
        {
            if (drag.EntityId != 0 && world.GetEntityById(drag.EntityId) == drag) drag.Die(EnumDespawnReason.Removed);
            world.Logger.Error("[vsscpmod] Unable to create quarry drag load: {0}", exception);
            Error(player, "quarry-drag-unavailable"); return;
        }
        if (drag.EntityId == 0 || world.GetEntityById(drag.EntityId) != drag)
        { Error(player, "quarry-drag-unavailable"); return; }
        world.BlockAccessor.SetBlock(0, pos); // Native multiblock removal clears every proxy, without drops.
        slot.TakeOut(1); slot.MarkDirty();
        drag.Toggle(player);
    }
    private static bool Stop(EntityAgent byEntity)
    {
        if (!bindings.Remove(byEntity)) return true;
        byEntity.StopAnimation("interactstatic");
        return false;
    }
    private static bool Cancel(EntityAgent byEntity, ref EnumHandInteract __result)
    {
        if (Stop(byEntity)) return true;
        __result = EnumHandInteract.None;
        return false;
    }
    public void Dispose()
    {
        if (!owners.Remove(this) || owners.Count != 0) return;
        harmony.UnpatchAll(HarmonyId); bindings.Clear();
    }
}
