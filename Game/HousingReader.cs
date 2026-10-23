using System.Numerics;
using FF14HousingKit.Memory;

namespace FF14HousingKit.Game;

public enum HousingKind
{
    None,
    Indoor,
    Outdoor,
    Workshop,
}

public sealed class FurnitureEntry
{
    public int Index { get; init; }
    public uint Id { get; init; }
    public Vector3 Position { get; init; }
    public float Rotation { get; init; }
    public byte Stain { get; init; }
}

/// <summary>
/// 从游戏进程里读房屋家具。只读，不写任何内存。
/// 链路：HousingManager → Indoor/OutdoorTerritory → FurnitureManager → FurnitureVector。
/// </summary>
public sealed class HousingReader
{
    private readonly GameProcess process;

    public IntPtr HousingManager { get; private set; }
    public string LastError { get; private set; } = string.Empty;
    public HousingKind Kind { get; private set; } = HousingKind.None;
    public IntPtr FurnitureManager { get; private set; }

    public HousingReader(GameProcess process)
    {
        this.process = process;
    }

    public bool TryLocate()
    {
        LastError = string.Empty;

        var signature = SigScanner.Scan(process, Offsets.HousingManagerSignature);
        if (signature == IntPtr.Zero)
        {
            LastError = "未找到 HousingManager 特征码（游戏版本可能已更新）";
            return false;
        }

        var manager = SigScanner.ResolveStaticAddress(
            process, signature, Offsets.HousingManagerSignatureOffset, isPointer: true);

        if (manager == IntPtr.Zero)
        {
            LastError = "特征码命中但 HousingManager 为空（可能还没进游戏世界）";
            return false;
        }

        HousingManager = manager;
        return true;
    }

    /// <summary>探测当前处于室内 / 庭院 / 工坊，返回家具管理器地址。</summary>
    public HousingKind Detect()
    {
        Kind = HousingKind.None;
        FurnitureManager = IntPtr.Zero;

        if (HousingManager == IntPtr.Zero)
            return Kind;

        if (TryTerritory(Offsets.HousingManager_IndoorTerritory, out var indoorManager)
            && GetFurnitureCount(indoorManager) > 0)
        {
            Kind = HousingKind.Indoor;
            FurnitureManager = indoorManager;
            return Kind;
        }

        if (TryTerritory(Offsets.HousingManager_OutdoorTerritory, out var outdoorManager)
            && GetFurnitureCount(outdoorManager) > 0)
        {
            Kind = HousingKind.Outdoor;
            FurnitureManager = outdoorManager;
            return Kind;
        }

        if (TryTerritory(Offsets.HousingManager_WorkshopTerritory, out _))
            Kind = HousingKind.Workshop;

        return Kind;
    }

    private bool TryTerritory(int managerOffset, out IntPtr furnitureManager)
    {
        furnitureManager = IntPtr.Zero;

        var territory = process.ReadPtr(IntPtr.Add(HousingManager, managerOffset));
        if (territory == IntPtr.Zero || IsSuspicious(territory))
            return false;

        furnitureManager = IntPtr.Add(territory, Offsets.Territory_FurnitureManager);
        return true;
    }

    private bool IsSuspicious(IntPtr pointer)
    {
        // 用户态指针，低于 64KB 基本可以判定为无效
        return pointer.ToInt64() < 0x10000;
    }

    public int GetFurnitureCount(IntPtr furnitureManager)
    {
        if (furnitureManager == IntPtr.Zero)
            return 0;

        var first = process.ReadPtr(IntPtr.Add(furnitureManager, Offsets.FurnitureManager_FurnitureVector + Offsets.Vector_First));
        var last = process.ReadPtr(IntPtr.Add(furnitureManager, Offsets.FurnitureManager_FurnitureVector + Offsets.Vector_Last));

        if (first == IntPtr.Zero || last == IntPtr.Zero)
            return 0;

        var distance = last.ToInt64() - first.ToInt64();
        if (distance <= 0 || distance > 4096L * Offsets.Furniture_Size)
            return 0;

        return (int)(distance / Offsets.Furniture_Size);
    }

    public List<FurnitureEntry> ReadFurniture()
    {
        var result = new List<FurnitureEntry>();

        if (FurnitureManager == IntPtr.Zero)
            return result;

        var count = GetFurnitureCount(FurnitureManager);
        if (count <= 0)
            return result;

        var vectorAddress = IntPtr.Add(FurnitureManager, Offsets.FurnitureManager_FurnitureVector);
        var first = process.ReadPtr(IntPtr.Add(vectorAddress, Offsets.Vector_First));

        var totalBytes = count * Offsets.Furniture_Size;
        var buffer = process.ReadBytes(first, totalBytes);
        if (buffer.Length < totalBytes)
            return result;

        for (var i = 0; i < count; i++)
        {
            var offset = i * Offsets.Furniture_Size;

            var id = BitConverter.ToUInt32(buffer, offset + Offsets.Furniture_Id);
            var position = new Vector3(
                BitConverter.ToSingle(buffer, offset + Offsets.Furniture_Position),
                BitConverter.ToSingle(buffer, offset + Offsets.Furniture_Position + 4),
                BitConverter.ToSingle(buffer, offset + Offsets.Furniture_Position + 8));
            var rotation = BitConverter.ToSingle(buffer, offset + Offsets.Furniture_Rotation);
            var index = BitConverter.ToInt32(buffer, offset + Offsets.Furniture_Index);
            var stain = buffer[offset + Offsets.Furniture_Stain];

            result.Add(new FurnitureEntry
            {
                Index = index,
                Id = id,
                Position = position,
                Rotation = rotation,
                Stain = stain,
            });
        }

        return result;
    }
}
