using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace SeraphCivilizationPatches;

/// <summary>A persistent slab load, never an auto-pickup/despawning item entity.</summary>
public sealed class EntityQuarryDrag : Entity
{
    private const string SpeedStat = "vsscpmod-quarrydrag";
    private EntityPlayer? holder;
    private ItemStack? slab;
    private bool placing;
    public ItemStack? Slab => slab;
    public string HolderUid => WatchedAttributes.GetString("holder", "");
    public bool Chain => WatchedAttributes.GetItemstack("binding")?.Collectible?.Code?.Domain == "vsrestraints";
    private bool HasBinding => WatchedAttributes.GetItemstack("binding")?.Collectible != null;
    private bool HasLegacyWheels => WatchedAttributes.GetItemstack("wheels") is { StackSize: > 0 };
    public float Width { get; private set; } = 1;
    public float Depth { get; private set; } = 1;
    public float Height { get; private set; } = 1;
    public Vec3d MeshOffset { get; private set; } = new(-.5, 0, -.5);
    private Vec3i controllerOffset = new();

    public override void Initialize(EntityProperties properties, ICoreAPI api, long chunkindex3d)
    {
        slab = WatchedAttributes.GetItemstack("slab");
        slab?.ResolveBlockOrItem(api.World);
        var material = WatchedAttributes.GetItemstack("binding");
        material?.ResolveBlockOrItem(api.World);
        // Old loads keep their exact wheel stack until the slab is placed.
        WatchedAttributes.GetItemstack("wheels")?.ResolveBlockOrItem(api.World);
        base.Initialize(properties, api, chunkindex3d);
        UpdateBounds();
        // A connection never survives unloading/restarting. The slab and materials do.
        if (api.Side == EnumAppSide.Server) WatchedAttributes.SetString("holder", "");
    }

    private void UpdateBounds()
    {
        if (QuarryShipCargoBehavior.IsSlab(slab))
        {
            var cells = QuarryDragCompatibility.Cells(slab!.Block, new BlockPos(0));
            Width = cells.Max(p => p.X) - cells.Min(p => p.X) + 1;
            Depth = cells.Max(p => p.Z) - cells.Min(p => p.Z) + 1;
            Height = cells.Max(p => p.Y) - cells.Min(p => p.Y) + 1;
            controllerOffset.Set(-cells.Min(p => p.X), -cells.Min(p => p.Y), -cells.Min(p => p.Z));
            MeshOffset = new(controllerOffset.X - Width / 2, controllerOffset.Y, controllerOffset.Z - Depth / 2);
            SelectionBox = new(-Width / 2, 0, -Depth / 2, Width / 2, Height, Depth / 2);
            OriginSelectionBox = SelectionBox.Clone();
            CollisionBox = new(-Width / 2 + .025f, 0, -Depth / 2 + .025f, Width / 2 - .025f, Height - .025f, Depth / 2 - .025f);
            OriginCollisionBox = CollisionBox.Clone();
        }
    }
    public override bool ReceiveDamage(DamageSource damageSource, float damage) => false;
    public override bool CanCollect(Entity byEntity) => false;
    public override string GetName() => Lang.Get(HasLegacyWheels && !HasBinding
        ? "vsscpmod:quarry-drag-legacy-name" : "vsscpmod:quarry-drag-name");
    public override string GetInfoText()
    {
        var info = Lang.Get(HasBinding ? "vsscpmod:quarry-drag-info" : "vsscpmod:quarry-drag-contents",
            QuarryCarryCompatibility.CountBlocks(slab));
        return HasLegacyWheels ? info + " " + Lang.Get("vsscpmod:quarry-drag-legacy-recover") : info;
    }

    public override WorldInteraction[] GetInteractionHelp(IClientWorldAccessor world, EntitySelection es, IClientPlayer player)
    {
        WorldInteraction place = new() { ActionLangCode = "vsscpmod:quarry-drag-place", MouseButton = EnumMouseButton.Right, HotKeyCode = "shift" };
        return HasBinding
            ? [new() { ActionLangCode = "vsscpmod:quarry-drag-toggle", MouseButton = EnumMouseButton.Right }, place]
            : [place];
    }

    public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition, EnumInteractMode mode)
    {
        if (mode != EnumInteractMode.Interact || byEntity is not EntityPlayer player || !player.Alive
            || player.MountedOn != null || World.Side != EnumAppSide.Server || placing
            || player.Pos.Dimension != Pos.Dimension || player.Pos.XYZ.DistanceTo(Pos.XYZ) > 6) return;
        if (!CanAccess(player, Pos.XYZ)) { QuarryDragCompatibility.Error(player, "quarry-drag-protected"); return; }
        if (HolderUid.Length > 0 && HolderUid != player.PlayerUID)
        { QuarryDragCompatibility.Error(player, "quarry-drag-busy"); return; }
        if (!QuarryDragCompatibility.EmptyHands(player) || !itemslot.Empty)
        { QuarryDragCompatibility.Error(player, "quarry-drag-empty-hands"); return; }
        if (player.Controls.Sneak) Place(player);
        else Toggle(player);
    }

    internal void Toggle(EntityPlayer player)
    {
        if (!HasBinding) return;
        if (HolderUid == player.PlayerUID) { Release(); return; }
        foreach (var entity in World.GetEntitiesAround(player.Pos.XYZ, 16, 16))
            if (entity is EntityQuarryDrag other && other != this && other.HolderUid == player.PlayerUID) other.Release();
        holder = player;
        WatchedAttributes.SetString("holder", player.PlayerUID);
        player.Stats.Set("walkspeed", SpeedStat, -.5f, false);
    }

    private void Release()
    {
        holder?.Stats.Remove("walkspeed", SpeedStat);
        holder = null;
        WatchedAttributes.SetString("holder", "");
        Pos.Motion.X = Pos.Motion.Z = 0;
    }

    private bool CanAccess(EntityPlayer player, Vec3d center)
    {
        BlockPos origin = new((int)Math.Floor(center.X + CollisionBox.X1 + .001), (int)Math.Floor(center.Y + .025),
            (int)Math.Floor(center.Z + CollisionBox.Z1 + .001), Pos.Dimension);
        var cells = new List<BlockPos>();
        for (int x = origin.X; x <= (int)Math.Floor(center.X + CollisionBox.X2 - .001); x++)
        for (int y = origin.Y; y <= (int)Math.Floor(center.Y + CollisionBox.Y2 - .001); y++)
        for (int z = origin.Z; z <= (int)Math.Floor(center.Z + CollisionBox.Z2 - .001); z++)
            cells.Add(new(x, y, z, Pos.Dimension));
        return QuarryDragCompatibility.Access(World, player.Player, cells, false);
    }

    public override void OnGameTick(float dt)
    {
        base.OnGameTick(dt);
        if (World.Side != EnumAppSide.Server) return;
        if (holder == null) return;
        if (!holder.Alive || holder.MountedOn != null || holder.Pos.Dimension != Pos.Dimension
            || World.PlayerByUid(holder.PlayerUID)?.Entity != holder
            || holder.Player is IServerPlayer { ConnectionState: not EnumClientState.Playing }
            || holder.Pos.XYZ.DistanceTo(Pos.XYZ) > 8 || !CanAccess(holder, Pos.XYZ))
        { Release(); return; }
        Vec3d delta = holder.Pos.XYZ.SubCopy(Pos.XYZ);
        double distance = Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        double slack = Math.Max(Width, Depth) / 2 + 1.15;
        // No lifting or teleporting: native passive physics supplies gravity and
        // collision. The player must build a clear path for the complete slab.
        double speed = distance <= slack ? 0 : Math.Min(.018, (distance - slack) / 60);
        Vec3d candidate = Pos.XYZ.AddCopy(delta.X / Math.Max(.001, distance) * .3, 0, delta.Z / Math.Max(.001, distance) * .3);
        if (speed > 0 && !CanAccess(holder, candidate)) { Release(); return; }
        Pos.Motion.X = delta.X / Math.Max(.001, distance) * speed;
        Pos.Motion.Z = delta.Z / Math.Max(.001, distance) * speed;
    }

    private void Place(EntityPlayer player)
    {
        if (slab == null || !OnGround) { QuarryDragCompatibility.Error(player, "quarry-drag-ground"); return; }
        Block block = slab.Block;
        var pos = new BlockPos((int)Math.Round(Pos.X - Width / 2) + controllerOffset.X,
            (int)Math.Round(Pos.Y) + controllerOffset.Y, (int)Math.Round(Pos.Z - Depth / 2) + controllerOffset.Z, Pos.Dimension);
        var cells = QuarryDragCompatibility.Cells(block, pos);
        if (!QuarryDragCompatibility.Access(World, player.Player, cells, false)
            || cells.Any(p => !World.BlockAccessor.GetBlock(p).IsReplacableBy(block)))
        { QuarryDragCompatibility.Error(player, "quarry-drag-no-space"); return; }
        // Each bottom cell needs support; don't restore a floating multiblock.
        if (cells.Where(p => p.Y == cells.Min(c => c.Y)).Any(p =>
            World.BlockAccessor.GetBlock(p.DownCopy()).GetCollisionBoxes(World.BlockAccessor, p.DownCopy())
                ?.Any(box => box.Y2 >= .999f && box.X1 <= .5f && box.X2 >= .5f && box.Z1 <= .5f && box.Z2 >= .5f) != true))
        { QuarryDragCompatibility.Error(player, "quarry-drag-ground"); return; }
        // CanPlaceBlock's native entity collision check would hit this load itself.
        // Check every *other* entity before calling DoPlaceBlock directly.
        Vec3d center = new(pos.X - controllerOffset.X + Width / 2, pos.Y - controllerOffset.Y, pos.Z - controllerOffset.Z + Depth / 2);
        if (World.GetEntitiesAround(center, 5, 5).Any(e => e != this && e.IsInteractable
            && e.Pos.Dimension == Pos.Dimension && e.CollisionBox != null
            && e.CollisionBox.ToDouble().Translate(e.Pos.XYZ).Intersects(new Cuboidd(-Width / 2, 0, -Depth / 2,
                Width / 2, Height, Depth / 2).Translate(center))))
        { QuarryDragCompatibility.Error(player, "quarry-drag-no-space"); return; }
        placing = true;
        try
        {
            var selection = new BlockSelection { Position = pos, Face = BlockFacing.UP, HitPosition = new(.5, 0, .5) };
            if (!block.DoPlaceBlock(World, player.Player, selection, slab.Clone()))
            { QuarryDragCompatibility.Error(player, "quarry-drag-no-space"); return; }
            ItemStack? binding = WatchedAttributes.GetItemstack("binding")?.Clone();
            ItemStack? wheels = WatchedAttributes.GetItemstack("wheels")?.Clone();
            Release();
            Die(EnumDespawnReason.Removed);
            if (binding != null && !player.TryGiveItemStack(binding)) World.SpawnItemEntity(binding, player.Pos.XYZ);
            if (wheels != null && !player.TryGiveItemStack(wheels)) World.SpawnItemEntity(wheels, player.Pos.XYZ);
        }
        finally { placing = false; }
    }
    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        if (World.Side == EnumAppSide.Server) Release();
        base.OnEntityDespawn(despawn);
    }
}
