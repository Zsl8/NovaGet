using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NovaGet.Core.Engine.Storage;

/// <summary>
/// The single <c>.ngpart</c> file a download writes into. Every connection writes its bytes at their final
/// offset (positional I/O on one handle), so a normal download never needs a merge step. On NTFS the file is
/// marked sparse before it is sized, so writing near the end doesn't force Windows to zero-fill the gap first.
/// </summary>
internal sealed partial class TempFile : IDisposable
{
    private readonly FileStream _stream;

    private TempFile(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public SafeFileHandle Handle => _stream.SafeFileHandle;

    public long Length => RandomAccess.GetLength(Handle);

    /// <summary>Creates (or truncates) the file and sizes it to <paramref name="size"/> when known.</summary>
    public static TempFile Create(string path, long size)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var stream = OpenStream(path, FileMode.Create);
        var file = new TempFile(path, stream);
        try
        {
            file.MakeSparse();
            if (size > 0)
            {
                stream.SetLength(size);
            }

            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Opens an existing partial file for resuming; returns null if it is missing.</summary>
    public static TempFile? OpenExisting(string path, long expectedSize)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var stream = OpenStream(path, FileMode.Open);
        var file = new TempFile(path, stream);
        if (expectedSize > 0 && stream.Length != expectedSize)
        {
            stream.SetLength(expectedSize);
        }

        return file;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken cancellationToken) =>
        RandomAccess.WriteAsync(Handle, data, offset, cancellationToken);

    /// <summary>Forces written data to disk (FlushFileBuffers / fsync). Called before a checkpoint is saved.</summary>
    public void FlushToDisk() => _stream.Flush(flushToDisk: true);

    public void SetLength(long length) => _stream.SetLength(length);

    public void Dispose() => _stream.Dispose();

    private static FileStream OpenStream(string path, FileMode mode) => new(path, new FileStreamOptions
    {
        Mode = mode,
        Access = FileAccess.ReadWrite,
        // Readers (antivirus, previews) may look; nobody else may write.
        Share = FileShare.Read | FileShare.Delete,
        Options = FileOptions.Asynchronous | FileOptions.RandomAccess,
        BufferSize = 0,
    });

    private void MakeSparse()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // ext4/APFS allocate lazily already.
        }

        const uint FsctlSetSparse = 0x000900C4;
        // Best effort: FAT/exFAT don't support sparse files; the download still works, just with zero-filling.
        _ = DeviceIoControl(Handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}

internal static class DiskSpace
{
    /// <summary>Free bytes available to the current user on the volume holding <paramref name="path"/>, or null if unknown.</summary>
    public static long? AvailableBytes(string path)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL on Windows, ENOSPC elsewhere.</summary>
    public static bool IsDiskFull(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 0x70 or 0x27 || ex.HResult == 28 || ex.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase);
    }
}
