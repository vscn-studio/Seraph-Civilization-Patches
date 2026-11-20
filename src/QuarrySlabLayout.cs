using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationPatches;

// Measure the actual rotated stone faces so the chain sits against the stone.
internal sealed class QuarrySlabLayout(Cuboidd stone)
{
    internal Cuboidd Stone { get; } = stone;

    internal static QuarrySlabLayout Measure(Shape source, Vec3f rotation, Vec3d offset, ITesselatorAPI tess)
    {
        var stone = Bounds(StoneFaces(source.Clone()), rotation, offset, tess);
        return new(stone ?? throw new InvalidOperationException("Quarry shape has no stone faces"));
    }

    private static Shape StoneFaces(Shape shape)
    {
        void Filter(ShapeElement element)
        {
            for (int i = 0; i < (element.FacesResolved?.Length ?? 0); i++)
                if (element.FacesResolved![i] is { } face && face.Texture?.TrimStart('#') == "wood")
                    element.FacesResolved[i] = null!;
            foreach (var child in element.Children ?? []) Filter(child);
        }
        foreach (var element in shape.Elements) Filter(element);
        return shape;
    }

    private static Cuboidd? Bounds(Shape shape, Vec3f rotation, Vec3d offset, ITesselatorAPI tess)
    {
        tess.TesselateShape("quarry-attachments", shape, out var mesh, new MeasurementTextures(), rotation);
        if (mesh.VerticesCount == 0) return null;
        double Min(int axis) => Enumerable.Range(0, mesh.VerticesCount).Min(i => mesh.xyz[3 * i + axis]);
        double Max(int axis) => Enumerable.Range(0, mesh.VerticesCount).Max(i => mesh.xyz[3 * i + axis]);
        return new Cuboidd(Min(0), Min(1), Min(2), Max(0), Max(1), Max(2)).Translate(offset);
    }

    private sealed class MeasurementTextures : ITexPositionSource
    {
        public Size2i AtlasSize => new(1024, 1024);
        public TextureAtlasPosition this[string code] => new() { x2 = 1, y2 = 1, atlasTextureId = 1 };
    }
}
