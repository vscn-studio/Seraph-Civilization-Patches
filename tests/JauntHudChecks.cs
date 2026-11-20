using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Path = System.IO.Path;

static class JauntHudChecks
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);

    public static void Run(string dll, string gamePath)
    {
        if (OperatingSystem.IsWindows()) SetDllDirectory(Path.Combine(gamePath, "Lib"));
        GuiStyle.StandardFontName = "Microsoft YaHei";
        var jaunt = Assembly.LoadFrom(Path.GetFullPath(dll));
        var hudType = jaunt.GetType("Jaunt.Hud.HudElementStaminaBar", true)!;
        var hud = (GuiDialog)RuntimeHelpers.GetUninitializedObject(hudType);
        hud.Composers = new GuiDialog.DlgComposers(hud);
        AccessTools.Field(typeof(GuiDialog), "opened").SetValue(hud, true);
        // Use the real HUD type and real GUI event handlers. Only skip its
        // constructor's Cairo/GPU setup; reproduce the logged composer bounds.
        var composer = (GuiComposer)RuntimeHelpers.GetUninitializedObject(typeof(GuiComposer));
        composer.Enabled = true;
        AccessTools.Field(typeof(GuiComposer), "interactiveElements").SetValue(composer, new Dictionary<string, GuiElement>());
        AccessTools.Field(typeof(GuiComposer), "bounds").SetValue(composer, new ElementBounds
        {
            ParentBounds = ElementBounds.Empty, absFixedX = 801.9, absFixedY = 1221.6,
            absInnerWidth = 956.2, absInnerHeight = 135
        });
        hud.Composers["jaunt:staminabar"] = composer;
        var lib = Assembly.Load("VintagestoryLib");
        Type gameType = lib.GetType("Vintagestory.Client.NoObf.ClientMain", true)!;
        Type managerType = lib.GetType("Vintagestory.Client.NoObf.GuiManager", true)!;
        var game = RuntimeHelpers.GetUninitializedObject(gameType);
        var manager = RuntimeHelpers.GetUninitializedObject(managerType);
        AccessTools.Field(managerType, "game").SetValue(manager, game);
        var dialogs = new List<GuiDialog> { hud };
        AccessTools.Field(gameType, "LoadedGuis").SetValue(game, dialogs);
        AccessTools.Field(gameType, "OpenedGuis").SetValue(game, new List<GuiDialog>());
        Type engineApiType = lib.GetType("Vintagestory.Client.NoObf.ClientCoreAPI", true)!;
        Type inputType = lib.GetType("Vintagestory.Client.NoObf.InputAPI", true)!;
        object engineApi = RuntimeHelpers.GetUninitializedObject(engineApiType);
        AccessTools.Field(engineApiType, "inputapi").SetValue(engineApi, Activator.CreateInstance(inputType, game));
        AccessTools.Field(gameType, "api").SetValue(game, engineApi);
        bool enabled = true;
        var messages = new List<string>();
        var logger = Proxy.Create<ILogger>((_, a) =>
        {
            messages.Add(string.Join(" ", (a ?? []).SelectMany(v => v is object[] values ? values : new object?[] { v })));
            return null;
        });
        var loader = Proxy.Create<IModLoader>((m, a) => m.Name == "IsModEnabled" && (string)a![0]! == "jaunt" ? enabled : null);
        var horse = new EntityAgent();
        var rider = new EntityPlayer();
        var supplier = Proxy.Create<IMountable>((m, _) => m.Name == "get_OnEntity" ? horse : null);
        var seat = Proxy.Create<IMountableSeat>((m, _) => m.Name == "get_MountSupplier" ? supplier : null);
        AccessTools.Property(typeof(EntityAgent), "MountedOn").SetValue(rider, seat);
        var playerType = lib.GetType("Vintagestory.Client.NoObf.ClientPlayer", true)!;
        var player = (IClientPlayer)RuntimeHelpers.GetUninitializedObject(playerType);
        var worldDataField = AccessTools.Field(playerType, "worlddata");
        var worldData = RuntimeHelpers.GetUninitializedObject(worldDataField.FieldType);
        worldDataField.SetValue(player, worldData);
        AccessTools.Field(worldDataField.FieldType, "entityplayer").SetValue(worldData, rider);
        var world = Proxy.Create<IClientWorldAccessor>((m, _) => m.Name == "get_Player" ? player : null);
        int width = 1920, height = 1080;
        bool failFill = false;
        var draws = new List<(int Texture, double X, double Y, double Size, Vec4f? Color, double? ClipTop, double? ClipHeight)>();
        var clips = new Stack<ElementBounds>();
        var labels = new List<(double X, double Y, double Width, double Height, bool Clipped)>();
        int uploads = 0, deletedLabels = 0;
        var render = Proxy.Create<IRenderAPI>((m, a) => {
            switch (m.Name)
            {
                case "get_FrameWidth": return width;
                case "get_FrameHeight": return height;
                case "PushScissor": clips.Push((ElementBounds)a![0]!); return null;
                case "PopScissor": clips.Pop(); return null;
                case "DeleteTexture": deletedLabels++; return null;
                case "RenderTexture":
                    if (failFill && clips.Count > 0) throw new InvalidOperationException("test render failure");
                    if ((int)a![0]! == 1000)
                    {
                        labels.Add(((double)a[1]!, (double)a[2]!, (double)a[3]!, (double)a[4]!, clips.Count > 0));
                        return null;
                    }
                    draws.Add(((int)a![0]!, (double)a[1]!, (double)a[2]!, (double)a[3]!, (Vec4f?)a[6],
                        clips.Count > 0 ? clips.Peek().renderY : null, clips.Count > 0 ? clips.Peek().InnerHeight : null));
                    return null;
                default: throw new Exception("Unexpected rendering call: " + m.Name);
            }
        });
        int rasterizations = 0;
        bool failSvg = false;
        byte[]? labelPixels = null;
        var svg = Proxy.Create<IAsset>((_, _) => null);
        var assets = Proxy.Create<IAssetManager>((m, _) => m.Name == "TryGet" ? svg : null);
        var gui = Proxy.Create<IGuiAPI>((m, a) => {
            if (m.Name == "DeleteTexture") { deletedLabels++; return null; }
            if (m.Name == "LoadOrUpdateCairoTexture")
            {
                var textSurface = (ImageSurface)a![0]!;
                textSurface.Flush();
                labelPixels = new byte[textSurface.Stride * textSurface.Height];
                Marshal.Copy(textSurface.DataPtr, labelPixels, 0, labelPixels.Length);
                var textTexture = (LoadedTexture)a[2]!;
                textTexture.TextureId = 1000;
                textTexture.Width = textSurface.Width;
                textTexture.Height = textSurface.Height;
                string previews = Path.Combine("artifacts", "jaunt-hud-inspection", "percentages");
                Directory.CreateDirectory(previews);
                textSurface.WriteToPng(Path.Combine(previews, $"label-{++uploads}.png"));
                return null;
            }
            if (m.Name != "DrawSvg") throw new Exception("Unexpected GUI call: " + m.Name);
            rasterizations++;
            if (failSvg) throw new InvalidOperationException("test SVG failure");
            var surface = (ImageSurface)a![1]!;
            using var context = new Context(surface);
            context.SetSourceRGBA(1, 1, 1, 1);
            context.Rectangle(10, 15, 50, 35);
            context.Fill();
            return null;
        });
        var api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_ModLoader" => loader, "get_Logger" => logger, "get_World" => world, "get_Render" => render,
            "get_Gui" => gui, "get_Assets" => assets, _ => null
        });
        var jauntSystem = jaunt.GetType("Jaunt.JauntModSystem", true)!;
        var singleton = AccessTools.Field(jauntSystem, "<Instance>k__BackingField");
        object? oldSystem = singleton.GetValue(null);
        var system = RuntimeHelpers.GetUninitializedObject(jauntSystem);
        object config = Activator.CreateInstance(jaunt.GetType("Jaunt.Config.JauntConfig", true)!)!;
        AccessTools.Field(jauntSystem, "<Config>k__BackingField").SetValue(system, config);
        singleton.SetValue(null, system);
        void Configure(string name, object value) => AccessTools.Property(config.GetType(), name).SetValue(config, value);
        var iconType = jaunt.GetType("Jaunt.Hud.HudIconRenderer", true)!;
        var icon = RuntimeHelpers.GetUninitializedObject(iconType);
        var texture = new LoadedTexture(api) { TextureId = 42, Width = 75, Height = 75 };
        AccessTools.Field(iconType, "activeTexture").SetValue(icon, texture);
        var renderIcon = AccessTools.Method(iconType, "OnRenderFrame");
        void Draw() { draws.Clear(); labels.Clear(); renderIcon.Invoke(icon, [0.016f, EnumRenderStage.Ortho]); }
        var stamina = new TreeAttribute();
        horse.WatchedAttributes.SetAttribute("jaunt:stamina", stamina);
        void Stamina(float value, float max) { stamina.SetFloat("currentstamina", value); stamina.SetFloat("maxstamina", max); }
        float oldScale = RuntimeEnv.GUIScale;
        var receiver = new SlotReceiver(api, () => AccessTools.Method(managerType, "OnMouseEnterSlot")
            .Invoke(manager, [new ItemSlot(null)]));
        dialogs.Add(receiver);
        Type fixType = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly
            .GetType("SeraphCivilizationPatches.JauntStaminaHudCompatibility", true)!;
        MethodInfo create = AccessTools.Method(fixType, "TryCreate");
        const string owner = "vsscpmod.jaunt-stamina-input";
        int checks = 0;
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception("FAIL: " + message + "\n" + string.Join("\n", messages));
            checks++; Console.WriteLine("PASS: " + message);
        }
        void Route(string method)
            => AccessTools.Method(managerType, method).Invoke(manager, [new MouseEvent(1085, 1319, EnumMouseButton.Left, 0)]);
        IDisposable? fix = null, replacement = null;
        try
        {
            Route("OnMouseMove"); Route("OnMouseDown"); Route("OnMouseUp");
            Check(hud.ShouldReceiveMouseEvents() && receiver.Moves == 0 && receiver.Downs == 0 && receiver.Ups == 0,
                "actual Jaunt 3.3.1 HUD reproduces blocked hotbar hover, press and release within the logged invisible bounds");
            enabled = false;
            Check(create.Invoke(null, [api]) == null, "missing optional Jaunt leaves GUI input untouched");
            enabled = true;
            fix = (IDisposable?)create.Invoke(null, [api]);
            Check(fix != null && !hud.ShouldReceiveMouseEvents(), "Jaunt stamina HUD no longer receives mouse events");
            Route("OnMouseMove"); Route("OnMouseDown"); Route("OnMouseUp");
            Check(receiver.Moves == 1 && receiver.Downs == 1 && receiver.Ups == 1
                && (bool)AccessTools.Field(managerType, "didHoverSlotEventTrigger").GetValue(manager)!,
                "real GUI routing reaches the underlying slots for hover, press and release");
            var other = new OtherDialog(api);
            AccessTools.Field(typeof(GuiDialog), "opened").SetValue(other, true);
            Check(other.ShouldReceiveMouseEvents(), "other dialogs still receive mouse events through the shared base method");
            Check(hud.IsOpened() && hud.Composers["jaunt:staminabar"] == composer && composer.Enabled
                && (Harmony.GetPatchInfo(AccessTools.Method(hudType, "OnRenderGUI"))?.Owners.Contains(owner) ?? false)
                && !(Harmony.GetPatchInfo(AccessTools.Method(hudType, "OnGameTick"))?.Owners.Contains(owner) ?? false),
                "only horizontal rendering is suppressed; original stamina updates remain intact");
            hud.OnRenderGUI(0.016f); // Original would dereference an absent API on this uninitialized HUD.
            Check(true, "horizontal HUD render returns without drawing its overlapping composer");
            RuntimeEnv.GUIScale = 1;
            Stamina(50, 100);
            Draw();
            Check(draws.Count == 2 && draws.All(d => d.Texture == 42 && d.Size == 75)
                && draws[0].ClipTop == null && draws[1].ClipHeight == 37.5 && clips.Count == 0,
                "real Jaunt renderer draws a dim silhouette plus bottom half of the same texture at half stamina");
            Check(draws[0].X == 537.5 && draws[0].Y == 882 && draws[1].ClipTop == 919.5,
                "horse icon keeps configured horizontal placement and moves up 18 GUI units");
            Check(draws[0].Color!.R < .3 && draws[1].Color!.G > .7, "empty silhouette is dark and available stamina is gold");
            Check(labels.Count == 1 && !labels[0].Clipped && labels[0].X + labels[0].Width / 2 == draws[0].X + 37.5
                && labels[0].Y + labels[0].Height / 2 == draws[0].Y + 37.5,
                "percentage overlay is centered on the horse and is outside the stamina fill clip");
            Check(labelPixels != null && Enumerable.Range(0, labelPixels.Length / 4).Any(i =>
                labelPixels[i * 4] == 255 && labelPixels[i * 4 + 1] == 255 && labelPixels[i * 4 + 2] == 255 && labelPixels[i * 4 + 3] == 255),
                "actual Cairo percentage texture contains opaque white lettering");
            Stamina(50.1f, 100); Draw();
            Check(uploads == 1, "unchanged displayed percentage reuses its GPU texture");
            Stamina(51, 100); Draw();
            Check(uploads == 2, "crossing a percentage boundary updates the existing text texture");
            Stamina(20, 100); Draw();
            Check(draws.Count == 2 && draws[1].Color!.G < .3 && draws[1].ClipHeight == 15,
                "low stamina fills one fifth of silhouette in red");
            Stamina(0, 100); Draw();
            Check(draws.Count == 1 && clips.Count == 0, "exhausted mount retains a visible empty silhouette");
            Check(labels.Count == 1 && !labels[0].Clipped && (int)AccessTools.Field(fixType, "lastPercentage").GetValue(fix)! == 0,
                "exhausted horse still shows an unclipped 0% label");
            Stamina(100, 100); Draw();
            Check(draws.Count == 2 && draws[1].ClipHeight == 75 && draws[1].ClipTop == draws[0].Y,
                "full stamina fills the complete silhouette and remains visible");
            Check(labels.Count == 1 && (int)AccessTools.Field(fixType, "lastPercentage").GetValue(fix)! == 100,
                "full horse shows 100% over the filled silhouette");
            Stamina(100, 200); Draw();
            Check(draws[1].ClipHeight == 37.5, "max stamina changes update the fill even when current stamina is unchanged");
            Stamina(300, 100); Draw();
            Check(draws[1].ClipHeight == 75, "stamina overflow is clamped to full");
            Stamina(-1, 100); Draw();
            Check(draws.Count == 1, "negative stamina is clamped to empty");
            foreach (var values in new[] { (50f, 0f), (float.NaN, 100f), (50f, float.PositiveInfinity) })
            {
                Stamina(values.Item1, values.Item2); Draw();
                Check(draws.Count == 1 && draws[0].Color == null, "invalid stamina retains plain gait icon without invalid clipping");
            }
            Stamina(50, 100);
            RuntimeEnv.GUIScale = 1.5f;
            width = 2560; height = 1440;
            Configure("IconOffsetX", -300f); Configure("IconOffsetY", -110f);
            Draw();
            Check(draws[0].Size == 112.5 && draws[0].X == 773.75 && draws[0].Y == 1135.5 && draws[1].ClipHeight == 56.25,
                "resize, configured offsets and GUI scale move and scale fill together");
            Check(labels.Count == 1 && labels[0].Width == 113 && labels[0].Height == 41
                && labels[0].X + labels[0].Width / 2 == draws[0].X + 56.25
                && labels[0].Y + labels[0].Height / 2 == draws[0].Y + 56.25,
                "percentage scales with GUI and remains centered after configured offset and resolution changes");
            texture.TextureId = 43; Draw();
            Check(draws.All(d => d.Texture == 43), "gait texture changes immediately update both empty and filled layers");
            Configure("EnableStamina", false); Draw();
            Check(draws.Count == 1 && draws[0].Color == null && labels.Count == 0, "disabled mount stamina preserves the raised plain gait icon without a percentage");
            Configure("EnableStamina", true);
            horse.WatchedAttributes.RemoveAttribute("jaunt:stamina"); Draw();
            Check(draws.Count == 1 && draws[0].Color == null && labels.Count == 0, "mount without stamina retains its gait icon without a percentage");
            horse.WatchedAttributes.SetAttribute("jaunt:stamina", stamina);
            Configure("ShowGaitIcon", false); Draw();
            Check(draws.Count == 0, "hidden gait icon preference respected");
            Configure("ShowGaitIcon", true);
            AccessTools.Property(typeof(EntityAgent), "MountedOn").SetValue(rider, null); Draw();
            Check(draws.Count == 0, "dismount removes icon immediately without waiting for Jaunt texture tick");
            AccessTools.Property(typeof(EntityAgent), "MountedOn").SetValue(rider, seat);
            failFill = true;
            try { Draw(); throw new Exception("Expected simulated render failure"); }
            catch (TargetInvocationException ex) when (ex.InnerException?.Message == "test render failure") { }
            finally { failFill = false; }
            Check(clips.Count == 0, "clipping stack restored even if fill rendering fails");
            Check(stamina.GetFloat("currentstamina") == 50 && stamina.GetFloat("maxstamina") == 100,
                "rendering never modifies mount stamina attributes");
            var textures = new FastSmallDictionary<string, LoadedTexture>(2);
            var paddedTexture = new LoadedTexture(api) { TextureId = 44, Width = 75, Height = 75 };
            textures.Add("jaunt:textures/hud/idle.svg", paddedTexture);
            AccessTools.Field(iconType, "texturesDict").SetValue(icon, textures);
            AccessTools.Field(iconType, "activeTexture").SetValue(icon, paddedTexture);
            RuntimeEnv.GUIScale = 1;
            Stamina(20, 100); Draw();
            Check(Math.Abs(draws[1].ClipHeight!.Value - 7) < 0.0001 && Math.Abs(draws[1].ClipTop!.Value - draws[0].Y - 43) < 0.0001 && rasterizations == 1,
                "low stamina fills visible silhouette height, excluding transparent top and bottom margins");
            Check(labels.Count == 1 && labels[0].Y + labels[0].Height / 2 == draws[0].Y + 32.5,
                "percentage remains at the visible horse center with transparent margins");
            Stamina(100, 100); Draw();
            Check(Math.Abs(draws[1].ClipHeight!.Value - 35) < 0.0001 && Math.Abs(draws[1].ClipTop!.Value - draws[0].Y - 15) < 0.0001 && rasterizations == 1,
                "full silhouette fills exactly and cached margins avoid repeated SVG rasterization");
            var brokenTexture = new LoadedTexture(api) { TextureId = 45, Width = 75, Height = 75 };
            textures.Add("jaunt:textures/hud/broken.svg", brokenTexture);
            AccessTools.Field(iconType, "activeTexture").SetValue(icon, brokenTexture);
            failSvg = true; Draw(); Draw();
            Check(draws[1].ClipHeight == 75 && rasterizations == 2,
                "SVG inspection failure falls back to full image and is not retried every frame");
            replacement = (IDisposable?)create.Invoke(null, [api]);
            fix!.Dispose(); fix = null;
            Check(replacement != null && !hud.ShouldReceiveMouseEvents(), "disposing an old client owner preserves the replacement patch");
            Check(deletedLabels == 1, "disposing the client owner releases its percentage GPU texture");
            replacement!.Dispose(); replacement = null;
            Check(hud.ShouldReceiveMouseEvents() && !(Harmony.GetPatchInfo(AccessTools.Method(hudType,
                nameof(GuiDialog.ShouldReceiveMouseEvents)))?.Owners.Contains(owner) ?? false)
                && !(Harmony.GetPatchInfo(renderIcon)?.Owners.Contains(owner) ?? false)
                && !(Harmony.GetPatchInfo(AccessTools.Method(hudType, "OnRenderGUI"))?.Owners.Contains(owner) ?? false),
                "disposal restores original mouse gate and both original render methods");
            Route("OnMouseDown");
            Check(receiver.Downs == 1, "unpatching reproduces the original input blocking");
            Console.WriteLine($"{checks} Jaunt HUD checks passed.");
        }
        finally { fix?.Dispose(); replacement?.Dispose(); singleton.SetValue(null, oldSystem); RuntimeEnv.GUIScale = oldScale; }
    }

    private sealed class SlotReceiver(ICoreClientAPI api, Action hover) : GuiDialog(api)
    {
        public int Moves, Downs, Ups;
        public override string ToggleKeyCombinationCode => null!;
        public override bool ShouldReceiveMouseEvents() => true;
        public override void OnMouseMove(MouseEvent args) { Moves++; hover(); args.Handled = true; }
        public override void OnMouseDown(MouseEvent args) { Downs++; args.Handled = true; }
        public override void OnMouseUp(MouseEvent args) { Ups++; args.Handled = true; }
    }

    private sealed class OtherDialog(ICoreClientAPI api) : GuiDialog(api)
    {
        public override string ToggleKeyCombinationCode => null!;
    }
}
