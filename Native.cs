using System.Runtime.InteropServices;

namespace OwkCopy;

static class Native
{
    public delegate uint CopyProgressRoutine(long totalFileSize, long totalBytesTransferred,
        long streamSize, long streamBytesTransferred, uint streamNumber, uint callbackReason,
        IntPtr sourceFile, IntPtr destinationFile, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CopyFileEx(string existingFileName, string newFileName,
        CopyProgressRoutine? progressRoutine, IntPtr data, IntPtr cancel, uint copyFlags);

    public const uint COPY_FILE_ALLOW_DECRYPTED_DESTINATION = 0x00000008;
    public const uint COPY_FILE_NO_BUFFERING = 0x00001000;
    public const uint PROGRESS_CONTINUE = 0;
    public const uint PROGRESS_CANCEL = 1;
    public const int ERROR_REQUEST_ABORTED = 1235;

    /// <summary>Adds the \\?\ prefix so Win32 calls accept paths longer than MAX_PATH.</summary>
    public static string LongPath(string path)
    {
        if (path.StartsWith(@"\\?\")) return path;
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\") ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}
