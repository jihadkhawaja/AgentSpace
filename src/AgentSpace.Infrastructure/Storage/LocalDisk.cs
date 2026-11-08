using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentSpace;

internal static partial class LocalDisk
{
    private const int CloseOnExec = 0x80000;
    public static void CheckOrdinary(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 ||
            attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new IOException("The workspace storage contains an unexpected file type or link.");
    }

    public static void EnsureDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is not null) EnsureDirectory(parent);
        if (Directory.Exists(path))
        {
            CheckOrdinary(path, directory: true);
            return;
        }

        if (OperatingSystem.IsLinux())
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(path);
        CheckOrdinary(path, directory: true);
        if (parent is not null) SyncDirectory(parent);
    }

    public static void Publish(string temporary, string destination, bool replace)
    {
        if (OperatingSystem.IsWindows())
        {
            const uint writeThrough = 8;
            const uint replaceExisting = 1;
            if (!MoveFileEx(temporary, destination, writeThrough | (replace ? replaceExisting : 0)))
                throw new IOException("Unable to publish workspace storage file.", new Win32Exception());
        }
        else
        {
            File.Move(temporary, destination, replace);
            SyncDirectory(Path.GetDirectoryName(destination)!);
        }
    }

    public static void SyncDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        const int oDirectory = 0x10000;
        var fd = Open(path, oDirectory | CloseOnExec);
        if (fd < 0) throw new IOException("Unable to open workspace directory.", new Win32Exception());
        try
        {
            if (Fsync(fd) != 0) throw new IOException("Unable to flush workspace directory.", new Win32Exception());
        }
        finally { Close(fd); }
    }

    public static IDisposable? TryAcquire(string path)
    {
        if (File.Exists(path)) CheckOrdinary(path, directory: false);
        if (OperatingSystem.IsLinux()) return TryAcquireLinux(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
    }

    private static UnixLease? TryAcquireLinux(string path)
    {
        const int readWrite = 2;
        const int create = 0x40;
        const int noFollow = 0x20000;
        var descriptor = OpenLock(path, readWrite | create | noFollow | CloseOnExec, 0x180); // 0600
        if (descriptor < 0) throw new IOException("Unable to open workspace lock.", new Win32Exception());
        var lease = new UnixLease(descriptor);
        const int exclusive = 2;
        const int nonBlocking = 4;
        if (Flock(lease, exclusive | nonBlocking) == 0) return lease;
        var code = Marshal.GetLastPInvokeError();
        lease.Dispose();
        if (code == 11) return null;
        throw new IOException("Unable to lock workspace storage.", new Win32Exception(code));
    }

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string source, string destination, uint flags);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenLock(string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(UnixLease fd, int operation);

    private sealed class UnixLease : SafeHandleMinusOneIsInvalid
    {
        public UnixLease(int descriptor) : base(ownsHandle: true) => SetHandle(descriptor);
        protected override bool ReleaseHandle() => LocalDisk.Close((int)handle) == 0;
    }
}
