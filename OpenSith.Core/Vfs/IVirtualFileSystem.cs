using System;
using System.IO;

namespace OpenSith.Core.Vfs;

/// <summary>
/// Unified Virtual File System (VFS) interface for mounting archives, directories, and reading game assets.
/// </summary>
public interface IVirtualFileSystem : IDisposable
{
    bool FileExists(string path);
    byte[] ReadAllBytes(string path);
    Stream OpenRead(string path);
    string ReadAllText(string path);
}
