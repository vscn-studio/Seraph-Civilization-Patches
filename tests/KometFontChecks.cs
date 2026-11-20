using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Path = System.IO.Path;

static class KometFontChecks
{
    public static void Run(string root, string game, string modsDirectory)
    {
        if (OperatingSystem.IsWindows()) SetDllDirectory(Path.Combine(game, "Lib"));
        string inputs = Path.Combine(root, "tests/dependencies/komet-font");
        Directory.CreateDirectory(inputs);
        using (var zip = ZipFile.OpenRead(TestPackages.Find(modsDirectory, "Komet-3.0.0-pre.3.zip")))
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                entry.ExtractToFile(Path.Combine(inputs, Path.GetFileName(entry.FullName)), true);
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            string file = Path.Combine(inputs, name.Name + ".dll");
            return File.Exists(file) ? context.LoadFromAssemblyPath(file) : null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        Assembly ui = Assembly.LoadFrom(Path.Combine(inputs, "Komet.Ui.dll"));
        Type canvasType = ui.GetType("Komet.Hud.HudCanvas", true)!;
        Type hudType = ui.GetType("Komet.Hud.HudFonts", true)!;
        Type uiType = ui.GetType("Komet.Hud.HudUiFonts", true)!;
        MethodInfo setup = AccessTools.Method(canvasType, "Setup");
        MethodInfo hudUpdate = AccessTools.Method(hudType, "Update");
        MethodInfo uiUpdate = AccessTools.Method(uiType, "Update");
        MethodInfo create = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly
            .GetType("SeraphCivilizationPatches.KometFontCompatibility", true)!.GetMethod("TryCreate")!;
        bool enabled = false;
        var messages = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) => { messages.Add(m.Name + " " + string.Join(" ", a ?? [])); return null; });
        var loader = Proxy.Create<IModLoader>((m, _) => m.Name == "IsModEnabled" ? enabled : null);
        var api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch {
            "get_ModLoader" => loader, "get_Logger" => logger, _ => null });
        string originalLocale = Lang.CurrentLocale;
        string originalFont = GuiStyle.StandardFontName;
        float originalScale = RuntimeEnv.GUIScale;
        string output = Path.Combine(root, "artifacts/komet-font-checks");
        Directory.CreateDirectory(output);
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        IDisposable? fix = null, newer = null;
        object? canvas = null;
        try
        {
            RuntimeEnv.GUIScale = 1;
            Lang.ChangeLanguage("zh-cn");
            GuiStyle.StandardFontName = "sans-serif";
            Check(create.Invoke(null, [api]) == null, "missing Komet does not install patches");
            using var surface = new ImageSurface(Format.Argb32, 640, 160);
            using var ctx = new Context(surface);
            var font = new CairoFont(22, "Arial", [0.1, 0.2, 0.3, 1]) { FontWeight = FontWeight.Bold };
            const string chinese = "中文界面 设置 性能 显示 返回";
            void Sample(string name)
            {
                ctx.SetSourceRGB(1, 1, 1); ctx.Paint();
                setup.Invoke(null, [ctx, font, 22d]);
                ctx.MoveTo(12, 45); ctx.ShowText(chinese);
                ctx.MoveTo(12, 90); ctx.ShowText("Komet ABC 123 FPS");
                surface.WriteToPng(Path.Combine(output, name + ".png"));
            }
            Sample("before");
            Check(!HasGlyphs(ctx, chinese), "original Komet Setup reproduces missing Chinese glyphs");
            enabled = true;
            fix = (IDisposable?)create.Invoke(null, [api]);
            Check(fix != null && !messages.Any(x => x.Contains("unavailable")), "patch installs against actual Komet Ui assembly");
            const string owner = "vsscpmod.komet-font";
            Check(new[] { setup, hudUpdate, uiUpdate }.All(m => Harmony.GetPatchInfo(m)?.Owners.Contains(owner) == true),
                "canvas, menu metrics and HUD metrics are patched");
            Sample("after");
            Check(HasGlyphs(ctx, chinese) && HasGlyphs(ctx, "Komet ABC 123 FPS"), "patched native Cairo supplies Chinese and ASCII glyphs");
            Check(font.Fontname == "Arial" && font.UnscaledFontsize == 22 && font.FontWeight == FontWeight.Bold
                && font.Color.SequenceEqual(new double[] { 0.1, 0.2, 0.3, 1 }), "canvas preserves source font, size, weight and color");
            using var expectedSurface = new ImageSurface(Format.Argb32, 1, 1);
            using var expected = new Context(expectedSurface);
            expected.SelectFontFace("Microsoft YaHei", font.Slant, font.FontWeight); expected.SetFontSize(22);
            double expectedWidth = expected.TextExtents(chinese).XAdvance;
            Check(ctx.TextExtents(chinese).XAdvance == expectedWidth, "drawing uses requested font size and Windows CJK face");
            canvas = Activator.CreateInstance(canvasType, [api, false])!;
            double width = (double)AccessTools.Method(canvasType, "TextWidth").Invoke(canvas, [font, chinese])!;
            Check(width == expectedWidth, "actual Komet TextWidth agrees with drawing width");
            Check(HasGlyphs((Context)AccessTools.Field(canvasType, "_measure").GetValue(canvas)!, chinese), "measurement context also contains Chinese glyphs");
            object hud = Activator.CreateInstance(hudType)!;
            object menu = Activator.CreateInstance(uiType)!;
            foreach (string themeFace in new[] { "Saira SemiCondensed", "Lora" })
            {
                AccessTools.Method(hudType, "Families").Invoke(hud, [themeFace, themeFace, themeFace, themeFace]);
                AccessTools.Property(uiType, "Family").SetValue(null, themeFace);
                AccessTools.Property(uiType, "StrongFamily").SetValue(null, themeFace);
                hudUpdate.Invoke(hud, [1d]); uiUpdate.Invoke(menu, [1d]);
                foreach ((Type type, object instance) in new[] { (hudType, hud), (uiType, menu) })
                {
                    var fonts = type.GetProperties().Where(p => p.PropertyType == typeof(CairoFont)).Select(p => (CairoFont)p.GetValue(instance)!).ToArray();
                    Check(fonts.Length == 7 && fonts.All(f => f.Fontname == "Microsoft YaHei"), themeFace + " " + type.Name + " repairs every font including Mono");
                    Check(type.GetProperties().Where(p => p.Name.EndsWith("Row")).All(p => (double)p.GetValue(instance)! > 0), type.Name + " computes valid row heights");
                }
                Check(((CairoFont)AccessTools.Property(uiType, "Strong").GetValue(menu)!).FontWeight == FontWeight.Bold, "menu retains bold headings");
            }
            int hudEpoch = (int)AccessTools.Property(hudType, "Epoch").GetValue(hud)!;
            int uiEpoch = (int)AccessTools.Property(uiType, "Epoch").GetValue(menu)!;
            hudUpdate.Invoke(hud, [1d]); uiUpdate.Invoke(menu, [1d]);
            Check((int)AccessTools.Property(hudType, "Epoch").GetValue(hud)! == hudEpoch
                && (int)AccessTools.Property(uiType, "Epoch").GetValue(menu)! == uiEpoch, "unchanged fonts preserve metric caches");
            GuiStyle.StandardFontName = "Microsoft YaHei Light";
            hudUpdate.Invoke(hud, [1d]); uiUpdate.Invoke(menu, [1d]);
            Check((int)AccessTools.Property(hudType, "Epoch").GetValue(hud)! > hudEpoch
                && (int)AccessTools.Property(uiType, "Epoch").GetValue(menu)! > uiEpoch, "font preference changes invalidate metrics at the same scale");
            setup.Invoke(null, [ctx, font, 22d]);
            Check(HasGlyphs(ctx, chinese), "explicit configured CJK font renders Chinese");
            Lang.ChangeLanguage("en");
            hudUpdate.Invoke(hud, [1d]); uiUpdate.Invoke(menu, [1d]);
            Check(((CairoFont)AccessTools.Property(hudType, "Text").GetValue(hud)!).Fontname == "Lora"
                && ((CairoFont)AccessTools.Property(uiType, "Body").GetValue(menu)!).Fontname == "Lora"
                && ((CairoFont)AccessTools.Property(uiType, "Mono").GetValue(menu)!).Fontname == "monospace", "switching to English restores theme fonts and Mono");
            setup.Invoke(null, [ctx, font, 22d]);
            Check(!HasGlyphs(ctx, chinese) && HasGlyphs(ctx, "ABC"), "English canvas keeps original font behavior");
            Lang.ChangeLanguage("zh-tw");
            hudUpdate.Invoke(hud, [1d]); uiUpdate.Invoke(menu, [1d]);
            setup.Invoke(null, [ctx, font, 22d]);
            Check(HasGlyphs(ctx, "繁體中文設定"), "traditional Chinese locale is supported");
            newer = (IDisposable?)create.Invoke(null, [api]); fix!.Dispose();
            Check(((CairoFont)AccessTools.Property(hudType, "Text").GetValue(hud)!).Fontname == "Lora"
                && (string?)AccessTools.Property(uiType, "Family").GetValue(null) == "Lora", "session disposal restores live fonts and static theme settings");
            Check(Harmony.GetPatchInfo(setup)?.Owners.Contains(owner) == true, "disposing older session preserves active patch");
            newer!.Dispose(); newer.Dispose();
            Check(new[] { setup, hudUpdate, uiUpdate }.All(m => Harmony.GetPatchInfo(m)?.Owners.Contains(owner) != true), "dispose removes all owned patches");
            setup.Invoke(null, [ctx, font, 22d]);
            Check(!HasGlyphs(ctx, chinese), "unpatched Komet Setup returns to original behavior");
            Check(!messages.Any(x => x.StartsWith("Error") || x.StartsWith("Warning")), "no patch warnings or errors");
            Console.WriteLine($"Komet font: {count} checks passed. Samples: {output}");
        }
        finally
        {
            if (canvas is IDisposable disposable) disposable.Dispose();
            newer?.Dispose(); fix?.Dispose();
            Lang.ChangeLanguage(originalLocale); GuiStyle.StandardFontName = originalFont; RuntimeEnv.GUIScale = originalScale;
            AssemblyLoadContext.Default.Resolving -= Resolve;
        }
    }

    private static bool HasGlyphs(Context ctx, string text)
    {
        using ScaledFont font = ctx.GetScaledFont();
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int status = TextToGlyphs(font.Handle, 0, 0, bytes, bytes.Length, out nint glyphs, out int count, 0, 0, 0);
        try
        {
            if (status != 0 || count != text.EnumerateRunes().Count()) return false;
            int stride = OperatingSystem.IsWindows() || IntPtr.Size == 4 ? 24 : Marshal.SizeOf<Glyph>();
            for (int i = 0; i < count; i++)
                if ((OperatingSystem.IsWindows() || IntPtr.Size == 4 ? Marshal.ReadInt32(glyphs + i * stride) : Marshal.ReadInt64(glyphs + i * stride)) == 0) return false;
            return true;
        }
        finally { if (glyphs != 0) FreeGlyphs(glyphs); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);
    [DllImport("libcairo-2", EntryPoint = "cairo_scaled_font_text_to_glyphs", CallingConvention = CallingConvention.Cdecl)]
    private static extern int TextToGlyphs(nint font, double x, double y, byte[] text, int length, out nint glyphs, out int count, nint clusters, nint clusterCount, nint flags);
    [DllImport("libcairo-2", EntryPoint = "cairo_glyph_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FreeGlyphs(nint glyphs);
}
