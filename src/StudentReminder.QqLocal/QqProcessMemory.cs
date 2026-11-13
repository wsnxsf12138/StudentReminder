using System.Runtime.InteropServices;

namespace StudentReminder.QqLocal;

/// <summary>Win32 process-memory primitives: open process, iterate regions, chunked reads.</summary>
public sealed class QqProcessMemory : IDisposable
{
    private const int ProcessQueryInformation = 0x0400;
    private const int ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const long MaxScanAddress = 0x7FFF_FFFF_FFFF;
    private static readonly HashSet<uint> ReadableProtections = new() { 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80 };
    private const long MaxRegionBytes = 500 * 1024 * 1024;

    private IntPtr _handle;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        private uint _pad1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        private uint _pad2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, ulong baseAddress, byte[] buffer, IntPtr size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(IntPtr process, ulong address, ref MemoryBasicInformation info, IntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private QqProcessMemory(IntPtr handle) => _handle = handle;

    public static QqProcessMemory? Open(int pid)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        return handle == IntPtr.Zero ? null : new QqProcessMemory(handle);
    }

    public byte[]? Read(ulong address, int size)
    {
        var buffer = new byte[size];
        if (!ReadProcessMemory(_handle, address, buffer, (IntPtr)size, out var read) || read.ToInt64() <= 0)
            return null;
        return read.ToInt64() == size ? buffer : buffer[..read.ToInt32()];
    }

    public IEnumerable<(ulong Base, byte[] Data)> ReadChunks(int chunkSize = 2 * 1024 * 1024, int overlap = 0)
    {
        foreach (var (regionBase, regionSize) in Regions())
        {
            long offset = 0;
            byte[] tail = [];
            ulong tailBase = regionBase;
            while (offset < (long)regionSize)
            {
                var current = (int)Math.Min(chunkSize, (long)regionSize - offset);
                var chunk = Read(regionBase + (ulong)offset, current) ?? [];
                var dataBase = tail.Length > 0 ? tailBase : regionBase + (ulong)offset;
                var data = tail.Length > 0 ? [.. tail, .. chunk] : chunk;
                if (data.Length > 0)
                {
                    yield return (dataBase, data);
                    if (overlap > 0)
                    {
                        tail = data[^Math.Min(overlap, data.Length)..];
                        tailBase = dataBase + (ulong)(data.Length - tail.Length);
                    }
                    else
                    {
                        tail = [];
                        tailBase = regionBase + (ulong)(offset + current);
                    }
                }
                else
                {
                    tail = [];
                    tailBase = regionBase + (ulong)(offset + current);
                }
                offset += current;
            }
        }
    }

    private IEnumerable<(ulong Base, ulong Size)> Regions()
    {
        ulong address = 0;
        var info = new MemoryBasicInformation();
        while (address < (ulong)MaxScanAddress)
        {
            var result = VirtualQueryEx(_handle, address, ref info, (IntPtr)Marshal.SizeOf<MemoryBasicInformation>());
            if (result == IntPtr.Zero) yield break;
            if (info.State == MemCommit && ReadableProtections.Contains(info.Protect)
                && info.RegionSize is > 0 and < (ulong)MaxRegionBytes)
                yield return (info.BaseAddress, info.RegionSize);
            var next = info.BaseAddress + info.RegionSize;
            if (next <= address) yield break;
            address = next;
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

/// <summary>Best-effort SeDebugPrivilege activation; a normal user account simply lacks it.</summary>
public static class QqProcessPrivilege
{
    private const int TokenAdjustPrivileges = 0x0020;
    private const int TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, ref Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static bool EnableDebugPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) return false;
        try
        {
            var luid = new Luid();
            if (!LookupPrivilegeValue(null, "SeDebugPrivilege", ref luid)) return false;
            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled }
            };
            return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
