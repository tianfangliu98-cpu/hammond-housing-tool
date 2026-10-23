namespace FF14HousingKit;

/// <summary>
/// 内存结构偏移。来源：FFXIVClientStructs 7.57.0.9472（对应游戏 2026.10.06）。
/// 游戏大版本更新后这些数字会变，需要按新版 FFXIVClientStructs 重新核对。
/// 核对方法见 README「怎么更新偏移」。
/// </summary>
internal static class Offsets
{
    // ---- HousingManager ----
    public const int HousingManager_OutdoorTerritory = 0x08;
    public const int HousingManager_IndoorTerritory = 0x10;
    public const int HousingManager_WorkshopTerritory = 0x18;

    // ---- IndoorTerritory / OutdoorTerritory（两者 FurnitureManager 都在 0x10）----
    public const int Territory_FurnitureManager = 0x10;

    // ---- HousingFurnitureManager ----
    public const int FurnitureManager_FurnitureVector = 0x11220;

    // ---- std::vector（StdVector<T> = { First, Last, End }，x64 各 8 字节）----
    public const int Vector_First = 0x00;
    public const int Vector_Last = 0x08;

    // ---- HousingFurniture（ClassSize = 48）----
    public const int Furniture_Size = 0x30;
    public const int Furniture_Id = 0x00;
    public const int Furniture_Position = 0x10;   // Vector3 = 3 × float
    public const int Furniture_Rotation = 0x20;   // float（弧度）
    public const int Furniture_Index = 0x24;      // int
    public const int Furniture_Stain = 0x28;      // byte

    /// <summary>HousingManager.Instance 的特征码：mov [rip+disp32], rbx</summary>
    public const string HousingManagerSignature = "48 89 1D ?? ?? ?? ?? EB 07";
    public const int HousingManagerSignatureOffset = 3;

    // =========================================================
    //  布局引擎（移动家具用）
    //  结构定义参考 BDTHPlugin（开源）+ FFXIVClientStructs
    // =========================================================

    /// <summary>LayoutWorld 指针：48 8B D1 | 48 8B 0D ?? ?? ?? ?? | 48 85 C9 74 0A</summary>
    public const string LayoutWorldSignature = "48 8B D1 48 8B 0D ?? ?? ?? ?? 48 85 C9 74 0A";

    /// <summary>上面签名里 mov 指令位于 +3，disp32 又在 mov 之后 3 字节，故为 6。</summary>
    public const int LayoutWorldDispOffset = 6;

    /// <summary>LayoutWorld -> HousingStructure*</summary>
    public const int LayoutWorld_HousingStruct = 0x40;

    // ---- HousingStructure ----
    public const int HousingStructure_Mode = 0x00;      // int：0=None 1=Move 2=Rotate 3=Store 4=Place 6=Remove
    public const int HousingStructure_State = 0x08;     // int：0=None 1=Hover 2=SoftSelect 3=Active
    public const int HousingStructure_ActiveItem = 0x18; // SharedGroupLayoutInstance*

    // ---- SharedGroupLayoutInstance ----
    public const int LayoutInstance_Transform = 0x50;

    // ---- Transform（ClassSize = 48）----
    public const int Transform_Translation = 0x00;  // Vector3
    public const int Transform_Rotation = 0x10;     // Quaternion

    // ---- 需要远程调用的游戏函数 ----
    /// <summary>选中/拿起家具。签名取自 BDTHPlugin。</summary>
    public const string SelectItemSignature = "48 85 D2 0F 84 ?? ?? ?? ?? 53 41 ?? 48 83 ?? ?? 48 89";

    /// <summary>刷新家具模型（墙挂、室外家具写坐标后需要它才会移动）。</summary>
    public const string LayoutModelUpdateSignature =
        "48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC 50 48 8B E9 48 8B 49";

    /// <summary>传给模型刷新函数的偏移（item + 0x80）。</summary>
    public const int ModelUpdateItemOffset = 0x80;
}
