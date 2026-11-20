using System;
using System.IO;
using System.Linq;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

public class GobArchiveTests
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    [Fact]
    public void SyntheticGob_Roundtrip_ReadsEntriesCorrectly()
    {
        // Construct a mini synthetic GOB in memory
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Header: "GOB ", version 20, index offset 12, file count 2
        writer.Write(new char[] { 'G', 'O', 'B', ' ' });
        writer.Write(20);
        writer.Write(12);
        writer.Write(2);

        int dataStart = 12 + 4 + (2 * 136); // Header (12) + count (4) + 2 entries (272) = 288
        byte[] payload1 = new byte[] { 1, 2, 3, 4, 5 };
        byte[] payload2 = new byte[] { 10, 20, 30 };

        // Entry 1
        writer.Write(dataStart);
        writer.Write(payload1.Length);
        byte[] name1 = new byte[128];
        System.Text.Encoding.ASCII.GetBytes(@"misc\test.txt").CopyTo(name1, 0);
        writer.Write(name1);

        // Entry 2
        writer.Write(dataStart + payload1.Length);
        writer.Write(payload2.Length);
        byte[] name2 = new byte[128];
        System.Text.Encoding.ASCII.GetBytes(@"3do\model.3do").CopyTo(name2, 0);
        writer.Write(name2);

        // Payloads
        writer.Write(payload1);
        writer.Write(payload2);

        ms.Position = 0;

        using var archive = GobArchive.FromStream(ms, leaveOpen: true);
        Assert.Equal(2, archive.EntryCount);
        Assert.True(archive.Contains("misc/test.txt"));
        Assert.True(archive.Contains(@"misc\test.txt"));
        Assert.True(archive.Contains("3do/model.3do"));

        byte[] read1 = archive.ReadAllBytes("misc/test.txt");
        Assert.Equal(payload1, read1);

        byte[] read2 = archive.ReadAllBytes("3DO/MODEL.3DO");
        Assert.Equal(payload2, read2);
    }

    [Fact]
    public void RetailSteamRes2Gob_IfPresent_ParsesSuccessfully()
    {
        if (!File.Exists(SteamRes2Path))
            return; // Skip on CI or machines without retail copy

        using var archive = GobArchive.FromFile(SteamRes2Path);
        Assert.True(archive.EntryCount > 1000, $"Expected >1000 files in Res2.gob, found {archive.EntryCount}");

        // Look for palettes
        var cmpEntries = archive.Entries.Where(e => e.Path.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(cmpEntries);

        // Verify we can read bytes of the first palette
        var firstCmp = cmpEntries.First();
        byte[] data = archive.ReadAllBytes(firstCmp.Path);
        Assert.Equal(firstCmp.Length, data.Length);
    }
}
