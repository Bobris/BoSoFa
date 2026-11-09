using System.Runtime.InteropServices;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon;

/// <summary>What this computer has: its platform, processors, memory, and the disk holding the user's home folder.
/// Free memory and disk change all the time, so the server asks when someone looks (<see cref="MachineResourcesRequest"/>);
/// enrollment and every connect also send one measurement, shown while the machine is offline.</summary>
public static partial class MachineInventory
{
    public static MachineResources Measure()
    {
        var (memory, available) = Memory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        long diskBytes = 0, diskFree = 0;
        var diskPath = home;
        try
        {
            var drive = new DriveInfo(home);
            (diskPath, diskBytes, diskFree) = (drive.RootDirectory.FullName, drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new(Platform(), Environment.ProcessorCount, memory, available, diskPath, diskBytes, diskFree, DateTime.UtcNow);
    }

    /// <summary>"macOS 26.1", the distribution's name ("Ubuntu 24.04.1 LTS"), or "Windows 10.0.26100".</summary>
    static string Platform()
    {
        if (OperatingSystem.IsMacOS()) return $"macOS {Environment.OSVersion.Version.ToString(Environment.OSVersion.Version.Build > 0 ? 3 : 2)}";
        if (OperatingSystem.IsWindows()) return $"Windows {Environment.OSVersion.Version}";
        if (OperatingSystem.IsLinux())
            try
            {
                foreach (var line in File.ReadLines("/etc/os-release"))
                    if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                        return line["PRETTY_NAME=".Length..].Trim('"');
            }
            catch (IOException) { }
        return RuntimeInformation.OSDescription;
    }

    /// <summary>Total and available physical memory in bytes; zero where they cannot be read.</summary>
    static (long Total, long Available) Memory()
    {
        try
        {
            if (OperatingSystem.IsLinux()) return LinuxMemory();
            if (OperatingSystem.IsMacOS()) return MacMemory();
            if (OperatingSystem.IsWindows()) return WindowsMemory();
        }
        catch (Exception exception) when (exception is IOException or FormatException or DllNotFoundException or EntryPointNotFoundException) { }
        return (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, 0);
    }

    /// <summary>MemAvailable is the kernel's own estimate of what can be used without swapping.</summary>
    static (long, long) LinuxMemory()
    {
        long total = 0, available = 0;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = Kilobytes(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = Kilobytes(line);
        }
        return (total, available);

        static long Kilobytes(string line) =>
            long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], System.Globalization.CultureInfo.InvariantCulture) * 1024;
    }

    // macOS: hw.memsize and the VM statistics (host_statistics64, HOST_VM_INFO64). Available is free, inactive,
    // speculative and purgeable pages: what can be handed out without compressing or swapping.
    const int HostVmInfo64 = 4, HostVmInfo64Count = 38;

    [LibraryImport("libSystem.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sysctlbyname(string name, out long value, ref nint size, nint newValue, nint newSize);

    [LibraryImport("libSystem.dylib")]
    private static partial uint mach_host_self();

    [LibraryImport("libSystem.dylib")]
    private static partial int host_page_size(uint host, out nuint pageSize);

    [LibraryImport("libSystem.dylib")]
    private static partial int host_statistics64(uint host, int flavor, [Out] int[] info, ref int count);

    static (long, long) MacMemory()
    {
        nint size = sizeof(long);
        var total = sysctlbyname("hw.memsize", out var memsize, ref size, 0, 0) == 0 ? memsize : 0;
        var host = mach_host_self();
        var info = new int[HostVmInfo64Count];
        var count = HostVmInfo64Count;
        if (host_page_size(host, out var pageSize) != 0 || host_statistics64(host, HostVmInfo64, info, ref count) != 0) return (total, 0);
        // vm_statistics64: free_count [0], active [1], inactive [2], wire [3], nine 64-bit counters, purgeable_count [22],
        // speculative_count [23].
        long pages = (uint)info[0] + (long)(uint)info[2] + (uint)info[22] + (uint)info[23];
        return (total, Math.Min(total, pages * (long)pageSize));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    static (long, long) WindowsMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhys, (long)status.AvailPhys) : (0, 0);
    }
}
