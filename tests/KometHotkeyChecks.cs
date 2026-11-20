using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using HotkeyDictionary = Vintagestory.API.Datastructures.OrderedDictionary<string, Vintagestory.API.Client.HotKey>;

static class KometHotkeyChecks
{
    public static void Run(string root, string game, string dll)
    {
        string oldPath = GamePaths.DataPath;
        GamePaths.DataPath = Path.Combine(root, "artifacts/komet-hotkey-checks");
        Directory.CreateDirectory(GamePaths.DataPath);
        var library = Assembly.LoadFrom(Path.Combine(game, "VintagestoryLib.dll"));
        var komet = Assembly.LoadFrom(Path.GetFullPath(dll));
        Type screen = komet.GetType("Komet.Options.OptionsScreen", true)!;
        Type engine = komet.GetType("Komet.Options.EngineOptions", true)!;
        Type rowType = komet.GetType("Komet.Api.OptionRow", true)!;
        Type compatibility = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly.GetType("SeraphCivilizationPatches.KometHotkeyCompatibility", true)!;
        MethodInfo create = compatibility.GetMethod("TryCreate")!;
        MethodInfo onKey = AccessTools.Method(screen, "OnKeyDown");
        MethodInfo capture = AccessTools.Method(screen, "CaptureAllInputs");
        MethodInfo clash = AccessTools.Method(engine, "Clash");
        MethodInfo prefix = AccessTools.Method(compatibility, "OnKeyDown");
        int count = 0, mouseX = 155, mouseY = 255;
        bool hasKomet = false, hasUnbind = false;
        var messages = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) => { messages.Add(m.Name + " " + string.Join(" ", a ?? [])); return null; });
        var loader = Proxy.Create<IModLoader>((m, a) => m.Name == "IsModEnabled" && ((string)a![0]! == "komet" ? hasKomet : hasUnbind));
        var input = Proxy.Create<IInputAPI>((m, _) => m.Name switch { "get_MouseX" => mouseX, "get_MouseY" => mouseY, _ => null });
        var api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch { "get_ModLoader" => loader, "get_Logger" => logger, "get_Input" => input, _ => null });
        void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); count++; Console.WriteLine("PASS: " + label); }
        IDisposable? patch = null;
        const string owner = "vsscpmod.komet-hotkeys";
        try
        {
            Check(create.Invoke(null, [api]) == null, "no dependencies skips compatibility");
            hasKomet = true;
            Check(create.Invoke(null, [api]) == null, "Komet alone skips compatibility");
            hasKomet = false; hasUnbind = true;
            Check(create.Invoke(null, [api]) == null, "Unbind alone skips compatibility");
            hasKomet = true;
            patch = (IDisposable?)create.Invoke(null, [api]);
            Check(patch != null && !messages.Any(m => m.Contains("unavailable")), "patch installs against real Komet 2.0.0");
            Check(Harmony.GetPatchInfo(onKey)?.Owners.Contains(owner) == true, "Komet keyboard entry point is patched");
            Check(Harmony.GetPatchInfo(capture)?.Owners.Contains(owner) == true, "Komet keyboard capture is patched");

            Type manager = library.GetType("Vintagestory.Client.HotkeyManager", true)!;
            object managerObject = Activator.CreateInstance(manager)!;
            AccessTools.Field(library.GetType("Vintagestory.Client.ScreenManager", true)!, "hotkeyManager").SetValue(null, managerObject);
            var keys = (HotkeyDictionary)AccessTools.Field(manager, "HotKeys").GetValue(managerObject)!;
            HotKey Add(string code, GlKeys key) => keys[code] = new HotKey { Code = code, Name = code,
                CurrentMapping = new KeyCombination { KeyCode = (int)key, Ctrl = true, SecondKeyCode = (int)GlKeys.B },
                DefaultMapping = new KeyCombination { KeyCode = (int)key } };
            Add("test", GlKeys.A); Add("other", GlKeys.C); Add("sneak", GlKeys.LShift); Add("shift", GlKeys.LShift);
            Type settings = library.GetType("Vintagestory.Client.NoObf.ClientSettings", true)!;
            AccessTools.Property(settings, "SeparateCtrl").SetValue(null, false);

            object dialog = RuntimeHelpers.GetUninitializedObject(screen);
            void Set(string field, object? value) => AccessTools.Field(screen, field).SetValue(dialog, value);
            Set("opened", true); Set("_query", "");
            FieldInfo canvasField = AccessTools.Field(screen, "_canvas");
            object canvas = RuntimeHelpers.GetUninitializedObject(canvasField.FieldType);
            AccessTools.Property(canvasField.FieldType, "X").SetValue(canvas, 100d);
            AccessTools.Property(canvasField.FieldType, "Y").SetValue(canvas, 200d);
            Set("_canvas", canvas);
            object row = Activator.CreateInstance(rowType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                [Enum.Parse(komet.GetType("Komet.Api.OptionKind", true)!, "Key"), "test", null], null)!;
            AccessTools.Property(rowType, "Code").SetValue(row, "test");
            Type hitType = screen.GetNestedType("Hit", BindingFlags.NonPublic)!;
            object hit = Activator.CreateInstance(hitType, [50d, 50d, 100d, 40d, (Action<double, bool>)((_, _) => { }), false, row, false, 0d, 0])!;
            object hoverHit = Activator.CreateInstance(hitType, [50d, 50d, 100d, 40d, (Action<double, bool>)((_, _) => { }), false, row, true, 0d, 0])!;
            IList hits = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(hitType))!;
            IList rows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(hitType))!;
            rows.Add(hit); rows.Add(hoverHit); Set("_hits", hits); Set("_rows", rows); Set("_view", (0d, 0d, 300d, 300d));
            // Use the real game dispatcher with an ESC-style menu behind Komet. That
            // menu captures every key before the dispatcher's focused-dialog pass.
            Type client = library.GetType("Vintagestory.Client.NoObf.ClientMain", true)!;
            Type guiManager = library.GetType("Vintagestory.Client.NoObf.GuiManager", true)!;
            object clientObject = RuntimeHelpers.GetUninitializedObject(client);
            object dispatcher = RuntimeHelpers.GetUninitializedObject(guiManager);
            var menu = new CapturingMenu();
            AccessTools.Field(client, "OpenedGuis").SetValue(clientObject, new List<GuiDialog> { (GuiDialog)dialog, menu });
            AccessTools.Field(guiManager, "game").SetValue(dispatcher, clientObject);
            MethodInfo dispatch = AccessTools.Method(guiManager, "OnKeyDown");
            KeyEvent Press(GlKeys key) { var e = new KeyEvent { KeyCode = (int)key }; dispatch.Invoke(dispatcher, [e]); return e; }
            var original = keys["test"];
            Set("focused", true);
            Check(Press(GlKeys.Delete).Handled && keys["test"].CurrentMapping.KeyCode == 0 && menu.KeyDownCount == 0,
                "Delete reaches hovered Komet row before capturing ESC menu through real GuiManager");
            Set("focused", false);
            Check(original.CurrentMapping.KeyCode == (int)GlKeys.A && keys["test"].DefaultMapping.KeyCode == (int)GlKeys.A,
                "cloning preserves original object and default binding");
            Check(!keys["test"].CurrentMapping.Ctrl && keys["test"].CurrentMapping.SecondKeyCode == null,
                "clearing removes modifiers and secondary key");
            Check((bool)AccessTools.Field(screen, "_dirty").GetValue(dialog)!, "unbind requests Komet redraw");
            var stored = (IDictionary)AccessTools.Property(settings, "KeyMapping").GetValue(null)!;
            Check(((KeyCombination)stored["test"]!).KeyCode == 0, "real client settings store cleared mapping");
            AccessTools.Property(rowType, "Code").SetValue(row, "other");
            Check(Press(GlKeys.D).Handled && keys["other"].CurrentMapping.KeyCode == 0, "D matches Unbind Hot Keys 1.0.1 secondary shortcut");
            Check((string)clash.Invoke(null, ["test"])! == "" && (string)clash.Invoke(null, ["other"])! == "", "cleared keys do not report conflicts");
            Check((string)AccessTools.Method(engine, "Mapping").Invoke(null, ["test"])! == "", "cleared mapping displays an empty binding");
            AccessTools.Method(engine, "Unbind").Invoke(null, ["other"]);
            Check(keys["other"].CurrentMapping.KeyCode == (int)GlKeys.C, "Komet right-click reset restores the default key");
            AccessTools.Method(engine, "Bind").Invoke(null, ["test", new KeyCombination { KeyCode = (int)GlKeys.B }]);
            Check(keys["test"].CurrentMapping.KeyCode == (int)GlKeys.B, "cleared key can be assigned again");
            AccessTools.Property(rowType, "Code").SetValue(row, "test");
            AccessTools.Field(client, "OpenedGuis").SetValue(clientObject, new List<GuiDialog> { (GuiDialog)dialog });
            Check(Press(GlKeys.Delete).Handled && keys["test"].CurrentMapping.KeyCode == 0,
                "standalone unfocused Komet receives hover Delete without ESC menu");
            AccessTools.Field(client, "OpenedGuis").SetValue(clientObject, new List<GuiDialog> { (GuiDialog)dialog, menu });
            AccessTools.Property(rowType, "Code").SetValue(row, "sneak");
            Check(Press(GlKeys.Delete).Handled && keys["shift"].CurrentMapping.KeyCode == 0, "linked sneak/shift follows Komet synchronization");
            Add("sneak", GlKeys.LShift);
            mouseX = 500;
            Check(!(bool)capture.Invoke(dialog, null)!, "pointer outside rows does not add keyboard capture");
            int received = menu.KeyDownCount;
            Press(GlKeys.Delete);
            Check(menu.KeyDownCount == received + 1 && keys["sneak"].CurrentMapping.KeyCode != 0, "pointer outside rows leaves key for ESC menu without clearing stale hover");
            mouseX = 155; Set("_view", (0d, 0d, 20d, 20d));
            received = menu.KeyDownCount; Press(GlKeys.Delete);
            Check(menu.KeyDownCount == received + 1, "clipped rows cannot be cleared");
            Set("_view", (0d, 0d, 300d, 300d)); Set("_searching", true);
            Check((bool)capture.Invoke(dialog, null)!, "search retains Komet keyboard capture");
            Check((bool)prefix.Invoke(null, [dialog, new KeyEvent { KeyCode = (int)GlKeys.D }])!, "search text bypasses unbind");
            Set("_searching", false); Set("_binding", row);
            Check((bool)capture.Invoke(dialog, null)!, "active binding retains Komet keyboard capture");
            Check((bool)prefix.Invoke(null, [dialog, new KeyEvent { KeyCode = (int)GlKeys.Delete }])!, "active capture keeps Delete assignment behavior");
            Set("_binding", null);
            AccessTools.Property(rowType, "Enabled").SetValue(row, (Func<bool>)(() => false));
            received = menu.KeyDownCount; Press(GlKeys.Delete);
            Check(menu.KeyDownCount == received + 1, "disabled row is protected");
            AccessTools.Property(rowType, "Enabled").SetValue(row, null);
            Set("opened", false);
            received = menu.KeyDownCount; Press(GlKeys.Delete);
            Check(menu.KeyDownCount == received + 1, "closed dialog cannot unbind");
            Set("opened", true);
            var handled = new KeyEvent { KeyCode = (int)GlKeys.Delete, Handled = true };
            onKey.Invoke(dialog, [handled]);
            Check(keys["sneak"].CurrentMapping.KeyCode != 0, "already handled key does not clear binding");
            received = menu.KeyDownCount; Press(GlKeys.A);
            Check(menu.KeyDownCount == received + 1, "unrelated keyboard input reaches ESC menu");
            var newer = (IDisposable)create.Invoke(null, [api])!;
            patch!.Dispose(); patch = newer;
            Check(Harmony.GetPatchInfo(onKey)?.Prefixes.Count(p => p.owner == owner) == 1
                && Harmony.GetPatchInfo(capture)?.Postfixes.Count(p => p.owner == owner) == 1,
                "repeat initialization has one keyboard and capture patch and old disposal is harmless");
            patch.Dispose(); patch = null;
            Check(new[] { onKey, capture, clash, AccessTools.Method(engine, "Mapping") }.All(m => Harmony.GetPatchInfo(m)?.Owners.Contains(owner) != true), "disposal removes all compatibility patches");
            Console.WriteLine($"Komet hotkey checks passed: {count}");
        }
        finally { patch?.Dispose(); GamePaths.DataPath = oldPath; }
    }

    private sealed class CapturingMenu() : GuiDialog(null!)
    {
        public int KeyDownCount;
        public override string ToggleKeyCombinationCode => "";
        public override bool CaptureAllInputs() => true;
        public override void OnKeyDown(KeyEvent args) { KeyDownCount++; args.Handled = true; }
    }
}
