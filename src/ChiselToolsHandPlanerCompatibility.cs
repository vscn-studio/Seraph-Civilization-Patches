using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationPatches;

/// <summary>
/// Keep independent planer defaults consistent and correct QP's plane hit rounding.
/// Tool mode packets are handled by the engine; MarkDirty publishes their result.
/// </summary>
internal sealed class ChiselToolsHandPlanerCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.chiseltools-hand-planer";
    private static readonly Harmony harmony = new(HarmonyId);
    private static readonly HashSet<ChiselToolsHandPlanerCompatibility> owners = [];
    private readonly ICoreAPI api;

    private ChiselToolsHandPlanerCompatibility(ICoreAPI api) => this.api = api;

    public static ChiselToolsHandPlanerCompatibility? TryCreate(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("chiseltools")) return null;
        var compatibility = new ChiselToolsHandPlanerCompatibility(api);
        try
        {
            if (owners.Count == 0)
            {
                Type type = AccessTools.TypeByName("chisel.src.ItemHandPlaner")
                    ?? throw new TypeLoadException("chisel.src.ItemHandPlaner");
                MethodInfo setMode = AccessTools.DeclaredMethod(type, "SetToolMode")
                    ?? throw new MissingMethodException(type.FullName, "SetToolMode");
                MethodInfo attack = AccessTools.DeclaredMethod(type, "OnHeldAttackStart")
                    ?? throw new MissingMethodException(type.FullName, "OnHeldAttackStart");
                MethodInfo interact = AccessTools.DeclaredMethod(type, "OnHeldInteractStart")
                    ?? throw new MissingMethodException(type.FullName, "OnHeldInteractStart");
                harmony.Patch(setMode, postfix: new HarmonyMethod(typeof(ChiselToolsHandPlanerCompatibility), nameof(AfterSetToolMode)));
                harmony.Patch(attack, prefix: new HarmonyMethod(typeof(ChiselToolsHandPlanerCompatibility), nameof(BeforeUse)));
                harmony.Patch(interact, prefix: new HarmonyMethod(typeof(ChiselToolsHandPlanerCompatibility), nameof(BeforeUse)));
                Type pantograph = AccessTools.TypeByName("chisel.src.ItemPantograph")
                    ?? throw new TypeLoadException("chisel.src.ItemPantograph");
                harmony.Patch(AccessTools.DeclaredMethod(pantograph, "SetToolMode"),
                    postfix: new HarmonyMethod(typeof(ChiselToolsHandPlanerCompatibility), nameof(AfterSetToolMode)));
                foreach (var tool in new[] { type, pantograph })
                    harmony.Patch(AccessTools.DeclaredMethod(tool, "ModifyChiseledBlock"),
                        prefix: new HarmonyMethod(typeof(ChiselToolsHandPlanerCompatibility), nameof(PreparePlaneHit)));
            }
            owners.Add(compatibility);
            api.Logger.Notification("[vsscpmod] QP planer fix v2 enabled: ItemPantograph + ItemHandPlaner plane hit rounding and mode updates.");
            return compatibility;
        }
        catch (Exception exception)
        {
            if (owners.Count == 0) harmony.UnpatchAll(HarmonyId);
            api.Logger.Warning("[vsscpmod] QP Chisel Tools hand planer compatibility unavailable: {0}",
                exception.GetBaseException().Message);
            return null;
        }
    }

    private static void AfterSetToolMode(ItemSlot slot)
    {
        // The engine already sends a tool mode packet. Publish the resulting
        // attributes instead of claiming MarkDirty itself sends a new mode command.
        slot?.MarkDirty();
    }

    private static void BeforeUse(ItemSlot slot)
    {
        if (slot?.Itemstack == null) return;
        int mode = slot.Itemstack.Attributes.GetInt("toolMode", 0);
        // 0..2 are the cutting modes; mode 3 is the transient undo selector.
        if (mode is >= 0 and <= 2 && slot.Itemstack.Attributes.GetInt("lastToolMode", -1) != mode)
        {
            slot.Itemstack.Attributes.SetInt("lastToolMode", mode);
            slot.MarkDirty();
        }
    }

    private static bool PreparePlaneHit(ref BlockSelection blockSel, ref int __result)
    {
        // QP casts hit*16 to int, THEN subtracts one on positive faces.
        // A hit at 8/16 minus floating/network error therefore selects layer 6,
        // not layer 7. The normal coordinate of a voxel face is always n/16.
        if (blockSel?.HitPosition == null || blockSel.Face == null) { __result = 0; return false; }
        var hit = blockSel.HitPosition;
        const double tolerance = 1.0 / 16384 + 1e-7; // native hand packet precision
        if (new[] { hit.X, hit.Y, hit.Z }.Any(v => !double.IsFinite(v) || v < -tolerance || v > 1 + tolerance))
        { __result = 0; return false; }
        var normal = blockSel.Face.Normali;
        double depth = normal.X != 0 ? hit.X : normal.Y != 0 ? hit.Y : hit.Z;
        double boundary = Math.Round(depth * 16) / 16;
        // Reject hits not on a voxel plane instead of cutting an arbitrary layer.
        if (Math.Abs(depth - boundary) > tolerance) { __result = 0; return false; }
        int layer = (int)(boundary * 16) - (normal.X + normal.Y + normal.Z > 0 ? 1 : 0);
        if (layer is < 0 or > 15) { __result = 0; return false; }
        var copy = blockSel.Clone();
        copy.HitPosition = new Vec3d(Math.Clamp(hit.X, 0, 1 - 1e-9), Math.Clamp(hit.Y, 0, 1 - 1e-9), Math.Clamp(hit.Z, 0, 1 - 1e-9));
        if (normal.X != 0) copy.HitPosition.X = boundary;
        else if (normal.Y != 0) copy.HitPosition.Y = boundary;
        else copy.HitPosition.Z = boundary;
        blockSel = copy;
        return true;
    }

    public void Dispose()
    {
        if (!owners.Remove(this) || owners.Count != 0) return;
        harmony.UnpatchAll(HarmonyId);
        api.Logger.Debug("[vsscpmod] QP Chisel Tools hand planer compatibility removed.");
    }
}
