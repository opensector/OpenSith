using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenSith.Core.Vfs;

/// <summary>
/// Composite Virtual File System that supports layered mounts (priority ordered).
/// Enables retail GOB archives to be mounted beneath user mods or HD texture overrides.
/// </summary>
public sealed class VirtualFileSystem : IVirtualFileSystem
{
    private readonly List<IVirtualFileSource> _sources = new();

    public void Mount(GobArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        _sources.Insert(0, new GobFileSource(archive));
    }

    public void MountDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"Mount directory not found: {directoryPath}");

        _sources.Insert(0, new DirectoryFileSource(directoryPath));
    }

    public bool FileExists(string path)
    {
        string normalized = GobArchive.NormalizePath(path);
        foreach (var source in _sources)
        {
            if (source.Exists(normalized))
                return true;
        }
        return false;
    }

    public byte[] ReadAllBytes(string path)
    {
        string normalized = GobArchive.NormalizePath(path);
        foreach (var source in _sources)
        {
            if (source.TryReadBytes(normalized, out var bytes))
                return bytes;
        }
        throw new FileNotFoundException($"Asset not found in VFS: '{path}'");
    }

    public Stream OpenRead(string path)
    {
        byte[] bytes = ReadAllBytes(path);
        return new MemoryStream(bytes, writable: false);
    }

    public string ReadAllText(string path)
    {
        byte[] bytes = ReadAllBytes(path);
        return Encoding.ASCII.GetString(bytes);
    }

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }
        _sources.Clear();
    }

    private interface IVirtualFileSource : IDisposable
    {
        bool Exists(string path);
        bool TryReadBytes(string path, out byte[] bytes);
    }

    private sealed class GobFileSource : IVirtualFileSource
    {
        private readonly GobArchive _archive;
        public GobFileSource(GobArchive archive) => _archive = archive;
        public bool Exists(string path) => _archive.Contains(path);
        public bool TryReadBytes(string path, out byte[] bytes)
        {
            if (_archive.Contains(path))
            {
                bytes = _archive.ReadAllBytes(path);
                return true;
            }
            bytes = Array.Empty<byte>();
            return false;
        }
        public void Dispose() => _archive.Dispose();
    }

    private sealed class DirectoryFileSource : IVirtualFileSource
    {
        private readonly string _rootPath;
        public DirectoryFileSource(string rootPath) => _rootPath = rootPath;

        public bool Exists(string path)
        {
            string fullPath = Path.Combine(_rootPath, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(fullPath);
        }

        public bool TryReadBytes(string path, out byte[] bytes)
        {
            string fullPath = Path.Combine(_rootPath, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                bytes = File.ReadAllBytes(fullPath);
                return true;
            }
            bytes = Array.Empty<byte>();
            return false;
        }

        public void Dispose() { }
    }
}
