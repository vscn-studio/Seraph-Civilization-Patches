using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;

namespace SeraphCivilizationPatches;

internal sealed class BillpostingFontCompatibility : IDisposable
{
    private const string HarmonyId = "vsscpmod.billposting-font";
    private readonly Harmony harmony = new(HarmonyId);
    private static BillpostingFontCompatibility? current;

    public static BillpostingFontCompatibility? TryCreate(ICoreClientAPI api)
    {
        if (!api.ModLoader.IsModEnabled("billposting")) return null;
        var compatibility = new BillpostingFontCompatibility();
        try
        {
            Type renderer = AccessTools.TypeByName("billposting.EntityBillpostRenderer")
                ?? throw new TypeLoadException("billposting.EntityBillpostRenderer was not found.");
            Type draw = renderer.Assembly.GetType("billposting.BillpostDrawUtil")
                ?? throw new TypeLoadException("billposting.BillpostDrawUtil was not found.");
            MethodInfo setText = AccessTools.Method(renderer, "SetNewText", [typeof(string), typeof(int)])
                ?? throw new MissingMethodException(renderer.FullName, "SetNewText");
            FieldInfo font = AccessTools.Field(renderer, "font")
                ?? throw new MissingFieldException(renderer.FullName, "font");
            if (font.FieldType != typeof(CairoFont))
                throw new InvalidOperationException("Billposting renderer font has an unsupported type.");
            MethodInfo reformat = AccessTools.Method(draw, "ReformatContext",
                [typeof(Cairo.Context).MakeByRefType(), typeof(CairoFont).MakeByRefType(),
                    typeof(double).MakeByRefType(), typeof(string), typeof(bool)])
                ?? throw new MissingMethodException(draw.FullName, "ReformatContext");

            current?.Dispose();
            compatibility.harmony.Patch(setText,
                prefix: new HarmonyMethod(typeof(BillpostingFontCompatibility), nameof(SetTextFont)));
            compatibility.harmony.Patch(reformat,
                prefix: new HarmonyMethod(typeof(BillpostingFontCompatibility), nameof(UseClientFormatFont)));
            current = compatibility;
            api.Logger.Notification("[vsscpmod] Billposting client font compatibility enabled.");
            return compatibility;
        }
        catch (Exception exception)
        {
            compatibility.harmony.UnpatchAll(HarmonyId);
            api.Logger.Warning("[vsscpmod] Billposting font compatibility unavailable: {0}",
                exception.GetBaseException().Message);
            return null;
        }
    }

    // Run before SetNewText measures text or creates its Cairo texture.
    private static void SetTextFont(CairoFont ___font)
    {
        if (___font != null) ___font.Fontname = ClientFontName();
    }

    // Billposting interprets standalone [font=...,fontSize=...,color=...] lines
    // during BOTH wrapping and drawing. Rewrite only the transient font option,
    // before it sets up Cairo; entity attributes and parchment text stay intact.
    private static void UseClientFormatFont(ref string formatText)
    {
        string trimmed = formatText.Trim();
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']')) return;
        string[] options = trimmed[1..^1].Split(',', StringSplitOptions.TrimEntries);
        bool changed = false;
        for (int i = 0; i < options.Length; i++)
        {
            if (!options[i].StartsWith("font=", StringComparison.Ordinal)) continue;
            options[i] = "font=" + ClientFontName();
            changed = true;
        }
        if (changed) formatText = "[" + string.Join(',', options) + "]";
    }

    private static string ClientFontName()
    {
        string name = GuiStyle.StandardFontName;
        // Respect FontSettings and the game's selected font. Stock Latin faces
        // need a CJK family; Cairo's toy text API does not supply glyph fallback.
        if (!string.IsNullOrWhiteSpace(name)
            && !new[] { "sans-serif", "serif", "Lora", "Montserrat", "Almendra" }
                .Contains(name, StringComparer.OrdinalIgnoreCase)) return name;
        if (OperatingSystem.IsWindows()) return "Microsoft YaHei";
        if (OperatingSystem.IsMacOS()) return "PingFang SC";
        return "Noto Sans CJK SC";
    }

    public void Dispose()
    {
        if (!ReferenceEquals(current, this)) return;
        current = null;
        harmony.UnpatchAll(HarmonyId);
    }
}
