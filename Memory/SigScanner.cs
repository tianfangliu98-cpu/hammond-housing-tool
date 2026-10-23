namespace FF14HousingKit.Memory;

/// <summary>
/// 特征码扫描。支持 "48 89 1D ?? ?? ?? ?? EB 07" 这种带通配的写法。
/// 分块读模块内存，块间保留 pattern 长度的重叠，避免特征码跨块漏掉。
/// </summary>
public static class SigScanner
{
    private const int ChunkSize = 1 << 20; // 1 MB

    public static IntPtr Scan(GameProcess process, string pattern)
    {
        var (bytes, mask) = Parse(pattern);
        var patternLength = bytes.Length;

        if (patternLength == 0 || patternLength > ChunkSize)
            return IntPtr.Zero;

        var baseAddress = process.ModuleBase;
        var totalSize = process.ModuleSize;
        if (totalSize <= 0)
            return IntPtr.Zero;

        var buffer = new byte[ChunkSize + patternLength];
        var offset = 0;

        while (offset < totalSize)
        {
            var readLength = Math.Min(ChunkSize + patternLength, totalSize - offset);
            if (!process.ReadChunk(IntPtr.Add(baseAddress, offset), buffer, readLength))
            {
                offset += ChunkSize;
                continue;
            }

            var limit = Math.Min(ChunkSize, readLength - patternLength + 1);
            for (var i = 0; i < limit; i++)
            {
                if (!MatchAt(buffer, i, bytes, mask, patternLength))
                    continue;

                return IntPtr.Add(baseAddress, offset + i);
            }

            offset += ChunkSize;
        }

        return IntPtr.Zero;
    }

    /// <summary>解析签名处 RIP 相对寻址得到的静态地址；isPointer=true 时再解引用一次。</summary>
    public static IntPtr ResolveStaticAddress(GameProcess process, IntPtr signatureAddress, int displacementOffset, bool isPointer)
    {
        var displacement = process.Read<int>(IntPtr.Add(signatureAddress, displacementOffset));
        var address = IntPtr.Add(signatureAddress, displacementOffset + 4 + displacement);

        if (isPointer)
        {
            var value = process.ReadPtr(address);
            return value;
        }

        return address;
    }

    private static bool MatchAt(byte[] haystack, int index, byte[] bytes, bool[] mask, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (mask[i] && haystack[index + i] != bytes[i])
                return false;
        }

        return true;
    }

    private static (byte[] Bytes, bool[] Mask) Parse(string pattern)
    {
        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        var mask = new bool[tokens.Length];

        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is "??" or "?")
            {
                bytes[i] = 0;
                mask[i] = false;
            }
            else
            {
                bytes[i] = Convert.ToByte(tokens[i], 16);
                mask[i] = true;
            }
        }

        return (bytes, mask);
    }
}
