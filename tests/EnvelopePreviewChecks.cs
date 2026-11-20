using System.Reflection;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

static class EnvelopePreviewChecks
{
    public static void Run(string game, (string name, Shape shape)[] shapes, JObject original, JObject patched, Action<bool, string> check)
    {
        // Tessellate the real Envelopes-generated shape, without a window/GPU.
        var type = Assembly.LoadFrom(Path.Combine(game, "VintagestoryLib.dll"))
            .GetType("Vintagestory.Client.NoObf.ShapeTesselator", true)!;
        var tessellator = (ITesselatorAPI)Activator.CreateInstance(type, new object?[] { null, null, null, null })!;
        var source = Proxy.Create<ITexPositionSource>((m, _) => m.Name switch {
            "get_AtlasSize" => new Size2i(1024, 1024),
            "get_Item" => new TextureAtlasPosition { x2 = 1, y2 = 1, atlasTextureId = 1 }, _ => null
        });
        var oldTransform = original["guiTransform"]!.ToObject<ModelTransform>()!;
        var transform = patched["guiTransform"]!.ToObject<ModelTransform>()!;
        foreach (var (name, shape) in shapes)
        {
            tessellator.TesselateShape("signetring-preview-check", shape, out var mesh, source);
            check(mesh.VerticesCount > 0, name + ": actual tessellator produces a nonempty mesh");
            var points = Enumerable.Range(0, mesh.VerticesCount)
                .Select(i => new Vec4f(mesh.xyz[i * 3], mesh.xyz[i * 3 + 1], mesh.xyz[i * 3 + 2], 1)).ToArray();
            foreach (float size in new float[] { 12.8f, 40, 80 })
            {
                var matrix = GuiMatrix(oldTransform, size, 1, 0);
                var projected = points.Select(p => matrix.TransformVector(p)).ToArray();
                check(projected.All(p => p.Y < -size) || projected.All(p => p.Y > size),
                    $"{name}: original GUI size {size} puts the entire ring outside its preview");
                Console.WriteLine($"Original size {size}: Y={projected.Min(p => p.Y):F2}..{projected.Max(p => p.Y):F2}, visible region={-size}..{size}");
            }
            foreach (float guiScale in new float[] { 0.75f, 1, 1.5f, 2, 3 })
            foreach (float nominalSize in new float[] { 12.8f, 25.6f, 40, 80 })
            {
                float size = nominalSize * guiScale;
                bool visible = true;
                for (int angle = 0; angle < 360; angle += 15)
                {
                    var matrix = GuiMatrix(transform, size, guiScale, angle);
                    var projected = points.Select(p => matrix.TransformVector(p)).ToArray();
                    visible &= projected.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y) && Math.Abs(p.X) < size && Math.Abs(p.Y) < size);
                    visible &= projected.Max(p => p.X) - projected.Min(p => p.X) > size * 0.3f;
                }
                check(visible, $"{name}: corrected size {nominalSize}, GUI scale {guiScale}, full rotation stays visible");
            }
        }
    }

    // Vintage Story 1.22.7 InventoryItemRenderer.RenderItemstackToGui matrix,
    // relative to the requested icon center; translation uses GUI pixels.
    private static Matrixf GuiMatrix(ModelTransform t, float size, float guiScale, float angle) => new Matrixf()
        .Identity().Translate(-3, -1, 0)
        .Translate(t.Origin.X + guiScale * t.Translation.X, t.Origin.Y + guiScale * t.Translation.Y,
            t.Origin.Z * size + guiScale * t.Translation.Z)
        .Scale(size * t.ScaleXYZ.X, size * t.ScaleXYZ.Y, size * t.ScaleXYZ.Z)
        .RotateXDeg(t.Rotation.X).RotateYDeg(t.Rotation.Y - angle).RotateZDeg(t.Rotation.Z)
        .Translate(-t.Origin.X, -t.Origin.Y, -t.Origin.Z);
}
