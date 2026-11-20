using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

static class InventoryDiagnosticsChecks
{
    public static void Run()
    {
        Assembly gameAssembly = Assembly.Load("VintagestoryLib");
        Type managerType = gameAssembly.GetType("Vintagestory.Client.NoObf.GuiManager", true)!;
        Type gameType = gameAssembly.GetType("Vintagestory.Client.NoObf.ClientMain", true)!;
        object game = RuntimeHelpers.GetUninitializedObject(gameType);
        object manager = RuntimeHelpers.GetUninitializedObject(managerType);
        AccessTools.Field(managerType, "game").SetValue(manager, game);
        var dialogs = new List<GuiDialog>();
        AccessTools.Field(gameType, "LoadedGuis").SetValue(game, dialogs);
        AccessTools.Field(gameType, "OpenedGuis").SetValue(game, new List<GuiDialog>());
        // GUI transitions invoke the concrete client API to leave the previous slot.
        Type clientApiType = gameAssembly.GetType("Vintagestory.Client.NoObf.ClientCoreAPI", true)!;
        Type inputType = gameAssembly.GetType("Vintagestory.Client.NoObf.InputAPI", true)!;
        object engineApi = RuntimeHelpers.GetUninitializedObject(clientApiType);
        object engineInput = Activator.CreateInstance(inputType, game)!;
        AccessTools.Field(clientApiType, "inputapi").SetValue(engineApi, engineInput);
        AccessTools.Field(gameType, "api").SetValue(game, engineApi);
        var messages = new List<string>();
        var logger = Proxy.Create<ILogger>((m, a) =>
        {
            if (a is [string format, object[] values]) messages.Add(string.Format(format, values));
            return null;
        });
        Action<float>? tick = null;
        bool removedListener = false;
        var events = Proxy.Create<IClientEventAPI>((m, a) =>
        {
            if (m.Name == "RegisterGameTickListener") { tick = (Action<float>)a![0]!; return 99L; }
            if (m.Name == "UnregisterGameTickListener") removedListener = true;
            return null;
        });
        var gui = Proxy.Create<IGuiAPI>((m, _) => m.Name == "get_LoadedGuis" ? dialogs : null);
        var input = Proxy.Create<IInputAPI>((_, _) => null);
        var world = Proxy.Create<IClientWorldAccessor>((_, _) => null);
        IChatCommand? command = null;
        command = Proxy.Create<IChatCommand>((m, _) => m.ReturnType == typeof(IChatCommand) ? command : null);
        ICoreClientAPI? api = null;
        var commands = Proxy.Create<IChatCommandApi>((m, _) => m.Name switch
        {
            "Create" or "GetOrCreate" => command, "get_Parsers" => new CommandArgumentParsers(api!), _ => null
        });
        api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        {
            "get_Gui" => gui, "get_Input" => input, "get_Event" => events,
            "get_Logger" => logger, "get_World" => world, "get_ChatCommands" => commands, _ => null
        });
        var blocker = new BlockingInventoryDialog(api);
        var inventory = new ReceivingInventoryDialog(api, () =>
            AccessTools.Method(managerType, "OnMouseEnterSlot").Invoke(manager, [new ItemSlot(null)]));
        dialogs.Add(blocker);
        dialogs.Add(inventory);
        Type diagnosticsType = typeof(SeraphCivilizationPatches.SeraphCivilizationPatchesSystem).Assembly
            .GetType("SeraphCivilizationPatches.InventoryInputDiagnostics", true)!;
        using var diagnostics = (IDisposable)Activator.CreateInstance(diagnosticsType, api)!;
        MethodInfo move = AccessTools.Method(managerType, "OnMouseMove");
        MethodInfo down = AccessTools.Method(managerType, "OnMouseDown");
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name + "\n" + string.Join("\n", messages));
            Console.WriteLine("PASS: " + name);
            checks++;
        }
        Check(Harmony.GetPatchInfo(move)?.Owners.Contains("vsscpmod.inventory-diagnostics") != true,
            "diagnostics leave input unpatched until explicitly started");
        AccessTools.Method(diagnosticsType, "Start").Invoke(diagnostics, null);
        var mouse = new MouseEvent(40, 50, EnumMouseButton.Left, 0);
        move.Invoke(manager, [mouse]);
        Check(mouse.Handled && inventory.Moves == 0 && messages.Any(m => m.Contains("consumer=BlockingInventoryDialog@")),
            "actual 1.22.7 GUI routing identifies the consuming overlay without forwarding blocked hover");
        Check(messages.Any(m => m.Contains("slotHoverEvent=False")), "blocked move does not fabricate slot hover");
        down.Invoke(manager, [new MouseEvent(40, 50, EnumMouseButton.Left, 0)]);
        Check(inventory.Clicks == 0 && messages.Any(m => m.Contains("OnMouseDown") && m.Contains("consumer=BlockingInventoryDialog@")),
            "blocked click remains blocked and names the same responsible dialog");
        blocker.Blocks = false;
        AccessTools.Field(diagnosticsType, "nextMoveLog").SetValue(diagnostics, 0L);
        move.Invoke(manager, [new MouseEvent(40, 50, EnumMouseButton.Left, 0)]);
        Check(inventory.Moves == 1 && messages.Any(m => m.Contains("consumer=ReceivingInventoryDialog@") && m.Contains("slotHoverEvent=True")),
            "unblocked routing reaches inventory and records an actual engine hover event");
        blocker.Failure = new InvalidOperationException("existing input failure");
        try { move.Invoke(manager, [new MouseEvent(40, 50, EnumMouseButton.Left, 0)]); throw new Exception("Expected input exception"); }
        catch (TargetInvocationException ex)
        {
            Check(ReferenceEquals(ex.InnerException, blocker.Failure), "diagnostic finalizers preserve the original input exception");
        }
        Check(messages.Any(m => m.Contains("exception in BlockingInventoryDialog") && m.Contains("existing input failure")),
            "failing dialog is captured even when GUI routing aborts");
        blocker.Failure = null;
        var late = new LateInventoryOverlay(api);
        dialogs.Insert(0, late);
        tick!(0f);
        AccessTools.Field(diagnosticsType, "nextMoveLog").SetValue(diagnostics, 0L);
        move.Invoke(manager, [new MouseEvent(40, 50)]);
        Check(messages.Any(m => m.Contains("consumer=LateInventoryOverlay@")),
            "dialogs registered after recording begins are observed on the next tick");
        AccessTools.Field(diagnosticsType, "deadline").SetValue(diagnostics, 0L);
        tick!(0f);
        Check(removedListener && Harmony.GetPatchInfo(move)?.Owners.Contains("vsscpmod.inventory-diagnostics") != true
            && Harmony.GetPatchInfo(AccessTools.Method(typeof(BlockingInventoryDialog), "OnMouseMove"))?.Owners.Contains("vsscpmod.inventory-diagnostics") != true,
            "automatic timeout unregisters its listener and removes engine and dialog hooks");
        Check(messages.Any(m => m.Contains("stop:") && m.Contains("down=1") && m.Contains("move=4")),
            "session totals distinguish absent routing from handled or failing events");
        AccessTools.Method(diagnosticsType, "Start").Invoke(diagnostics, null);
        diagnostics.Dispose();
        Check(Harmony.GetPatchInfo(move)?.Owners.Contains("vsscpmod.inventory-diagnostics") != true,
            "a restarted diagnostic session also removes all hooks on disposal");
        Console.WriteLine($"Inventory diagnostics checks passed: {checks}");
    }
}

sealed class BlockingInventoryDialog(ICoreClientAPI api) : GuiDialog(api)
{
    public bool Blocks = true;
    public Exception? Failure;
    public override string ToggleKeyCombinationCode => null!;
    public override bool ShouldReceiveMouseEvents() => true;
    public override void OnMouseMove(MouseEvent args)
    {
        if (Failure != null) throw Failure;
        args.Handled = Blocks;
    }
    public override void OnMouseDown(MouseEvent args) => args.Handled = Blocks;
}

sealed class ReceivingInventoryDialog(ICoreClientAPI api, Action hover) : GuiDialog(api)
{
    public int Moves, Clicks;
    public override string ToggleKeyCombinationCode => null!;
    public override bool ShouldReceiveMouseEvents() => true;
    public override void OnMouseMove(MouseEvent args) { Moves++; hover(); args.Handled = true; }
    public override void OnMouseDown(MouseEvent args) { Clicks++; args.Handled = true; }
}

sealed class LateInventoryOverlay(ICoreClientAPI api) : GuiDialog(api)
{
    public override string ToggleKeyCombinationCode => null!;
    public override bool ShouldReceiveMouseEvents() => true;
    public override void OnMouseMove(MouseEvent args) => args.Handled = true;
}
