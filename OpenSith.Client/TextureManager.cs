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
    private readonly Dictionary<string, TextureInfo> _textureCache = new(StringComparer.OrdinalIgnoreCase);

    public uint DefaultWhiteTexture { get; }

    public readonly record struct TextureInfo(uint Handle, int Width, int Height);

    public TextureManager(GL gl, IVirtualFileSystem vfs, CmpPalette palette)
    {
        _gl = gl;
        _vfs = vfs;
        _palette = palette;
        DefaultWhiteTexture = CreateDefaultWhiteTexture();
    }

    public TextureInfo GetOrCreateTexture(string materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName))
            return new TextureInfo(DefaultWhiteTexture, 128, 128);

        if (_textureCache.TryGetValue(materialName, out var info))
            return info;

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
            info = new TextureInfo(DefaultWhiteTexture, 128, 128);
            _textureCache[materialName] = info;
            return info;
        }

        try
        {
            var mat = MatMaterial.Parse(matBytes);
            byte[] rgbaBuffer = mat.ToRgbaByteBuffer(_palette);

            uint handle = UploadTexture((uint)mat.Width, (uint)mat.Height, rgbaBuffer);
            info = new TextureInfo(handle, mat.Width, mat.Height);
            _textureCache[materialName] = info;
            return info;
        }
        catch
        {
            info = new TextureInfo(DefaultWhiteTexture, 128, 128);
            _textureCache[materialName] = info;
            return info;
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

        // Crisp retro texture filtering: Point sampling (Nearest) by default for authentic 1997 LucasArts pixel art!
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);

        return handle;
    }

    private bool _bilinear = false;
    public bool IsBilinear => _bilinear;

    public void ToggleFiltering()
    {
        _bilinear = !_bilinear;
        var minFilter = _bilinear ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Nearest;
        var magFilter = _bilinear ? TextureMagFilter.Linear : TextureMagFilter.Nearest;

        foreach (var info in _textureCache.Values)
        {
            _gl.BindTexture(TextureTarget.Texture2D, info.Handle);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)minFilter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)magFilter);
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
        foreach (var info in _textureCache.Values)
        {
            _gl.DeleteTexture(info.Handle);
        }
        _textureCache.Clear();
        _gl.DeleteTexture(DefaultWhiteTexture);
    }
}
