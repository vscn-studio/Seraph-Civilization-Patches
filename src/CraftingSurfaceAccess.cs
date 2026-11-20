using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationPatches;

internal sealed class CraftingSurfaceAccess
{
    private readonly PropertyInfo inventory;
    private readonly FieldInfo matrices;

    private CraftingSurfaceAccess(PropertyInfo inventory, FieldInfo matrices)
    {
        this.inventory = inventory;
        this.matrices = matrices;
    }

    public static CraftingSurfaceAccess? TryCreate(Type type)
    {
        if (type.FullName != "RKN.Crafting.Entities.BlockEntityCraftingSurface") return null;
        PropertyInfo? inventory = type.GetProperty("Inventory");
        FieldInfo? matrices = null;
        for (Type? current = type; current != null; current = current.BaseType)
        {
            matrices = current.GetField("tfMatrices", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (matrices != null) break;
        }
        if (inventory == null || !typeof(InventoryBase).IsAssignableFrom(inventory.PropertyType)
            || matrices?.FieldType != typeof(float[][])) return null;
        return new CraftingSurfaceAccess(inventory, matrices);
    }

    public InventoryBase? GetInventory(BlockEntity entity) => inventory.GetValue(entity) as InventoryBase;

    public float[][]? GetMatrices(BlockEntity entity) => matrices.GetValue(entity) as float[][];

    public static Vec3d GetAnchor(BlockPos pos, float[] matrix)
    {
        // The displayed mesh is centered from local coordinates (0..1) using the same
        // transform as RKN. Transforming its center keeps the label over the item after
        // scale and rotation are applied.
        return new Vec3d(
            pos.X + matrix[0] * 0.5 + matrix[8] * 0.5 + matrix[12],
            pos.Y + matrix[1] * 0.5 + matrix[9] * 0.5 + matrix[13] + 0.55,
            pos.Z + matrix[2] * 0.5 + matrix[10] * 0.5 + matrix[14]);
    }
}
