using System;

namespace FF14HousingKit.Memory;

/// <summary>
/// 可复用的远程调用桩：一次分配可执行内存，之后反复改参数执行。
/// 避免每帧调用都做一次 VirtualAllocEx（BDTH 那种"每帧刷新模型"需要高频调用）。
///
/// 调用桩内容（x64）：
///   sub rsp, 0x28 ; mov rcx, arg1 ; mov rdx, arg2 ; mov rax, func ; call rax ; add rsp, 0x28 ; ret
/// </summary>
public sealed class RemoteCallStub : IDisposable
{
    private readonly GameProcess process;
    private readonly IntPtr memory;
    private bool disposed;

    public RemoteCallStub(GameProcess process)
    {
        this.process = process;
        memory = process.AllocateRemote(0x100);
    }

    public bool IsValid => memory != IntPtr.Zero && !disposed;

    public bool Invoke(IntPtr function, ulong arg1, ulong arg2, out int exitCode, int timeoutMs = 2000)
    {
        exitCode = -1;

        if (!IsValid || function == IntPtr.Zero)
            return false;

        if (!process.Write(memory, BuildShellcode(function, arg1, arg2)))
            return false;

        process.ExecuteRemote(memory, timeoutMs, out exitCode);
        return true;
    }

    private static byte[] BuildShellcode(IntPtr function, ulong arg1, ulong arg2)
    {
        var code = new List<byte>(48);

        code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });          // sub rsp, 0x28

        code.AddRange(new byte[] { 0x48, 0xB9 });                       // mov rcx, arg1
        code.AddRange(BitConverter.GetBytes(arg1));

        code.AddRange(new byte[] { 0x48, 0xBA });                       // mov rdx, arg2
        code.AddRange(BitConverter.GetBytes(arg2));

        code.AddRange(new byte[] { 0x48, 0xB8 });                       // mov rax, function
        code.AddRange(BitConverter.GetBytes((ulong)function.ToInt64()));

        code.AddRange(new byte[] { 0xFF, 0xD0 });                       // call rax

        code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });           // add rsp, 0x28
        code.Add(0xC3);                                                 // ret

        return code.ToArray();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        if (memory != IntPtr.Zero)
            process.FreeRemote(memory);

        disposed = true;
    }
}
