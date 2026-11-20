using System.Reflection;
using System.Runtime.CompilerServices;
using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace SeraphCivilizationPatches;

/// <summary>Komet's private Cairo canvas bypasses the game's font setup and glyph fallback.</summary>
internal sealed class KometFontCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.komet-font";
    private static readonly HashSet<string> LatinFaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "sans-serif", "serif", "monospace", "Lora", "Montserrat", "Almendra", "Arial", "Saira SemiCondensed", "Saira SemiCondensed Medium"
    };
    private readonly Harmony harmony = new(HarmonyId);
    private static KometFontCompatibility? current;
    private PropertyInfo[] hudFonts = [];
    private PropertyInfo[] uiFonts = [];
    private PropertyInfo? family;
    private PropertyInfo? strongFamily;
    private FieldInfo hudScale = null!;
    private readonly ConditionalWeakTable<CairoFont, SavedFace> savedFaces = new();
    private readonly List<WeakReference<CairoFont>> changedFonts = [];
    private string? originalFamily, originalStrong, appliedFamily;

    private sealed class SavedFace(string original, string applied)
    {
        public string Original = original;
        public string Applied = applied;
    }

    public static KometFontCompatibility? TryCreate(ICoreClientAPI api)
    {
        if (!api.ModLoader.IsModEnabled("komet")) return null;
        var fix = new KometFontCompatibility();
        try
        {
            Type canvas = AccessTools.TypeByName("Komet.Hud.HudCanvas")
                ?? throw new TypeLoadException("Komet.Hud.HudCanvas");
            Type fonts = canvas.Assembly.GetType("Komet.Hud.HudFonts", true)!;
            Type ui = canvas.Assembly.GetType("Komet.Hud.HudUiFonts", true)!;
            MethodInfo setup = AccessTools.DeclaredMethod(canvas, "Setup", [typeof(Context), typeof(CairoFont), typeof(double)])
                ?? throw new MissingMethodException(canvas.FullName, "Setup");
            MethodInfo update = AccessTools.DeclaredMethod(fonts, "Update", [typeof(double)])
                ?? throw new MissingMethodException(fonts.FullName, "Update");
            MethodInfo updateUi = AccessTools.DeclaredMethod(ui, "Update", [typeof(double)])
                ?? throw new MissingMethodException(ui.FullName, "Update");
            fix.hudFonts = FontProperties(fonts);
            fix.uiFonts = FontProperties(ui);
            fix.hudScale = AccessTools.Field(fonts, "_scale") ?? throw new MissingFieldException(fonts.FullName, "_scale");
            fix.family = AccessTools.Property(ui, "Family") ?? throw new MissingMemberException(ui.FullName, "Family");
            fix.strongFamily = AccessTools.Property(ui, "StrongFamily") ?? throw new MissingMemberException(ui.FullName, "StrongFamily");
            if (!setup.IsStatic || setup.ReturnType != typeof(void) || fix.hudScale.FieldType != typeof(long)
                || fix.family.PropertyType != typeof(string) || fix.family.SetMethod?.IsStatic != true
                || fix.strongFamily.PropertyType != typeof(string) || fix.strongFamily.SetMethod?.IsStatic != true)
                throw new InvalidOperationException("Unsupported Komet font signature");

            current?.Dispose();
            current = fix;
            // Setup is shared by measuring and painting, including background canvases.
            fix.harmony.Patch(setup, postfix: new HarmonyMethod(typeof(KometFontCompatibility), nameof(UseChineseFace)));
            // Set names before Update computes cached row heights and font epochs.
            fix.harmony.Patch(update, prefix: new HarmonyMethod(typeof(KometFontCompatibility), nameof(UpdateHudFonts)));
            fix.harmony.Patch(updateUi, prefix: new HarmonyMethod(typeof(KometFontCompatibility), nameof(UpdateUiFonts)));
            api.Logger.Notification("[vsscpmod] Komet Chinese font compatibility enabled (measurement, menu and HUD).");
            return fix;
        }
        catch (Exception exception)
        {
            fix.Dispose();
            api.Logger.Warning("[vsscpmod] Komet font compatibility unavailable: {0}", exception.GetBaseException().Message);
            return null;
        }
    }

    private static PropertyInfo[] FontProperties(Type type)
    {
        PropertyInfo[] properties = type.GetProperties().Where(p => p.PropertyType == typeof(CairoFont) && p.GetMethod?.IsStatic == false).ToArray();
        if (properties.Length != 7) throw new MissingMemberException(type.FullName, "seven Cairo font properties");
        return properties;
    }

    private static bool Chinese => Lang.CurrentLocale?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;

    private static string ChineseFace()
    {
        string name = GuiStyle.StandardFontName;
        // Preserve explicit CJK font preferences. Stock Latin/generic faces have
        // no fallback in Cairo's toy text API, unlike the normal game renderer.
        if (!string.IsNullOrWhiteSpace(name) && !LatinFaces.Contains(name)) return name;
        if (OperatingSystem.IsWindows()) return "Microsoft YaHei";
        if (OperatingSystem.IsMacOS()) return "PingFang SC";
        return "Noto Sans CJK SC";
    }

    private static void UseChineseFace(Context __0, CairoFont __1)
    {
        if (Chinese) __0.SelectFontFace(ChineseFace(), __1.Slant, __1.FontWeight);
    }

    private static void UpdateHudFonts(object __instance)
    {
        if (current is { } fix && fix.SetFonts(fix.hudFonts, __instance, Chinese))
            fix.hudScale.SetValue(__instance, BitConverter.DoubleToInt64Bits(double.NaN));
    }

    private static void UpdateUiFonts(object __instance)
    {
        if (current is not { } fix) return;
        if (!Chinese)
        {
            fix.RestoreFamily();
            fix.SetFonts(fix.uiFonts, __instance, false);
            return;
        }
        string face = ChineseFace();
        if (fix.appliedFamily == null || (string?)fix.family!.GetValue(null) != fix.appliedFamily || fix.strongFamily!.GetValue(null) != null)
        {
            fix.originalFamily = (string?)fix.family!.GetValue(null);
            fix.originalStrong = (string?)fix.strongFamily!.GetValue(null);
        }
        fix.appliedFamily = face;
        fix.family!.SetValue(null, face);
        // Let Komet preserve its bold weight rather than treating this as a
        // separately supplied regular font for headings.
        fix.strongFamily!.SetValue(null, null);
        fix.SetFonts(fix.uiFonts, __instance, true);
    }

    private bool SetFonts(PropertyInfo[] properties, object instance, bool chinese)
    {
        string face = ChineseFace();
        bool changed = false;
        foreach (PropertyInfo property in properties)
        {
            if (property.GetValue(instance) is not CairoFont font) continue;
            if (!chinese)
            {
                changed |= RestoreFont(font);
                continue;
            }
            if (!savedFaces.TryGetValue(font, out SavedFace? saved))
            {
                savedFaces.Add(font, saved = new SavedFace(font.Fontname, face));
                changedFonts.Add(new WeakReference<CairoFont>(font));
            }
            else if (font.Fontname != saved.Applied) saved.Original = font.Fontname;
            saved.Applied = face;
            changed |= font.Fontname != face;
            font.Fontname = face;
        }
        return changed;
    }

    private bool RestoreFont(CairoFont font)
    {
        if (!savedFaces.TryGetValue(font, out SavedFace? saved)) return false;
        bool changed = font.Fontname == saved.Applied && font.Fontname != saved.Original;
        if (changed) font.Fontname = saved.Original;
        savedFaces.Remove(font);
        return changed;
    }

    private void RestoreFamily()
    {
        if (appliedFamily == null) return;
        if ((string?)family!.GetValue(null) == appliedFamily && strongFamily!.GetValue(null) == null)
        {
            family.SetValue(null, originalFamily);
            strongFamily!.SetValue(null, originalStrong);
        }
        appliedFamily = null;
    }

    public void Dispose()
    {
        if (!ReferenceEquals(current, this)) return;
        current = null;
        harmony.UnpatchAll(HarmonyId);
        RestoreFamily();
        foreach (var reference in changedFonts)
            if (reference.TryGetTarget(out CairoFont? font)) RestoreFont(font);
        changedFonts.Clear();
    }
}
