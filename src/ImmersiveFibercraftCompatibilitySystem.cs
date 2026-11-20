using System.Reflection;
using Vintagestory.API.Common;

namespace SeraphCivilizationPatches;

/// <summary>Replaces Immersive Fibercraft's full grid-recipe scan with asset patches.</summary>
public sealed class ImmersiveFibercraftCompatibilitySystem : ModSystem
{
    public override double ExecuteOrder() => 0.1;

    public override void AssetsLoaded(ICoreAPI api)
    {
        if (!api.ModLoader.IsModEnabled("spinningwheel")) return;

        try
        {
            Type? configType = Type.GetType("SpinningWheel.ModConfig.ModConfig, SpinningWheel")
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("SpinningWheel.ModConfig.ModConfig"))
                    .FirstOrDefault(type => type != null);
            PropertyInfo? loadedProperty = configType?.GetProperty("Loaded", BindingFlags.Public | BindingFlags.Static);
            object? config = loadedProperty?.GetValue(null);
            PropertyInfo? disableProperty = config?.GetType().GetProperty("DisableTwineGridRecipes", BindingFlags.Public | BindingFlags.Instance);
            if (disableProperty?.PropertyType != typeof(bool) || config is null || !(bool)(disableProperty.GetValue(config) ?? false)) return;

            disableProperty.SetValue(config, false);
            api.Logger.Notification("[vsscpmod] Immersive Fibercraft twine recipe scan disabled; fixed recipe asset patches are active.");
        }
        catch (Exception error)
        {
            api.Logger.Warning("[vsscpmod] Could not disable Immersive Fibercraft's full recipe scan: {0}", error.Message);
        }
    }
}
