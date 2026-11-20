using System.Collections;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SeraphCivilizationPatches;

// Opt-in observation only: never replay input, clear locks, or alter inventory state.
internal sealed class InventoryInputDiagnostics : IDisposable
{
    private const string HarmonyId = "vsscpmod.inventory-diagnostics";
    private const string Tag = "[vsscpmodinvdiag]";
    private readonly ICoreClientAPI api;
    private readonly Harmony harmony = new(HarmonyId);
    private readonly HashSet<MethodInfo> patched = [];
    private static InventoryInputDiagnostics? active;
    private Frame? frame;
    private long listener;
    private long deadline;
    private long nextMoveLog;
    private int downCount, upCount, moveCount;
    private bool running;

    private sealed class Frame
    {
        public required string Kind;
        public required MouseEvent Args;
        public bool HandledOnEntry;
        public string? Consumer;
        public List<string> Visited = [];
        public Frame? Parent;
    }

    public InventoryInputDiagnostics(ICoreClientAPI api)
    {
        this.api = api;
        ModCommands.Root(api).BeginSubCommand("inventory")
            .WithDescription("Record inventory mouse routing for 30 seconds, or stop recording")
            .WithArgs(api.ChatCommands.Parsers.OptionalWordRange("mode", "start", "stop"))
            .HandleWith(args => RunCommand(args[0] as string)).EndSubCommand();
        api.Event.LeaveWorld += Stop;
    }

    private TextCommandResult RunCommand(string? mode)
    {
        if (mode == "stop")
        {
            Stop();
            return TextCommandResult.Success("库存诊断已停止，记录在 client-main.log / client-debug.log，标记 [vsscpmodinvdiag]。");
        }
        if (api.World.Player == null) return TextCommandResult.Error("请先进入世界。");
        try
        {
            Start();
            return TextCommandResult.Success("库存诊断开启 30 秒。关闭聊天，打开背包和箱子，移动鼠标到物品上并点击；随后输入 .vsscpmod inventory stop。记录标记 [vsscpmodinvdiag]。");
        }
        catch (Exception ex)
        {
            Stop();
            api.Logger.Warning("{0} Cannot start: {1}", Tag, ex);
            return TextCommandResult.Error("库存诊断启动失败，详情见客户端日志。");
        }
    }

    private void Start()
    {
        Stop();
        active?.Stop();
        Type manager = AccessTools.TypeByName("Vintagestory.Client.NoObf.GuiManager")
            ?? throw new TypeLoadException("GuiManager");
        active = this;
        running = true;
        downCount = upCount = moveCount = 0;
        nextMoveLog = 0;
        deadline = Environment.TickCount64 + 30_000;
        foreach (string name in new[] { "OnMouseDown", "OnMouseUp", "OnMouseMove" })
        {
            MethodInfo method = AccessTools.DeclaredMethod(manager, name, [typeof(MouseEvent)])
                ?? throw new MissingMethodException(manager.FullName, name);
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(InventoryInputDiagnostics), nameof(BeginRoute)),
                finalizer: new HarmonyMethod(typeof(InventoryInputDiagnostics), nameof(EndRoute)));
        }
        PatchDialogs();
        Snapshot("start");
        listener = api.Event.RegisterGameTickListener(_ =>
        {
            if (Environment.TickCount64 >= deadline) Stop();
            else Safe(PatchDialogs);
        }, 500);
    }

    private void PatchDialogs()
    {
        foreach (GuiDialog dialog in api.Gui.LoadedGuis.ToArray())
        foreach (string name in new[] { "OnMouseDown", "OnMouseUp", "OnMouseMove" })
        {
            MethodInfo? method = AccessTools.Method(dialog.GetType(), name, [typeof(MouseEvent)]);
            if (method != null) method = AccessTools.DeclaredMethod(method.DeclaringType, name, [typeof(MouseEvent)]);
            if (method == null || method.IsAbstract || patched.Contains(method)) continue;
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(InventoryInputDiagnostics), nameof(BeginDialog)),
                finalizer: new HarmonyMethod(typeof(InventoryInputDiagnostics), nameof(EndDialog)));
            patched.Add(method);
        }
    }

    private static void BeginRoute(MouseEvent __0, MethodBase __originalMethod, out Frame? __state)
    {
        __state = null;
        var self = active;
        if (self?.running != true) return;
        __state = new Frame { Kind = __originalMethod.Name, Args = __0,
            HandledOnEntry = __0.Handled, Parent = self.frame };
        self.frame = __state;
    }

    private static void BeginDialog(GuiDialog __instance, MouseEvent __0, out bool __state)
    {
        __state = __0.Handled;
        var current = active?.frame;
        if (current == null || !ReferenceEquals(current.Args, __0)) return;
        string name = Identity(__instance);
        if (current.Visited.LastOrDefault() != name) current.Visited.Add(name);
    }

    private static Exception? EndDialog(GuiDialog __instance, MouseEvent __0, bool __state, Exception? __exception)
    {
        var self = active;
        var current = self?.frame;
        if (current != null && ReferenceEquals(current.Args, __0))
        {
            if (!__state && __0.Handled) current.Consumer ??= Identity(__instance);
            if (__exception != null) self!.Safe(() => self.api.Logger.Warning(
                "{0} {1} exception in {2}: {3}", Tag, current.Kind, Identity(__instance), __exception));
        }
        return __exception;
    }

    private static Exception? EndRoute(object __instance, Frame? __state, Exception? __exception)
    {
        var self = active;
        if (self == null || __state == null) return __exception;
        self.frame = __state.Parent;
        self.Safe(() => self.RecordRoute(__instance, __state, __exception));
        return __exception;
    }

    private void RecordRoute(object manager, Frame current, Exception? exception)
    {
        if (current.Kind == "OnMouseDown") downCount++;
        else if (current.Kind == "OnMouseUp") upCount++;
        else moveCount++;
        long now = Environment.TickCount64;
        if (current.Kind == "OnMouseMove" && exception == null)
        {
            if (now < nextMoveLog) return;
            nextMoveLog = now + 250;
        }
        api.Logger.Notification("{0} {1} screen=({2},{3}) button={4} handled={5}->{6} grabbed={7} consumer={8} slotHoverEvent={9} hoverSlot={10} visited=[{11}] exception={12}",
            Tag, current.Kind, current.Args.X, current.Args.Y, current.Args.Button,
            current.HandledOnEntry, current.Args.Handled, api.Input.MouseGrabbed,
            current.Consumer ?? "none", Read(manager, "didHoverSlotEventTrigger"),
            (Read(manager, "prevHoverSlot") as ItemSlot)?.GetType().FullName ?? "none",
            string.Join(" -> ", current.Visited), exception?.ToString() ?? "none");
    }

    private void Snapshot(string phase)
    {
        api.Logger.Notification("{0} {1}: mouse=({2},{3}) grabbed={4}; down={5} up={6} move={7}",
            Tag, phase, api.Input.MouseX, api.Input.MouseY, api.Input.MouseGrabbed, downCount, upCount, moveCount);
        int index = 0;
        foreach (GuiDialog dialog in api.Gui.LoadedGuis.ToArray())
        {
            Safe(() =>
            {
                api.Logger.Notification("{0} GUI[{1}] {2} opened={3} receivesMouse={4} focused={5} inputOrder={6} captureAll={7}",
                    Tag, index, Identity(dialog), dialog.IsOpened(), dialog.ShouldReceiveMouseEvents(),
                    dialog.Focused, dialog.InputOrder, dialog.CaptureAllInputs());
                foreach (var entry in dialog.Composers)
                {
                    GuiComposer composer = entry.Value;
                    if (composer == null) continue;
                    api.Logger.Notification("{0} composer={1} composed={2} enabled={3} bounds={4}",
                        Tag, entry.Key, composer.Composed, composer.Enabled, Bounds(composer.Bounds));
                    if (Read(composer, "interactiveElements") is not IDictionary elements) continue;
                    foreach (GuiElementItemSlotGridBase grid in elements.Values.OfType<GuiElementItemSlotGridBase>())
                    {
                        int hits = grid.SlotBounds?.Count(b => b != null && b.PointInside(api.Input.MouseX, api.Input.MouseY)) ?? 0;
                        api.Logger.Notification("{0} grid={1} rendered={2} slotBounds={3} mouseHits={4} bounds={5} parent={6}",
                            Tag, grid.GetType().FullName, grid.renderedSlots.Count, grid.SlotBounds?.Length ?? 0,
                            hits, Bounds(grid.Bounds), Bounds(grid.Bounds.ParentBounds));
                    }
                }
            });
            index++;
        }
        var inventories = api.World.Player?.InventoryManager.Inventories;
        if (inventories == null) return;
        foreach (var entry in inventories.ToArray()) Safe(() =>
        {
            var inventory = entry.Value;
            string slots = string.Join(", ", inventory.Select((slot, i) => (slot, i))
                .Where(p => p.slot != null && p.slot.GetType().Name.Contains("Locked", StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{p.i}:{p.slot.GetType().FullName}"));
            api.Logger.Notification("{0} inventoryClass={1} count={2} paused={3} lockedSlots=[{4}]",
                Tag, inventory.ClassName, inventory.Count, (inventory as InventoryBase)?.InvNetworkUtil?.PauseInventoryUpdates, slots);
        });
    }

    private static string Identity(GuiDialog dialog)
        => $"{dialog.GetType().FullName}@{dialog.GetType().Assembly.GetName().Name}";

    private static object? Read(object target, string name)
        => AccessTools.Field(target.GetType(), name)?.GetValue(target);

    private static string Bounds(ElementBounds? bounds)
        => bounds == null ? "none" : FormattableString.Invariant($"({bounds.absX:F1},{bounds.absY:F1},{bounds.OuterWidth:F1},{bounds.OuterHeight:F1})");

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            // A logger failure must not change the input operation being observed.
            try { api.Logger.Warning("{0} Observation failed: {1}", Tag, ex.Message); }
            catch { }
        }
    }

    private void Stop()
    {
        if (!running) return;
        running = false;
        if (listener != 0) api.Event.UnregisterGameTickListener(listener);
        listener = 0;
        Safe(() => Snapshot("stop"));
        harmony.UnpatchAll(HarmonyId);
        patched.Clear();
        frame = null;
        if (ReferenceEquals(active, this)) active = null;
    }

    public void Dispose()
    {
        Stop();
        api.Event.LeaveWorld -= Stop;
    }
}
