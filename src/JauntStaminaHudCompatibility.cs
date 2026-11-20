using System.Reflection;
using System.Runtime.InteropServices;
using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationPatches;

// Jaunt's display-only stamina HUD stays open even when nothing is drawn.
// Its composer overlaps the hotbar, so GuiDialog otherwise consumes mouse
// input across that invisible area before the hotbar can receive it.
internal sealed class JauntStaminaHudCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.jaunt-stamina-input";
    private readonly Harmony harmony = new(HarmonyId);
    private readonly Type hudType;
    private readonly FieldInfo iconTexture;
    private readonly FieldInfo iconTextures;
    private readonly Dictionary<LoadedTexture, (double Top, double Bottom)> visibleBounds = new();
    private readonly Func<object?> getSystem;
    private readonly PropertyInfo configProperty, showIcon, enableStamina, iconSize, iconX, iconY;
    private readonly ICoreClientAPI api;
    private LoadedTexture? percentageTexture;
    private int lastPercentage = -1;
    private double lastTextSize;
    private string? lastFont;
    private readonly ElementBounds clip = ElementBounds.Fixed(0, 0, 1, 1).WithParent(ElementBounds.Empty);
    private static readonly Vec4f EmptyColor = new(0.24f, 0.24f, 0.24f, 0.85f);
    private static readonly Vec4f FillColor = new(1f, 0.78f, 0.25f, 1f);
    private static readonly Vec4f LowColor = new(1f, 0.28f, 0.2f, 1f);
    internal const double IconRaise = 18;
    private static JauntStaminaHudCompatibility? current;

    private JauntStaminaHudCompatibility(ICoreClientAPI api, Type hudType, Type renderer)
    {
        this.api = api;
        this.hudType = hudType;
        iconTexture = AccessTools.Field(renderer, "activeTexture") ?? throw new MissingFieldException(renderer.FullName, "activeTexture");
        iconTextures = AccessTools.Field(renderer, "texturesDict") ?? throw new MissingFieldException(renderer.FullName, "texturesDict");
        var system = AccessTools.PropertyGetter(renderer, "ModSystem") ?? throw new MissingMemberException("Jaunt ModSystem");
        getSystem = system.CreateDelegate<Func<object?>>();
        configProperty = AccessTools.Property(system.ReturnType, "Config") ?? throw new MissingMemberException("Jaunt Config");
        PropertyInfo Config(string name) => AccessTools.Property(configProperty.PropertyType, name)
            ?? throw new MissingMemberException(configProperty.PropertyType.FullName, name);
        showIcon = Config("ShowGaitIcon");
        enableStamina = Config("EnableStamina");
        iconSize = Config("IconSize");
        iconX = Config("IconOffsetX");
        iconY = Config("IconOffsetY");
    }

    public static JauntStaminaHudCompatibility? TryCreate(ICoreClientAPI api)
    {
        if (!api.ModLoader.IsModEnabled("jaunt")) return null;
        JauntStaminaHudCompatibility? owner = null;
        try
        {
            Type type = AccessTools.TypeByName("Jaunt.Hud.HudElementStaminaBar")
                ?? throw new TypeLoadException("Jaunt.Hud.HudElementStaminaBar");
            if (!typeof(HudElement).IsAssignableFrom(type))
                throw new InvalidOperationException("Jaunt stamina HUD has an unsupported base type.");
            var receive = AccessTools.Method(type, nameof(GuiDialog.ShouldReceiveMouseEvents), Type.EmptyTypes)
                ?? throw new MissingMethodException(type.FullName, nameof(GuiDialog.ShouldReceiveMouseEvents));
            receive = AccessTools.DeclaredMethod(receive.DeclaringType, receive.Name, Type.EmptyTypes)
                ?? throw new MissingMethodException(type.FullName, nameof(GuiDialog.ShouldReceiveMouseEvents));
            if (receive.ReturnType != typeof(bool) || receive.IsStatic)
                throw new InvalidOperationException("Jaunt stamina HUD has an unsupported mouse gate.");
            Type renderer = AccessTools.TypeByName("Jaunt.Hud.HudIconRenderer")
                ?? throw new TypeLoadException("Jaunt.Hud.HudIconRenderer");
            var renderIcon = AccessTools.DeclaredMethod(renderer, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)])
                ?? throw new MissingMethodException(renderer.FullName, "OnRenderFrame");
            var renderBar = AccessTools.DeclaredMethod(type, nameof(GuiDialog.OnRenderGUI), [typeof(float)])
                ?? throw new MissingMethodException(type.FullName, nameof(GuiDialog.OnRenderGUI));
            owner = new JauntStaminaHudCompatibility(api, type, renderer);
            current?.Dispose();
            current = owner;
            owner.harmony.Patch(receive,
                prefix: new HarmonyMethod(typeof(JauntStaminaHudCompatibility), nameof(DisplayOnly)));
            owner.harmony.Patch(renderBar,
                prefix: new HarmonyMethod(typeof(JauntStaminaHudCompatibility), nameof(HideHorizontalBar)));
            owner.harmony.Patch(renderIcon,
                prefix: new HarmonyMethod(typeof(JauntStaminaHudCompatibility), nameof(RenderFilledIcon)));
            api.Logger.Notification("[vsscpmod] Jaunt stamina fills the raised gait icon; horizontal stamina bar hidden and mouse input passes through.");
            return owner;
        }
        catch (Exception exception)
        {
            if (owner != null)
            {
                owner.harmony.UnpatchAll(HarmonyId);
                if (ReferenceEquals(current, owner)) current = null;
            }
            api.Logger.Warning("[vsscpmod] Jaunt stamina HUD compatibility unavailable: {0}",
                exception.GetBaseException().Message);
            return null;
        }
    }

    private static bool HideHorizontalBar() => current == null;

    private static bool RenderFilledIcon(object __instance)
    {
        if (current is not { } owner) return true;
        object? config = owner.configProperty.GetValue(owner.getSystem());
        if (config == null || !(bool)owner.showIcon.GetValue(config)!
            || owner.iconTexture.GetValue(__instance) is not LoadedTexture { TextureId: > 0 } texture) return false;
        var mount = owner.api.World?.Player?.Entity?.MountedOn?.MountSupplier?.OnEntity;
        if (mount == null) return false;
        double size = GuiElement.scaled((float)owner.iconSize.GetValue(config)!);
        double x = owner.api.Render.FrameWidth / 2 - size / 2 + GuiElement.scaled((float)owner.iconX.GetValue(config)!);
        double y = owner.api.Render.FrameHeight - size + GuiElement.scaled((float)owner.iconY.GetValue(config)! - IconRaise);
        var stamina = mount.WatchedAttributes?.GetTreeAttribute("jaunt:stamina");
        float value = stamina?.GetFloat("currentstamina") ?? float.NaN;
        float max = stamina?.GetFloat("maxstamina") ?? float.NaN;
        bool progress = (bool)owner.enableStamina.GetValue(config)! && float.IsFinite(value) && float.IsFinite(max) && max > 0;
        var render = owner.api.Render;
        // Reuse Jaunt's current gait SVG texture; no uploads or image generation per frame.
        render.RenderTexture(texture.TextureId, x, y, size, size, 50, progress ? EmptyColor : null);
        if (!progress) return false;
        double fraction = Math.Clamp((double)value / max, 0, 1);
        var visible = owner.GetVisibleBounds(__instance, texture);
        if (fraction > 0)
        {
            double fillHeight = size * (visible.Bottom - visible.Top) * fraction;
            owner.clip.absFixedX = x;
            owner.clip.absFixedY = y + size * visible.Bottom - fillHeight;
            owner.clip.absInnerWidth = size;
            owner.clip.absInnerHeight = fillHeight;
            render.PushScissor(owner.clip, true);
            try { render.RenderTexture(texture.TextureId, x, y, size, size, 50.1f, fraction < 0.3 ? LowColor : FillColor); }
            finally { render.PopScissor(); }
        }
        owner.RenderPercentage(fraction, x + size / 2, y + size * (visible.Top + visible.Bottom) / 2, size);
        return false;
    }

    private void RenderPercentage(double fraction, double centerX, double centerY, double size)
    {
        int percentage = (int)Math.Round(fraction * 100, MidpointRounding.AwayFromZero);
        string font = GuiStyle.StandardFontName;
        if (percentageTexture == null || percentage != lastPercentage || size != lastTextSize || font != lastFont)
        {
            int width = Math.Max(1, (int)Math.Ceiling(size));
            int height = Math.Max(1, (int)Math.Ceiling(size * 0.36));
            using var surface = new ImageSurface(Format.Argb32, width, height);
            using var context = new Context(surface);
            context.SelectFontFace(font, FontSlant.Normal, FontWeight.Bold);
            context.SetFontSize(size * 16 / 75);
            string text = percentage.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
            var extents = context.TextExtents(text);
            context.MoveTo((width - extents.Width) / 2 - extents.XBearing,
                (height - extents.Height) / 2 - extents.YBearing);
            context.TextPath(text);
            context.SetSourceRGBA(0, 0, 0, 0.9);
            context.LineWidth = Math.Max(1, size / 75);
            context.StrokePreserve();
            context.SetSourceRGBA(1, 1, 1, 1);
            context.Fill();
            percentageTexture ??= new LoadedTexture(api);
            api.Gui.LoadOrUpdateCairoTexture(surface, false, ref percentageTexture);
            lastPercentage = percentage;
            lastTextSize = size;
            lastFont = font;
        }
        // Draw after popping the fill clip, so even 0% remains fully readable.
        api.Render.RenderTexture(percentageTexture.TextureId,
            centerX - percentageTexture.Width / 2d, centerY - percentageTexture.Height / 2d,
            percentageTexture.Width, percentageTexture.Height, 50.2f);
    }

    private (double Top, double Bottom) GetVisibleBounds(object renderer, LoadedTexture texture)
    {
        if (visibleBounds.TryGetValue(texture, out var bounds)) return bounds;
        bounds = (0, 1);
        try
        {
            if (iconTextures.GetValue(renderer) is FastSmallDictionary<string, LoadedTexture> textures)
            {
                foreach (var entry in textures)
                {
                    if (!ReferenceEquals(entry.Value, texture) || entry.Key == "empty") continue;
                    var asset = api.Assets.TryGet(new AssetLocation(entry.Key));
                    if (asset == null) break;
                    // Rasterize each gait once on the CPU so transparent SVG margins
                    // do not swallow the first/last part of the stamina range.
                    int width = Math.Clamp(texture.Width, 1, 512), height = Math.Clamp(texture.Height, 1, 512);
                    using var surface = new ImageSurface(Format.Argb32, width, height);
                    api.Gui.DrawSvg(asset, surface, 0, 0, width, height, -1);
                    surface.Flush();
                    var pixels = new byte[surface.Stride * height];
                    Marshal.Copy(surface.DataPtr, pixels, 0, pixels.Length);
                    int top = height, bottom = -1;
                    for (int row = 0; row < height; row++)
                        for (int col = 0; col < width; col++)
                            if (pixels[row * surface.Stride + col * 4 + 3] > 0)
                            {
                                top = Math.Min(top, row);
                                bottom = row;
                                break;
                            }
                    if (bottom >= top) bounds = ((double)top / height, (double)(bottom + 1) / height);
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            api.Logger.Warning("[vsscpmod] Jaunt icon margins unavailable; using full image bounds: {0}",
                exception.GetBaseException().Message);
        }
        visibleBounds[texture] = bounds;
        return bounds;
    }

    private static bool DisplayOnly(GuiDialog __instance, ref bool __result)
    {
        // In 3.3.1 this method is inherited from GuiDialog. Limit the shared
        // hook to Jaunt's stamina HUD; every other dialog keeps its own gate.
        if (current?.hudType.IsInstanceOfType(__instance) != true) return true;
        __result = false;
        return false;
    }

    public void Dispose()
    {
        if (!ReferenceEquals(current, this)) return;
        current = null;
        harmony.UnpatchAll(HarmonyId);
        visibleBounds.Clear();
        percentageTexture?.Dispose();
        percentageTexture = null;
    }
}
