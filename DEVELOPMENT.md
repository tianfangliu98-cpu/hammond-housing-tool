# 开发笔记

给以后的自己（和想改代码的人）看的。游戏大版本更新后，照着这里修。

## 技术原理

不注入、不加壳、不改游戏文件，纯外部读写。

### 读取家具列表

```
OpenProcess(ffxiv_dx11.exe)
  ↓ 特征码扫描定位
HousingManager (静态指针)
  ↓ +0x10
IndoorTerritory
  ↓ +0x10
HousingFurnitureManager
  ↓ +0x11220
std::vector<HousingFurniture>   // Id / Position / Rotation / Index / Stain
```

### 移动家具

走的是布局引擎，不是上面那个列表：

```
LayoutWorld + 0x40   → HousingStructure
  +0x00 Mode          → 必须是 Rotate
  +0x08 State         → SoftSelect / Active
  +0x18 ActiveItem    → SharedGroupLayoutInstance*
  +0x50 Transform
     +0x00 Translation (Vector3)
     +0x10 Rotation    (Quaternion)
```

写入后用 `CreateRemoteThread` 远程调用游戏的 `HousingLayoutModelUpdate` 刷新模型。

### 几个容易踩的点

- **坐标是 Y-up**：X 东西、Y 高度、Z 南北
- **旋转是四元数**，且 FF14 的轴向与标准库不同（`QuatUtil.FromQ` 里交换了 Y/Z 分量），照抄 BDTH 的换算
- **只改 yaw**，把 pitch/roll 清零会把墙挂家具「拍正」
- **游戏每帧会用内部状态刷家具位置**，跨进程写必须以约 60 次/秒的频率持续覆盖，否则只在松手那一下生效
- **软选中（SoftSelect）的家具不会保存**，写入前要调用一次 `SelectItem` 让游戏「拿起」

## 偏移表（游戏 2026.10.06 / FFXIVClientStructs 7.57）

| 常量 | 值 | 说明 |
|---|---|---|
| `HousingManager_IndoorTerritory` | 0x10 | HousingManager 字段 |
| `HousingManager_OutdoorTerritory` | 0x08 | 同上 |
| `Territory_FurnitureManager` | 0x10 | 室内 / 庭院都在这个偏移（内嵌结构） |
| `FurnitureManager_FurnitureVector` | 0x11220 | std::vector |
| `Furniture_Size` | 0x30 | sizeof(HousingFurniture) |
| `Furniture_Id` | 0x00 | uint32 |
| `Furniture_Position` | 0x10 | Vector3 |
| `Furniture_Rotation` | 0x20 | float（弧度） |
| `Furniture_Index` | 0x24 | int |
| `Furniture_Stain` | 0x28 | byte |
| `LayoutWorld_HousingStruct` | 0x40 | LayoutWorld 字段 |
| `HousingStructure_Mode` | 0x00 | int |
| `HousingStructure_State` | 0x08 | int |
| `HousingStructure_ActiveItem` | 0x18 | 指针 |
| `LayoutInstance_Transform` | 0x50 | SharedGroupLayoutInstance 字段 |
| `Transform_Translation` | 0x00 | Vector3 |
| `Transform_Rotation` | 0x10 | Quaternion |

### 特征码

```
HousingManager : 48 89 1D ?? ?? ?? ?? EB 07      (disp 在 +3)
LayoutWorld    : 48 8B D1 48 8B 0D ?? ?? ?? ?? 48 85 C9 74 0A   (disp 在 +6)
SelectItem     : 48 85 D2 0F 84 ?? ?? ?? ?? 53 41 ?? 48 83 ?? ?? 48 89
ModelUpdate    : 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC 50 48 8B E9 48 8B 49
```

解除限制的三处开关（立即数字节必须写成通配 `??`，因为 BDTH 的 Place Anywhere 会把它改成 01）：

```
C6 83 ?? ?? 00 00 ?? 0F 29 44 24        (+6)
48 85 C0 74 ?? C6 87 ?? ?? 00 00 ??     (+11)
C6 87 83 01 00 00 ?? 48 83 C4           (+6)
```

## 游戏更新后怎么修

1. 打开 `%APPDATA%\XIVLauncherCN\addon\Hooks\dev\FFXIVClientStructs.dll`
2. 用 dnSpy / ILSpy 查看对应类型的 `[FieldOffset]`：

| 类型 | 需要的字段 |
|---|---|
| `HousingManager` | `IndoorTerritory` / `OutdoorTerritory` |
| `IndoorTerritory` / `OutdoorTerritory` | `FurnitureManager` |
| `HousingFurnitureManager` | `FurnitureVector` |
| `HousingFurniture` | `Id` / `Position` / `Rotation` / `Index` / `Stain` |
| `SharedGroupLayoutInstance` | `Transform` |

3. 同步修改 `Offsets.cs`
4. 特征码在 `HousingManager.Instance`、`LayoutWorld` 的 `[StaticAddress]` 特性里
5. 重新编译，点「自检」验证

### 已知待修

- **庭院家具列表会混入邻居地块的数据**：`OutdoorTerritory` 管理的是整个街区级别，需要按 `HouseId` 或 `_plots` 过滤。当前清单功能已隐藏。
- **家具中文名缺失**：现在只有 ItemId，可以从 Lumina 读 HousingFurniture 表补名称。

## 构建

需要 .NET 10 SDK：

```powershell
dotnet build -c Release
```

输出在 `bin/Release/net10.0-windows/`。

exe 需要管理员清单（`app.manifest` 里的 `requireAdministrator`），因为游戏由 WeGame 拉起，权限更高。
