using System.Numerics;
using FF14HousingKit.Memory;

namespace FF14HousingKit.Game;

public enum LayoutMode
{
    None = 0,
    Move = 1,
    Rotate = 2,
    Store = 3,
    Place = 4,
    Remove = 6,
}

public enum ItemState
{
    None = 0,
    Hover = 1,
    SoftSelect = 2,
    Active = 3,
}

/// <summary>
/// 布局引擎读取：定位 LayoutWorld → HousingStructure → 当前选中的家具（ActiveItem）。
///
/// 移动家具只写 ActiveItem 的 Transform，这是 BDTH 的做法：
/// 游戏只保存"被拿起并放下过"的家具位置，直接改静态列表里的坐标不会保存。
/// </summary>
public sealed class LayoutReader : IDisposable
{
    private readonly GameProcess process;
    private RemoteCallStub? stub;

    public IntPtr LayoutWorld { get; private set; }
    public IntPtr HousingStructure { get; private set; }

    public IntPtr SelectItemFunction { get; private set; }
    public IntPtr ModelUpdateFunction { get; private set; }

    public LayoutReader(GameProcess process)
    {
        this.process = process;
    }

    /// <summary>定位代码地址（拿起来 / 刷新模型），并准备好可复用的调用桩。</summary>
    public void ResolveFunctions()
    {
        SelectItemFunction = SigScanner.Scan(process, Offsets.SelectItemSignature);
        ModelUpdateFunction = SigScanner.Scan(process, Offsets.LayoutModelUpdateSignature);

        stub?.Dispose();
        stub = new RemoteCallStub(process);
    }

    /// <summary>让游戏把软选中的家具真正「拿起」（否则改完离开房屋会还原）。</summary>
    public bool PickUpActiveItem(out string message)
    {
        message = string.Empty;
        var item = ActiveItem;
        if (item == IntPtr.Zero)
        {
            message = "没有选中家具";
            return false;
        }

        if (SelectItemFunction == IntPtr.Zero || stub is null)
        {
            message = "未找到 SelectItem 函数";
            return false;
        }

        stub.Invoke(
            SelectItemFunction,
            (ulong)HousingStructure.ToInt64(),
            (ulong)item.ToInt64(),
            out var exitCode);

        message = $"已调用拿起（返回 {exitCode}）";
        return true;
    }

    /// <summary>刷新选中家具的模型，墙挂/室外家具写坐标后必须调用。</summary>
    public bool RefreshModel(out string message)
    {
        message = string.Empty;
        var item = ActiveItem;
        if (item == IntPtr.Zero)
        {
            message = "没有选中家具";
            return false;
        }

        if (ModelUpdateFunction == IntPtr.Zero || stub is null)
        {
            message = "未找到模型刷新函数";
            return false;
        }

        stub.Invoke(
            ModelUpdateFunction,
            (ulong)(item + Offsets.ModelUpdateItemOffset).ToInt64(),
            0,
            out var exitCode);

        message = $"已刷新模型（返回 {exitCode}）";
        return true;
    }

    public void Dispose()
    {
        stub?.Dispose();
        stub = null;
    }

    public bool TryLocate()
    {
        var signature = SigScanner.Scan(process, Offsets.LayoutWorldSignature);
        if (signature == IntPtr.Zero)
            return false;

        var staticAddress = SigScanner.ResolveStaticAddress(
            process, signature, Offsets.LayoutWorldDispOffset, isPointer: false);

        LayoutWorld = process.ReadPtr(staticAddress);
        if (LayoutWorld == IntPtr.Zero)
            return false;

        return RefreshStructure();
    }

    /// <summary>轻量刷新：只重读 HousingStructure 指针，不做特征码扫描（可高频调用）。</summary>
    public bool RefreshStructure()
    {
        if (LayoutWorld == IntPtr.Zero)
            return false;

        HousingStructure = process.ReadPtr(IntPtr.Add(LayoutWorld, Offsets.LayoutWorld_HousingStruct));
        return HousingStructure != IntPtr.Zero;
    }

    public LayoutMode Mode => HousingStructure == IntPtr.Zero
        ? LayoutMode.None
        : (LayoutMode)process.Read<int>(IntPtr.Add(HousingStructure, Offsets.HousingStructure_Mode));

    public ItemState State => HousingStructure == IntPtr.Zero
        ? ItemState.None
        : (ItemState)process.Read<int>(IntPtr.Add(HousingStructure, Offsets.HousingStructure_State));

    public IntPtr ActiveItem => HousingStructure == IntPtr.Zero
        ? IntPtr.Zero
        : process.ReadPtr(IntPtr.Add(HousingStructure, Offsets.HousingStructure_ActiveItem));

    /// <summary>游戏里是否处于可编辑状态（布置模式且选中了家具）。</summary>
    public bool CanEdit => Mode == LayoutMode.Rotate && ActiveItem != IntPtr.Zero;

    public bool TryReadTransform(out Vector3 translation, out Quaternion rotation)
    {
        translation = Vector3.Zero;
        rotation = Quaternion.Identity;

        var item = ActiveItem;
        if (item == IntPtr.Zero)
            return false;

        var transform = IntPtr.Add(item, Offsets.LayoutInstance_Transform);
        translation = process.Read<Vector3>(IntPtr.Add(transform, Offsets.Transform_Translation));
        rotation = process.Read<Quaternion>(IntPtr.Add(transform, Offsets.Transform_Rotation));
        return true;
    }

    public bool TryWriteTranslation(Vector3 translation)
    {
        var item = ActiveItem;
        if (item == IntPtr.Zero)
            return false;

        var address = IntPtr.Add(
            item,
            Offsets.LayoutInstance_Transform + Offsets.Transform_Translation);

        var buffer = new byte[12];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), translation.X);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), translation.Y);
        BitConverter.TryWriteBytes(buffer.AsSpan(8), translation.Z);

        return process.Write(address, buffer);
    }

    public bool TryWriteRotation(Quaternion rotation)
    {
        var item = ActiveItem;
        if (item == IntPtr.Zero)
            return false;

        var address = IntPtr.Add(
            item,
            Offsets.LayoutInstance_Transform + Offsets.Transform_Rotation);

        var buffer = new byte[16];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), rotation.X);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), rotation.Y);
        BitConverter.TryWriteBytes(buffer.AsSpan(8), rotation.Z);
        BitConverter.TryWriteBytes(buffer.AsSpan(12), rotation.W);

        return process.Write(address, buffer);
    }

    /// <summary>只改 yaw，保留家具原有的俯仰/翻滚，避免把墙挂或倾斜家具拍正。</summary>
    public bool TryWriteYaw(float yawDegrees)
    {
        var item = ActiveItem;
        if (item == IntPtr.Zero)
            return false;

        var transform = IntPtr.Add(item, Offsets.LayoutInstance_Transform);
        var current = process.Read<Quaternion>(IntPtr.Add(transform, Offsets.Transform_Rotation));
        var updated = QuatUtil.WithYaw(current, yawDegrees);

        return TryWriteRotation(updated);
    }
}
