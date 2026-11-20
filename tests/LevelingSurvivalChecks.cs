using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.InteropServices;
using System.Text.Json;
using HarmonyLib;
using ProtoBuf;
using SeraphCivilizationPatches;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.Server;

static class LevelingSurvivalChecks
{
    private sealed class Player : ServerPlayer, IServerPlayer
    {
        private Player() : base(null!, null!) { }
        public new EntityPlayer Entity { get; set; } = null!;
        public new string PlayerUID => "skill-test";
        public new string LanguageCode => "en";
        internal static Player Create(EntityPlayer entity)
        {
            var p = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player)); p.Entity = entity; return p;
        }
    }
    private static object? action;
    private static Type hostMetric = null!;
    private static readonly Dictionary<string, string> drawnText = [];
    private static bool SkipGraphics() => false;
    private static bool FixedRow(ref double __result) { __result = 30; return false; }
    private static bool CaptureText(string __1, string __2) { drawnText[__1] = __2; return false; }
    private static bool ComposeWithoutGpu(GuiComposer __instance, ref GuiComposer __result) { __instance.Bounds.CalcWorldBounds(); __result = __instance; return false; }
    private static bool HostActions(ref object __result) { __result = action!; return false; }
    private static bool HostMetrics(ref object __result)
    {
        var m = Activator.CreateInstance(hostMetric)!;
        hostMetric.GetProperty("Key")!.SetValue(m, "consciousness");
        hostMetric.GetProperty("Value")!.SetValue(m, 72d);
        var array = Array.CreateInstance(hostMetric, 1); array.SetValue(m, 0); __result = array; return false;
    }

    public static void Run(string root, string survivalDirectory)
    {
        if (OperatingSystem.IsWindows()) SetDllDirectory("E:/vintagestory/Vintagestory/Lib");
        var deps = Path.Combine(root, "tests/dependencies/leveling");
        survivalDirectory = Path.GetFullPath(survivalDirectory.Length == 0 ? Path.Combine(deps, "survival") : survivalDirectory);
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            var path = Path.Combine(survivalDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        var slss = Assembly.LoadFrom(Path.Combine(survivalDirectory, "vsslssmod.dll"));
        var leveling = Assembly.LoadFrom(Path.Combine(deps, "leveling/SeraphLeveling.dll")).GetType("SeraphLeveling.SeraphLevelingModSystem", true)!;
        var assembly = typeof(SeraphCivilizationPatchesSystem).Assembly;
        var integration = assembly.GetType("SeraphCivilizationPatches.LevelingSurvivalCompatibility", true)!;
        var effects = assembly.GetType("SeraphCivilizationPatches.LevelingEffects", true)!;
        var ui = assembly.GetType("SeraphCivilizationPatches.LevelingSkillPanel", true)!;
        var coordinator = slss.GetType("Seraph.Server.ServerCoordinator", true)!;
        var panel = slss.GetType("Seraph.Client.CharacterPanelIntegration", true)!;
        var metricType = slss.GetType("Seraph.Network.PanelMetric", true)!;
        hostMetric = metricType;
        var functionType = slss.GetType("Seraph.Network.FunctionSnapshot", true)!;
        int checks = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
        void Near(double actual, double expected, string label) => Check(Math.Abs(actual - expected) < .00001, label + $" ({actual:0.####})");
        object Get(object o, string key) => o.GetType().GetProperty(key)!.GetValue(o)!;
        void Set(object o, string key, object value) => o.GetType().GetProperty(key)!.SetValue(o, value);
        object? Call(Type t, string method, params object?[] args) => AccessTools.Method(t, method).Invoke(null, args);
        foreach (string language in new[] { "en", "zh-cn" })
        {
            var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "assets/vsscpmod/lang/" + language + ".json")))!;
            Lang.AvailableLanguages[language] = Proxy.Create<ITranslationService>((m, a) => {
                if (m.Name == "HasTranslation") return true;
                if (m.Name != "Get") return null;
                string key = ((string)a![0]!).Replace("vsscpmod:", "");
                var text = translations.GetValueOrDefault(key, key);
                return a.Length > 1 && a[1] is object[] values && values.Length > 0 ? string.Format(text, values) : text;
            });
            foreach (var definition in (IEnumerable)AccessTools.Field(effects, "Definitions").GetValue(null)!)
            {
                string stat = (string)Get(definition, "Stat");
                Check(translations.ContainsKey("skill-label-" + stat) && translations.ContainsKey("skill-scope-" + stat), language + " label and scope: " + stat);
            }
        }
        Lang.ChangeLanguage("en");
        var messages = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) => { messages.Add(m.Name + " " + (a is [string f, object[] values] ? string.Format(f, values) : string.Join(" ", a ?? []))); return null; });
        var enabled = new HashSet<string>();
        var loader = Proxy.Create<IModLoader>((m, a) => m.Name == "IsModEnabled" ? enabled.Contains((string)a![0]!) : null);
        var render = Proxy.Create<IRenderAPI>((m, _) => m.Name switch { "get_FrameHeight" => 1080, "get_FrameWidth" => 1920, _ => null });
        ICoreClientAPI api = null!;
        GuiComposer CreateComposer(ElementBounds bounds, string key)
        {
            bounds.ParentBounds = ElementBounds.Empty;
            return (GuiComposer)Activator.CreateInstance(typeof(GuiComposer), BindingFlags.NonPublic | BindingFlags.Instance, null, [api, bounds, key], null)!;
        }
        var gui = Proxy.Create<IGuiAPI>((m, a) => m.Name switch { "CreateCompo" => CreateComposer((ElementBounds)a![1]!, (string)a[0]!), "get_WindowBounds" => ElementBounds.Empty, _ => null });
        api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch { "get_ModLoader" => loader, "get_Logger" => logger, "get_Side" => EnumAppSide.Client, "get_Render" => render, "get_Gui" => gui, _ => null });
        AccessTools.Property(leveling, "ServerApi").SetValue(null, Proxy.Create<ICoreServerAPI>((m, _) => m.Name switch {
            "get_ModLoader" => loader, "get_Logger" => logger, "get_Side" => EnumAppSide.Server, _ => null }));
        foreach (var mods in new[] { Array.Empty<string>(), new[] { "seraphleveling" }, new[] { "vsslssmod" } })
        {
            enabled.Clear(); enabled.UnionWith(mods);
            Check(Call(integration, "TryCreate", api) == null && Call(ui, "TryCreate", api) == null, "optional without both mods: " + string.Join(',', mods));
        }
        enabled.UnionWith(["seraphleveling", "vsslssmod"]);
        var host = new Harmony("vsscpmod.tests.leveling-host");
        var functionMetrics = AccessTools.Method(coordinator, "FunctionMetrics");
        var actionFunctions = AccessTools.Method(coordinator, "ActionFunctions");
        host.Patch(functionMetrics, prefix: new HarmonyMethod(typeof(LevelingSurvivalChecks), nameof(HostMetrics)));
        host.Patch(actionFunctions, prefix: new HarmonyMethod(typeof(LevelingSurvivalChecks), nameof(HostActions)));
        var originalHeight = AccessTools.Method(panel, "FunctionPanelHeight");
        double oldHeight = (double)originalHeight.Invoke(null, [400d, 30d])!;
        IDisposable? fix = null, second = null, display = null;
        try
        {
            fix = (IDisposable?)Call(integration, "TryCreate", api);
            Check(fix != null, "actual SLSS and Leveling contracts bind: " + string.Join("; ", messages));
            display = (IDisposable?)Call(ui, "TryCreate", api);
            Check(display != null, "actual client composition/refresh/hover contracts bind: " + string.Join("; ", messages));
            Near((double)originalHeight.Invoke(null, [400d, 30d])!, oldHeight, "body-functions window retains its original height");
            var entity = new EntityPlayer { Api = api };
            var p = Player.Create(entity);
            entity.WatchedAttributes.SetString("playerUID", p.PlayerUID);
            action = Activator.CreateInstance(functionType)!;
            Set(action, "Manipulation", true); Set(action, "Mining", .5d);
            var session = RuntimeHelpers.GetUninitializedObject(functionMetrics.GetParameters()[0].ParameterType);
            AccessTools.Field(session.GetType(), "<Player>k__BackingField").SetValue(session, p);
            var server = RuntimeHelpers.GetUninitializedObject(coordinator);
            object[] Snapshot() => ((IEnumerable)functionMetrics.Invoke(server, [session])!).Cast<object>().ToArray();
            object Row(string stat) => Snapshot().Single(m => (string)Get(m, "Key") == "vsscpmod.skill." + stat);
            double Value(string stat) => (double)Get(Row(stat), "Value");
            var traitCache = AccessTools.Field(leveling, "VanillaTraitsCache").GetValue(null)!;
            var traits = Activator.CreateInstance(traitCache.GetType().GetGenericArguments()[1])!;
            traitCache.GetType().GetProperty("Item")!.SetValue(traitCache, traits, [p.PlayerUID]);
            void Trait(string name, bool value) => Set(traits, name, value);
            var levelMod = Activator.CreateInstance(leveling)!;
            Trait("HasWeak", true);
            entity.Stats.Set("miningSpeedMul", "trait", -.1f);
            AccessTools.Method(leveling, "ApplyMiningBonus").Invoke(levelMod, [p, 5]);
            Near(Value("miningSpeedMul"), .45, "real Leveling partial weak penalty retains native loss × body mining");
            AccessTools.Method(leveling, "ApplyMiningBonus").Invoke(levelMod, [p, 10]);
            Near(Value("miningSpeedMul"), .5, "real Leveling cancellation milestone restores baseline before body factor");
            AccessTools.Method(leveling, "ApplyMiningBonus").Invoke(levelMod, [p, 30]);
            Near(Value("miningSpeedMul"), .6, "real Leveling post-cancellation reward applies once");
            Check(((string)Get(Row("miningSpeedMul"), "Detail")).Contains("Skill penalty cancellation"), "tooltip describes cancellation without exposing source identifiers");
            entity.Stats.Set("meleeWeaponsDamage", "trait", -.15f).Set("meleeWeaponsDamage", "sitMeleeBonus", .2f)
                .Set("meleeWeaponsDamage", "vsslssmod:function", -.4f).Set("meleeWeaponsDamage", "wearablemod", .05f);
            Near(Value("meleeWeaponsDamage"), .7, "melee body/class/equipment use native additive blending");
            entity.Stats.Set("walkspeed", "sitWalkingBonus", .15f).Set("walkspeed", "vsslssmod:function", -.3f);
            Near(Value("walkspeed"), .85, "movement is not multiplied by body twice");
            Trait("HasRavenous", true); entity.Stats.Set("hungerrate", "trait", .3f);
            Call(leveling, "ApplyHungerBonusStatic", p, 15);
            Near(Value("hungerrate"), 1.15, "real hunger reward retains ravenous penalty");
            var native = slss.GetType("Seraph.Server.NativeStatInputs", true)!;
            Near((double)Call(native, "PhysiologyMultiplier", entity, "hungerrate")!, Value("hungerrate"), "SLSS energy input equals display with no extra application");
            entity.Stats.Set("healingeffectivness", "sitArmorHealing", .2f);
            Near(Value("healingeffectivness"), (double)Call(native, "PhysiologyMultiplier", entity, "healingeffectivness")!, "healing matches actual SLSS boundary");
            entity.Stats.Set("maxhealthExtraPoints", "sitHardyHealthBonus", 5);
            Near(Value("maxhealthExtraPoints"), entity.Stats.GetBlended("maxhealthExtraPoints") - 1, "extra health is points, not a multiplier");
            Call(leveling, "ApplyResourcefulBonusStatic", p, 25);
            Near(Value("animalHarvestingTime"), entity.Stats.GetBlended("animalHarvestingTime"), "real Resourceful reward remains time reduction");
            entity.Stats.Set("wholeVesselLootChance", "sitPilfererWholeVessel", .1f);
            Near(Value("wholeVesselLootChance"), .1, "whole-vessel probability removes implicit native base");
            entity.Stats.Set("temporalGearTLRepairCost", "sitTechnicalBonus", -2);
            Near(Value("temporalGearTLRepairCost"), -2, "gear cost keeps negative point offset");
            var bow = leveling.Assembly.GetType("SeraphLeveling.BowDrawSpeedPatches")!;
            var melee = leveling.Assembly.GetType("SeraphLeveling.MeleeAttackSpeedPatches")!;
            Set(action, "Bow", .5d); Set(action, "MeleeAttackSpeed", 1.1d);
            AccessTools.Field(leveling, "RangedDrawSpeedEnabled").SetValue(null, false);
            AccessTools.Field(leveling, "MeleeAttackSpeedEnabled").SetValue(null, false);
            Near(Value("bowTiming"), .5, "disabled optional bow bonus leaves only body timing");
            Near(Value("meleeTiming"), 1.1, "disabled optional melee bonus leaves body timing");
            foreach (var prefix in new[] { "RangedDrawSpeed", "MeleeAttackSpeed" })
            {
                AccessTools.Field(leveling, prefix + "Enabled").SetValue(null, true);
                AccessTools.Field(leveling, prefix + "AtLevel").SetValue(null, 50);
                AccessTools.Field(leveling, prefix + "MaxReductionPercent").SetValue(null, 20);
            }
            AccessTools.Field(bow, "PatchedInProcess").SetValue(null, true);
            AccessTools.Field(melee, "patchedInProcess").SetValue(null, true);
            entity.WatchedAttributes.SetInt("sitRangedLevel", 50); entity.WatchedAttributes.SetInt("sitMeleeLevel", 50);
            Near(Value("bowTiming"), .625, "real optional bow helper composes time reduction and body factor");
            Near(Value("meleeTiming"), 1.375, "real optional melee helper composes with adrenaline timing");
            AccessTools.Field(bow, "PatchedInProcess").SetValue(null, false);
            Check(!(bool)Get(Row("bowTiming"), "Available"), "enabled but unpatched optional route is unavailable, not fake success");
            Set(action, "CanUseActiveHand", false); Set(action, "CanDrawBow", false);
            Near(Value("miningSpeedMul"), 0, "disabled hand blocks mining despite skill rewards");
            Check((string)Get(Row("animalHarvestingTime"), "Text") == "Blocked", "blocked harvesting is not reported as instant zero-duration work");
            Set(action, "Manipulation", false);
            Near(Value("miningSpeedMul"), 1.2, "inactive SLSS action ownership does not apply body impairment");
            byte[] before = entity.WatchedAttributes.ToBytes();
            var snapshot = Snapshot();
            for (int i = 0; i < 5; i++) Snapshot();
            Check(before.SequenceEqual(entity.WatchedAttributes.ToBytes()), "repeated metrics do not mutate stats, traits, progress or hunger");
            Check(snapshot.Length == 26 && (double)Get(snapshot[0], "Value") == 72, "original physiological metric survives unchanged with 25 appended effects");
            entity.Stats.Set("meleeWeaponsDamage", "thirdparty:DEBUG_source", .03f);
            var chinese = ((IEnumerable)Call(effects, "Read", entity, action, "zh-cn")!).Cast<object>().ToArray();
            foreach (var result in chinese)
                Check(!System.Text.RegularExpressions.Regex.IsMatch((string)Get(result, "Text") + (string)Get(result, "Detail"), "[A-Za-z]"),
                    "Chinese value and tooltip contain no English or source code: " + Get(result, "Key"));
            Check(((string)Get(chinese.Single(m => (string)Get(m, "Key") == "vsscpmod.skill.meleeWeaponsDamage"), "Detail")).Contains("其他影响"), "unknown third-party source uses a localized fallback");
            var array = Array.CreateInstance(metricType, snapshot.Length);
            for (int i = 0; i < snapshot.Length; i++) array.SetValue(snapshot[i], i);
            using var stream = new MemoryStream(); Serializer.NonGeneric.Serialize(stream, array); stream.Position = 0;
            var roundTrip = (Array)Serializer.NonGeneric.Deserialize(array.GetType(), stream);
            Check(roundTrip.Length == array.Length && (string)Get(roundTrip.GetValue(1)!, "Key") == "vsscpmod.skill.miningSpeedMul", "actual PanelMetric protobuf contract round-trips appended data");
            // Exercise real clipped GUI elements and SLSS freshness filtering without an OpenGL host.
            host.Patch(AccessTools.Method(panel, "MetricRowHeight"), prefix: new HarmonyMethod(typeof(LevelingSurvivalChecks), nameof(FixedRow)));
            foreach (var method in new[] { "ComposeMetrics", "RefreshMetrics" })
                host.Patch(AccessTools.Method(panel, method), prefix: new HarmonyMethod(typeof(LevelingSurvivalChecks), nameof(SkipGraphics)));
            foreach (var method in new[] { "SetFittedValue", "SetPanelHoverText" })
                host.Patch(AccessTools.Method(panel, method), prefix: new HarmonyMethod(typeof(LevelingSurvivalChecks), nameof(CaptureText)));
            host.Patch(AccessTools.Method(typeof(GuiElementScrollbar), "RecomposeHandle"), prefix: new HarmonyMethod(typeof(LevelingSurvivalChecks), nameof(SkipGraphics)));
            var view = RuntimeHelpers.GetUninitializedObject(panel);
            AccessTools.Field(panel, "valueFontSizes").SetValue(view, new Dictionary<string, float>());
            var bridgeType = slss.GetType("Seraph.Client.SeraphClientBridge", true)!;
            var bridge = RuntimeHelpers.GetUninitializedObject(bridgeType);
            AccessTools.Field(panel, "bridge").SetValue(view, bridge);
            AccessTools.Field(bridgeType, "channel").SetValue(bridge, Proxy.Create<IClientNetworkChannel>((m, _) => m.Name == "get_Connected" ? true : null));
            var packet = Activator.CreateInstance(slss.GetType("Seraph.Network.ServerSnapshot", true)!)!;
            Set(packet, "FunctionMetrics", array); Set(bridge, "Snapshot", packet); Set(bridge, "LastReceivedUtc", DateTime.UtcNow);
            var functionsPanel = Enum.Parse(AccessTools.Method(panel, "Metrics").GetParameters()[0].ParameterType, "Functions");
            foreach (double width in new[] { 220d, 400d })
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                RuntimeEnv.GUIScale = scale;
                var composer = (GuiComposer)Activator.CreateInstance(typeof(GuiComposer), BindingFlags.NonPublic | BindingFlags.Instance, null,
                    [api, ElementBounds.Fixed(0, 0, width, 800), "skill-test"], null)!;
                Call(ui, "ComposeRows", view, composer, width, 214d);
                var first = composer.GetDynamicText("vsscpmod.skill.miningSpeedMul");
                var last = composer.GetDynamicText("vsscpmod.skill.unlocks");
                Check(first != null && last != null && first.Bounds.ParentBounds == last.Bounds.ParentBounds,
                    $"all skill rows share real scrolling content at width {width}, scale {scale}");
                var hoverElement = composer.GetElement("vsscpmod.skill.miningSpeedMul-help");
                Check(hoverElement.GetType().Name == "CharacterPanelHoverText" && hoverElement.Bounds.ParentBounds == first!.Bounds.ParentBounds,
                    "tooltip moves with the clipped row");
                var label = composer.GetDynamicText("vsscpmod.skill.miningSpeedMul-label");
                Check(label.Bounds.fixedX + label.Bounds.fixedWidth < first!.Bounds.fixedX, "label and value occupy separate non-overlapping columns");
                Check(composer.GetDynamicText("vsscpmod-skill-group-combat") is not null
                    && composer.GetDynamicText("vsscpmod-skill-group-craft") is not null, "group headers are inside the scrolling list");
                Call(ui, "RefreshRows", view, composer);
                Check(drawnText["vsscpmod.skill.miningSpeedMul"].Contains("×1.2"), "UI uses server metric values");
                var scroll = composer.GetScrollbar("vsscpmod-skill-scroll");
                scroll.CurrentYPosition = 60 * scale; scroll.TriggerChanged();
                Near(first!.Bounds.ParentBounds.fixedY, -60, "scroll offset converts device pixels to layout units");
                scroll.CurrentYPosition = 100000; scroll.TriggerChanged();
                Near(first.Bounds.ParentBounds.fixedY, -690, "scroll clamps to last visible rows");
                Set(bridge, "LastReceivedUtc", DateTime.UtcNow.AddSeconds(-10));
                Call(ui, "RefreshRows", view, composer);
                Check(drawnText["vsscpmod.skill.miningSpeedMul"].Contains("Unavailable"), "stale snapshot clears previously visible skill values");
                Set(bridge, "LastReceivedUtc", DateTime.UtcNow);
            }
            RuntimeEnv.GUIScale = 1;
            // The real SLSS entry hooks own a distinct composer, anchored under the body window.
            foreach (var method in new[] { "ComposeExtraPanels", "RefreshPanels", "OnCharacterClosed", "RemoveOwnedPanels", "DetachDialog" })
                host.Patch(AccessTools.Method(panel, method), prefix: new HarmonyMethod(typeof(LevelingSurvivalChecks), nameof(SkipGraphics)));
            host.Patch(AccessTools.Method(typeof(GuiComposer), "Compose"), prefix: new HarmonyMethod(typeof(LevelingSurvivalChecks), nameof(ComposeWithoutGpu)));
            var dialog = (GuiDialog)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer).Assembly.GetType("Vintagestory.Client.NoObf.GuiDialogCharacter", true)!);
            dialog.Composers = new GuiDialog.DlgComposers(dialog);
            AccessTools.Field(panel, "dialog").SetValue(view, dialog);
            AccessTools.Field(panel, "api").SetValue(view, api);
            var body = CreateComposer(ElementBounds.Fixed(300, 30, 320, 440), "body");
            dialog.Composers["vsslssmod-character-body"] = body;
            void ComposeWindow() => AccessTools.Method(panel, "ComposeExtraPanels").Invoke(view, null);
            ComposeWindow();
            var window = dialog.Composers["vsscpmod-character-skills"];
            Check(window is not null, "opening the character screen creates a separate skill window: " + string.Join("; ", messages));
            Near(window!.Bounds.fixedX, 300, "skill window aligns with body column");
            Near(window.Bounds.fixedY, 480, "skill window starts ten units below body bottom");
            Check(body.GetDynamicText("vsscpmod.skill.miningSpeedMul") is null, "skill rows are not inserted into the body composer");
            ComposeWindow();
            Check(ReferenceEquals(window, dialog.Composers["vsscpmod-character-skills"]), "repeated composition reuses one skill window");
            AccessTools.Method(panel, "OnCharacterClosed").Invoke(view, null);
            Check(dialog.Composers["vsscpmod-character-skills"] is null, "closing detaches the independent window before vanilla cleanup");
            ComposeWindow();
            Check(ReferenceEquals(window, dialog.Composers["vsscpmod-character-skills"]), "reopening reattaches the retained window");
            AccessTools.Method(panel, "RemoveOwnedPanels").Invoke(view, null);
            Check(dialog.Composers["vsscpmod-character-skills"] is null, "upstream layout rebuild removes the owned skill composer");
            ComposeWindow();
            Check(!ReferenceEquals(window, dialog.Composers["vsscpmod-character-skills"]), "layout rebuild creates a fresh composer");
            second = (IDisposable?)Call(integration, "TryCreate", api);
            fix!.Dispose(); fix = null;
            Check(Snapshot().Length == 26, "one owner disposing keeps the shared patch active without duplicates");
            second!.Dispose(); second = null;
            Check(Snapshot().Length == 1, "last owner restores the upstream snapshot method");
            display!.Dispose(); display = null;
            Check(dialog.Composers["vsscpmod-character-skills"] is null, "unloading releases the independent window");
            Near((double)originalHeight.Invoke(null, [400d, 30d])!, oldHeight, "UI unload restores original height");
            Check(!messages.Any(m => m.StartsWith("Warning")), "no compatibility fallback warnings during supported paths");
        }
        finally { display?.Dispose(); fix?.Dispose(); second?.Dispose(); host.UnpatchAll(host.Id); action = null; }
        Console.WriteLine($"Leveling/SLSS checks passed: {checks}; SLSS {slss.Location}");
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string path);
}
