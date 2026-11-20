using System.Reflection;
using HarmonyLib;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace SeraphCivilizationPatches;

[ProtoContract]
public sealed class InformationAccessPacket
{
    [ProtoMember(1)] public bool Unrestricted { get; set; }
    [ProtoMember(2)] public bool HideTime { get; set; }
    [ProtoMember(3)] public bool HideCoordinates { get; set; }
    [ProtoMember(4)] public bool HideDebugInfo { get; set; }
    [ProtoMember(5)] public bool HideImmersiveMapsCoordinateUi { get; set; }
    [ProtoMember(7)] public bool RedactCoordinateNotifications { get; set; }
    [ProtoMember(8)] public bool BlockInformationCommands { get; set; }
}

public sealed class InformationRestrictionsConfig
{
    public bool InformationRestrictionsEnabled { get; set; } = true;
    public bool HideTime { get; set; } = true;
    public bool HideCoordinates { get; set; } = true;
    public bool HideDebugInfo { get; set; } = true;
    public bool BlockInformationCommands { get; set; } = true;
    public bool HideImmersiveMapsCoordinateUi { get; set; } = true;
    public bool RedactCoordinateNotifications { get; set; } = true;
}

internal sealed class InformationRestrictions : IDisposable
{
    private const string ChannelName = "vsscpmod.info";
    private const string ConfigFileName = "vsscpmod-privacy.json";
    private static InformationRestrictions? server;
    private static InformationRestrictions? client;
    private readonly ICoreServerAPI? sapi;
    private readonly ICoreClientAPI? capi;
    private readonly IServerNetworkChannel? serverChannel;
    private readonly HashSet<string> exemptPlayers = new(StringComparer.Ordinal);
    private readonly Harmony harmony;
    private readonly InformationDisplayPatches? displays;
    private readonly long privilegeListener;
    private InformationRestrictionsConfig config = new();
    private bool unrestricted;
    private bool clientHideTime;
    private bool clientHideCoordinates;
    private bool clientHideDebugInfo;
    private bool clientHideImmersiveMaps;
    private bool clientRedactCoordinateNotifications;
    private bool clientBlockInformationCommands;
    private bool disposed;

    internal static bool ClientRestricted => client?.unrestricted != true;
    internal static bool ClientTimeRestricted => client?.unrestricted != true && client?.clientHideTime == true;
    internal static bool ClientCoordinatesRestricted => client?.unrestricted != true && client?.clientHideCoordinates == true;
    internal static bool ClientDebugRestricted => client?.unrestricted != true && client?.clientHideDebugInfo == true;
    internal static bool ClientImmersiveMapsRestricted => client?.unrestricted != true && client?.clientHideImmersiveMaps == true;
    internal static bool ClientCoordinateNotificationsRestricted => client?.unrestricted != true && client?.clientRedactCoordinateNotifications == true;

    public InformationRestrictions(ICoreServerAPI api)
    {
        server?.Dispose();
        server = this;
        sapi = api;
        harmony = new Harmony("vsscpmod.info.server");
        LoadConfig();
        serverChannel = api.Network.RegisterChannel(ChannelName).RegisterMessageType<InformationAccessPacket>();
        PatchCommands(api, nameof(CheckServerCommand));
        ModCommands.Root(api).BeginSubCommand("info")
            .WithDescription("Control your own time and coordinate information restrictions")
            .RequiresPrivilege(Privilege.controlserver).RequiresPlayer()
            .WithArgs(api.ChatCommands.Parsers.OptionalWordRange("mode", "status", "on", "off", "reload"))
            .HandleWith(ChangeAccess).EndSubCommand();
        api.Event.PlayerNowPlaying += PlayerNowPlaying;
        api.Event.PlayerDisconnect += PlayerDisconnected;
        privilegeListener = api.Event.RegisterGameTickListener(CheckPrivileges, 1000);
    }

    public InformationRestrictions(ICoreClientAPI api)
    {
        client?.Dispose();
        client = this;
        capi = api;
        harmony = new Harmony("vsscpmod.info.client");
        api.Network.RegisterChannel(ChannelName).RegisterMessageType<InformationAccessPacket>()
            .SetMessageHandler<InformationAccessPacket>(ReceiveAccess);
        PatchCommands(api, nameof(CheckClientCommand));
        displays = new InformationDisplayPatches(api, harmony);
        api.Event.LeaveWorld += ResetAccess;
    }

    private void PatchCommands(ICoreAPI api, string patch)
    {
        Type type = AccessTools.TypeByName("Vintagestory.Common.ChatCommandImpl")
            ?? throw new TypeLoadException("Vintage Story chat command implementation was not found.");
        MethodInfo method = AccessTools.Method(type, "Execute", [typeof(TextCommandCallingArgs), typeof(Action<TextCommandResult>)])
            ?? throw new MissingMethodException(type.FullName, "Execute");
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(InformationRestrictions), patch) { priority = Priority.First });
        api.Logger.Notification("[vsscpmod] Time and coordinate command restrictions enabled.");
    }

    private bool IsRestricted(IPlayer player)
    {
        return HasAnyFeatureEnabled && (!player.HasPrivilege(Privilege.controlserver) || !exemptPlayers.Contains(player.PlayerUID));
    }

    private bool HasAnyFeatureEnabled => config.InformationRestrictionsEnabled &&
        (config.HideTime || config.HideCoordinates || config.HideDebugInfo || config.BlockInformationCommands ||
         config.HideImmersiveMapsCoordinateUi || config.RedactCoordinateNotifications);

    private enum Feature { Time, Coordinates, Debug, Commands, ImmersiveMaps, CoordinateNotifications }

    private bool IsFeatureRestricted(IPlayer player, Feature feature)
    {
        if (!config.InformationRestrictionsEnabled || (player.HasPrivilege(Privilege.controlserver) && exemptPlayers.Contains(player.PlayerUID))) return false;
        return feature switch
        {
            Feature.Time => config.HideTime,
            Feature.Coordinates => config.HideCoordinates,
            Feature.Debug => config.HideDebugInfo,
            Feature.Commands => config.BlockInformationCommands,
            Feature.ImmersiveMaps => config.HideImmersiveMapsCoordinateUi,
            Feature.CoordinateNotifications => config.RedactCoordinateNotifications,
            _ => false
        };
    }

    private bool LoadConfig()
    {
        try
        {
            config = sapi!.LoadModConfig<InformationRestrictionsConfig>(ConfigFileName)
                ?? new InformationRestrictionsConfig();
            sapi.StoreModConfig(config, ConfigFileName);
            sapi.Logger.Notification("[vsscpmod] Information restriction settings loaded.");
            return true;
        }
        catch (Exception exception)
        {
            sapi!.Logger.Error("[vsscpmod] Failed to load {0}; keeping the previous setting ({1}).",
                ConfigFileName, exception.GetBaseException().Message);
            return false;
        }
    }

    private TextCommandResult ChangeAccess(TextCommandCallingArgs args)
    {
        IPlayer? player = args.Caller.Player;
        if (player is not IServerPlayer serverPlayer || !player.HasPrivilege(Privilege.controlserver))
            return TextCommandResult.Error(Lang.GetL(args.LanguageCode, "vsscpmod:info-admin-only"));
        string mode = args.Parsers[0].IsMissing ? "status" : (string)args[0];
        if (mode == "reload")
        {
            if (!LoadConfig())
                return TextCommandResult.Error(Lang.GetL(args.LanguageCode, "vsscpmod:info-config-reload-failed"));
            foreach (IServerPlayer onlinePlayer in sapi!.World.AllOnlinePlayers.OfType<IServerPlayer>())
                SendAccess(onlinePlayer);
            return TextCommandResult.Success(Lang.GetL(args.LanguageCode, "vsscpmod:info-config-reloaded"));
        }
        if (mode == "off") exemptPlayers.Add(player.PlayerUID);
        if (mode == "on") exemptPlayers.Remove(player.PlayerUID);
        SendAccess(serverPlayer);
        return TextCommandResult.Success(Lang.GetL(args.LanguageCode,
            IsRestricted(player) ? "vsscpmod:info-restricted" : "vsscpmod:info-unrestricted"));
    }

    private void PlayerNowPlaying(IServerPlayer player)
    {
        exemptPlayers.Remove(player.PlayerUID);
        SendAccess(player);
    }

    private void PlayerDisconnected(IServerPlayer player) => exemptPlayers.Remove(player.PlayerUID);

    private void SendAccess(IServerPlayer player)
    {
        bool restricted = IsRestricted(player);
        serverChannel!.SendPacket(new InformationAccessPacket
        {
            Unrestricted = !restricted,
            HideTime = restricted && config.HideTime,
            HideCoordinates = restricted && config.HideCoordinates,
            HideDebugInfo = restricted && config.HideDebugInfo,
            HideImmersiveMapsCoordinateUi = restricted && config.HideImmersiveMapsCoordinateUi,
            RedactCoordinateNotifications = restricted && config.RedactCoordinateNotifications,
            BlockInformationCommands = restricted && config.BlockInformationCommands
        }, player);
    }

    private void CheckPrivileges(float dt)
    {
        if (exemptPlayers.Count == 0) return;
        foreach (IServerPlayer player in sapi!.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (exemptPlayers.Contains(player.PlayerUID) && !player.HasPrivilege(Privilege.controlserver))
            {
                exemptPlayers.Remove(player.PlayerUID);
                SendAccess(player);
            }
        }
    }

    private void ReceiveAccess(InformationAccessPacket packet)
    {
        unrestricted = packet.Unrestricted;
        clientHideTime = packet.HideTime;
        clientHideCoordinates = packet.HideCoordinates;
        clientHideDebugInfo = packet.HideDebugInfo;
        clientHideImmersiveMaps = packet.HideImmersiveMapsCoordinateUi;
        clientRedactCoordinateNotifications = packet.RedactCoordinateNotifications;
        clientBlockInformationCommands = packet.BlockInformationCommands;
        displays?.Refresh();
    }

    private void ResetAccess()
    {
        unrestricted = false;
        clientHideTime = clientHideCoordinates = clientHideDebugInfo = false;
        clientHideImmersiveMaps = clientRedactCoordinateNotifications = false;
        clientBlockInformationCommands = false;
        displays?.Refresh();
    }

    // Execute is also called for each resolved subcommand, covering aliases and player selectors.
    private static bool CheckServerCommand(IChatCommand __instance, TextCommandCallingArgs callargs,
        Action<TextCommandResult>? onCommandComplete)
    {
        if (__instance.CommandPrefix != "/" || server == null || callargs.Caller?.Player is not { } player
            || !server.IsCommandRestricted(player, CanonicalPath(__instance), false)) return true;
        onCommandComplete?.Invoke(TextCommandResult.Error(Lang.GetL(callargs.LanguageCode, "vsscpmod:info-command-disabled")));
        return false;
    }

    private static bool CheckClientCommand(IChatCommand __instance, TextCommandCallingArgs callargs,
        Action<TextCommandResult>? onCommandComplete)
    {
        if (__instance.CommandPrefix != "." || !ClientCommandRestricted(CanonicalPath(__instance))) return true;
        onCommandComplete?.Invoke(TextCommandResult.Error(Lang.Get("vsscpmod:info-command-disabled")));
        return false;
    }

    private static string CanonicalPath(IChatCommand command)
    {
        var names = new List<string>();
        for (IChatCommand? current = command; current != null;
            current = AccessTools.Field(current.GetType(), "_parent")?.GetValue(current) as IChatCommand)
            names.Add(current.Name);
        names.Reverse();
        return string.Join(" ", names);
    }

    internal static bool BlocksCommand(string path, bool clientSide)
    {
        path = path.TrimStart('.', '/').ToLowerInvariant();
        if (clientSide) return Matches(path, "cp posi", "cp aposi", "cp apos", "time", "debug datetime");
        return Matches(path, "tp", "tpwp", "time", "player entity", "waypoint list", "waypoint addat", "waypoint addati", "debug time");
    }

    private static bool Matches(string path, params string[] commands)
        => commands.Any(command => path == command || path.StartsWith(command + " ", StringComparison.Ordinal));

    private bool IsCommandRestricted(IPlayer player, string path, bool clientSide)
    {
        if (!IsFeatureRestricted(player, Feature.Commands)) return false;
        path = path.TrimStart('.', '/').ToLowerInvariant();
        if (clientSide) return ClientCommandRestricted(path);
        if (!BlocksCommand(path, false)) return false;
        if (Matches(path, "time", "debug time"))
            return IsFeatureRestricted(player, Feature.Time);
        if (Matches(path, "debug")) return IsFeatureRestricted(player, Feature.Debug);
        return IsFeatureRestricted(player, Feature.Coordinates);
    }

    private static bool ClientCommandRestricted(string path)
    {
        if (client?.clientBlockInformationCommands != true) return false;
        path = path.TrimStart('.', '/').ToLowerInvariant();
        if (Matches(path, "cp posi", "cp aposi", "cp apos")) return ClientCoordinatesRestricted;
        if (Matches(path, "time")) return ClientTimeRestricted;
        if (Matches(path, "debug datetime")) return ClientDebugRestricted || ClientTimeRestricted;
        return false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (sapi != null)
        {
            sapi.Event.PlayerNowPlaying -= PlayerNowPlaying;
            sapi.Event.PlayerDisconnect -= PlayerDisconnected;
            sapi.Event.UnregisterGameTickListener(privilegeListener);
        }
        if (capi != null) capi.Event.LeaveWorld -= ResetAccess;
        displays?.Dispose();
        harmony.UnpatchAll(harmony.Id);
        exemptPlayers.Clear();
        if (ReferenceEquals(server, this)) server = null;
        if (ReferenceEquals(client, this)) client = null;
    }
}
