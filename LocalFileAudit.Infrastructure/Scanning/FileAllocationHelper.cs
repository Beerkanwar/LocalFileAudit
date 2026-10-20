using System.IO;
using System.Runtime.InteropServices;

namespace LocalFileAudit.Infrastructure.Scanning;

/// <summary>
/// Helper for on-demand query of allocated on-disk size and volume capacity stats.
/// Zero scan slowdown: Invoked purely on-demand when inspecting items in the UI.
/// </summary>
public static class FileAllocationHelper
{
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode)]
    private static extern uint GetCompressedFileSize(string lpFileName, out uint lpFileSizeHigh);

    private const uint INVALID_FILE_SIZE = 0xFFFFFFFF;

    /// <summary>
    /// Computes the actual on-disk allocated (compressed/sparse) size in bytes for a file.
    /// Returns null if inaccessible.
    /// </summary>
    public static long? GetAllocatedSizeBytes(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        try
        {
            if (!File.Exists(fullPath)) return null;

            uint high;
            uint low = GetCompressedFileSize(fullPath, out high);
            if (low == INVALID_FILE_SIZE && Marshal.GetLastWin32Error() != 0)
            {
                return null;
            }

            return ((long)high << 32) | low;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Retrieves total volume capacity and free space for the drive hosting the specified root or file path.
    /// </summary>
    public static (long TotalBytes, long FreeBytes)? GetVolumeSpace(string pathOrRoot)
    {
        if (string.IsNullOrWhiteSpace(pathOrRoot)) return null;

        try
        {
            var root = Path.GetPathRoot(pathOrRoot);
            if (string.IsNullOrEmpty(root)) return null;

            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                return (drive.TotalSize, drive.TotalFreeSpace);
            }
        }
        catch
        {
            // Best effort
        }

        return null;
    }
}
