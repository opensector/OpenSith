using System;
using System.IO;
using OpenSith.Core.Vfs;
using Xunit;

namespace OpenSith.Tests;

public class VfsAnimationLookupTest
{
    private const string SteamRes2Path = @"C:\Program Files (x86)\Steam\steamapps\common\Star Wars Jedi Knight\Resource\Res2.gob";

    [Fact]
    public void VfsFindsPupAndKey()
    {
        if (!File.Exists(SteamRes2Path)) return;

        var vfs = new VirtualFileSystem();
        vfs.Mount(GobArchive.FromFile(SteamRes2Path));

        Assert.True(vfs.FileExists("misc/pup/rystr.pup"));
        Assert.True(vfs.FileExists("3do/key/ryidleg.key"));
        Assert.True(vfs.FileExists("3do/key/rystandg.key"));
    }
}
