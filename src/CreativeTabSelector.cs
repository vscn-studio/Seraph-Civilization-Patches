using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.Common;

namespace SeraphCivilizationPatches;

// Keep the native tab objects and callback: the replacement only changes their presentation.
internal sealed class CreativeTabSelector : IDisposable
{
    private sealed class ScrollableTabs : GuiElementVerticalTabs
    {
        public GuiElementScrollbar? Scrollbar;
        public ScrollableTabs(ICoreClientAPI client, GuiTab[] tabs, ElementBounds bounds, Action<int, GuiTab> clicked)
            : base(client, tabs, CairoFont.WhiteDetailText().WithFontSize(17f),
                CairoFont.WhiteDetailText().WithFontSize(17f).WithColor(GuiStyle.ActiveButtonTextColor), bounds, clicked) { }

        public override void OnMouseWheel(ICoreClientAPI client, MouseWheelEventArgs args)
        {
            if (InsideClipBounds?.PointInside(client.Input.MouseX, client.Input.MouseY) == true)
                Scrollbar?.OnMouseWheel(client, args);
        }
    }
    private sealed class CategoryScrollbar : GuiElementScrollbar
    {
        public ElementBounds? ListBounds;
        public CategoryScrollbar(ICoreClientAPI client, Action<float> changed, ElementBounds bounds)
            : base(client, changed, bounds) { }

        public override void OnMouseWheel(ICoreClientAPI client, MouseWheelEventArgs args)
        {
            if (ListBounds?.PointInside(client.Input.MouseX, client.Input.MouseY) == true ||
                Bounds.PointInside(client.Input.MouseX, client.Input.MouseY)) base.OnMouseWheel(client, args);
        }
    }
    private const string Id = "vsscpmod.creative-tab-selector";
    private const string Key = "vsscpmod-creative-categories";
    private static Harmony? harmony;
    private static FieldInfo composerField = null!, tabsField = null!, currentTabField = null!;
    private static readonly Dictionary<GuiDialog, GuiComposer> panels = new();
    private static ICoreClientAPI api = null!;
    private static bool warned;

    public static CreativeTabSelector? TryCreate(ICoreClientAPI client)
    {
        try
        {
            var type = AccessTools.TypeByName("Vintagestory.Client.NoObf.GuiDialogInventory") ?? throw new TypeLoadException("GuiDialogInventory");
            var compose = AccessTools.Method(type, "ComposeCreativeInvDialog") ?? throw new MissingMethodException(type.FullName, "ComposeCreativeInvDialog");
            composerField = AccessTools.Field(type, "creativeInvDialog") ?? throw new MissingFieldException(type.FullName, "creativeInvDialog");
            currentTabField = AccessTools.Field(type, "currentTabIndex") ?? throw new MissingFieldException(type.FullName, "currentTabIndex");
            tabsField = AccessTools.Field(typeof(GuiElementVerticalTabs), "tabs") ?? throw new MissingFieldException("GuiElementVerticalTabs", "tabs");
            if (composerField.FieldType != typeof(GuiComposer) || tabsField.FieldType != typeof(GuiTab[])) throw new InvalidOperationException("Creative inventory UI contract changed");
            api = client;
            harmony = new Harmony(Id);
            harmony.Patch(compose, postfix: new HarmonyMethod(typeof(CreativeTabSelector), nameof(Compose)));
            harmony.Patch(AccessTools.Method(type, "ComposeGui"), postfix: new HarmonyMethod(typeof(CreativeTabSelector), nameof(AfterComposeGui)));
            harmony.Patch(AccessTools.Method(type, "OnTabClicked"), postfix: new HarmonyMethod(typeof(CreativeTabSelector), nameof(SyncSelection)));
            // GuiDialogInventory inherits this method; Harmony requires patching the
            // declaring virtual method rather than the inherited lookup result.
            harmony.Patch(AccessTools.DeclaredMethod(typeof(GuiDialog), "OnMouseWheel")
                ?? throw new MissingMethodException(typeof(GuiDialog).FullName, "OnMouseWheel"),
                prefix: new HarmonyMethod(typeof(CreativeTabSelector), nameof(RouteMouseWheel)));
            harmony.Patch(AccessTools.Method(type, "OnGuiClosed"), prefix: new HarmonyMethod(typeof(CreativeTabSelector), nameof(Remove)));
            return new CreativeTabSelector();
        }
        catch (Exception ex)
        {
            harmony?.UnpatchAll(Id); harmony = null;
            client.Logger.Warning("[vsscpmod] Creative category selector unavailable: {0}", ex.GetBaseException().Message);
            return null;
        }
    }

    private static void Compose(GuiDialog __instance)
    {
        Remove(__instance);
        GuiElementVerticalTabs? right = null;
        try
        {
            var native = (GuiComposer?)composerField.GetValue(__instance);
            right = native?["verticalTabsR"] as GuiElementVerticalTabs;
            if (right == null) return;
            var tabs = (GuiTab[])tabsField.GetValue(right)!;
            if (tabs.Length == 0) return;
            native!.Bounds.CalcWorldBounds();
            double scale = Math.Max(.1, RuntimeEnv.GUIScale);
            using var measureFont = CairoFont.WhiteDetailText().WithFontSize(17f);
            double labelWidth = tabs.Max(t => measureFont.GetTextExtents(t.Name ?? "").Width) / scale;
            // The list needs room for the tab padding, scrollbar and dialog padding.
            double width = Math.Min(Math.Max(220, Math.Ceiling(labelWidth + 64)), Math.Max(1, api.Render.FrameWidth / scale - 16));
            double total = tabs.Length * 30.0;
            double height = Math.Min(native.Bounds.OuterHeight / scale, total + 58);
            double visible = Math.Max(1, height - 58);
            double x = Math.Min((native.Bounds.absX + native.Bounds.OuterWidth) / scale + 8, api.Render.FrameWidth / scale - width - 8);
            double y = Math.Max(0, native.Bounds.absY / scale);
            var background = ElementBounds.Fixed(0, 0, width - 24, height - 24).WithFixedPadding(12);
            var bounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.None).WithFixedPosition(Math.Max(0, x), y);
            var panel = api.Gui.CreateCompo(Key, bounds);
            try
            {
                var listTabs = tabs.Select(t => new GuiTab { Name = t.Name, DataInt = t.DataInt }).ToArray();
                int selected = Array.FindIndex(tabs, t => t.DataInt == (int)currentTabField.GetValue(__instance)!);
                var content = ElementBounds.Fixed(0, 0, width - 48, total);
                var clip = ElementBounds.Fixed(4, 34, width - 48, visible);
                var list = new ScrollableTabs(api, listTabs, content, (index, _) => right.SetValue(index, true));
                var scrollbar = new CategoryScrollbar(api, value => LevelingSkillPanel.Position(content, value / scale, total, visible),
                    ElementBounds.Fixed(width - 40, 34, 16, visible)) { ListBounds = clip };
                for (int i = 0; i < listTabs.Length; i++) listTabs[i].Active = i == selected;
                panel.AddShadedDialogBG(background).AddDialogTitleBar(Lang.Get("vsscpmod:creative-categories"), () => __instance.TryClose());
                panel.BeginChildElements(background);
                panel.BeginClip(clip);
                panel.AddInteractiveElement(list, "categories");
                panel.EndClip();
                panel.AddInteractiveElement(scrollbar, "category-scroll");
                panel.EndChildElements().Compose();
                list.Scrollbar = panel.GetScrollbar("category-scroll");
                list.Scrollbar.SetHeights((float)(visible * scale), (float)(total * scale));
                panels[__instance] = panel;
                __instance.Composers[Key] = panel;
                // Original composer still owns and disposes this element. Move it outside the screen.
                right.Bounds.fixedX += 100000;
                right.Bounds.CalcWorldBounds();
            }
            catch { panel.Dispose(); throw; }
        }
        catch (Exception ex)
        {
            Remove(__instance);
            if (!warned) { warned = true; api.Logger.Warning("[vsscpmod] Creative category selector failed: {0}", ex.GetBaseException().Message); }
        }
    }

    private static void AfterComposeGui(GuiDialog __instance)
    {
        if (api.World.Player.WorldData.CurrentGameMode != EnumGameMode.Creative) Remove(__instance);
    }

    private static bool RouteMouseWheel(GuiDialog __instance, MouseWheelEventArgs args)
    {
        if (args.IsHandled || !panels.TryGetValue(__instance, out var panel) || !panel.Enabled ||
            panel["category-scroll"] is not CategoryScrollbar scrollbar) return true;
        int x = api.Input.MouseX, y = api.Input.MouseY;
        if (scrollbar.ListBounds?.PointInside(x, y) != true && !scrollbar.Bounds.PointInside(x, y)) return true;
        scrollbar.OnMouseWheel(api, args);
        // Even a fully visible list must not scroll the inventory underneath it.
        args.SetHandled();
        return false;
    }

    private static void SyncSelection(GuiDialog __instance, GuiTab tab)
    {
        if (!panels.TryGetValue(__instance, out var panel)) return;
        var native = (GuiComposer?)composerField.GetValue(__instance);
        if (native?["verticalTabsR"] is not GuiElementVerticalTabs right) return;
        var tabs = (GuiTab[])tabsField.GetValue(right)!;
        int index = Array.FindIndex(tabs, t => t.DataInt == tab.DataInt);
        if (panel["categories"] is not ScrollableTabs list) return;
        var listTabs = (GuiTab[])tabsField.GetValue(list)!;
        for (int i = 0; i < listTabs.Length; i++) listTabs[i].Active = i == index;
    }

    private static void Remove(GuiDialog __instance)
    {
        if (!panels.Remove(__instance, out var panel)) return;
        __instance.Composers.Remove(Key);
        panel.Dispose();
    }

    public void Dispose()
    {
        foreach (var dialog in panels.Keys.ToArray()) Remove(dialog);
        harmony?.UnpatchAll(Id); harmony = null;
    }
}
