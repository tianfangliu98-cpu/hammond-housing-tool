using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FF14HousingKit.Memory;

/// <summary>
/// 目标进程句柄与内存读写。只做最基础的 ReadProcessMemory / WriteProcessMemory 封装。
/// </summary>
public sealed unsafe class GameProcess : IDisposable
{
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;

    private IntPtr _handle;

    public Process Process { get; private init; } = null!;
    public IntPtr ModuleBase { get; private init; }
    public int ModuleSize { get; private init; }
    public string ModuleName { get; private init; } = string.Empty;

    public bool IsAlive => _handle != IntPtr.Zero && !Process.HasExited;

    public static GameProcess? TryAttach(string processName = "ffxiv_dx11")
    {
        var process = Process.GetProcessesByName(processName).FirstOrDefault();
        if (process is null)
            return null;

        var handle = OpenProcess(
            ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation,
            false,
            process.Id);

        if (handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"OpenProcess 失败（错误码 {Marshal.GetLastWin32Error()}）。尝试以管理员身份运行。");

        var module = process.MainModule
            ?? throw new InvalidOperationException("读取进程模块信息失败。");

        return new GameProcess
        {
            Process = process,
            _handle = handle,
            ModuleBase = module.BaseAddress,
            ModuleSize = module.ModuleMemorySize,
            ModuleName = module.ModuleName,
        };
    }

    public bool Read(IntPtr address, byte[] buffer, int length)
    {
        if (_handle == IntPtr.Zero)
            return false;

        return ReadProcessMemory(_handle, address, buffer, length, out var read) && read == length;
    }

    public bool Write(IntPtr address, byte[] buffer)
    {
        if (_handle == IntPtr.Zero)
            return false;

        return WriteProcessMemory(_handle, address, buffer, buffer.Length, out var written)
               && written == buffer.Length;
    }

    public T Read<T>(IntPtr address) where T : unmanaged
    {
        var size = sizeof(T);
        var buffer = new byte[size];
        if (!Read(address, buffer, size))
            return default;

        fixed (byte* p = buffer)
            return *(T*)p;
    }

    public IntPtr ReadPtr(IntPtr address) => Read<IntPtr>(address);

    public byte[] ReadBytes(IntPtr address, int length)
    {
        var buffer = new byte[length];
        Read(address, buffer, length);
        return buffer;
    }

    /// <summary>把模块镜像按块读出来做特征码扫描时用；这里是分块回调版本，避免一次读上百 MB。</summary>
    public bool ReadChunk(IntPtr address, byte[] buffer, int length) => Read(address, buffer, length);

    /// <summary>写只读页（.text 段），先临时改保护再写回。</summary>
    public bool WriteProtected(IntPtr address, byte value)
    {
        if (_handle == IntPtr.Zero || address == IntPtr.Zero)
            return false;

        const uint pageExecuteReadWrite = 0x40;
        if (!VirtualProtectEx(_handle, address, 1, pageExecuteReadWrite, out var oldProtect))
            return false;

        var ok = Write(address, new[] { value });
        VirtualProtectEx(_handle, address, 1, oldProtect, out _);
        return ok;
    }

    // ---------- 远程调用支持 ----------

    public IntPtr Handle => _handle;

    public IntPtr AllocateRemote(int size) =>
        VirtualAllocEx(_handle, IntPtr.Zero, (nuint)size, MemCommit | MemReserve, PageExecuteReadWrite);

    public bool FreeRemote(IntPtr address) =>
        _handle != IntPtr.Zero && VirtualFreeEx(_handle, address, 0, MemRelease);

    public IntPtr ExecuteRemote(IntPtr startAddress, int timeoutMs, out int exitCode)
    {
        exitCode = -1;
        if (_handle == IntPtr.Zero)
            return IntPtr.Zero;

        var thread = CreateRemoteThread(_handle, IntPtr.Zero, 0, startAddress, IntPtr.Zero, 0, out _);
        if (thread == IntPtr.Zero)
            return IntPtr.Zero;

        var wait = WaitForSingleObject(thread, (uint)timeoutMs);
        if (wait == WaitObject0 && GetExitCodeThread(thread, out var code))
            exitCode = (int)code;

        CloseHandle(thread);
        return thread;
    }

    public const uint MemCommit = 0x1000;
    public const uint MemReserve = 0x2000;
    public const uint MemRelease = 0x8000;
    public const uint PageExecuteReadWrite = 0x40;
    private const uint WaitObject0 = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr handle, IntPtr address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr handle, IntPtr address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr handle, IntPtr attributes, nuint stackSize,
        IntPtr startAddress, IntPtr parameter, uint flags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeThread(IntPtr handle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtectEx(IntPtr handle, IntPtr address, nuint size, uint newProtect, out uint oldProtect);

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, int size, out IntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
