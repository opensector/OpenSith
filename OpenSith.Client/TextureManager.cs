using System;
using System.Collections.Generic;
using OpenSith.Core.Materials;
using OpenSith.Core.Vfs;
using Silk.NET.OpenGL;

namespace OpenSith.Client;

public sealed class TextureManager : IDisposable
{
    private readonly GL _gl;
    private readonly IVirtualFileSystem _vfs;
    private readonly CmpPalette _palette;
    private readonly Dictionary<string, TextureInfo[]> _textureCelsCache = new(StringComparer.OrdinalIgnoreCase);

    public uint DefaultWhiteTexture { get; }

    public readonly record struct TextureInfo(uint Handle, int Width, int Height);

    public TextureManager(GL gl, IVirtualFileSystem vfs, CmpPalette palette)
    {
        _gl = gl;
        _vfs = vfs;
        _palette = palette;
        DefaultWhiteTexture = CreateDefaultWhiteTexture();
    }

    public TextureInfo GetOrCreateTexture(string materialName) =>
        GetOrCreateTextureCels(materialName)[0];

    /// <summary>
    /// Loads and caches every animation cel stored in <paramref name="materialName"/>.
    /// </summary>
    public TextureInfo[] GetOrCreateTextureCels(string materialName)
    {
        if (_disposed)
            return [new TextureInfo(0, 128, 128)];

        if (string.IsNullOrWhiteSpace(materialName))
            return [new TextureInfo(DefaultWhiteTexture, 128, 128)];

        if (_textureCelsCache.TryGetValue(materialName, out var cached))
            return cached;

        string[] searchPaths = [
            $"mat/{materialName}",
            $"3do/mat/{materialName}",
            $"misc/mat/{materialName}",
            materialName
        ];

        byte[]? matBytes = null;
        foreach (var p in searchPaths)
        {
            if (_vfs.FileExists(p))
            {
                matBytes = _vfs.ReadAllBytes(p);
                break;
            }
        }

        if (matBytes == null)
        {
            TextureInfo[] fallback = [new TextureInfo(DefaultWhiteTexture, 128, 128)];
            _textureCelsCache[materialName] = fallback;
            return fallback;
        }

        var uploaded = new List<TextureInfo>();
        try
        {
            var mat = MatMaterial.Parse(matBytes);
            int celCount = Math.Max(1, mat.CelCount);
            var infos = new TextureInfo[celCount];
            for (int c = 0; c < celCount; c++)
            {
                MatCel cel = mat.Cels[c];
                byte[] rgbaBuffer = mat.ToRgbaByteBuffer(_palette, lightLevel: 0, celIndex: c);
                uint handle = UploadTexture((uint)cel.Width, (uint)cel.Height, rgbaBuffer);
                var info = new TextureInfo(handle, cel.Width, cel.Height);
                uploaded.Add(info);
                infos[c] = info;
            }

            _textureCelsCache[materialName] = infos;
            return infos;
        }
        catch
        {
            foreach (var info in uploaded)
            {
                if (info.Handle != 0 && info.Handle != DefaultWhiteTexture)
                    _gl.DeleteTexture(info.Handle);
            }
            TextureInfo[] fallback = [new TextureInfo(DefaultWhiteTexture, 128, 128)];
            _textureCelsCache[materialName] = fallback;
            return fallback;
        }
    }

    private unsafe uint UploadTexture(uint width, uint height, byte[] rgbaData)
    {
        uint handle = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, handle);

        fixed (byte* ptr = rgbaData)
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                width,
                height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                ptr
            );
        }

        _gl.GenerateMipmap(TextureTarget.Texture2D);

        // Respect current filtering preference on lazily loaded materials
        var minFilter = _bilinear ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Nearest;
        var magFilter = _bilinear ? TextureMagFilter.Linear : TextureMagFilter.Nearest;
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)minFilter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)magFilter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);

        return handle;
    }

    private bool _bilinear = false;
    private bool _disposed = false;
    public bool IsBilinear => _bilinear;

    public void ToggleFiltering()
    {
        if (_disposed)
            return;

        _bilinear = !_bilinear;
        var minFilter = _bilinear ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Nearest;
        var magFilter = _bilinear ? TextureMagFilter.Linear : TextureMagFilter.Nearest;

        foreach (var cels in _textureCelsCache.Values)
        {
            foreach (var info in cels)
            {
                if (info.Handle == 0 || info.Handle == DefaultWhiteTexture)
                    continue;
                _gl.BindTexture(TextureTarget.Texture2D, info.Handle);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)minFilter);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)magFilter);
            }
        }
    }

    private unsafe uint CreateDefaultWhiteTexture()
    {
        uint handle = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, handle);

        byte[] whitePixel = [200, 200, 200, 255];
        fixed (byte* ptr = whitePixel)
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                1,
                1,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                ptr
            );
        }

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        return handle;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var cels in _textureCelsCache.Values)
        {
            foreach (var info in cels)
            {
                if (info.Handle != 0 && info.Handle != DefaultWhiteTexture)
                    _gl.DeleteTexture(info.Handle);
            }
        }
        _textureCelsCache.Clear();
        _gl.DeleteTexture(DefaultWhiteTexture);
    }
}
