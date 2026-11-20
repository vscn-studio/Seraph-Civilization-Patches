using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HarmonyLib;
using ProtoBuf;
using SeraphCivilizationPatches;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

static class CoexistenceChecks
{
    public static void Run(string root, string game)
    {
        int count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; Console.WriteLine("PASS: " + name); }
        var assembly = typeof(SeraphCivilizationPatchesSystem).Assembly;
        var mapPolicy = AccessTools.Method(typeof(SeraphCivilizationPatchesSystem), "UseIndependentMaps");
        var coordinates = assembly.GetType("SeraphCivilizationPatches.CoordinateDisplayCompatibility")!;
        var logger = Proxy.Create<ILogger>((_, _) => null);
        foreach (var enabled in new[] { Array.Empty<string>(), new[] { "vsslssmod", "vsscsmod" }, new[] { "immersivemaps" },
            new[] { "immersivemaps", "vsslssmod" }, new[] { "immersivemaps", "vsscsmod" }, new[] { "immersivemaps", "vsslssmod", "vsscsmod" } })
        {
            var loader = Proxy.Create<IModLoader>((m, a) => m.Name == "IsModEnabled" && enabled.Contains((string)a![0]!));
            bool expected = enabled.Contains("immersivemaps") && !enabled.Contains("vsscsmod");
            Check((bool)mapPolicy.Invoke(null, [loader])! == expected, "map ownership: " + string.Join(",", enabled));
            if (!expected)
            {
                var api = Proxy.Create<ICoreClientAPI>((m, _) => m.Name == "get_ModLoader" ? loader : null);
                Check(AccessTools.Method(coordinates, "TryCreate").Invoke(null, [api]) == null, "no map hooks outside independent-map mode");
            }
        }
        foreach (string language in new[] { "en", "zh-cn" })
        {
            using var lang = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "assets/vsscpmod/lang/" + language + ".json")));
            var keys = lang.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
            foreach (var source in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs"))
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(source), "\"vsscpmod:([a-z][a-z0-9-]*)\""))
                    if (!match.Groups[1].Value.EndsWith('-')) // Dynamic localization prefixes are checked by their feature suite.
                        Check(keys.Contains(match.Groups[1].Value), language + " translation: " + match.Groups[1].Value);
        }
        var restrictionType = assembly.GetType("SeraphCivilizationPatches.InformationRestrictions")!;
        var server = RuntimeHelpers.GetUninitializedObject(restrictionType);
        var config = new InformationRestrictionsConfig();
        AccessTools.Field(restrictionType, "config").SetValue(server, config);
        var exemptions = new HashSet<string>();
        AccessTools.Field(restrictionType, "exemptPlayers").SetValue(server, exemptions);
        bool admin = false;
        var player = Proxy.Create<IPlayer>((m, _) => m.Name switch { "HasPrivilege" => admin, "get_PlayerUID" => "test-player", _ => null });
        bool Restricted() => (bool)AccessTools.Method(restrictionType, "IsRestricted").Invoke(server, [player])!;
        Check(Restricted(), "ordinary players receive default restrictions");
        admin = true; Check(Restricted(), "administrators remain restricted until explicitly exempt");
        exemptions.Add("test-player"); Check(!Restricted(), "administrator session exemption works");
        admin = false; Check(Restricted(), "privilege loss immediately cancels effective exemption");
        config.InformationRestrictionsEnabled = false; Check(!Restricted(), "master switch disables restrictions");
        config.InformationRestrictionsEnabled = true;
        var client = RuntimeHelpers.GetUninitializedObject(restrictionType);
        AccessTools.Field(restrictionType, "client").SetValue(null, client);
        try
        {
            var packet = new InformationAccessPacket { HideTime = true, HideCoordinates = true, HideDebugInfo = true, BlockInformationCommands = true };
            using var stream = new MemoryStream(); Serializer.Serialize(stream, packet); stream.Position = 0;
            var received = Serializer.Deserialize<InformationAccessPacket>(stream);
            AccessTools.Method(restrictionType, "ReceiveAccess").Invoke(client, [received]);
            bool ClientCommand(string path) => (bool)AccessTools.Method(restrictionType, "ClientCommandRestricted").Invoke(null, [path])!;
            Check(ClientCommand("time") && ClientCommand("cp apos"), "serialized server settings restrict client commands");
            Check(!ClientCommand("vsscpmod inventory"), "diagnostic command remains available");
            AccessTools.Method(restrictionType, "ResetAccess").Invoke(client, null);
            Check(!ClientCommand("time") && !ClientCommand("cp apos"), "leaving a world clears received restriction state");
        }
        finally { AccessTools.Field(restrictionType, "client").SetValue(null, null); }

        // Exercise the real server constructor and registrations against substituted host services.
        Assembly.LoadFrom(Path.Combine(game, "VintagestoryLib.dll"));
        ICoreServerAPI sapi = null!;
        var reads = new List<string>(); var writes = new List<string>(); var channels = new List<string>();
        bool listenerRemoved = false;
        IChatCommandApi commands = null!;
        var packets = new List<InformationAccessPacket>();
        IServerNetworkChannel channel = null!;
        channel = Proxy.Create<IServerNetworkChannel>((m, a) => {
            if (m.Name == "SendPacket") packets.Add((InformationAccessPacket)a![0]!);
            return m.ReturnType.IsInstanceOfType(channel) ? channel : null;
        });
        var network = Proxy.Create<IServerNetworkAPI>((m, a) => { if (m.Name == "RegisterChannel") channels.Add((string)a![0]!); return channel; });
        var events = Proxy.Create<IServerEventAPI>((m, _) => {
            if (m.Name == "RegisterGameTickListener") return 42L;
            if (m.Name == "UnregisterGameTickListener") listenerRemoved = true;
            return null;
        });
        var commandPlayer = Proxy.Create<IServerPlayer>((m, a) => m.Name switch {
            "HasPrivilege" => (string)a![0]! == Privilege.chat || admin,
            "get_PlayerUID" => "command-player", "get_LanguageCode" => "en", _ => null
        });
        var world = Proxy.Create<IServerWorldAccessor>((m, _) => m.Name == "get_AllOnlinePlayers" ? new IPlayer[] { commandPlayer } : null);
        sapi = Proxy.Create<ICoreServerAPI>((m, a) => {
            if (m.Name == "LoadModConfig") { reads.Add((string)a![0]!); return null; }
            if (m.Name == "StoreModConfig") { writes.Add((string)a![1]!); return null; }
            return m.Name switch { "get_ChatCommands" => commands, "get_Network" => network, "get_Event" => events, "get_Logger" => logger, "get_World" => world, "get_Side" => EnumAppSide.Server, _ => null };
        });
        commands = (IChatCommandApi)Activator.CreateInstance(AccessTools.TypeByName("Vintagestory.Common.ChatCommandApi")!, [sapi])!;
        Lang.AvailableLanguages["en"] = Proxy.Create<ITranslationService>((m, a) => m.Name switch {
            "HasTranslation" => true, "Get" => a![0], _ => null
        });
        Lang.ChangeLanguage("en");
        using (var instance = (IDisposable)Activator.CreateInstance(restrictionType, [sapi])!)
        {
            Check(reads.SequenceEqual(new[] { "vsscpmod-privacy.json" }) && writes.SequenceEqual(reads), "only the new configuration is read and written");
            Check(channels.SequenceEqual(new[] { "vsscpmod.info" }), "only the new network channel is registered");
            var command = commands.Get("vsscpmod");
            Check(!command.Incomplete, "real game command registry accepts the root command");
            command.Validate();
            Check((string)AccessTools.Method(command["info"].GetType(), "GetPrivilege").Invoke(command["info"], null)! == Privilege.controlserver,
                "info subcommand retains administrator authorization");
            TextCommandResult Execute(string arguments, bool withPlayer = true) {
                TextCommandResult? result = null;
                var caller = new Caller();
                if (withPlayer) caller.Player = commandPlayer;
                else caller.CallerPrivileges = ["*"];
                commands.Execute("vsscpmod", new TextCommandCallingArgs {
                    Caller = caller,
                    RawArgs = new CmdArgs(arguments)
                }, value => result = value);
                return result ?? throw new Exception("Command did not complete: " + arguments);
            }
            admin = true;
            Check(Execute("info off").Status == EnumCommandStatus.Success && packets.Last().Unrestricted,
                "/vsscpmod info off executes through real command dispatch and sends unrestricted access");
            Check(Execute("info status").Status == EnumCommandStatus.Success && packets.Last().Unrestricted,
                "status preserves administrator exemption");
            Check(Execute("info on").Status == EnumCommandStatus.Success && !packets.Last().Unrestricted,
                "on restores information restrictions");
            Check(Execute("info").Status == EnumCommandStatus.Success && !packets.Last().Unrestricted,
                "missing mode defaults to status");
            Check(Execute("info reload").Status == EnumCommandStatus.Success && reads.Count == 2 && writes.Count == 2,
                "reload reads settings and sends online players their access");
            int sent = packets.Count;
            admin = false;
            Check(Execute("info off").ErrorCode == "noprivilege" && packets.Count == sent,
                "ordinary players cannot disable restrictions");
            Check(Execute("info off", false).Status == EnumCommandStatus.Error && packets.Count == sent,
                "info subcommand requires a player even with console privileges");
            Check(Execute("").Status == EnumCommandStatus.Success, "ordinary players can view root command help");
        }
        Check(listenerRemoved && AccessTools.Field(restrictionType, "server").GetValue(null) == null, "server disposal releases listener and global state");
        Console.WriteLine($"{count} coexistence and identity checks passed.");
    }
}
