using System.Globalization;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationPatches;

internal sealed class CraftingCountRenderer(ICoreClientAPI api) : IRenderer
{
    private const int Range = 8;
    private const int MaxTextures = 128;
    private readonly List<(BlockEntity Entity, CraftingSurfaceAccess Access)> surfaces = [];
    private readonly Dictionary<Type, CraftingSurfaceAccess?> adapters = [];
    private readonly Dictionary<int, LoadedTexture> textures = [];
    private readonly Queue<int> textureOrder = [];
    private long nextScan;
    private bool disposed;
    private bool failed;

    public double RenderOrder => 0.9;
    public int RenderRange => Range;
    public bool IsOperational => !disposed && !failed;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (disposed || failed || api.HideGuis || api.World.Player?.Entity == null) return;
        try
        {
            RenderCounts();
        }
        catch (Exception exception)
        {
            failed = true;
            api.Logger.Error("[vsscpmod] Crafting count rendering disabled after a compatibility error: {0}", exception);
        }
    }

    private void RenderCounts()
    {
        var player = api.World.Player.Entity;
        if (api.ElapsedMilliseconds >= nextScan)
        {
            Scan(player.Pos.AsBlockPos);
            nextScan = api.ElapsedMilliseconds + 250;
        }

        IRenderAPI render = api.Render;
        Vec3d camera = player.CameraPos;
        foreach (var (entity, access) in surfaces)
        {
            if (entity.Pos.dimension != player.Pos.Dimension
                || !ReferenceEquals(api.World.BlockAccessor.GetBlockEntity(entity.Pos), entity)) continue;
            InventoryBase? inventory = access.GetInventory(entity);
            float[][]? matrices = access.GetMatrices(entity);
            if (inventory == null || matrices == null) continue;

            for (int slot = 0; slot < Math.Min(9, Math.Min(inventory.Count, matrices.Length)); slot++)
            {
                int count = inventory[slot].StackSize;
                float[]? matrix = matrices[slot];
                if (count <= 0 || matrix == null || matrix.Length != 16) continue;
                Vec3d anchor = CraftingSurfaceAccess.GetAnchor(entity.Pos, matrix);
                double distance = camera.DistanceTo(anchor);
                if (distance > Range) continue;
                Vec3d projected = MatrixToolsd.Project(anchor, render.PerspectiveProjectionMat,
                    render.PerspectiveViewMat, render.FrameWidth, render.FrameHeight);
                if (projected.Z < 0 || !double.IsFinite(projected.X) || !double.IsFinite(projected.Y)) continue;

                LoadedTexture texture = GetTexture(count);
                double scale = Math.Clamp(3.2 / Math.Max(1, distance), 0.65, 1.1);
                double width = texture.Width * scale;
                double height = texture.Height * scale;
                if (projected.X < -width || projected.X > render.FrameWidth + width
                    || projected.Y < -height || projected.Y > render.FrameHeight + height) continue;
                render.Render2DTexturePremultipliedAlpha(texture.TextureId,
                    projected.X - width / 2, render.FrameHeight - projected.Y - height / 2,
                    width, height, 10);
            }
        }
    }

    private void Scan(BlockPos center)
    {
        surfaces.Clear();
        var pos = new BlockPos(center.dimension);
        for (int x = center.X - Range; x <= center.X + Range; x++)
        for (int y = Math.Max(0, center.Y - Range); y <= center.Y + Range; y++)
        for (int z = center.Z - Range; z <= center.Z + Range; z++)
        {
            pos.Set(x, y, z);
            BlockEntity? entity = api.World.BlockAccessor.GetBlockEntity(pos);
            if (entity == null) continue;
            Type type = entity.GetType();
            if (!adapters.TryGetValue(type, out CraftingSurfaceAccess? access))
            {
                access = CraftingSurfaceAccess.TryCreate(type);
                adapters[type] = access;
                if (access == null && type.FullName == "RKN.Crafting.Entities.BlockEntityCraftingSurface")
                    api.Logger.Warning("[vsscpmod] Unsupported RKN crafting entity API; skipping floating counts.");
            }
            if (access != null) surfaces.Add((entity, access));
        }
    }

    private LoadedTexture GetTexture(int count)
    {
        if (textures.TryGetValue(count, out LoadedTexture? texture)) return texture;
        if (textures.Count >= MaxTextures)
        {
            int oldest = textureOrder.Dequeue();
            textures[oldest].Dispose();
            textures.Remove(oldest);
        }
        CairoFont font = CairoFont.WhiteSmallText()
            .WithFontSize(28)
            .WithWeight(FontWeight.Bold)
            .WithStroke([0, 0, 0, 1], 4.5);
        texture = api.Gui.TextTexture.GenUnscaledTextTexture(count.ToString(CultureInfo.InvariantCulture), font,
            new TextBackground
            {
                // Transparent padding keeps the thick outline from being clipped without
                // adding a visible background behind the number.
                Padding = 6,
                FillColor = [0, 0, 0, 0],
                BorderColor = [0, 0, 0, 0],
                BorderWidth = 0,
                Radius = 2
            });
        textures.Add(count, texture);
        textureOrder.Enqueue(count);
        return texture;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        api.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        foreach (LoadedTexture texture in textures.Values) texture.Dispose();
        textures.Clear();
        textureOrder.Clear();
        surfaces.Clear();
        adapters.Clear();
    }
}
