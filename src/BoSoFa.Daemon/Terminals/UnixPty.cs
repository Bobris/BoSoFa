using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using BoSoFa.Daemon.Harnesses;

namespace BoSoFa.Daemon.Terminals;

/// <summary>
/// A pseudo-terminal on Linux and macOS. The shell starts through <see cref="HelperCommand"/>, a fresh process of this
/// same executable: forking inside a running .NET process is not safe, and the session and controlling terminal must
/// be set between fork and exec.
/// </summary>
public sealed partial class UnixPty : IPty
{
    public const string HelperCommand = "__pty-exec";
    const int ORdWr = 2, EIntr = 4, PollIn = 1, PollTimeoutMs = 200, SigPipe = 13, SigHup = 1;
    static readonly int ONoCtty = OperatingSystem.IsLinux() ? 0x100 : 0x20000;
    static readonly nuint TiocSWinSz = OperatingSystem.IsLinux() ? 0x5414u : 0x80087467u;
    static readonly nuint TiocSCTty = OperatingSystem.IsLinux() ? 0x540Eu : 0x20007461u;

    readonly int _master, _slave;
    readonly Process _process;
    int _stopped, _slaveClosed, _masterClosed;

    public UnixPty(string shell, string[] arguments, string workingDirectory, Dictionary<string, string> environment, int cols, int rows)
    {
        _master = posix_openpt(ORdWr | ONoCtty);
        if (_master < 0) throw new IOException($"posix_openpt failed ({Marshal.GetLastPInvokeError()}).");
        Span<byte> name = stackalloc byte[256];
        if (grantpt(_master) != 0 || unlockpt(_master) != 0 || ptsname_r(_master, ref name[0], name.Length) != 0)
        {
            close(_master);
            throw new IOException($"Could not prepare a terminal ({Marshal.GetLastPInvokeError()}).");
        }
        var slavePath = Encoding.UTF8.GetString(name[..name.IndexOf((byte)0)]);
        // Held until the shell has exited: without any open slave a master's read fails before the shell gets to open it.
        _slave = open(slavePath, ORdWr | ONoCtty);
        Resize(cols, rows);
        var start = new ProcessStartInfo(HelperPath()) { WorkingDirectory = workingDirectory, UseShellExecute = false };
        start.ArgumentList.Add(HelperCommand);
        start.ArgumentList.Add(slavePath);
        start.ArgumentList.Add(shell);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (key, value) in environment) start.Environment[key] = value;
        try { _process = Process.Start(start)!; }
        catch
        {
            close(_slave);
            close(_master);
            throw;
        }
    }

    static string HelperPath()
    {
        // In a test host the current process is not the daemon, but the daemon's apphost sits next to it.
        var sibling = Path.Combine(AppContext.BaseDirectory, "bosofad");
        return File.Exists(sibling) ? sibling : Environment.ProcessPath!;
    }

    public int Read(Span<byte> buffer)
    {
        var poll = new PollFd { Fd = _master, Events = PollIn };
        while (Volatile.Read(ref _stopped) == 0)
        {
            var ready = poll_(ref poll, 1, PollTimeoutMs);
            if (ready < 0 && Marshal.GetLastPInvokeError() != EIntr) return 0;
            if (ready <= 0) continue;
            var count = (int)read(_master, ref buffer[0], buffer.Length);
            if (count >= 0) return count; // Linux reports a hung-up terminal as EIO, macOS as 0
            if (Marshal.GetLastPInvokeError() != EIntr) return 0;
        }
        return 0;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0 && Volatile.Read(ref _stopped) == 0)
        {
            var count = (int)write(_master, in data[0], data.Length);
            if (count > 0) data = data[count..];
            else if (Marshal.GetLastPInvokeError() != EIntr) return;
        }
    }

    public unsafe void Resize(int cols, int rows)
    {
        var size = new WinSize { Rows = (ushort)rows, Cols = (ushort)cols };
        Ioctl(_master, TiocSWinSz, (nint)(&size));
    }

    public async Task<int> WaitForExitAsync()
    {
        await _process.WaitForExitAsync();
        if (Interlocked.Exchange(ref _slaveClosed, 1) == 0) close(_slave);
        return _process.ExitCode;
    }

    public void StopReading() => Volatile.Write(ref _stopped, 1);

    public void Terminate()
    {
        if (_process.HasExited) return;
        kill(_process.Id, SigHup);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _slaveClosed, 1) == 0) close(_slave);
        if (Interlocked.Exchange(ref _masterClosed, 1) == 0) close(_master);
        _process.Dispose();
    }

    /// <summary>Runs in the helper process: makes the terminal this process's own and becomes the shell.</summary>
    public static int Exec(string[] args)
    {
        // args: __pty-exec <slave> <shell> [arguments…]
        setsid();
        var slave = open(args[1], ORdWr);
        if (slave < 0) return 126;
        Ioctl(slave, TiocSCTty, 0);
        dup2(slave, 0);
        dup2(slave, 1);
        dup2(slave, 2);
        if (slave > 2) close(slave);
        signal(SigPipe, 0); // the .NET runtime ignores SIGPIPE, and an ignored signal stays ignored across exec
        var argv = new string?[args.Length - 1];
        Array.Copy(args, 2, argv, 0, args.Length - 2);
        argv[^1] = null;
        execvp(args[2], argv);
        return 127;
    }

    /// <summary>ioctl is variadic, and on Apple Silicon variadic arguments travel on the stack: the padding moves it there.</summary>
    static int Ioctl(int fd, nuint request, nint argument) =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ioctl_stack(fd, request, 0, 0, 0, 0, 0, 0, argument)
            : ioctl_registers(fd, request, argument);

    [StructLayout(LayoutKind.Sequential)]
    struct WinSize
    {
        public ushort Rows, Cols, XPixel, YPixel;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PollFd
    {
        public int Fd;
        public short Events, ReturnedEvents;
    }

    static UnixPty() => NativeLibrary.SetDllImportResolver(typeof(UnixPty).Assembly, static (name, _, _) =>
    {
        // Debian-style systems have no plain libc.so without the development package.
        if (name != Libc || !OperatingSystem.IsLinux()) return 0;
        foreach (var candidate in new[] { "libc.so.6", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1" })
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        return 0;
    });

    const string Libc = "libc";

    [LibraryImport(Libc, SetLastError = true)] private static partial int posix_openpt(int flags);
    [LibraryImport(Libc, SetLastError = true)] private static partial int grantpt(int fd);
    [LibraryImport(Libc, SetLastError = true)] private static partial int unlockpt(int fd);
    [LibraryImport(Libc, SetLastError = true)] private static partial int ptsname_r(int fd, ref byte buffer, nint length);
    [LibraryImport(Libc, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int open(string path, int flags);
    [LibraryImport(Libc, SetLastError = true)] private static partial int close(int fd);
    [LibraryImport(Libc, SetLastError = true)] private static partial nint read(int fd, ref byte buffer, nint count);
    [LibraryImport(Libc, SetLastError = true)] private static partial nint write(int fd, in byte buffer, nint count);
    [LibraryImport(Libc, EntryPoint = "poll", SetLastError = true)] private static partial int poll_(ref PollFd fds, nuint count, int timeout);
    [LibraryImport(Libc, SetLastError = true)] private static partial int setsid();
    [LibraryImport(Libc, SetLastError = true)] private static partial int dup2(int from, int to);
    [LibraryImport(Libc, SetLastError = true)] private static partial int kill(int pid, int signal);
    [LibraryImport(Libc)] private static partial nint signal(int number, nint handler);
    [LibraryImport(Libc, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)] private static partial int execvp(string file, string?[] argv);
    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)] private static partial int ioctl_registers(int fd, nuint request, nint argument);
    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ioctl_stack(int fd, nuint request, nint p2, nint p3, nint p4, nint p5, nint p6, nint p7, nint argument);
}
