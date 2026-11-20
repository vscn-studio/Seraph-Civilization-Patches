using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SeraphCivilizationPatches;

internal static class ModCommands
{
    internal static IChatCommand Root(ICoreAPI api) => api.ChatCommands.GetOrCreate("vsscpmod")
        .WithDescription("Seraph Civilization Patches commands")
        .RequiresPrivilege(Privilege.chat)
        .HandleWith(_ => TextCommandResult.Success(api.Side == EnumAppSide.Server
            ? "/vsscpmod info status|on|off|reload; /vsscpmod fixstack; /vsscpmod trunkrelease"
            : ".vsscpmod inventory start|stop"));
}
