using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Path = System.IO.Path;

static class BillpostingChecks
{
    public static void Run(string root, string game, string dll)
    {
        if (OperatingSystem.IsWindows()) SetDllDirectory(Path.Combine(game, "Lib"));
        Assembly billposting = Assembly.LoadFrom(Path.GetFullPath(dll));
        Type renderer = billposting.GetType("billposting.EntityBillpostRenderer", true)!;
        Type draw = billposting.GetType("billposting.BillpostDrawUtil", true)!;
        Type mod = billposting.GetType("billposting.billpostingModSystem", true)!;
        Type texture = billposting.GetType("billposting.BillpostTextureUtil", true)!;
        Type compatibility = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly
            .GetType("SeraphCivilizationPatches.BillpostingFontCompatibility", true)!;
        MethodInfo create = compatibility.GetMethod("TryCreate")!;
        MethodInfo setText = AccessTools.Method(renderer, "SetNewText");
        MethodInfo reformat = AccessTools.Method(draw, "ReformatContext");
        var messages = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) => { messages.Add(m.Name + " " + string.Join(" ", a ?? [])); return null; });
        bool enabled = false;
        var loader = Proxy.Create<IModLoader>((m, _) => m.Name == "IsModEnabled" ? enabled : null);
        string output = Path.Combine(root, "artifacts/billposting-checks");
        Directory.CreateDirectory(output);
        int uploads = 0;
        var gui = Proxy.Create<IGuiAPI>((m, a) =>
        {
            if (m.Name == "LoadOrUpdateCairoTexture")
            {
                ((ImageSurface)a![0]!).WriteToPng(Path.Combine(output, $"billpost-{++uploads}.png"));
            }
            return null;
        });
        var api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_ModLoader" => loader, "get_Logger" => logger, "get_Gui" => gui,
            "get_Render" => Proxy.Create<IRenderAPI>((_, _) => null), _ => null
        });
        int count = 0;
        void Check(bool ok, string label)
        {
            if (!ok) throw new Exception("FAIL: " + label);
            count++;
            Console.WriteLine("PASS: " + label);
        }
        string originalFont = GuiStyle.StandardFontName;
        float originalScale = RuntimeEnv.GUIScale;
        IDisposable? patch = null, newer = null;
        try
        {
            Check(create.Invoke(null, [api]) == null, "missing Billposting is skipped without patches");
            RuntimeEnv.GUIScale = 1;
            GuiStyle.StandardFontName = "Microsoft YaHei Light";
            using var surface = new ImageSurface(Format.Argb32, 1, 1);
            using var ctx = new Context(surface);
            var legacy = new CairoFont(20, "Arial", [0, 0, 0, 1]);
            legacy.SetupContext(ctx);
            Check(!HasGlyphs(ctx, "中文公告"), "Latin-only Cairo font reproduces missing Chinese glyphs");

            enabled = true;
            patch = (IDisposable?)create.Invoke(null, [api]);
            Check(patch != null && !messages.Any(m => m.Contains("unavailable")), "patch installs against real Billposting 2.0.3");
            const string owner = "vsscpmod.billposting-font";
            Check(new[] { setText, reformat }.All(m => Harmony.GetPatchInfo(m)?.Owners.Contains(owner) == true),
                "renderer and format measurement/drawing entry points are patched");

            object instance = RuntimeHelpers.GetUninitializedObject(renderer);
            object modInstance = Activator.CreateInstance(mod)!;
            AccessTools.Field(mod, "textureUtil").SetValue(modInstance, Activator.CreateInstance(texture, [api]));
            AccessTools.Field(renderer, "billpostingMod").SetValue(instance, modInstance);
            AccessTools.Field(renderer, "api").SetValue(instance, api);
            AccessTools.Field(renderer, "font").SetValue(instance, legacy);
            AccessTools.Field(renderer, "fontSize").SetValue(instance, 20f);
            AccessTools.Field(renderer, "TextWidth").SetValue(instance, 240);
            AccessTools.Field(renderer, "TextHeight").SetValue(instance, 440);
            const string text = "中文公告\n玩家写下的内容 ABC 123\n[font=Lora,fontSize=24,color=FF0000]\n中文格式内容\n第二行";
            setText.Invoke(instance, [text, unchecked((int)0xff000000)]);
            Check(uploads == 1 && legacy.Fontname == GuiStyle.StandardFontName,
                "real SetNewText renders a Cairo texture with the configured client font");
            Check((string)AccessTools.Field(renderer, "text").GetValue(instance)! == text,
                "saved/display text including format commands stays intact");
            legacy.SetupContext(ctx);
            Check(HasGlyphs(ctx, "中文公告玩家写下的内容"), "selected font supplies actual Chinese glyphs in Cairo");

            object util = Activator.CreateInstance(draw)!;
            var formatted = new CairoFont(20, "Arial", [0, 0, 0, 1]);
            object?[] arguments = [ctx, formatted, 20d, "[font=Lora,fontSize=24,color=FF0000]", true];
            reformat.Invoke(util, arguments);
            Check(formatted.Fontname == GuiStyle.StandardFontName && HasGlyphs(ctx, "中文格式"),
                "inline Latin font cannot reintroduce missing Chinese glyphs");
            Check(formatted.UnscaledFontsize == 24 && formatted.Color.SequenceEqual(new double[] { 1, 0, 0, 1 }),
                "real format parser preserves requested font size and color");

            GuiStyle.StandardFontName = "sans-serif";
            setText.Invoke(instance, ["默认字体回退\n中文公告", unchecked((int)0xff000000)]);
            legacy.SetupContext(ctx);
            Check(legacy.Fontname == "Microsoft YaHei" && HasGlyphs(ctx, "默认字体回退"),
                "stock default falls back to a Windows CJK font");

            newer = (IDisposable?)create.Invoke(null, [api]);
            patch!.Dispose();
            Check(Harmony.GetPatchInfo(setText)?.Owners.Contains(owner) == true,
                "disposing an older instance keeps the active patch");
            newer!.Dispose();
            Check(new[] { setText, reformat }.All(m => Harmony.GetPatchInfo(m)?.Owners.Contains(owner) != true),
                "disposal restores both original methods");
            arguments = [ctx, formatted, 20d, "[font=Arial,fontSize=18]", true];
            reformat.Invoke(util, arguments);
            Check(formatted.Fontname == "Arial" && !HasGlyphs(ctx, "中文"), "unpatched format parser restores original font behavior");
            Console.WriteLine($"{count} Billposting checks passed. Rendered samples: {output}");
        }
        finally
        {
            newer?.Dispose(); patch?.Dispose();
            GuiStyle.StandardFontName = originalFont;
            RuntimeEnv.GUIScale = originalScale;
        }
    }

    private static bool HasGlyphs(Context ctx, string text)
    {
        using ScaledFont font = ctx.GetScaledFont();
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        int status = TextToGlyphs(font.Handle, 0, 0, utf8, utf8.Length, out nint glyphs, out int count,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            if (status != 0 || count != text.EnumerateRunes().Count()) return false;
            int stride = OperatingSystem.IsWindows() || IntPtr.Size == 4 ? 24 : Marshal.SizeOf<Glyph>();
            for (int i = 0; i < count; i++)
            {
                nint glyph = glyphs + i * stride;
                long index = OperatingSystem.IsWindows() || IntPtr.Size == 4
                    ? Marshal.ReadInt32(glyph) : Marshal.ReadInt64(glyph);
                if (index == 0) return false;
            }
            return true;
        }
        finally { if (glyphs != IntPtr.Zero) FreeGlyphs(glyphs); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);
    [DllImport("libcairo-2", EntryPoint = "cairo_scaled_font_text_to_glyphs", CallingConvention = CallingConvention.Cdecl)]
    private static extern int TextToGlyphs(nint font, double x, double y, byte[] text, int length,
        out nint glyphs, out int count, nint clusters, nint clusterCount, nint flags);
    [DllImport("libcairo-2", EntryPoint = "cairo_glyph_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FreeGlyphs(nint glyphs);
}
