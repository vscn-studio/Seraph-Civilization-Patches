using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SeraphCivilizationPatches;

public sealed class QuarryDragRenderer(Entity entity, ICoreClientAPI api) : EntityRenderer(entity, api)
{
    private readonly EntityQuarryDrag load = (EntityQuarryDrag)entity;
    private readonly ItemSlot slot = new DummySlot();
    private TextureAtlasPosition? texture;
    private MeshData? link;
    private Action<MeshData, Vec3d, Vec3d, double, MeshData>? addChain;
    private MeshRef? wrapping, tether;
    private long tetherUpdated;
    private readonly Matrixf model = new();
    private QuarrySlabLayout? layout;

    public override void DoRender3DOpaque(float dt, bool isShadowPass)
    {
        if (load.Slab?.Collectible == null || capi.World.Player?.Entity?.CameraPos is not { } camera) return;
        slot.Itemstack = load.Slab;
        // Stone Quarry builds its own content-dependent, full-size block mesh.
        // Ignore the dropped-item transform (scale, bobbing and random rotation).
        var info = capi.Render.GetItemStackRenderInfo(slot, EnumItemRenderTarget.Ground, dt);
        if (info.ModelRef == null) return;
        model.Identity().Translate(entity.Pos.X - camera.X, entity.Pos.InternalY - camera.Y, entity.Pos.Z - camera.Z);
        var slabModel = model.Clone().Translate(load.MeshOffset.X, load.MeshOffset.Y, load.MeshOffset.Z);
        EnsureLayout();
        EnsureWrapping();
        var programs = QuarryLoadShaders.Current;
        var shader = programs?.Get(isShadowPass);
        var previousShader = capi.Render.CurrentActiveShader;
        // Keep the original renderer as a fallback for unsupported graphics
        // drivers. Never leave a different shader active in the engine's pass.
        bool custom = shader != null;
        if (isShadowPass && !custom)
        {
            SetShadowMatrix(slabModel);
            previousShader.Uniform("origin", 0f, 0f, 0f);
            capi.Render.RenderMultiTextureMesh(info.ModelRef, "tex2d");
            return;
        }
        previousShader?.Stop();
        shader ??= capi.Render.StandardShader;
        try
        {
            shader.Use();
            programs?.Prepare(shader, load, isShadowPass);
            shader.UniformMatrix("modelMatrix", slabModel.Values);
            if (isShadowPass)
            {
                capi.Render.RenderMultiTextureMesh(info.ModelRef, "tex2d");
                return;
            }
            if (!custom) shader.Uniform("normalShaded", 1);
            shader.Uniform("alphaTest", info.AlphaTest);
            capi.Render.RenderMultiTextureMesh(info.ModelRef, "tex");
            if (texture == null || wrapping == null) return;
            shader.UniformMatrix("modelMatrix", model.Values);
            shader.Uniform("alphaTest", .05f);
            shader.BindTexture2D("tex", texture.atlasTextureId, 0);
            capi.Render.RenderMesh(wrapping);
            var holder = string.IsNullOrEmpty(load.HolderUid) ? null : capi.World.PlayerByUid(load.HolderUid)?.Entity;
            if (holder?.Pos == null || holder.Pos.Dimension != entity.Pos.Dimension) return;
            if (tether == null || capi.World.ElapsedMilliseconds - tetherUpdated >= 50)
            {
                var mesh = new MeshData(256, 384);
                var stone = layout!.Stone;
                var from = new Vec3d((stone.X1 + stone.X2) / 2, stone.Y1 + stone.Height * .8, stone.Z1 - ChainClearance(link));
                var to = new Vec3d(holder.Pos.X - entity.Pos.X, holder.Pos.Y - entity.Pos.Y + .85, holder.Pos.Z - entity.Pos.Z);
                if (to.Length() > 10) return;
                if (load.Chain && link != null) addChain!(mesh, from, to, .1, link);
                else
                {
                    Vec3d previous = from;
                    for (int i = 1; i <= 12; i++)
                    {
                        double t = i / 12d;
                        var next = from.AddCopy(to.SubCopy(from).Mul(t)).Add(0, -.32 * t * (1 - t), 0);
                        Tube(mesh, previous, next); previous = next;
                    }
                }
                tether?.Dispose(); tether = capi.Render.UploadMesh(mesh);
                tetherUpdated = capi.World.ElapsedMilliseconds;
            }
            if (tether != null) capi.Render.RenderMesh(tether);
        }
        finally { shader.Stop(); previousShader?.Use(); }
    }

    private void SetShadowMatrix(Matrixf matrix)
    {
        var mvp = Mat4f.Mul(new float[16], capi.Render.CurrentModelviewMatrix, matrix.Values);
        Mat4f.Mul(mvp, capi.Render.CurrentProjectionMatrix, mvp);
        capi.Render.CurrentActiveShader.UniformMatrix("mvpMatrix", mvp);
    }

    private void EnsureLayout()
    {
        if (layout != null || load.Slab?.Block?.Shape is not { } source) return;
        var shape = Shape.TryGet(capi, new AssetLocation(source.Base.Domain, "shapes/" + source.Base.Path + ".json"));
        if (shape != null) layout = QuarrySlabLayout.Measure(shape,
            new Vec3f(source.rotateX, source.rotateY, source.rotateZ), load.MeshOffset, capi.Tesselator);
    }

    private void EnsureWrapping()
    {
        if (wrapping != null || !load.Chain || layout == null) return;
        var asset = new AssetLocation("game:block/metal/tarnished/iron");
        if (!capi.BlockTextureAtlas.GetOrInsertTexture(asset, out _, out texture) || texture == null) return;
        if (load.Chain)
        {
            var type = AccessTools.TypeByName("VSRestraints.ChainMesh");
            var shape = Shape.TryGet(capi, new AssetLocation("vsrestraints:shapes/ironchain.json"));
            var create = AccessTools.Method(type, "CreateTemplate");
            var add = AccessTools.Method(type, "Add");
            if (shape != null && create != null && add != null)
            {
                link = create.Invoke(null, [shape, capi.Tesselator, texture, capi.BlockTextureAtlas.Size]) as MeshData;
                addChain = add.CreateDelegate<Action<MeshData, Vec3d, Vec3d, double, MeshData>>();
            }
        }
        var mesh = new MeshData(1024, 1536);
        void Segment(Vec3d a, Vec3d b)
        {
            if (load.Chain && link != null) addChain!(mesh, a, b, 0, link);
            else Tube(mesh, a, b);
        }
        BuildWrapping(layout.Stone, ChainClearance(link), Segment);
        wrapping = capi.Render.UploadMesh(mesh);
    }

    // Measure the native link thickness; seat its outer rings into the stone
    // by a fraction of a pixel instead of floating beyond the block boundary.
    internal static double ChainClearance(MeshData? template) => template == null ? .02
        : Enumerable.Range(0, template.VerticesCount).Max(i => Math.Max(Math.Abs(template.xyz[3 * i + 1]), Math.Abs(template.xyz[3 * i + 2]))) - .003;

    internal static void BuildWrapping(Cuboidd stone, double clearance, Action<Vec3d, Vec3d> segment)
    {
        double x1 = stone.X1 - clearance, x2 = stone.X2 + clearance;
        double z1 = stone.Z1 - clearance, z2 = stone.Z2 + clearance;
        double bottom = stone.Y1 + stone.Height * .15, top = stone.Y1 + stone.Height * .8;
        foreach (double y in new[] { bottom, top })
        {
            Vec3d[] ring = [new(x1, y, z1), new(x2, y, z1), new(x2, y, z2), new(x1, y, z2)];
            for (int i = 0; i < 4; i++) segment(ring[i], ring[(i + 1) % 4]);
        }
        void Crossing(Vec3d from, Vec3d to, Vec3d normal)
        {
            var previous = from;
            for (int i = 1; i <= 8; i++)
            {
                double t = i / 8d;
                var next = from.AddCopy(to.SubCopy(from).Mul(t))
                    .Add(normal.Clone().Mul(Math.Pow(Math.Sin(Math.PI * t), 8) * (2 * clearance + .009)));
                segment(previous, next); previous = next;
            }
        }
        // Only arch over the other chain at the crossing; both ends stay snug
        // against the surface, rather than offsetting the complete diagonal.
        foreach (var (faceZ, sign) in new[] { (z1, -1), (z2, 1) })
        {
            segment(new(x1, bottom, faceZ), new(x2, top, faceZ));
            Crossing(new(x1, top, faceZ), new(x2, bottom, faceZ), new(0, 0, sign));
        }
        foreach (var (faceX, sign) in new[] { (x1, -1), (x2, 1) })
        {
            segment(new(faceX, bottom, z1), new(faceX, top, z2));
            Crossing(new(faceX, top, z1), new(faceX, bottom, z2), new(sign, 0, 0));
        }
    }

    private void Tube(MeshData mesh, Vec3d from, Vec3d to)
    {
        var along = to.SubCopy(from).Normalize();
        var across = along.Cross(Math.Abs(along.Y) < .9 ? new Vec3d(0, 1, 0) : new Vec3d(1, 0, 0)).Normalize();
        var normal = along.Cross(across).Normalize();
        for (int face = 0; face < 6; face++)
        {
            double a = face * Math.PI / 3, b = (face + 1) * Math.PI / 3;
            Vec3d Offset(double angle) => across.Clone().Mul(Math.Cos(angle) * .022).Add(normal.Clone().Mul(Math.Sin(angle) * .022));
            var oa = Offset(a); var ob = Offset(b);
            Vec3d[] points = [from.AddCopy(oa), to.AddCopy(oa), to.AddCopy(ob), from.AddCopy(ob)];
            int first = mesh.VerticesCount;
            var faceNormal = oa.AddCopy(ob).Normalize();
            int flags = VertexFlags.PackNormal((float)faceNormal.X, (float)faceNormal.Y, (float)faceNormal.Z);
            for (int i = 0; i < 4; i++)
                mesh.AddVertexWithFlags((float)points[i].X, (float)points[i].Y, (float)points[i].Z,
                    i is 0 or 3 ? texture!.x1 : texture!.x2, i < 2 ? texture.y1 : texture.y2, ColorUtil.WhiteArgb, flags);
            mesh.AddIndices(first, first + 1, first + 2, first, first + 2, first + 3);
            mesh.AddTextureId(texture!.atlasTextureId);
        }
    }
    public override void Dispose()
    {
        wrapping?.Dispose(); wrapping = null;
        tether?.Dispose(); tether = null;
        // The slab's mesh belongs to Stone Quarry's shared cache.
    }
}
