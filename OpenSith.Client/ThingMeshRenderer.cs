using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OpenSith.Core.Animation;
using OpenSith.Core.Models;
using OpenSith.Core.Simulation;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;
using Silk.NET.OpenGL;

namespace OpenSith.Client;

/// <summary>
/// GPU renderer for 3DO meshes (props, doors, enemies, pickups) placed in the level.
/// Supports both static meshes and real-time hierarchical skeletal keyframe animations (.KEY / .PUP).
/// </summary>
public sealed class ThingMeshRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly IVirtualFileSystem _vfs;
    private readonly TextureManager _textureManager;

    private readonly struct Vertex
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public readonly Vector2 TexCoord;
        public readonly float Intensity;

        public Vertex(Vector3 pos, Vector3 norm, Vector2 uv, float intensity)
        {
            Position = pos;
            Normal = norm;
            TexCoord = uv;
            Intensity = intensity;
        }
    }

    private readonly Dictionary<string, ThreeDoGpuModel?> _modelCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ThreeDoModel?> _parsedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PuppetDefinition?> _puppetCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, KeyframeTrack?> _keyframeCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<int, AnimatedThingGpu> _animatedInstances = new();
    private readonly Dictionary<int, (string Action, int Mode, bool Loop)> _desiredAnimations = new();
    private readonly Dictionary<int, (string KeyframeName, int Mode, bool Loop)> _pendingKeyframes = new();
    private readonly Dictionary<int, float> _pendingDistances = new();
    private readonly Dictionary<string, AnimatedThingGpu> _viewmodelInstances = new(StringComparer.OrdinalIgnoreCase);
    private ThreeDoGpuModel? _fallbackBoltModel;
    private float _globalTime = 0.0f;

    public sealed class MaterialBatch
    {
        public string MaterialName { get; }
        public int ElementStart { get; }
        public int ElementCount { get; }
        public bool IsTranslucent { get; }

        public MaterialBatch(string materialName, int elementStart, int elementCount, bool isTranslucent = false)
        {
            MaterialName = materialName;
            ElementStart = elementStart;
            ElementCount = elementCount;
            IsTranslucent = isTranslucent;
        }
    }

    private sealed class MaterialBatchKeyComparer : IEqualityComparer<(string MaterialName, bool IsTranslucent)>
    {
        public static readonly MaterialBatchKeyComparer Instance = new();

        public bool Equals((string MaterialName, bool IsTranslucent) x, (string MaterialName, bool IsTranslucent) y)
            => x.IsTranslucent == y.IsTranslucent &&
               StringComparer.OrdinalIgnoreCase.Equals(x.MaterialName, y.MaterialName);

        public int GetHashCode((string MaterialName, bool IsTranslucent) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.MaterialName), obj.IsTranslucent);
    }

    public sealed class ThreeDoGpuModel
    {
        public uint Vao { get; }
        public uint Vbo { get; }
        public uint Ebo { get; }
        public List<MaterialBatch> Batches { get; }

        public ThreeDoGpuModel(uint vao, uint vbo, uint ebo, List<MaterialBatch> batches)
        {
            Vao = vao;
            Vbo = vbo;
            Ebo = ebo;
            Batches = batches;
        }
    }

    private sealed class AnimatedVertexInfo
    {
        public int NodeIndex;
        public Vector3 LocalPos;
        public Vector3 LocalNorm;
        public Vector2 Uv;
        public float Intensity;
    }

    private sealed class AnimatedThingGpu
    {
        public uint Vao;
        public uint Vbo;
        public uint Ebo;
        public List<MaterialBatch> Batches = new();
        public string ModelName = string.Empty;
        public ThreeDoModel Model = null!;
        public PuppetDefinition? Puppet;
        public KeyframeTrack Keyframe = null!;
        public AnimatedVertexInfo[] VertexInfos = [];
        public Vertex[] CpuVertices = [];
        public string Action = "stand";
        public int Mode;
        public bool Loop = true;
        public int SubmodeFlags;
        public float PendingDistanceMoved;
        public float AnimTime;
    }

    public ThingMeshRenderer(GL gl, IVirtualFileSystem vfs, TextureManager textureManager)
    {
        _gl = gl;
        _vfs = vfs;
        _textureManager = textureManager;
    }

    /// <summary>
    /// Computes animation time delta (in seconds) from horizontal distance moved using
    /// LucasArts Sith Engine's <c>sithPuppet_UpdatePuppet</c> factor (<c>distanceMoved * 280.0f</c> frames/JK unit).
    /// </summary>
    public static float ComputeVelocityDrivenTimeDelta(float distanceMoved, float fps)
        => (MathF.Abs(distanceMoved) * 280.0f) / MathF.Max(1.0f, fps);

    /// <summary>
    /// Records horizontal distance moved this tick for velocity-driven puppet animations (submode flag 0x01).
    /// </summary>
    public void SetThingDistanceMoved(int thingIndex, float distance)
    {
        float absDist = MathF.Abs(distance);
        if (_animatedInstances.TryGetValue(thingIndex, out var anim))
        {
            anim.PendingDistanceMoved += absDist;
        }
        else
        {
            _pendingDistances[thingIndex] = _pendingDistances.GetValueOrDefault(thingIndex) + absDist;
        }
    }

    public void Update(float dt)
    {
        _globalTime += dt;
        foreach (var anim in _animatedInstances.Values)
        {
            if ((anim.SubmodeFlags & 0x01) != 0)
            {
                anim.AnimTime += ComputeVelocityDrivenTimeDelta(anim.PendingDistanceMoved, anim.Keyframe.Fps);
            }
            else
            {
                anim.AnimTime += dt;
            }

            anim.PendingDistanceMoved = 0f;
        }
        _pendingDistances.Clear();
    }

    /// <summary>
    /// Sets or updates the active puppet animation action (e.g. "stand", "walk", "fire", "death") for a specific Thing.
    /// </summary>
    public void SetThingAnimation(int thingIndex, string action, int mode = 0, bool loop = true)
    {
        _desiredAnimations[thingIndex] = (action, mode, loop);

        if (!_animatedInstances.TryGetValue(thingIndex, out var anim) || anim.Puppet == null)
            return;

        if (anim.Action.Equals(action, StringComparison.OrdinalIgnoreCase) &&
            anim.Mode == mode &&
            anim.Loop == loop)
        {
            return;
        }

        if (!ResolvePuppetSubmode(anim.Puppet, mode, action, out string? keyName, out int submodeFlags))
            return;

        var keyframe = !string.IsNullOrEmpty(keyName) ? GetOrCreateKeyframe(keyName) : null;
        if (keyframe != null)
        {
            anim.Keyframe = keyframe;
            anim.Action = action;
            anim.Mode = mode;
            anim.Loop = loop;
            anim.SubmodeFlags = submodeFlags;
            anim.AnimTime = 0.0f;
        }
    }

    /// <summary>
    /// Directly plays a named keyframe animation (analogous to COG PlayKey) on a Thing,
    /// used both for weapons like the lightsaber and for non-puppet 3DOs (e.g. TIE bomber takeoff).
    /// </summary>
    public void SetThingKeyframe(int thingIndex, string keyframeName, int mode = 0, bool loop = false)
    {
        if (string.IsNullOrEmpty(keyframeName))
            return;

        if (!_animatedInstances.TryGetValue(thingIndex, out var anim))
        {
            _pendingKeyframes[thingIndex] = (keyframeName, mode, loop);
            return;
        }

        _pendingKeyframes.Remove(thingIndex);

        if (anim.Action.Equals(keyframeName, StringComparison.OrdinalIgnoreCase) &&
            anim.Mode == mode &&
            anim.Loop == loop)
        {
            return;
        }

        var keyframe = GetOrCreateKeyframe(keyframeName);
        if (keyframe != null)
        {
            anim.Keyframe = keyframe;
            anim.Action = keyframeName;
            anim.Mode = mode;
            anim.Loop = loop;
            anim.SubmodeFlags = 0;
            anim.AnimTime = 0.0f;
        }
    }

    /// <summary>
    /// Determines whether a Thing should be rendered in the world.
    /// Things with <see cref="SithThingFlags.Disabled"/>, <see cref="SithThingFlags.Destroyed"/>,
    /// or <see cref="SithThingFlags.Invisible"/> are skipped, and the local player thing
    /// (<paramref name="playerThingIndex"/> &gt;= 0) is skipped in first-person view.
    /// </summary>
    public static bool ShouldRenderThing(JklThing thing, int playerThingIndex)
    {
        const uint HiddenFlags = (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed | SithThingFlags.Invisible);
        if ((thing.ThingFlags & HiddenFlags) != 0)
            return false;

        if (playerThingIndex >= 0 && thing.ThingIndex == playerThingIndex)
            return false;

        return true;
    }

    public void RenderThings(ShaderProgram shader, JklLevel level, int playerThingIndex)
    {
        foreach (var thing in level.Things)
        {
            if (!ShouldRenderThing(thing, playerThingIndex))
                continue;

            string modelName = thing.Model3D;
            if (string.IsNullOrEmpty(modelName))
            {
                if (level.Templates.TryGetValue(thing.TemplateName, out var tmpl) && !string.IsNullOrEmpty(tmpl.Model3D))
                {
                    modelName = tmpl.Model3D;
                }
            }

            if (string.IsNullOrEmpty(modelName))
                continue;

            // Compute thing world model matrix using authentic LucasArts Sith Engine rdMatrix_Build34
            Matrix4x4 modelMatrix = SithMatrix34.Build34(thing.PitchYawRoll, thing.Position).ToMatrix4x4();
            shader.SetUniform("uModel", modelMatrix);

            // Check if this thing has an animated puppet / keyframe
            var animGpu = GetOrCreateAnimatedInstance(thing, modelName, level);
            if (animGpu != null)
            {
                float sampleTime = GetAnimatedSampleTime(animGpu);
                UpdateAndDrawAnimatedInstance(shader, animGpu, sampleTime);
            }
            else if (!_animatedInstances.ContainsKey(thing.ThingIndex) &&
                     !_pendingKeyframes.ContainsKey(thing.ThingIndex))
            {
                var gpuModel = GetOrCreateGpuModel(modelName);
                if (gpuModel != null)
                {
                    DrawGpuModel(shader, gpuModel.Vao, gpuModel.Batches);
                }
            }
        }
    }

    /// <summary>
    /// Renders a third-person held weapon mesh (e.g. <c>rptg.3do</c>, <c>bryg.3do</c>, <c>sabg.3do</c>)
    /// attached to the actor's primary weapon joint (<c>k_rhand</c> / PUP joint 3 / node 12).
    /// </summary>
    public void RenderAttachedWeaponMesh(ShaderProgram shader, JklThing thing, string? weaponModelName)
    {
        if (string.IsNullOrWhiteSpace(weaponModelName))
            return;

        var gpuModel = GetOrCreateGpuModel(weaponModelName);
        if (gpuModel == null)
            return;

        Matrix4x4 thingWorld = SithMatrix34.Build34(thing.PitchYawRoll, thing.Position).ToMatrix4x4();
        Matrix4x4 weaponWorld = TryGetThingPrimaryWeaponNodeTransform(thing.ThingIndex, out Matrix4x4 handLocal)
            ? handLocal * thingWorld
            : Matrix4x4.CreateTranslation(0.0104f, 0.0557f, -0.0129f) * thingWorld;

        shader.SetUniform("uModel", weaponWorld);
        shader.SetUniform("uAlpha", 1.0f);
        DrawGpuModel(shader, gpuModel.Vao, gpuModel.Batches);
        shader.SetUniform("uModel", Matrix4x4.Identity);
    }

    public const float ViewmodelVerticalFovDegrees = 73.739795f;

    /// <summary>
    /// Renders a first-person weapon viewmodel (e.g. "bryv.3do" or "sabv.3do") in camera/view space.
    /// </summary>
    public void RenderViewmodel(
        ShaderProgram shader,
        Camera camera,
        string modelName,
        string keyframeName,
        float animTime,
        float aspectRatio,
        Vector3 localOffset = default)
    {
        var keyframe = GetOrCreateKeyframe(keyframeName);
        if (!_viewmodelInstances.TryGetValue(modelName, out var vmGpu))
        {
            var threeDo = GetOrCreateThreeDoModel(modelName);
            if (threeDo == null || threeDo.Nodes.Count == 0 || keyframe == null)
                return;

            vmGpu = BuildAnimatedThingGpu(threeDo, keyframe);
            _viewmodelInstances[modelName] = vmGpu;
        }
        else if (keyframe != null)
        {
            vmGpu.Keyframe = keyframe;
        }

        // Clear depth buffer so first-person weapon never clips into nearby walls
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);

        // Retail Jedi Knight uses 4:3 90-deg horizontal FOV (vertical FOV = 2*atan(0.75) = 73.7398 deg)
        // with -eyeOffset (0, 0, -0.037), which frames the weapon barrel in bottom-right while
        // keeping the hand/forearm below the bottom viewport edge (NDC Y < -1.0).
        float fovRad = MathF.PI / 180.0f * ViewmodelVerticalFovDegrees;
        Matrix4x4 vmProjection = Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspectRatio, 0.005f, 10.0f);
        shader.SetUniform("uProjection", vmProjection);
        shader.SetUniform("uView", camera.GetViewMatrix());
        shader.SetUniform("uModel", BuildViewmodelWorldMatrix(camera, localOffset));
        shader.SetUniform("uTint", Vector3.Zero);
        shader.SetUniform("uAlpha", 1.0f);

        UpdateAndDrawAnimatedInstance(shader, vmGpu, animTime);

        shader.SetUniform("uProjection", camera.GetProjectionMatrix(aspectRatio));
    }

    /// <summary>
    /// Builds the world-space transform matrix for a first-person weapon viewmodel placed relative to the camera.
    /// </summary>
    public static Matrix4x4 BuildViewmodelWorldMatrix(Camera camera, Vector3 localOffset)
    {
        Vector3 pyr = new Vector3(camera.Pitch, camera.Yaw, 0.0f);
        Vector3 worldOffset = camera.Right * localOffset.X + camera.Forward * localOffset.Y + camera.Up * localOffset.Z;
        return SithMatrix34.Build34(pyr, camera.Position + worldOffset).ToMatrix4x4();
    }

    /// <summary>
    /// Evaluates the local skeletal hierarchy transform of a single node for a 3DO model and keyframe track.
    /// </summary>
    public static bool TryComputeNodeTransform(
        ThreeDoModel? model,
        KeyframeTrack? keyframe,
        float animTime,
        int nodeIndex,
        out Matrix4x4 nodeLocalTransform)
    {
        if (model == null || nodeIndex < 0 || nodeIndex >= model.Nodes.Count)
        {
            nodeLocalTransform = Matrix4x4.Identity;
            return false;
        }

        Matrix4x4[] nodeTransforms = ThreeDoHierarchy.ComputeTransforms(model, keyframe, animTime);
        if (nodeIndex >= nodeTransforms.Length)
        {
            nodeLocalTransform = Matrix4x4.Identity;
            return false;
        }

        nodeLocalTransform = nodeTransforms[nodeIndex];
        return true;
    }

    /// <summary>
    /// Gets the evaluated local skeletal transform of <paramref name="nodeIndex"/> for a first-person viewmodel.
    /// </summary>
    public bool TryGetViewmodelNodeTransform(
        string modelName,
        string keyframeName,
        float animTime,
        int nodeIndex,
        out Matrix4x4 nodeLocalTransform)
    {
        var model = GetOrCreateThreeDoModel(modelName);
        var keyframe = GetOrCreateKeyframe(keyframeName);
        return TryComputeNodeTransform(model, keyframe, animTime, nodeIndex, out nodeLocalTransform);
    }

    /// <summary>
    /// Gets the evaluated local skeletal transform of <paramref name="nodeIndex"/> for an animated world Thing.
    /// </summary>
    public bool TryGetThingNodeTransform(int thingIndex, int nodeIndex, out Matrix4x4 nodeLocalTransform)
    {
        if (!_animatedInstances.TryGetValue(thingIndex, out var anim))
        {
            nodeLocalTransform = Matrix4x4.Identity;
            return false;
        }

        float sampleTime = GetAnimatedSampleTime(anim);
        return TryComputeNodeTransform(anim.Model, anim.Keyframe, sampleTime, nodeIndex, out nodeLocalTransform);
    }

    /// <summary>
    /// Gets the evaluated local skeletal transform of the primary weapon joint
    /// (PUP <c>JOINTTYPE_PRIMARYWEAP</c> = 3, falling back to node 12 if unmapped).
    /// </summary>
    public bool TryGetThingPrimaryWeaponNodeTransform(int thingIndex, out Matrix4x4 transform)
    {
        if (!_animatedInstances.TryGetValue(thingIndex, out var anim))
        {
            transform = Matrix4x4.Identity;
            return false;
        }

        int nodeIdx = anim.Puppet?.Joints.TryGetValue(3, out int mappedNode) == true ? mappedNode : 12;
        return TryGetThingNodeTransform(thingIndex, nodeIdx, out transform);
    }

    private static float GetAnimatedSampleTime(AnimatedThingGpu anim)
    {
        return anim.Loop
            ? anim.AnimTime
            : MathF.Min(anim.AnimTime, MathF.Max(0f, anim.Keyframe.Duration - 0.001f));
    }

    /// <summary>
    /// Renders active 3D blaster projectiles using authentic retail 3DO meshes at 1:1 scale.
    /// </summary>
    public unsafe void RenderProjectiles(ShaderProgram shader, IReadOnlyList<WorldSimulation.Projectile> projectiles)
    {
        if (projectiles.Count == 0)
            return;

        bool wasDepthTestEnabled = _gl.IsEnabled(EnableCap.DepthTest);
        bool wasBlendEnabled = _gl.IsEnabled(EnableCap.Blend);
        bool wasCullEnabled = _gl.IsEnabled(EnableCap.CullFace);
        _gl.GetBoolean(GetPName.DepthWritemask, out bool prevDepthMask);
        _gl.GetInteger(GetPName.BlendSrcRgb, out int prevSrcRgb);
        _gl.GetInteger(GetPName.BlendDstRgb, out int prevDstRgb);
        _gl.GetInteger(GetPName.BlendSrcAlpha, out int prevSrcAlpha);
        _gl.GetInteger(GetPName.BlendDstAlpha, out int prevDstAlpha);
        _gl.GetInteger(GetPName.VertexArrayBinding, out int prevVao);
        _gl.GetInteger(GetPName.ActiveTexture, out int prevActiveTexture);
        _gl.GetInteger(GetPName.TextureBinding2D, out int prevTexture2D);

        try
        {
            _gl.Enable(EnableCap.DepthTest);

            foreach (var proj in projectiles)
            {
                var gpuModel = !string.IsNullOrEmpty(proj.ModelName)
                    ? GetOrCreateGpuModel(proj.ModelName, forceFullbright: true)
                    : null;

                if (gpuModel == null)
                    continue;

                Vector3 dir = proj.Direction.LengthSquared() > 1e-6f
                    ? Vector3.Normalize(proj.Direction)
                    : Vector3.UnitY;

                float yawDeg = MathF.Atan2(-dir.X, dir.Y) * (180f / MathF.PI);
                float pitchDeg = MathF.Asin(Math.Clamp(dir.Z, -1f, 1f)) * (180f / MathF.PI);

                Matrix4x4 world = SithMatrix34.Build34(new Vector3(pitchDeg, yawDeg, 0f), proj.Position).ToMatrix4x4();
                shader.SetUniform("uModel", world);
                shader.SetUniform("uTint", Vector3.Zero);
                shader.SetUniform("uAlpha", 1.0f);

                _gl.Disable(EnableCap.Blend);
                _gl.Disable(EnableCap.CullFace);
                _gl.DepthMask(true);
                DrawGpuModel(shader, gpuModel.Vao, gpuModel.Batches);
            }
        }
        finally
        {
            shader.SetUniform("uTint", Vector3.Zero);
            shader.SetUniform("uAlpha", 1.0f);
            _gl.BindVertexArray((uint)prevVao);
            _gl.ActiveTexture((TextureUnit)prevActiveTexture);
            _gl.BindTexture(TextureTarget.Texture2D, (uint)prevTexture2D);
            _gl.DepthMask(prevDepthMask);
            _gl.BlendFuncSeparate(
                (BlendingFactor)prevSrcRgb,
                (BlendingFactor)prevDstRgb,
                (BlendingFactor)prevSrcAlpha,
                (BlendingFactor)prevDstAlpha);

            if (wasBlendEnabled)
                _gl.Enable(EnableCap.Blend);
            else
                _gl.Disable(EnableCap.Blend);

            if (wasCullEnabled)
                _gl.Enable(EnableCap.CullFace);
            else
                _gl.Disable(EnableCap.CullFace);

            if (wasDepthTestEnabled)
                _gl.Enable(EnableCap.DepthTest);
            else
                _gl.Disable(EnableCap.DepthTest);
        }
    }

    private unsafe ThreeDoGpuModel UploadStaticBoltMesh(Vertex[] verts, uint[] indices)
    {
        uint vao = _gl.GenVertexArray();
        uint vbo = _gl.GenBuffer();
        uint ebo = _gl.GenBuffer();

        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (Vertex* vPtr = verts)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(Vertex)), vPtr, BufferUsageARB.StaticDraw);
        }

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (uint* iPtr = indices)
        {
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), iPtr, BufferUsageARB.StaticDraw);
        }

        uint stride = (uint)sizeof(Vertex);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)12);
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)24);
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)32);
        _gl.BindVertexArray(0);

        return new ThreeDoGpuModel(vao, vbo, ebo, [new MaterialBatch(string.Empty, 0, indices.Length)]);
    }

    /// <summary>
    /// Renders glowing impact sparks from blaster wall/NPC hits.
    /// </summary>
    public void RenderSparks(ShaderProgram shader, IReadOnlyList<WorldSimulation.ImpactSpark> sparks)
    {
        if (sparks.Count == 0)
            return;

        var sparkModel = GetOrCreateFallbackBoltModel();
        foreach (var spark in sparks)
        {
            float lifeFrac = Math.Clamp(spark.Life / MathF.Max(0.001f, spark.MaxLife), 0f, 1f);
            Vector3 dir = spark.Velocity.LengthSquared() > 1e-6f
                ? Vector3.Normalize(spark.Velocity)
                : Vector3.UnitZ;

            float yawDeg = MathF.Atan2(-dir.X, dir.Y) * (180f / MathF.PI);
            float pitchDeg = MathF.Asin(Math.Clamp(dir.Z, -1f, 1f)) * (180f / MathF.PI);

            Matrix4x4 scale = Matrix4x4.CreateScale(0.28f * lifeFrac, 0.45f * lifeFrac, 0.28f * lifeFrac);
            Matrix4x4 world = SithMatrix34.Build34(new Vector3(pitchDeg, yawDeg, 0f), spark.Position).ToMatrix4x4();
            shader.SetUniform("uModel", scale * world);
            shader.SetUniform("uTint", new Vector3(1.0f, 0.72f * lifeFrac, 0.15f * lifeFrac));

            DrawGpuModel(shader, sparkModel.Vao, sparkModel.Batches);
        }

        shader.SetUniform("uTint", Vector3.Zero);
    }

    /// <summary>
    /// Renders camera-facing textured sprite/explosion visual effects (<see cref="WorldSimulation.VisualEffect"/>).
    /// </summary>
    public unsafe void RenderVisualEffects(ShaderProgram shader, Camera camera, IReadOnlyList<WorldSimulation.VisualEffect> effects)
    {
        if (effects.Count == 0)
            return;

        bool wasDepthTestEnabled = _gl.IsEnabled(EnableCap.DepthTest);
        bool wasBlendEnabled = _gl.IsEnabled(EnableCap.Blend);
        bool wasCullEnabled = _gl.IsEnabled(EnableCap.CullFace);
        _gl.GetBoolean(GetPName.DepthWritemask, out bool prevDepthMask);
        _gl.GetInteger(GetPName.BlendSrcRgb, out int prevSrcRgb);
        _gl.GetInteger(GetPName.BlendDstRgb, out int prevDstRgb);
        _gl.GetInteger(GetPName.BlendSrcAlpha, out int prevSrcAlpha);
        _gl.GetInteger(GetPName.BlendDstAlpha, out int prevDstAlpha);
        _gl.GetInteger(GetPName.VertexArrayBinding, out int prevVao);
        _gl.GetInteger(GetPName.ActiveTexture, out int prevActiveTexture);
        _gl.GetInteger(GetPName.TextureBinding2D, out int prevTexture2D);

        try
        {
            var quadModel = GetOrCreateEffectQuadModel();
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.Blend);
            _gl.Disable(EnableCap.CullFace);
            _gl.DepthMask(false);
            _gl.BindVertexArray(quadModel.Vao);
            _gl.ActiveTexture(TextureUnit.Texture0);
            shader.SetUniform("uTexture", 0);
            shader.SetUniform("uAlphaTest", 0.0f);

            Vector3 right = camera.Right;
            Vector3 up = camera.Up;
            Vector3 back = -camera.Forward;

            foreach (var fx in effects)
            {
                float lifeFrac = Math.Clamp(fx.Age / MathF.Max(0.01f, fx.Duration), 0f, 1f);
                if (fx.IsAdditive)
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                else
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

                switch (fx.Kind)
                {
                    case WorldSimulation.VisualEffectKind.ParticleRing:
                        RenderParticleRingEffect(shader, quadModel, right, up, back, fx, lifeFrac);
                        break;

                    case WorldSimulation.VisualEffectKind.BeamTrail:
                        RenderBeamTrailEffect(shader, camera, quadModel, right, up, back, fx, lifeFrac);
                        break;

                    default:
                        RenderBillboardEffect(shader, quadModel, right, up, back, fx, lifeFrac);
                        break;
                }
            }
        }
        finally
        {
            shader.SetUniform("uModel", Matrix4x4.Identity);
            shader.SetUniform("uTint", Vector3.Zero);
            shader.SetUniform("uAlpha", 1.0f);
            shader.SetUniform("uAlphaTest", 0.0f);
            _gl.BindVertexArray((uint)prevVao);
            _gl.ActiveTexture((TextureUnit)prevActiveTexture);
            _gl.BindTexture(TextureTarget.Texture2D, (uint)prevTexture2D);
            _gl.DepthMask(prevDepthMask);
            _gl.BlendFuncSeparate(
                (BlendingFactor)prevSrcRgb,
                (BlendingFactor)prevDstRgb,
                (BlendingFactor)prevSrcAlpha,
                (BlendingFactor)prevDstAlpha);

            if (wasBlendEnabled)
                _gl.Enable(EnableCap.Blend);
            else
                _gl.Disable(EnableCap.Blend);

            if (wasCullEnabled)
                _gl.Enable(EnableCap.CullFace);
            else
                _gl.Disable(EnableCap.CullFace);

            if (wasDepthTestEnabled)
                _gl.Enable(EnableCap.DepthTest);
            else
                _gl.Disable(EnableCap.DepthTest);
        }
    }

    private unsafe void RenderBillboardEffect(
        ShaderProgram shader,
        ThreeDoGpuModel quadModel,
        Vector3 right,
        Vector3 up,
        Vector3 back,
        WorldSimulation.VisualEffect fx,
        float lifeFrac)
    {
        var cels = _textureManager.GetOrCreateTextureCels(fx.MaterialName);
        int celIdx = cels.Length > 1 ? Math.Clamp((int)(lifeFrac * cels.Length), 0, cels.Length - 1) : 0;
        float alpha = cels.Length > 1
            ? Math.Clamp(1.0f - MathF.Max(0f, lifeFrac - 0.65f) / 0.35f, 0.1f, 1.0f)
            : Math.Clamp(1.0f - lifeFrac, 0.05f, 1.0f);
        float scaleCurve = cels.Length > 1 ? (0.85f + lifeFrac * 0.35f) : (0.65f + lifeFrac * 0.55f);
        float w = MathF.Max(0.05f, fx.Width * scaleCurve);
        float h = MathF.Max(0.05f, fx.Height * scaleCurve);

        DrawEffectQuad(shader, quadModel, cels[celIdx].Handle, fx.Position, right * w, up * h, back, fx.ColorTint, alpha);
    }

    private void RenderParticleRingEffect(
        ShaderProgram shader,
        ThreeDoGpuModel quadModel,
        Vector3 right,
        Vector3 up,
        Vector3 back,
        WorldSimulation.VisualEffect fx,
        float lifeFrac)
    {
        uint texHandle = string.IsNullOrWhiteSpace(fx.MaterialName)
            ? _textureManager.DefaultWhiteTexture
            : _textureManager.GetOrCreateTexture(fx.MaterialName).Handle;
        uint whiteHandle = _textureManager.DefaultWhiteTexture;

        float ease = 1.0f - MathF.Pow(1.0f - lifeFrac, 2.2f);
        float maxRadius = MathF.Max(0.15f, fx.ExpandRadius);
        float alpha = Math.Clamp((1.0f - lifeFrac) * 1.15f, 0.05f, 1.0f);
        float size = MathF.Max(0.025f, fx.Width * (1.25f - 0.55f * lifeFrac));
        bool isFlatRing = fx.PitchSpreadDeg <= 15f;
        int count = Math.Clamp(fx.ParticleCount > 0 ? fx.ParticleCount : 48, 12, 96);
        float spreadRad = fx.PitchSpreadDeg * (MathF.PI / 180f);
        int baseSeed = HashEffectSeed(fx.Position);

        for (int i = 0; i < count; i++)
        {
            uint h0 = NextHash((uint)(baseSeed + i * 7919));
            uint h1 = NextHash(h0);
            uint h2 = NextHash(h1);
            float yaw = (i / (float)count) * MathF.Tau + ((h0 & 0x3FF) / 1024f - 0.5f) * 0.45f;
            float pitch = isFlatRing
                ? (((h1 & 0x3FF) / 1024f - 0.5f) * 2f * spreadRad)
                : (MathF.Asin(Math.Clamp(((h1 & 0x3FF) / 512f) - 1f, -0.99f, 0.99f)));
            float radialScale = isFlatRing ? (0.65f + ((h2 & 0xFF) / 255f) * 0.45f) : (0.35f + ((h2 & 0xFF) / 255f) * 0.70f);
            float cosP = MathF.Cos(pitch);
            Vector3 dir = new(cosP * MathF.Cos(yaw), cosP * MathF.Sin(yaw), MathF.Sin(pitch));
            Vector3 pPos = fx.Position + dir * (maxRadius * ease * radialScale);

            DrawEffectQuad(shader, quadModel, whiteHandle, pPos, right * size, up * size, back, fx.ColorTint, alpha * 0.9f);
        }

        // Concussion-style outer expanding shockwave halo ring
        if (isFlatRing)
        {
            const int ringDots = 24;
            float ringR = maxRadius * ease;
            float dotSize = size * 1.4f;
            uint haloTex = texHandle != 0 ? texHandle : whiteHandle;
            for (int i = 0; i < ringDots; i++)
            {
                float a = (i / (float)ringDots) * MathF.Tau;
                Vector3 pPos = fx.Position + new Vector3(MathF.Cos(a) * ringR, MathF.Sin(a) * ringR, 0f);
                DrawEffectQuad(shader, quadModel, haloTex, pPos, right * dotSize, up * dotSize, back, fx.ColorTint, alpha * 0.75f);
            }
        }
    }

    private unsafe void RenderBeamTrailEffect(
        ShaderProgram shader,
        Camera camera,
        ThreeDoGpuModel quadModel,
        Vector3 right,
        Vector3 up,
        Vector3 back,
        WorldSimulation.VisualEffect fx,
        float lifeFrac)
    {
        Vector3 delta = fx.EndPosition - fx.Position;
        float len = delta.Length();
        if (len < 1e-4f)
            return;

        Vector3 dir = delta / len;
        Vector3 ortho1 = Vector3.Cross(dir, Vector3.UnitZ);
        if (ortho1.LengthSquared() < 1e-5f)
            ortho1 = Vector3.Cross(dir, Vector3.UnitX);
        ortho1 = Vector3.Normalize(ortho1);
        Vector3 ortho2 = Vector3.Normalize(Vector3.Cross(dir, ortho1));

        uint whiteTex = _textureManager.DefaultWhiteTexture;
        float alpha = Math.Clamp(1.0f - lifeFrac * 0.85f, 0.1f, 1.0f);

        if (fx.JitterRadius > 0.001f)
        {
            int steps = Math.Clamp((int)(len / MathF.Max(0.06f, fx.TrailSegmentSize)), 4, 28);
            int timeBucket = (int)(fx.Age * 33f);
            int baseSeed = HashEffectSeed(fx.Position) ^ (timeBucket * 48611);

            for (int bolt = 0; bolt < 2; bolt++)
            {
                Vector3 prev = fx.Position;
                for (int s = 1; s <= steps; s++)
                {
                    float t = s / (float)steps;
                    Vector3 pt = Vector3.Lerp(fx.Position, fx.EndPosition, t);
                    if (s < steps)
                    {
                        uint h1 = NextHash((uint)(baseSeed + bolt * 997 + s * 313));
                        uint h2 = NextHash(h1);
                        float envelope = MathF.Sin(t * MathF.PI);
                        float j1 = (((h1 & 0x3FF) / 512f) - 1f) * fx.JitterRadius * envelope * (bolt == 0 ? 1.0f : 1.5f);
                        float j2 = (((h2 & 0x3FF) / 512f) - 1f) * fx.JitterRadius * envelope * (bolt == 0 ? 1.0f : 1.5f);
                        pt += ortho1 * j1 + ortho2 * j2;
                    }

                    DrawBeamRibbon(shader, quadModel, camera, whiteTex, prev, pt, 0.032f, fx.ColorTint, alpha * 0.85f);
                    DrawBeamRibbon(shader, quadModel, camera, whiteTex, prev, pt, 0.011f, Vector3.One, alpha);
                    prev = pt;
                }
            }
            return;
        }

        // Concussion secondary wave: glowing cyan core beam + perpendicular expanding shockwave rings
        DrawBeamRibbon(shader, quadModel, camera, whiteTex, fx.Position, fx.EndPosition, 0.055f * (1.2f - 0.5f * lifeFrac), fx.ColorTint, alpha * 0.9f);
        DrawBeamRibbon(shader, quadModel, camera, whiteTex, fx.Position, fx.EndPosition, 0.020f, Vector3.One, alpha);

        float spacing = MathF.Max(0.22f, fx.TrailSegmentSize);
        int ringCount = Math.Clamp((int)(len / spacing), 2, 24);
        float ringRadius = 0.03f + lifeFrac * 0.19f;
        float dotSize = 0.045f * (1.0f - 0.4f * lifeFrac);
        for (int r = 1; r <= ringCount; r++)
        {
            Vector3 center = Vector3.Lerp(fx.Position, fx.EndPosition, r / (float)(ringCount + 1));
            const int dotsPerRing = 8;
            for (int d = 0; d < dotsPerRing; d++)
            {
                float a = (d / (float)dotsPerRing) * MathF.Tau;
                Vector3 dotPos = center + (ortho1 * MathF.Cos(a) + ortho2 * MathF.Sin(a)) * ringRadius;
                DrawEffectQuad(shader, quadModel, whiteTex, dotPos, right * dotSize, up * dotSize, back, fx.ColorTint, alpha * 0.85f);
            }
        }
    }

    private unsafe void DrawBeamRibbon(
        ShaderProgram shader,
        ThreeDoGpuModel quadModel,
        Camera camera,
        uint texHandle,
        Vector3 p0,
        Vector3 p1,
        float width,
        Vector3 tint,
        float alpha)
    {
        Vector3 seg = p1 - p0;
        if (seg.LengthSquared() < 1e-8f)
            return;

        Vector3 mid = (p0 + p1) * 0.5f;
        Vector3 tangent = Vector3.Normalize(seg);
        Vector3 toCam = camera.Position - mid;
        Vector3 viewDir = toCam.LengthSquared() > 1e-6f ? Vector3.Normalize(toCam) : camera.Forward;
        Vector3 side = Vector3.Cross(viewDir, tangent);
        if (side.LengthSquared() < 1e-5f)
            side = Vector3.Cross(camera.Right, tangent);
        side = side.LengthSquared() > 1e-5f ? Vector3.Normalize(side) : camera.Right;
        Vector3 faceNorm = Vector3.Normalize(Vector3.Cross(side, tangent));

        DrawEffectQuad(shader, quadModel, texHandle, mid, side * width, seg, faceNorm, tint, alpha);
    }

    private unsafe void DrawEffectQuad(
        ShaderProgram shader,
        ThreeDoGpuModel quadModel,
        uint texHandle,
        Vector3 center,
        Vector3 axisX,
        Vector3 axisY,
        Vector3 axisZ,
        Vector3 colorTint,
        float alpha)
    {
        Matrix4x4 model = new(
            axisX.X, axisX.Y, axisX.Z, 0f,
            axisY.X, axisY.Y, axisY.Z, 0f,
            axisZ.X, axisZ.Y, axisZ.Z, 0f,
            center.X, center.Y, center.Z, 1f);

        _gl.BindTexture(TextureTarget.Texture2D, texHandle);
        shader.SetUniform("uModel", model);
        shader.SetUniform("uTint", colorTint == Vector3.One ? Vector3.Zero : colorTint * alpha);
        shader.SetUniform("uAlpha", alpha);

        _gl.DrawElements(PrimitiveType.Triangles, (uint)quadModel.Batches[0].ElementCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    private static int HashEffectSeed(Vector3 pos)
    {
        int hx = (int)MathF.Round(pos.X * 100f);
        int hy = (int)MathF.Round(pos.Y * 100f);
        int hz = (int)MathF.Round(pos.Z * 100f);
        return unchecked((hx * 73856093) ^ (hy * 19349663) ^ (hz * 83492791));
    }

    private static uint NextHash(uint x)
    {
        unchecked
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }
    }

    private ThreeDoGpuModel GetOrCreateEffectQuadModel()
    {
        if (_effectQuadModel != null)
            return _effectQuadModel;

        Vertex[] verts =
        [
            new(new Vector3(-0.5f, -0.5f, 0f), Vector3.UnitZ, new Vector2(0f, 1f), 1f),
            new(new Vector3( 0.5f, -0.5f, 0f), Vector3.UnitZ, new Vector2(1f, 1f), 1f),
            new(new Vector3( 0.5f,  0.5f, 0f), Vector3.UnitZ, new Vector2(1f, 0f), 1f),
            new(new Vector3(-0.5f,  0.5f, 0f), Vector3.UnitZ, new Vector2(0f, 0f), 1f),
        ];

        uint[] indices = [0, 1, 2, 0, 2, 3];
        _effectQuadModel = UploadStaticBoltMesh(verts, indices);
        return _effectQuadModel;
    }

    private ThreeDoGpuModel? _effectQuadModel;

    private ThreeDoGpuModel GetOrCreateFallbackBoltModel()
    {
        if (_fallbackBoltModel != null)
            return _fallbackBoltModel;

        const float r = 0.008f;
        const float halfLen = 0.05f;
        Vertex[] verts =
        [
            new(new Vector3(0f, halfLen, 0f), Vector3.UnitY, Vector2.Zero, 1f),
            new(new Vector3(0f, -halfLen, 0f), -Vector3.UnitY, Vector2.Zero, 1f),
            new(new Vector3(r, 0f, 0f), Vector3.UnitX, Vector2.Zero, 1f),
            new(new Vector3(-r, 0f, 0f), -Vector3.UnitX, Vector2.Zero, 1f),
            new(new Vector3(0f, 0f, r), Vector3.UnitZ, Vector2.Zero, 1f),
            new(new Vector3(0f, 0f, -r), -Vector3.UnitZ, Vector2.Zero, 1f),
        ];

        uint[] indices =
        [
            0, 2, 4,  0, 4, 3,  0, 3, 5,  0, 5, 2,
            1, 4, 2,  1, 3, 4,  1, 5, 3,  1, 2, 5
        ];

        _fallbackBoltModel = UploadStaticBoltMesh(verts, indices);
        return _fallbackBoltModel;
    }

    private unsafe void DrawGpuModel(ShaderProgram shader, uint vao, List<MaterialBatch> batches)
    {
        _gl.BindVertexArray(vao);

        bool hasTranslucent = false;
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        shader.SetUniform("uAlpha", 1.0f);

        foreach (var batch in batches)
        {
            if (batch.IsTranslucent)
            {
                hasTranslucent = true;
                continue;
            }

            DrawMaterialBatch(shader, batch);
        }

        if (hasTranslucent)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.DepthMask(false);
            shader.SetUniform("uAlpha", 0.353f);

            foreach (var batch in batches)
            {
                if (!batch.IsTranslucent)
                    continue;

                DrawMaterialBatch(shader, batch);
            }

            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
            shader.SetUniform("uAlpha", 1.0f);
        }

        _gl.BindVertexArray(0);
    }

    private unsafe void DrawMaterialBatch(ShaderProgram shader, MaterialBatch batch)
    {
        var texInfo = _textureManager.GetOrCreateTexture(batch.MaterialName);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texInfo.Handle);
        shader.SetUniform("uTexture", 0);

        _gl.DrawElements(
            PrimitiveType.Triangles,
            (uint)batch.ElementCount,
            DrawElementsType.UnsignedInt,
            (void*)(batch.ElementStart * sizeof(uint))
        );
    }

    private unsafe void UpdateAndDrawAnimatedInstance(ShaderProgram shader, AnimatedThingGpu anim, float time)
    {
        var model = anim.Model;
        var key = anim.Keyframe;

        // Compute animated node hierarchy using authentic LucasArts Sith Engine hierarchy
        Matrix4x4[] nodeTransforms = ThreeDoHierarchy.ComputeTransforms(model, key, time);
        int nodeCount = nodeTransforms.Length;

        // Transform vertices on CPU
        for (int i = 0; i < anim.VertexInfos.Length; i++)
        {
            ref var info = ref anim.VertexInfos[i];
            Matrix4x4 xform = (info.NodeIndex >= 0 && info.NodeIndex < nodeCount) ? nodeTransforms[info.NodeIndex] : Matrix4x4.Identity;

            Vector3 worldPos = Vector3.Transform(info.LocalPos, xform);
            Vector3 worldNorm = Vector3.TransformNormal(info.LocalNorm, xform);

            anim.CpuVertices[i] = new Vertex(worldPos, worldNorm, info.Uv, info.Intensity);
        }

        // Upload updated vertices to dynamic VBO
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, anim.Vbo);
        fixed (Vertex* vPtr = anim.CpuVertices)
        {
            _gl.BufferSubData(
                BufferTargetARB.ArrayBuffer,
                0,
                (nuint)(anim.CpuVertices.Length * sizeof(Vertex)),
                vPtr
            );
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

        // Draw
        DrawGpuModel(shader, anim.Vao, anim.Batches);
    }

    private AnimatedThingGpu? GetOrCreateAnimatedInstance(JklThing thing, string modelName, JklLevel level)
    {
        float carriedAnimTime = 0f;
        if (_animatedInstances.TryGetValue(thing.ThingIndex, out var existing))
        {
            if (string.Equals(existing.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
                return existing;

            carriedAnimTime = existing.AnimTime;
            if (existing.Puppet != null && !_desiredAnimations.ContainsKey(thing.ThingIndex))
                _desiredAnimations[thing.ThingIndex] = (existing.Action, existing.Mode, existing.Loop);

            _gl.DeleteBuffer(existing.Vbo);
            _gl.DeleteBuffer(existing.Ebo);
            _gl.DeleteVertexArray(existing.Vao);
            _animatedInstances.Remove(thing.ThingIndex);
        }

        // Determine if template has a puppet (optional for PlayKey-driven 3DOs)
        string? puppetName = FindPuppetForThing(thing, level);
        PuppetDefinition? puppet = !string.IsNullOrEmpty(puppetName) ? GetOrCreatePuppet(puppetName) : null;

        if (_pendingKeyframes.Remove(thing.ThingIndex, out var pending))
        {
            var pendingKeyframe = GetOrCreateKeyframe(pending.KeyframeName);
            if (pendingKeyframe != null)
            {
                var pendingModel = GetOrCreateThreeDoModel(modelName);
                if (pendingModel != null && pendingModel.Nodes.Count > 0)
                {
                    return EnsureAnimatedInstanceForKeyframe(
                        thing, pendingModel, pendingKeyframe,
                        pending.KeyframeName, pending.Mode, pending.Loop, puppet, modelName: modelName, initialAnimTime: carriedAnimTime);
                }
            }
        }

        if (puppet == null)
            return null;

        var desired = _desiredAnimations.TryGetValue(thing.ThingIndex, out var d)
            ? d
            : ("stand", 0, true);

        if (!ResolvePuppetSubmode(puppet, desired.Item2, desired.Item1, out string? keyName, out int submodeFlags) ||
            string.IsNullOrEmpty(keyName))
            return null;

        var keyframe = GetOrCreateKeyframe(keyName);
        if (keyframe == null) return null;

        var threeDo = GetOrCreateThreeDoModel(modelName);
        if (threeDo == null || threeDo.Nodes.Count == 0) return null;

        return EnsureAnimatedInstanceForKeyframe(
            thing, threeDo, keyframe,
            desired.Item1, desired.Item2, desired.Item3, puppet, submodeFlags, modelName, carriedAnimTime);
    }

    private AnimatedThingGpu EnsureAnimatedInstanceForKeyframe(
        JklThing thing,
        ThreeDoModel threeDo,
        KeyframeTrack keyframe,
        string actionOrKeyframeName,
        int mode,
        bool loop,
        PuppetDefinition? puppet = null,
        int submodeFlags = 0,
        string modelName = "",
        float initialAnimTime = 0f)
    {
        var animGpu = BuildAnimatedThingGpu(threeDo, keyframe);
        animGpu.ModelName = modelName;
        animGpu.AnimTime = initialAnimTime;
        animGpu.Puppet = puppet;
        animGpu.Action = actionOrKeyframeName;
        animGpu.Mode = mode;
        animGpu.Loop = loop;
        animGpu.SubmodeFlags = submodeFlags;
        if (_pendingDistances.Remove(thing.ThingIndex, out float pendingDist))
            animGpu.PendingDistanceMoved = pendingDist;
        _animatedInstances[thing.ThingIndex] = animGpu;
        return animGpu;
    }

    private static bool ResolvePuppetSubmode(
        PuppetDefinition puppet,
        int modeIndex,
        string action,
        out string? keyframe,
        out int flags)
    {
        // First resolve in the requested mode (PuppetDefinition.GetSubmode walks basedon and Mode 0)
        if (puppet.GetSubmode(modeIndex, action, out keyframe, out flags) && !string.IsNullOrEmpty(keyframe))
            return true;

        // "stand" must strictly resolve to an idle standing keyframe, never a combat/fire keyframe
        if (action.Equals("stand", StringComparison.OrdinalIgnoreCase))
        {
            return (puppet.GetSubmode(0, "stand", out keyframe, out flags) && !string.IsNullOrEmpty(keyframe)) ||
                   (puppet.GetSubmode(1, "stand", out keyframe, out flags) && !string.IsNullOrEmpty(keyframe));
        }

        if (modeIndex != 1 && puppet.GetSubmode(1, action, out keyframe, out flags) && !string.IsNullOrEmpty(keyframe))
            return true;

        if (modeIndex != 0 && puppet.GetSubmode(0, action, out keyframe, out flags) && !string.IsNullOrEmpty(keyframe))
            return true;

        // Fall back cleanly from "run" to "walk" when a .pup has no separate "run" entry
        if (action.Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            if (ResolvePuppetSubmode(puppet, modeIndex, "walk", out keyframe, out flags))
                return true;
        }

        return (puppet.GetSubmode(modeIndex, "stand", out keyframe, out flags) && !string.IsNullOrEmpty(keyframe)) ||
               (puppet.GetSubmode(0, "stand", out keyframe, out flags) && !string.IsNullOrEmpty(keyframe));
    }

    private string? FindPuppetForThing(JklThing thing, JklLevel level)
    {
        string current = thing.TemplateName;
        while (!string.IsNullOrEmpty(current) && !current.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (level.Templates.TryGetValue(current, out var tmpl))
            {
                if (tmpl.Parameters.TryGetValue("puppet", out var pup) && !string.IsNullOrEmpty(pup))
                {
                    return pup;
                }
                current = tmpl.BasedOn;
            }
            else break;
        }
        return null;
    }

    private unsafe AnimatedThingGpu BuildAnimatedThingGpu(ThreeDoModel model, KeyframeTrack keyframe)
    {
        var vInfos = new List<AnimatedVertexInfo>();
        var indicesByMat = new Dictionary<(string MaterialName, bool IsTranslucent), List<uint>>(MaterialBatchKeyComparer.Instance);

        for (int i = 0; i < model.Nodes.Count; i++)
        {
            int meshIdx = model.Nodes[i].MeshIndex;
            if (meshIdx >= 0 && meshIdx < model.Meshes.Count)
            {
                var mesh = model.Meshes[meshIdx];
                foreach (var face in mesh.Faces)
                {
                    if (face.VertexIndices.Length < 3) continue;

                    string matName = (face.MaterialIndex >= 0 && face.MaterialIndex < model.Materials.Count)
                        ? model.Materials[face.MaterialIndex]
                        : string.Empty;
                    bool isTranslucent = (face.FaceType & 0x2) != 0;

                    var texInfo = _textureManager.GetOrCreateTexture(matName);
                    float texW = texInfo.Width > 0 ? texInfo.Width : 64.0f;
                    float texH = texInfo.Height > 0 ? texInfo.Height : 64.0f;

                    var batchKey = (matName, isTranslucent);
                    if (!indicesByMat.TryGetValue(batchKey, out var indexList))
                    {
                        indexList = new List<uint>();
                        indicesByMat[batchKey] = indexList;
                    }

                    int nverts = face.VertexIndices.Length;
                    uint baseVertexIndex = (uint)vInfos.Count;

                    for (int v = 0; v < nverts; v++)
                    {
                        int vIdx = face.VertexIndices[v];
                        int uvIdx = (v < face.TextureIndices.Length) ? face.TextureIndices[v] : -1;

                        Vector3 localPos = (vIdx >= 0 && vIdx < mesh.Vertices.Count) ? mesh.Vertices[vIdx] : Vector3.Zero;
                        Vector2 rawUv = (uvIdx >= 0 && uvIdx < mesh.TextureVertices.Count) ? mesh.TextureVertices[uvIdx] : Vector2.Zero;
                        Vector2 uv = new Vector2(rawUv.X / texW, 1.0f + (rawUv.Y / texH));
                        float intensity = ResolveVertexIntensity(mesh, vIdx);

                        vInfos.Add(new AnimatedVertexInfo
                        {
                            NodeIndex = i,
                            LocalPos = localPos,
                            LocalNorm = face.Normal,
                            Uv = uv,
                            Intensity = intensity
                        });
                    }

                    for (int v = 1; v < nverts - 1; v++)
                    {
                        indexList.Add(baseVertexIndex + 0);
                        indexList.Add(baseVertexIndex + (uint)v);
                        indexList.Add(baseVertexIndex + (uint)(v + 1));
                    }
                }
            }
        }

        var allIndices = new List<uint>();
        var batches = new List<MaterialBatch>();
        foreach (var (batchKey, indexList) in indicesByMat)
        {
            int start = allIndices.Count;
            allIndices.AddRange(indexList);
            batches.Add(new MaterialBatch(batchKey.MaterialName, start, indexList.Count, batchKey.IsTranslucent));
        }

        uint vao = _gl.GenVertexArray();
        uint vbo = _gl.GenBuffer();
        uint ebo = _gl.GenBuffer();

        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);

        var cpuVertices = new Vertex[vInfos.Count];
        fixed (Vertex* vPtr = cpuVertices)
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(cpuVertices.Length * sizeof(Vertex)),
                vPtr,
                BufferUsageARB.DynamicDraw
            );
        }

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (uint* iPtr = allIndices.ToArray())
        {
            _gl.BufferData(
                BufferTargetARB.ElementArrayBuffer,
                (nuint)(allIndices.Count * sizeof(uint)),
                iPtr,
                BufferUsageARB.StaticDraw
            );
        }

        uint stride = (uint)sizeof(Vertex);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)12);
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)24);
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)32);

        _gl.BindVertexArray(0);

        return new AnimatedThingGpu
        {
            Vao = vao,
            Vbo = vbo,
            Ebo = ebo,
            Batches = batches,
            Model = model,
            Keyframe = keyframe,
            VertexInfos = vInfos.ToArray(),
            CpuVertices = cpuVertices
        };
    }

    private PuppetDefinition? GetOrCreatePuppet(string puppetName)
    {
        if (_puppetCache.TryGetValue(puppetName, out var cached))
            return cached;

        string[] searchPaths = [
            $"misc/pup/{puppetName}",
            $"pup/{puppetName}",
            puppetName
        ];

        foreach (var p in searchPaths)
        {
            if (_vfs.FileExists(p))
            {
                string text = _vfs.ReadAllText(p);
                var pup = PuppetReader.Parse(text, puppetName);
                _puppetCache[puppetName] = pup;
                return pup;
            }
        }

        _puppetCache[puppetName] = null;
        return null;
    }

    private KeyframeTrack? GetOrCreateKeyframe(string keyName)
    {
        if (_keyframeCache.TryGetValue(keyName, out var cached))
            return cached;

        string[] searchPaths = [
            $"3do/key/{keyName}",
            $"key/{keyName}",
            keyName
        ];

        foreach (var p in searchPaths)
        {
            if (_vfs.FileExists(p))
            {
                string text = _vfs.ReadAllText(p);
                var key = KeyframeReader.Parse(text, keyName);
                _keyframeCache[keyName] = key;
                return key;
            }
        }

        _keyframeCache[keyName] = null;
        return null;
    }

    private ThreeDoModel? GetOrCreateThreeDoModel(string modelName)
    {
        if (_parsedModels.TryGetValue(modelName, out var existing))
            return existing;

        string[] searchPaths = [
            $"3do/{modelName}",
            $"3do/mat/{modelName}",
            modelName
        ];

        foreach (var p in searchPaths)
        {
            if (_vfs.FileExists(p))
            {
                string text = _vfs.ReadAllText(p);
                var parsed = ThreeDoReader.Parse(text);
                _parsedModels[modelName] = parsed;
                return parsed;
            }
        }

        _parsedModels[modelName] = null;
        return null;
    }

    private ThreeDoGpuModel? GetOrCreateGpuModel(string modelName, bool forceFullbright = false)
    {
        string cacheKey = forceFullbright ? $"{modelName}#fullbright" : modelName;
        if (_modelCache.TryGetValue(cacheKey, out var existing))
            return existing;

        var threeDo = GetOrCreateThreeDoModel(modelName);
        if (threeDo == null)
        {
            _modelCache[cacheKey] = null;
            return null;
        }

        try
        {
            var gpuModel = BuildGpuModel(threeDo, forceFullbright);
            _modelCache[cacheKey] = gpuModel;
            return gpuModel;
        }
        catch
        {
            _modelCache[cacheKey] = null;
            return null;
        }
    }

    private unsafe ThreeDoGpuModel BuildGpuModel(ThreeDoModel model, bool forceFullbright = false)
    {
        var vertices = new List<Vertex>();
        var indicesByMat = new Dictionary<(string MaterialName, bool IsTranslucent), List<uint>>(MaterialBatchKeyComparer.Instance);

        // Precompute node hierarchy matrices using authentic LucasArts Sith Engine hierarchy
        Matrix4x4[] nodeTransforms = ThreeDoHierarchy.ComputeTransforms(model);

        // Emit geometry
        if (model.Nodes.Count > 0)
        {
            for (int i = 0; i < model.Nodes.Count; i++)
            {
                int meshIdx = model.Nodes[i].MeshIndex;
                if (meshIdx >= 0 && meshIdx < model.Meshes.Count)
                {
                    EmitMesh(model.Meshes[meshIdx], model, nodeTransforms[i], vertices, indicesByMat, forceFullbright);
                }
            }
        }
        else
        {
            foreach (var mesh in model.Meshes)
            {
                EmitMesh(mesh, model, Matrix4x4.Identity, vertices, indicesByMat, forceFullbright);
            }
        }

        // Assemble indices and batches
        var allIndices = new List<uint>();
        var batches = new List<MaterialBatch>();
        foreach (var (batchKey, indexList) in indicesByMat)
        {
            int start = allIndices.Count;
            allIndices.AddRange(indexList);
            batches.Add(new MaterialBatch(batchKey.MaterialName, start, indexList.Count, batchKey.IsTranslucent));
        }

        // Upload to GPU
        uint vao = _gl.GenVertexArray();
        uint vbo = _gl.GenBuffer();
        uint ebo = _gl.GenBuffer();

        _gl.BindVertexArray(vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (Vertex* vPtr = vertices.ToArray())
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(vertices.Count * sizeof(Vertex)),
                vPtr,
                BufferUsageARB.StaticDraw
            );
        }

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (uint* iPtr = allIndices.ToArray())
        {
            _gl.BufferData(
                BufferTargetARB.ElementArrayBuffer,
                (nuint)(allIndices.Count * sizeof(uint)),
                iPtr,
                BufferUsageARB.StaticDraw
            );
        }

        uint stride = (uint)sizeof(Vertex);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)12);
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)24);
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)32);

        _gl.BindVertexArray(0);

        return new ThreeDoGpuModel(vao, vbo, ebo, batches);
    }

    private void EmitMesh(
        ThreeDoMesh mesh,
        ThreeDoModel model,
        Matrix4x4 transform,
        List<Vertex> vertices,
        Dictionary<(string MaterialName, bool IsTranslucent), List<uint>> indicesByMat,
        bool forceFullbright = false)
    {
        foreach (var face in mesh.Faces)
        {
            if (face.VertexIndices.Length < 3) continue;

            string matName = (face.MaterialIndex >= 0 && face.MaterialIndex < model.Materials.Count)
                ? model.Materials[face.MaterialIndex]
                : string.Empty;
            bool isTranslucent = (face.FaceType & 0x2) != 0;

            var texInfo = _textureManager.GetOrCreateTexture(matName);
            float texW = texInfo.Width > 0 ? texInfo.Width : 64.0f;
            float texH = texInfo.Height > 0 ? texInfo.Height : 64.0f;

            var batchKey = (matName, isTranslucent);
            if (!indicesByMat.TryGetValue(batchKey, out var indexList))
            {
                indexList = new List<uint>();
                indicesByMat[batchKey] = indexList;
            }

            int nverts = face.VertexIndices.Length;
            uint baseVertexIndex = (uint)vertices.Count;

            Vector3 norm = Vector3.TransformNormal(face.Normal, transform);

            for (int i = 0; i < nverts; i++)
            {
                int vIdx = face.VertexIndices[i];
                int uvIdx = (i < face.TextureIndices.Length) ? face.TextureIndices[i] : -1;

                Vector3 localPos = (vIdx >= 0 && vIdx < mesh.Vertices.Count) ? mesh.Vertices[vIdx] : Vector3.Zero;
                Vector3 worldPos = Vector3.Transform(localPos, transform);

                Vector2 rawUv = (uvIdx >= 0 && uvIdx < mesh.TextureVertices.Count) ? mesh.TextureVertices[uvIdx] : Vector2.Zero;
                Vector2 uv = new Vector2(rawUv.X / texW, 1.0f + (rawUv.Y / texH));

                float intensity = ResolveVertexIntensity(mesh, vIdx, forceFullbright);

                vertices.Add(new Vertex(worldPos, norm, uv, intensity));
            }

            for (int i = 1; i < nverts - 1; i++)
            {
                indexList.Add(baseVertexIndex + 0);
                indexList.Add(baseVertexIndex + (uint)i);
                indexList.Add(baseVertexIndex + (uint)(i + 1));
            }
        }
    }

    public static float ResolveVertexIntensity(ThreeDoMesh mesh, int vIdx, bool forceFullbright = false)
    {
        if (forceFullbright || mesh.LightingMode == 0)
            return 1.0f;

        return (vIdx >= 0 && vIdx < mesh.VertexIntensities.Count)
            ? mesh.VertexIntensities[vIdx]
            : 1.0f;
    }

    public void Dispose()
    {
        foreach (var model in _modelCache.Values)
        {
            if (model != null)
            {
                _gl.DeleteBuffer(model.Vbo);
                _gl.DeleteBuffer(model.Ebo);
                _gl.DeleteVertexArray(model.Vao);
            }
        }
        _modelCache.Clear();

        foreach (var anim in _animatedInstances.Values)
        {
            _gl.DeleteBuffer(anim.Vbo);
            _gl.DeleteBuffer(anim.Ebo);
            _gl.DeleteVertexArray(anim.Vao);
        }
        _animatedInstances.Clear();
        _pendingKeyframes.Clear();

        foreach (var vm in _viewmodelInstances.Values)
        {
            _gl.DeleteBuffer(vm.Vbo);
            _gl.DeleteBuffer(vm.Ebo);
            _gl.DeleteVertexArray(vm.Vao);
        }
        _viewmodelInstances.Clear();

        DeleteBoltModel(ref _fallbackBoltModel);
        DeleteBoltModel(ref _effectQuadModel);
    }

    private void DeleteBoltModel(ref ThreeDoGpuModel? model)
    {
        if (model == null)
            return;

        _gl.DeleteBuffer(model.Vbo);
        _gl.DeleteBuffer(model.Ebo);
        _gl.DeleteVertexArray(model.Vao);
        model = null;
    }
}
