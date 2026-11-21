using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSith.Core.Data;
using OpenSith.Core.Vfs;
using OpenSith.Core.World;

namespace OpenSith.Client;

public sealed partial class WorldSimulation
{
    private enum LightAnimKind
    {
        SurfaceLight,
        ThingLight,
        SurfaceCel,
        MaterialCel
    }

    private sealed class ActiveLightAnim
    {
        public int AnimId { get; init; }
        public LightAnimKind Kind { get; init; }
        public int TargetId { get; init; }
        public string? MaterialName { get; init; }
        public float StartValue { get; init; }
        public float EndValue { get; init; }
        public float Period { get; init; }
        public int Flags { get; init; }
        public float Elapsed { get; set; }
    }

    private sealed class ColorEffectEntry
    {
        public int Id { get; init; }
        public Vector3 Tint { get; init; }
        public Vector3 Add { get; init; }
        public float Fade { get; init; }
    }

    private readonly Dictionary<int, (string Sound, float Volume)> _sectorSounds = new();
    private readonly Dictionary<int, string> _sectorColormaps = new();
    private readonly Dictionary<int, ActiveLightAnim> _activeAnims = new();
    private readonly Dictionary<int, int> _surfaceAnimIds = new();
    private readonly Dictionary<int, ColorEffectEntry> _colorEffects = new();
    private int _nextAnimId = 1;
    private int _nextColorEffectId = 1;

    private Vector3 _dynamicTint = Vector3.Zero;
    private Vector3 _dynamicAdd = Vector3.Zero;
    private bool _irModeEnabled;
    private float _irModeIntensity;

    /// <summary>
    /// Invoked when a COG script changes a world surface's geometry visibility mode (<c>SetFaceGeoMode</c>).
    /// </summary>
    public event Action<int, int>? OnSurfaceGeoModeChanged;

    /// <summary>
    /// Invoked when a COG script assigns a new material to a world surface (<c>SetSurfaceMat</c>).
    /// </summary>
    public event Action<int, string>? OnSurfaceMaterialChanged;

    /// <summary>
    /// Invoked when <c>SurfaceLightAnim</c> updates a surface's dynamic extra light.
    /// </summary>
    public event Action<int, float>? OnSurfaceLightChanged;

    /// <summary>
    /// Effective RGB additive tint applied to the 3D scene from active sector tint,
    /// dynamic COG tint/add flashes, color effects, and IR mode.
    /// </summary>
    public Vector3 CurrentScreenTint { get; private set; } = Vector3.Zero;

    /// <summary>
    /// Gets whether IR Goggles mode is currently enabled via COG.
    /// </summary>
    public bool IsIrModeEnabled => _irModeEnabled;

    private static Dictionary<int, ItemDefinition> LoadItemDefinitions(IVirtualFileSystem vfs)
    {
        foreach (string path in new[] { "misc/items.dat", "items.dat" })
        {
            if (vfs.FileExists(path))
            {
                try
                {
                    return ItemsDatReader.ParseById(vfs.ReadAllText(path));
                }
                catch
                {
                    return new Dictionary<int, ItemDefinition>();
                }
            }
        }
        return new Dictionary<int, ItemDefinition>();
    }

    private static Dictionary<int, int> BuildSurfaceToSectorMap(JklLevel level)
    {
        var map = new Dictionary<int, int>(level.Surfaces.Count);
        foreach (var sector in level.Sectors)
        {
            foreach (var surface in sector.Surfaces)
            {
                map[surface.SurfaceIndex] = sector.SectorIndex;
            }
        }
        return map;
    }

    private void UpdateSectorAmbientSound(int sectorId)
    {
        if (_audio == null)
            return;

        if (sectorId >= 0 && _sectorSounds.TryGetValue(sectorId, out var ambient) && !string.IsNullOrWhiteSpace(ambient.Sound))
        {
            _audio.SetSectorAmbient(ambient.Sound, ambient.Volume);
        }
        else
        {
            _audio.SetSectorAmbient(null, 0f);
        }
    }

    private void UpdateLightAnimations(float dt)
    {
        if (dt <= 0f || _activeAnims.Count == 0)
            return;

        foreach (var anim in _activeAnims.Values)
        {
            anim.Elapsed += dt;
            switch (anim.Kind)
            {
                case LightAnimKind.SurfaceLight:
                {
                    float light = EvaluateOscillatingValue(anim.StartValue, anim.EndValue, anim.Period, anim.Elapsed);
                    if (anim.TargetId >= 0 && anim.TargetId < _level.Surfaces.Count)
                    {
                        _level.Surfaces[anim.TargetId].ExtraLight = light;
                        OnSurfaceLightChanged?.Invoke(anim.TargetId, light);
                    }
                    break;
                }
                case LightAnimKind.ThingLight:
                {
                    float light = EvaluateOscillatingValue(anim.StartValue, anim.EndValue, anim.Period, anim.Elapsed);
                    _thingLights[anim.TargetId] = light;
                    break;
                }
                case LightAnimKind.SurfaceCel:
                {
                    float fps = MathF.Abs(anim.Period);
                    if (fps > 0.01f && anim.TargetId >= 0)
                    {
                        int cel = (int)(anim.Elapsed * fps) & 1;
                        _surfaceCels[anim.TargetId] = cel;
                    }
                    break;
                }
            }
        }
    }

    private static float EvaluateOscillatingValue(float start, float end, float period, float elapsed)
    {
        if (period <= 1e-4f)
            return end;

        float phase = (elapsed / period) % 2.0f;
        float t = phase <= 1.0f ? phase : 2.0f - phase;
        return start + (end - start) * Math.Clamp(t, 0f, 1f);
    }

    private void UpdateScreenColorEffects(float dt)
    {
        if (dt > 0f)
        {
            float decay = MathF.Max(0f, 1f - 1.8f * dt);
            _dynamicTint *= decay;
            _dynamicAdd *= decay;
            if (_dynamicTint.LengthSquared() < 1e-5f) _dynamicTint = Vector3.Zero;
            if (_dynamicAdd.LengthSquared() < 1e-5f) _dynamicAdd = Vector3.Zero;
        }

        Vector3 tint = _dynamicTint + _dynamicAdd;

        if (_prevPlayerSector >= 0 && _prevPlayerSector < _level.Sectors.Count)
        {
            Vector3 secTint = _level.Sectors[_prevPlayerSector].Tint;
            tint += new Vector3(
                Math.Clamp(secTint.X, 0f, 1f),
                Math.Clamp(secTint.Y, 0f, 1f),
                Math.Clamp(secTint.Z, 0f, 1f)) * 0.35f;
        }

        foreach (var effect in _colorEffects.Values)
        {
            tint += effect.Tint + effect.Add;
        }

        if (_irModeEnabled)
        {
            tint += new Vector3(0.0f, Math.Clamp(_irModeIntensity * 0.25f, 0.08f, 0.35f), 0.02f);
        }

        CurrentScreenTint = Vector3.Clamp(tint, Vector3.Zero, new Vector3(0.75f, 0.75f, 0.75f));
    }

    // -------------------------------------------------------------------------
    // Surface & Adjoin COG Verbs
    // -------------------------------------------------------------------------

    public void SetFaceGeoMode(int surfaceId, int geoMode)
    {
        if (surfaceId < 0 || surfaceId >= _level.Surfaces.Count)
            return;

        _level.Surfaces[surfaceId].GeometryMode = geoMode;
        OnSurfaceGeoModeChanged?.Invoke(surfaceId, geoMode);
    }

    public int GetFaceGeoMode(int surfaceId)
    {
        if (surfaceId < 0 || surfaceId >= _level.Surfaces.Count)
            return 0;

        return _level.Surfaces[surfaceId].GeometryMode;
    }

    public void SetFaceType(int surfaceId, int faceFlags)
    {
        if (surfaceId < 0 || surfaceId >= _level.Surfaces.Count)
            return;

        int prev = _level.Surfaces[surfaceId].FaceFlags;
        _level.Surfaces[surfaceId].FaceFlags = prev | faceFlags;
        if (((prev ^ _level.Surfaces[surfaceId].FaceFlags) & 0x3) != 0)
            OnSurfaceGeoModeChanged?.Invoke(surfaceId, _level.Surfaces[surfaceId].GeometryMode);
    }

    public void SetSurfaceFlags(int surfaceId, int flags)
    {
        if (surfaceId < 0 || surfaceId >= _level.Surfaces.Count)
            return;

        _level.Surfaces[surfaceId].SurfaceFlags |= flags;
    }

    public void ClearSurfaceFlags(int surfaceId, int flags)
    {
        if (surfaceId < 0 || surfaceId >= _level.Surfaces.Count)
            return;

        _level.Surfaces[surfaceId].SurfaceFlags &= ~flags;
    }

    public int GetSurfaceFlags(int surfaceId)
    {
        if (surfaceId < 0 || surfaceId >= _level.Surfaces.Count)
            return 0;

        return _level.Surfaces[surfaceId].SurfaceFlags;
    }

    public void SetSurfaceMat(int surfaceId, string materialName)
    {
        if (surfaceId < 0 || surfaceId >= _level.Surfaces.Count || string.IsNullOrWhiteSpace(materialName))
            return;

        int matIdx = -1;
        for (int i = 0; i < _level.Materials.Count; i++)
        {
            if (string.Equals(_level.Materials[i], materialName, StringComparison.OrdinalIgnoreCase))
            {
                matIdx = i;
                break;
            }
        }

        if (matIdx < 0)
        {
            matIdx = _level.Materials.Count;
            _level.Materials.Add(materialName);
        }

        _level.Surfaces[surfaceId].MaterialIndex = matIdx;
        OnSurfaceMaterialChanged?.Invoke(surfaceId, materialName);
    }

    public void SetAdjoinFlags(int surfaceId, int flags)
    {
        if (!TryGetSurfaceAdjoinIndex(surfaceId, out int adjoinIdx))
            return;

        _level.Adjoins[adjoinIdx].Flags |= flags;
    }

    public void ClearAdjoinFlags(int surfaceId, int flags)
    {
        if (!TryGetSurfaceAdjoinIndex(surfaceId, out int adjoinIdx))
            return;

        _level.Adjoins[adjoinIdx].Flags &= ~flags;
    }

    public int GetAdjoinFlags(int surfaceId) =>
        TryGetSurfaceAdjoinIndex(surfaceId, out int adjoinIdx) ? _level.Adjoins[adjoinIdx].Flags : 0;

    private bool TryGetSurfaceAdjoinIndex(int surfaceId, out int adjoinIndex)
    {
        adjoinIndex = -1;
        if (surfaceId < 0 || surfaceId >= _level.Surfaces.Count)
            return false;

        int idx = _level.Surfaces[surfaceId].AdjoinIndex;
        if (idx < 0)
            _origSurfaceAdjoins.TryGetValue(surfaceId, out idx);

        if (idx < 0 || idx >= _level.Adjoins.Count)
            return false;

        adjoinIndex = idx;
        return true;
    }

    public int GetSurfaceSector(int surfaceId) =>
        _surfaceToSector.TryGetValue(surfaceId, out int sectorId) ? sectorId : -1;

    public Vector3 GetSurfaceNormal(int surfaceId)
    {
        if (surfaceId >= 0 && surfaceId < _level.Surfaces.Count)
            return _level.Surfaces[surfaceId].Normal;

        return Vector3.Zero;
    }

    public int GetNumSurfaceVertices(int surfaceId)
    {
        if (surfaceId >= 0 && surfaceId < _level.Surfaces.Count)
            return _level.Surfaces[surfaceId].VertexIndices.Length;

        return 0;
    }

    public Vector3 GetSurfaceVertexPos(int surfaceId, int vertexIdx)
    {
        if (surfaceId < 0 || surfaceId >= _level.Surfaces.Count)
            return Vector3.Zero;

        int[] verts = _level.Surfaces[surfaceId].VertexIndices;
        if (vertexIdx < 0 || vertexIdx >= verts.Length)
            return Vector3.Zero;

        int worldVertIdx = verts[vertexIdx];
        return worldVertIdx >= 0 && worldVertIdx < _level.Vertices.Count
            ? _level.Vertices[worldVertIdx]
            : Vector3.Zero;
    }

    public int SurfaceAnim(int surfaceId, float fps, int flags)
    {
        if (surfaceId < 0 || surfaceId >= _level.Surfaces.Count)
            return -1;

        if (_surfaceAnimIds.TryGetValue(surfaceId, out int oldAnimId))
            _activeAnims.Remove(oldAnimId);

        int animId = _nextAnimId++;
        _activeAnims[animId] = new ActiveLightAnim
        {
            AnimId = animId,
            Kind = LightAnimKind.SurfaceCel,
            TargetId = surfaceId,
            Period = fps,
            Flags = flags
        };
        _surfaceAnimIds[surfaceId] = animId;
        return animId;
    }

    public void StopSurfaceAnim(int surfaceId)
    {
        if (_surfaceAnimIds.Remove(surfaceId, out int animId))
            _activeAnims.Remove(animId);
    }

    public int GetSurfaceAnim(int surfaceId) =>
        _surfaceAnimIds.GetValueOrDefault(surfaceId, -1);

    public int MaterialAnim(string material, float fps, int flags)
    {
        int animId = _nextAnimId++;
        _activeAnims[animId] = new ActiveLightAnim
        {
            AnimId = animId,
            Kind = LightAnimKind.MaterialCel,
            TargetId = -1,
            MaterialName = material ?? string.Empty,
            Period = fps,
            Flags = flags
        };
        return animId;
    }

    public int SurfaceLightAnim(int surfaceId, float startLight, float endLight, float period)
    {
        if (surfaceId < 0 || surfaceId >= _level.Surfaces.Count)
            return -1;

        if (_surfaceAnimIds.TryGetValue(surfaceId, out int oldAnimId))
            _activeAnims.Remove(oldAnimId);

        int animId = _nextAnimId++;
        _activeAnims[animId] = new ActiveLightAnim
        {
            AnimId = animId,
            Kind = LightAnimKind.SurfaceLight,
            TargetId = surfaceId,
            StartValue = startLight,
            EndValue = endLight,
            Period = period
        };
        _surfaceAnimIds[surfaceId] = animId;
        return animId;
    }

    public int ThingLightAnim(int thingId, float startLight, float endLight, float period)
    {
        if (thingId < 0 || thingId >= _level.Things.Count)
            return -1;

        int animId = _nextAnimId++;
        _activeAnims[animId] = new ActiveLightAnim
        {
            AnimId = animId,
            Kind = LightAnimKind.ThingLight,
            TargetId = thingId,
            StartValue = startLight,
            EndValue = endLight,
            Period = period
        };
        return animId;
    }

    public void StopAnim(int animId)
    {
        if (_activeAnims.Remove(animId, out var anim) && anim.TargetId >= 0)
        {
            if (_surfaceAnimIds.TryGetValue(anim.TargetId, out int mappedId) && mappedId == animId)
                _surfaceAnimIds.Remove(anim.TargetId);
        }
    }

    // -------------------------------------------------------------------------
    // Sector COG Verbs & Screen Color Effects
    // -------------------------------------------------------------------------

    public void SetSectorFlags(int sectorId, int flags)
    {
        if (sectorId >= 0 && sectorId < _level.Sectors.Count)
            _level.Sectors[sectorId].Flags |= flags;
    }

    public void ClearSectorFlags(int sectorId, int flags)
    {
        if (sectorId >= 0 && sectorId < _level.Sectors.Count)
            _level.Sectors[sectorId].Flags &= ~flags;
    }

    public int GetSectorFlags(int sectorId) =>
        sectorId >= 0 && sectorId < _level.Sectors.Count ? _level.Sectors[sectorId].Flags : 0;

    public void SetSectorTint(int sectorId, Vector3 tint)
    {
        if (sectorId >= 0 && sectorId < _level.Sectors.Count)
        {
            _level.Sectors[sectorId].Tint = tint;
            UpdateScreenColorEffects(0f);
        }
    }

    public void SectorSound(int sectorId, string sound, float volume)
    {
        if (sectorId < 0 || sectorId >= _level.Sectors.Count)
            return;

        if (string.IsNullOrWhiteSpace(sound) || volume <= 0f)
            _sectorSounds.Remove(sectorId);
        else
            _sectorSounds[sectorId] = (sound, volume);

        if (sectorId == _prevPlayerSector)
            UpdateSectorAmbientSound(sectorId);
    }

    public void SetColormap(int sectorId, string colormap)
    {
        if (sectorId < 0 || sectorId >= _level.Sectors.Count || string.IsNullOrWhiteSpace(colormap))
            return;

        _sectorColormaps[sectorId] = colormap;

        int cmapIdx = -1;
        for (int i = 0; i < _level.ColorMaps.Count; i++)
        {
            if (string.Equals(_level.ColorMaps[i], colormap, StringComparison.OrdinalIgnoreCase))
            {
                cmapIdx = i;
                break;
            }
        }

        if (cmapIdx < 0)
        {
            cmapIdx = _level.ColorMaps.Count;
            _level.ColorMaps.Add(colormap);
        }

        _level.Sectors[sectorId].ColorMapIndex = cmapIdx;
    }

    public int FirstThingInSector(int sectorId) => FindNextThingInSector(sectorId, startAfterIndex: -1);

    public int NextThingInSector(int prevThingId)
    {
        if (prevThingId < 0 || prevThingId >= _level.Things.Count)
            return -1;

        int sectorId = _level.Things[prevThingId].SectorIndex;
        return FindNextThingInSector(sectorId, startAfterIndex: prevThingId);
    }

    private int FindNextThingInSector(int sectorId, int startAfterIndex)
    {
        if (sectorId < 0 || sectorId >= _level.Sectors.Count)
            return -1;

        for (int i = startAfterIndex + 1; i < _level.Things.Count; i++)
        {
            if (_destroyedThings.Contains(i))
                continue;

            var thing = _level.Things[i];
            if ((thing.ThingFlags & (uint)(SithThingFlags.Disabled | SithThingFlags.Destroyed)) != 0)
                continue;

            if (thing.SectorIndex == sectorId)
                return i;
        }

        return -1;
    }

    public int GetNumSectorVertices(int sectorId) =>
        sectorId >= 0 && sectorId < _level.Sectors.Count ? _level.Sectors[sectorId].VertexIndices.Length : 0;

    public Vector3 GetSectorVertexPos(int sectorId, int vertexIdx)
    {
        if (sectorId < 0 || sectorId >= _level.Sectors.Count)
            return Vector3.Zero;

        int[] verts = _level.Sectors[sectorId].VertexIndices;
        if (vertexIdx < 0 || vertexIdx >= verts.Length)
            return Vector3.Zero;

        int worldVertIdx = verts[vertexIdx];
        return worldVertIdx >= 0 && worldVertIdx < _level.Vertices.Count
            ? _level.Vertices[worldVertIdx]
            : Vector3.Zero;
    }

    public void AddDynamicTint(int thingId, float r, float g, float b)
    {
        if (thingId == _localPlayerThingIndex || thingId < 0)
        {
            _dynamicTint += new Vector3(r, g, b);
            UpdateScreenColorEffects(0f);
        }
    }

    public void AddDynamicAdd(int thingId, float r, float g, float b)
    {
        if (thingId == _localPlayerThingIndex || thingId < 0)
        {
            _dynamicAdd += new Vector3(r, g, b);
            UpdateScreenColorEffects(0f);
        }
    }

    public void EnableIRMode(float intensity, float fade)
    {
        _irModeEnabled = true;
        _irModeIntensity = intensity;
        UpdateScreenColorEffects(0f);
    }

    public void DisableIRMode()
    {
        _irModeEnabled = false;
        _irModeIntensity = 0f;
        UpdateScreenColorEffects(0f);
    }

    public int NewColorEffect(
        int filterR, int filterG, int filterB,
        float tintR, float tintG, float tintB,
        int addR, int addG, int addB,
        float fade)
    {
        int id = _nextColorEffectId++;
        _colorEffects[id] = new ColorEffectEntry
        {
            Id = id,
            Tint = new Vector3(tintR, tintG, tintB),
            Add = new Vector3(addR / 255f, addG / 255f, addB / 255f),
            Fade = fade
        };
        UpdateScreenColorEffects(0f);
        return id;
    }

    public void FreeColorEffect(int effectId)
    {
        if (_colorEffects.Remove(effectId))
            UpdateScreenColorEffects(0f);
    }
}
