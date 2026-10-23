using FF14HousingKit.Memory;

namespace FF14HousingKit.Game;

/// <summary>
/// 「解除家具限制」—— 对应肥肥的复选框、BDTH 的 Place Anywhere。
/// 做法是把游戏里三处距离/墙面校验的开关字节从 0 改成 1。
/// 只改数据字节，不改指令，游戏一重启就恢复原样。
/// </summary>
public sealed class PlaceAnywhereService
{
    private readonly GameProcess process;
    private readonly IntPtr[] patchAddresses = new IntPtr[3];

    private static readonly string[] Signatures =
    {
        // 注意：立即数字节（第 6/11/6 位）原来是 00，但 BDTH 的 Place Anywhere 会把它改成 01，
        // 所以这里必须通配，否则一旦对方开过就再也扫不到。
        "C6 83 ?? ?? 00 00 ?? 0F 29 44 24",
        "48 85 C0 74 ?? C6 87 ?? ?? 00 00 ??",
        "C6 87 83 01 00 00 ?? 48 83 C4",
    };

    private static readonly int[] SignatureOffsets = { 6, 11, 6 };

    public PlaceAnywhereService(GameProcess process)
    {
        this.process = process;
    }

    public bool IsResolved => patchAddresses.All(x => x != IntPtr.Zero);

    public bool Enabled { get; private set; }

    /// <summary>诊断用：第一处 patch 的地址。</summary>
    public IntPtr FirstPatchAddress => patchAddresses.Length > 0 ? patchAddresses[0] : IntPtr.Zero;

    public bool Resolve()
    {
        for (var i = 0; i < Signatures.Length; i++)
        {
            var hit = SigScanner.Scan(process, Signatures[i]);
            patchAddresses[i] = hit == IntPtr.Zero
                ? IntPtr.Zero
                : IntPtr.Add(hit, SignatureOffsets[i]);
        }

        return IsResolved;
    }

    public bool SetState(bool enabled)
    {
        if (!IsResolved)
            return false;

        var value = (byte)(enabled ? 1 : 0);

        foreach (var address in patchAddresses)
            process.WriteProtected(address, value);

        Enabled = enabled;
        return true;
    }
}
