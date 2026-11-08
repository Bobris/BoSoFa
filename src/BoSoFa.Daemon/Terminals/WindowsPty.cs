using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BoSoFa.Daemon.Terminals;

/// <summary>A pseudo-console (ConPTY) on Windows.</summary>
public sealed unsafe partial class WindowsPty : IPty
{
    const uint ExtendedStartupInfo = 0x80000, UnicodeEnvironment = 0x400, UseStdHandles = 0x100, Infinite = 0xFFFFFFFF;
    const nint PseudoConsoleAttribute = 0x20016;

    readonly FileStream _output, _input;
    readonly nint _process;
    nint _console;
    int _closed, _disposed;

    public WindowsPty(string shell, string workingDirectory, Dictionary<string, string> environment, int cols, int rows)
    {
        if (!CreatePipe(out var consoleInput, out var inputWrite, 0, 0) || !CreatePipe(out var outputRead, out var consoleOutput, 0, 0))
            throw new Win32Exception();
        var result = CreatePseudoConsole(Size(cols, rows), consoleInput, consoleOutput, 0, out _console);
        CloseHandle(consoleInput); // the pseudo-console holds its own copies
        CloseHandle(consoleOutput);
        if (result != 0) throw new Win32Exception(result);
        _output = new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read, 1, false);
        _input = new FileStream(new SafeFileHandle(inputWrite, true), FileAccess.Write, 1, false);

        nint listSize = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref listSize);
        var list = Marshal.AllocHGlobal(listSize);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref listSize)) throw new Win32Exception();
            try
            {
                if (!UpdateProcThreadAttribute(list, 0, PseudoConsoleAttribute, _console, sizeof(nint), 0, 0)) throw new Win32Exception();
                // No inherited standard handles: a daemon with redirected output must not leak them past the console.
                var startup = new StartupInfoEx { Cb = sizeof(StartupInfoEx), Flags = UseStdHandles, AttributeList = list };
                var environmentBlock = new StringBuilder();
                foreach (var (key, value) in environment.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
                    environmentBlock.Append(key).Append('=').Append(value).Append('\0');
                environmentBlock.Append('\0');
                var commandLine = ("\"" + shell + "\"\0").ToCharArray();
                var block = environmentBlock.ToString();
                fixed (char* command = commandLine, variables = block)
                    if (!CreateProcessW(null, command, 0, 0, false, ExtendedStartupInfo | UnicodeEnvironment, variables, workingDirectory,
                            ref startup, out var information))
                        throw new Win32Exception();
                    else
                    {
                        CloseHandle(information.Thread);
                        _process = information.Process;
                    }
            }
            finally { DeleteProcThreadAttributeList(list); }
        }
        finally { Marshal.FreeHGlobal(list); }
    }

    static Coord Size(int cols, int rows) => new() { X = (short)cols, Y = (short)rows };

    public int Read(Span<byte> buffer)
    {
        try { return _output.Read(buffer); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { return 0; }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        try
        {
            _input.Write(data);
            _input.Flush();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    public void Resize(int cols, int rows)
    {
        if (Volatile.Read(ref _closed) == 0) ResizePseudoConsole(_console, Size(cols, rows));
    }

    public Task<int> WaitForExitAsync() => Task.Factory.StartNew(() =>
    {
        WaitForSingleObject(_process, Infinite);
        GetExitCodeProcess(_process, out var code);
        return (int)code;
    }, TaskCreationOptions.LongRunning);

    /// <summary>Closing the pseudo-console ends the output pipe; it needs a reader draining the pipe meanwhile.</summary>
    public void StopReading()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        ClosePseudoConsole(_console);
        _console = 0;
    }

    public void Terminate() => TerminateProcess(_process, 1);

    public void Dispose()
    {
        StopReading();
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _output.Dispose();
        _input.Dispose();
        CloseHandle(_process);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Coord
    {
        public short X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx
    {
        public int Cb;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute;
        public uint Flags;
        public short ShowWindow, Reserved2Size;
        public nint Reserved2, StdInput, StdOutput, StdError, AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    const string Kernel32 = "kernel32.dll";

    [LibraryImport(Kernel32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out nint read, out nint write, nint attributes, int size);
    [LibraryImport(Kernel32)] private static partial int CreatePseudoConsole(Coord size, nint input, nint output, uint flags, out nint console);
    [LibraryImport(Kernel32)] private static partial int ResizePseudoConsole(nint console, Coord size);
    [LibraryImport(Kernel32)] private static partial void ClosePseudoConsole(nint console);
    [LibraryImport(Kernel32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
    [LibraryImport(Kernel32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returned);
    [LibraryImport(Kernel32)] private static partial void DeleteProcThreadAttributeList(nint list);
    [LibraryImport(Kernel32, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(string? application, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, char* environment, string workingDirectory, ref StartupInfoEx startup,
        out ProcessInformation information);
    [LibraryImport(Kernel32, SetLastError = true)] private static partial uint WaitForSingleObject(nint handle, uint milliseconds);
    [LibraryImport(Kernel32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint code);
    [LibraryImport(Kernel32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint code);
    [LibraryImport(Kernel32, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
