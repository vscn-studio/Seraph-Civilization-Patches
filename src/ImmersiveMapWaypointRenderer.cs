using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationPatches;

/// <summary>
/// Draws the waypoints stored in an Immersive Maps item at the top of the game view.
/// Their horizontal positions come from the active camera projection. The optional map dependency is accessed by
/// reflection so this assembly still loads when Immersive Maps is not installed.
/// </summary>
internal sealed class ImmersiveMapWaypointRenderer : IRenderer, IDisposable
{
    private const double RenderZ = 970;
    private const double StripTop = 6;
    private const double IconSize = 24;
    private const double MaxWaypointDistance = 200;
    private readonly ICoreClientAPI api;
    private readonly Dictionary<string, LoadedTexture> iconTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoadedTexture> labelTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssetLocation> iconLocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly MethodInfo? readWaypoints;
    private readonly Type? mapItemType;
    private bool disposed;
    private bool failed;

    public ImmersiveMapWaypointRenderer(ICoreClientAPI api)
    {
        this.api = api;
        mapItemType = Type.GetType("ImmersiveMaps.Items.ItemMap, ImmersiveMaps")
            ?? FindType("ImmersiveMaps.Items.ItemMap");
        Type? waypointsType = FindType("ImmersiveMaps.Waypoints.MapWaypoints");
        readWaypoints = waypointsType == null
            ? null
            : waypointsType.GetMethod("Read", BindingFlags.Public | BindingFlags.Static, null, [typeof(ItemStack)], null);
        LoadIconLocations();
    }

    public double RenderOrder => 0.95;
    public int RenderRange => 0;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (disposed || failed || api.HideGuis || mapItemType == null || readWaypoints == null) return;
        try
        {
            RenderWaypoints();
        }
        catch (Exception exception)
        {
            failed = true;
            api.Logger.Warning("[vsscpmod] Immersive Maps waypoint strip disabled after a compatibility error: {0}", exception.Message);
        }
    }

    private void RenderWaypoints()
    {
        IClientPlayer? clientPlayer = api.World.Player;
        EntityPlayer? player = clientPlayer?.Entity;
        if (clientPlayer == null || player == null) return;
        ItemStack? map = FindHeldMap(player);
        if (map == null) return;
        if (readWaypoints!.Invoke(null, [map]) is not IDictionary waypoints || waypoints.Count == 0) return;

        int width = api.Render.FrameWidth;
        if (width <= 0) return;
        double stripWidth = Math.Min(width, 920);
        double stripLeft = (width - stripWidth) / 2d;
        foreach (DictionaryEntry entry in waypoints)
        {
            object? waypoint = entry.Value;
            if (waypoint == null) continue;
            if (!ReadBool(waypoint, "Pinned")) continue;
            double x = ReadNumber(waypoint, "X");
            double z = ReadNumber(waypoint, "Z");
            double distance = Math.Sqrt(Math.Pow(x - player.Pos.X, 2) + Math.Pow(z - player.Pos.Z, 2));
            if (distance > MaxWaypointDistance) continue;
            Vec3d projected = MatrixToolsd.Project(new Vec3d(x, player.Pos.Y + 1.6, z),
                api.Render.PerspectiveProjectionMat, api.Render.PerspectiveViewMat, width, api.Render.FrameHeight);
            if (projected.Z < 0 || !double.IsFinite(projected.X) || !double.IsFinite(projected.Y)) continue;
            double screenX = stripLeft + projected.X / width * stripWidth;
            if (screenX < stripLeft + IconSize / 2 || screenX > stripLeft + stripWidth - IconSize / 2) continue;
            string icon = ReadString(waypoint, "Icon") ?? "circle";
            string name = ReadString(waypoint, "Name") ?? string.Empty;
            int color = ReadInt(waypoint, "Color");
            LoadedTexture iconTexture = GetIcon(icon, color);
            Draw(iconTexture, screenX - IconSize / 2, StripTop, IconSize, IconSize, RenderZ + 1);
            if (name.Length == 0) continue;
            string labelText = name + " [" + Math.Round(distance).ToString("0", CultureInfo.InvariantCulture) + "m]";
            LoadedTexture label = GetLabel(labelText, color);
            double labelWidth = Math.Min(label.Width, 180);
            Draw(label, screenX - labelWidth / 2, StripTop + IconSize + 7,
                labelWidth, label.Height * (labelWidth / label.Width), RenderZ + 2);
        }
    }

    private ItemStack? FindHeldMap(EntityPlayer player)
    {
        ItemStack? right = player.RightHandItemSlot?.Itemstack;
        if (right?.Item != null && mapItemType!.IsInstanceOfType(right.Item)) return right;
        ItemStack? left = player.LeftHandItemSlot?.Itemstack;
        return left?.Item != null && mapItemType!.IsInstanceOfType(left.Item) ? left : null;
    }

    private LoadedTexture GetIcon(string name, int color)
    {
        string key = name + "|" + color.ToString(CultureInfo.InvariantCulture);
        if (iconTextures.TryGetValue(key, out LoadedTexture? texture)) return texture;
        if (!iconLocations.TryGetValue(name, out AssetLocation? location)
            && !iconLocations.TryGetValue("circle", out location))
        {
            return GetFallbackIcon();
        }
        texture = api.Gui.LoadSvg(location, (int)IconSize, (int)IconSize, (int)IconSize, (int)IconSize,
            color == 0 ? -1 : color);
        iconTextures[key] = texture;
        return texture;
    }

    private LoadedTexture GetFallbackIcon()
    {
        const string key = "__fallback";
        if (iconTextures.TryGetValue(key, out LoadedTexture? texture)) return texture;
        texture = api.Gui.LoadSvg(new AssetLocation("textures/icons/circle.svg"), 24, 24, 24, 24, -1);
        iconTextures[key] = texture;
        return texture;
    }

    private LoadedTexture GetLabel(string name, int color)
    {
        string key = name + "|" + color.ToString(CultureInfo.InvariantCulture);
        if (labelTextures.TryGetValue(key, out LoadedTexture? texture)) return texture;
        CairoFont font = CairoFont.WhiteSmallText().WithFontSize(14).WithWeight(FontWeight.Bold)
            .WithStroke([0, 0, 0, 1], 3);
        texture = api.Gui.TextTexture.GenUnscaledTextTexture(name, font, new TextBackground
        {
            Padding = 2,
            FillColor = [0, 0, 0, 0],
            BorderColor = [0, 0, 0, 0],
            BorderWidth = 0,
            Radius = 0
        });
        labelTextures[key] = texture;
        return texture;
    }

    private void Draw(LoadedTexture texture, double x, double y, double width, double height, double z)
        => api.Render.Render2DTexturePremultipliedAlpha(texture.TextureId, (float)x, (float)y,
            (float)width, (float)height, (float)z, null);

    private void LoadIconLocations()
    {
        try
        {
            foreach (IAsset asset in api.Assets.GetMany("textures/icons/worldmap/", null, false))
            {
                string file = System.IO.Path.GetFileNameWithoutExtension(asset.Name);
                file = Regex.Replace(file, "^\\d+-", string.Empty);
                if (!iconLocations.ContainsKey(file)) iconLocations[file] = asset.Location;
            }
        }
        catch (Exception exception)
        {
            api.Logger.Debug("[vsscpmod] Immersive Maps icon discovery skipped: {0}", exception.Message);
        }
    }

    private static Type? FindType(string name)
        => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type != null);

    private static double ReadNumber(object value, string property)
        => Convert.ToDouble(value.GetType().GetProperty(property)?.GetValue(value) ?? 0, CultureInfo.InvariantCulture);

    private static int ReadInt(object value, string property)
        => Convert.ToInt32(value.GetType().GetProperty(property)?.GetValue(value) ?? 0, CultureInfo.InvariantCulture);

    private static bool ReadBool(object value, string property)
        => Convert.ToBoolean(value.GetType().GetProperty(property)?.GetValue(value) ?? false, CultureInfo.InvariantCulture);

    private static string? ReadString(object value, string property)
        => value.GetType().GetProperty(property)?.GetValue(value) as string;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        api.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
        foreach (LoadedTexture texture in iconTextures.Values) texture.Dispose();
        foreach (LoadedTexture texture in labelTextures.Values) texture.Dispose();
        iconTextures.Clear();
        labelTextures.Clear();
        iconLocations.Clear();
    }
}
