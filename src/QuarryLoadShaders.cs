using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationPatches;

// Share programs across loads; file registration resolves game lighting includes
// and supplies the engine's shadow quality, dynamic light and SSAO defines.
internal sealed class QuarryLoadShaders : IDisposable
{
    internal static QuarryLoadShaders? Current { get; private set; }
    private readonly ICoreClientAPI api;
    private IShaderProgram? opaque, shadow;
    private bool failed;

    internal QuarryLoadShaders(ICoreClientAPI api)
    {
        this.api = api;
        Current = this;
        api.Event.ReloadShader += Reload;
    }

    internal IShaderProgram? Get(bool shadowPass)
    {
        if (failed) return null;
        try
        {
            if (shadowPass) return shadow ??= Create("quarryloadshadow");
            return opaque ??= Create("quarryload");
        }
        catch (Exception e)
        {
            failed = true;
            api.Logger.Error("[vsscpmod] Quarry load shader unavailable: {0}", e);
            return null;
        }
    }

    private IShaderProgram Create(string name)
    {
        var program = api.Shader.NewShaderProgram();
        try
        {
            program.AssetDomain = "vsscpmod";
            api.Shader.RegisterFileShaderProgram(name, program);
            if (!program.Compile()) throw new InvalidOperationException(name + " compilation failed");
            return program;
        }
        catch { program.Dispose(); throw; }
    }

    internal void Prepare(IShaderProgram shader, EntityQuarryDrag load, bool shadowPass)
    {
        shader.UniformMatrix("viewMatrix", shadowPass ? api.Render.CurrentModelviewMatrix : api.Render.CameraMatrixOriginf);
        shader.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);
        if (shadowPass) return;
        shader.Uniform("rgbaTint", ColorUtil.WhiteArgbVec);
        shader.Uniform("rgbaAmbientIn", api.Render.AmbientColor);
        shader.Uniform("rgbaLightIn", api.World.BlockAccessor.GetLightRGBs((int)load.Pos.X, (int)load.Pos.InternalY, (int)load.Pos.Z));
        shader.Uniform("rgbaFogIn", api.Render.FogColor);
        shader.Uniform("fogMinIn", api.Render.FogMin);
        shader.Uniform("fogDensityIn", api.Render.FogDensity);
        // Normal and shadow brightness are combined once in the fragment shader,
        // like terrain. Multiplying both separately makes the back nearly black.
        shader.Uniform("normalShaded", 0);
        shader.Uniform("dontWarpVertices", 0);
        shader.Uniform("rgbaGlowIn", new Vec4f());
        shader.Uniform("extraGlow", 0);
        shader.Uniform("addRenderFlags", 0);
        shader.Uniform("overlayOpacity", 0f);
        shader.Uniform("tempGlowMode", 0);
        shader.Uniform("extraZOffset", 0f);
        shader.Uniform("damageEffect", 0f);
        shader.Uniform("extraGodray", 0f);
        shader.Uniform("skyShaded", 0);
        shader.Uniform("fadeFromSpheresFog", 0);
    }

    private bool Reload()
    {
        opaque?.Dispose(); shadow?.Dispose();
        opaque = shadow = null;
        failed = false;
        return true;
    }

    public void Dispose()
    {
        api.Event.ReloadShader -= Reload;
        Reload();
        if (Current == this) Current = null;
    }
}
