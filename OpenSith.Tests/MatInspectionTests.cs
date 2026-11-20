using System;
using System.Buffers.Binary;
using System.IO;
using OpenSith.Core.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace OpenSith.Tests;

public class MatInspectionTests
{
    private readonly ITestOutputHelper _output;
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    public MatInspectionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("3do/mat/00fmtl1.mat")]
    [InlineData("3do/mat/00dbig2.mat")]
    [InlineData("3do/mat/00cab01.mat")]
    public void VerifyMatDimensionsAndData(string matPath)
    {
        if (!File.Exists(SteamRes2Path)) return;

        using var archive = GobArchive.FromFile(SteamRes2Path);
        byte[] bytes = archive.ReadAllBytes(matPath);

        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(116, 4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(120, 4));
        int mipmaps = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(136, 4));

        _output.WriteLine($"{matPath}: Width={width}, Height={height}, Mipmaps={mipmaps}, FileSize={bytes.Length}");

        int expectedPixelBytes = width * height;
        int pixelDataOffset = 140;
        int remainingBytes = bytes.Length - pixelDataOffset;

        _output.WriteLine($"Expected pixels: {expectedPixelBytes}, Remaining bytes: {remainingBytes}");
        Assert.True(width > 0 && width <= 1024);
        Assert.True(height > 0 && height <= 1024);
        Assert.Equal(expectedPixelBytes, width * height);
        Assert.True(remainingBytes >= expectedPixelBytes);
    }
}
