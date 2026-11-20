using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace SeraphCivilizationPatches;

/// <summary>1.22 server hand packets contain coordinates but do not populate BlockSelection.Block.</summary>
internal sealed class VintageCanvasPaintCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.vintagecanvas-paint";
    private static readonly Harmony harmony = new(HarmonyId);
    private static readonly HashSet<VintageCanvasPaintCompatibility> owners = [];
    private static Type? easelBlock;
    private static Type? easelEntity;

    public static VintageCanvasPaintCompatibility? TryCreate(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("vintagecanvas")) return null;
        if (owners.Count == 0)
        {
            try
            {
                var tool = AccessTools.TypeByName("VintageCanvas.src.Items.CollectibleBehaviorPaintTool")
                    ?? throw new TypeLoadException("VintageCanvas paint tool");
                easelBlock = tool.Assembly.GetType("VintageCanvas.src.Blocks.BlockEasel", true)!;
                easelEntity = tool.Assembly.GetType("VintageCanvas.src.Entities.BlockEntityEasel", true)!;
                // Each server step/cancel packet constructs a new selection. Fixing only
                // IsPaintTarget leaves OnHeldInteractStep dereferencing the next null Block.
                foreach (string name in new[] { "OnHeldAttackStart", "OnHeldAttackStep", "OnHeldAttackCancel",
                    "OnHeldInteractStart", "OnHeldInteractCancel" })
                    Patch(tool, name, nameof(PrepareHeldSelection));
                Patch(tool, "OnHeldInteractStep", nameof(InteractStep));
                Patch(tool, "EndStroke", nameof(PrepareEndSelection));
                Patch(tool, "IsPaintTarget", nameof(IsPaintTarget));
                Patch(tool, "isChiselSurface", nameof(IsChiselSurface));
                var roller = tool.Assembly.GetType("VintageCanvas.src.Items.CollectibleBehaviorDecorTool", true)!;
                Patch(roller, "OnHeldInteractStart", nameof(ValidateDecorSurface));
            }
            catch (Exception e)
            {
                harmony.UnpatchAll(HarmonyId);
                easelBlock = easelEntity = null;
                api.Logger.Warning("[vsscpmod] VintageCanvas paint compatibility unavailable: {0}", e.GetBaseException().Message);
                return null;
            }
        }
        var owner = new VintageCanvasPaintCompatibility();
        owners.Add(owner);
        api.Logger.Notification("[vsscpmod] VintageCanvas painting resolves block selections from world coordinates and validates easels and fresco entities.");
        return owner;
    }

    private static void Patch(Type tool, string method, string prefix) => harmony.Patch(
        AccessTools.DeclaredMethod(tool, method) ?? throw new MissingMethodException(tool.FullName, method),
        prefix: new HarmonyMethod(typeof(VintageCanvasPaintCompatibility), prefix));

    private static void PrepareHeldSelection(EntityAgent byEntity, ref BlockSelection? blockSel)
        => PrepareSelection(byEntity.World, ref blockSel);

    private static bool PrepareEndSelection(IWorldAccessor world, ref BlockSelection? blockSel)
    {
        // Pixels and stroke bookkeeping belong to the client; VintageCanvas saves
        // them through PaintSavePacket. The server has no picking ray or texture API.
        if (world.Side == EnumAppSide.Server) return false;
        PrepareSelection(world, ref blockSel);
        return true;
    }

    private static bool InteractStep(EntityAgent byEntity, ref BlockSelection? blockSel,
        ref EnumHandling handling, ref bool __result)
    {
        PrepareSelection(byEntity.World, ref blockSel);
        if (byEntity.World.Side != EnumAppSide.Server) return true;
        handling = EnumHandling.Handled;
        // Do not execute CanvasAngleRaycast/ApplyTool on a dedicated server. In
        // addition to null Block, that path requires ICoreClientAPI and would fail next.
        bool target = false;
        IsPaintTarget(blockSel, byEntity.World, ref target);
        __result = target;
        return false;
    }

    private static void PrepareSelection(IWorldAccessor world, ref BlockSelection? blockSel)
    {
        if (ResolveBlock(blockSel, world) == null) blockSel = null;
    }

    private static Block? ResolveBlock(BlockSelection? selection, IWorldAccessor world)
    {
        if (selection?.Position == null) return null;
        // Always refresh, including populated client selections: the target may have
        // been removed/replaced since the previous stroke or hand interaction.
        var block = selection.Block = world.BlockAccessor.GetBlock(selection.Position);
        if (block?.Code == null) return null;
        // Roofing 2.1.1 uses AutoRoofEntity for ordinary roofs, and only its
        // chiseled variant inherits BlockEntityMicroBlock. VintageCanvas casts
        // every roof-filled target to that type before it even checks fresco data.
        if (IsSurface(block) && world.BlockAccessor.GetBlockEntity(selection.Position) is not BlockEntityMicroBlock)
            return null;
        if (easelBlock!.IsInstanceOfType(block))
            return easelEntity!.IsInstanceOfType(world.BlockAccessor.GetBlockEntity(selection.Position)) ? block : null;
        if (block is BlockMultiblock multiblock)
        {
            var main = selection.Position.AddCopy(multiblock.OffsetInv);
            if (!easelBlock.IsInstanceOfType(world.BlockAccessor.GetBlock(main))
                || !easelEntity!.IsInstanceOfType(world.BlockAccessor.GetBlockEntity(main))) return null;
        }
        return block;
    }

    private static bool ValidateDecorSurface(EntityAgent byEntity, BlockSelection? blockSel,
        ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        if (blockSel?.Position == null) return true;
        var accessor = byEntity.World.BlockAccessor;
        var block = accessor.GetBlock(blockSel.Position);
        // The roller has the same invalid cast, with a broader path-only test.
        if (block is not BlockMicroBlock && block?.Code?.PathStartsWith("roof-filled") != true) return true;
        if (accessor.GetBlockEntity(blockSel.Position) is BlockEntityMicroBlock) return true;
        handHandling = EnumHandHandling.PreventDefault;
        handling = EnumHandling.PreventDefault;
        return false;
    }

    private static bool IsPaintTarget(BlockSelection? blockSel, IWorldAccessor world, ref bool __result)
    {
        var block = ResolveBlock(blockSel, world);
        __result = block != null && (easelBlock!.IsInstanceOfType(block)
            || block is BlockMultiblock || IsSurface(block));
        return false;
    }

    private static bool IsSurface(Block? block) => block is BlockMicroBlock
        || block?.Code?.ToString().StartsWith("vsroofing:roof-filled", StringComparison.Ordinal) == true;

    private static bool IsChiselSurface(Block? block, ref bool __result)
    {
        __result = IsSurface(block);
        return false;
    }

    public void Dispose()
    {
        if (!owners.Remove(this) || owners.Count != 0) return;
        harmony.UnpatchAll(HarmonyId);
        easelBlock = easelEntity = null;
    }
}
