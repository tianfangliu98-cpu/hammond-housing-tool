using System.Numerics;
using System.Text;
using FF14HousingKit.Game;
using FF14HousingKit.Memory;

namespace FF14HousingKit;

/// <summary>
/// 诊断：把「能不能读、能不能写、特征码在不在」一次性列清楚。
/// 输出面向玩家 —— 每一项都带「如果不对该怎么办」，方便转发给作者排查。
/// </summary>
public static class Diagnostics
{
    public static string Run()
    {
        var version = typeof(Diagnostics).Assembly.GetName().Version?.ToString() ?? "?";
        var sb = new StringBuilder();

        sb.AppendLine("==========================================");
        sb.AppendLine("  哈德蒙的装修工具  自检报告");
        sb.AppendLine("==========================================");
        sb.AppendLine($"时间    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"版本    : {version}");
        sb.AppendLine($"运行身份: {(IsAdmin() ? "管理员" : "普通用户")}");
        sb.AppendLine();
        sb.AppendLine("把这份内容原样发给工具作者，他就能定位问题。");
        sb.AppendLine("常见项：管理员权限 / 是否选中家具 / 游戏版本是否匹配。");
        sb.AppendLine("------------------------------------------");
        sb.AppendLine();

        try
        {
            using var process = GameProcess.TryAttach();
            if (process is null)
            {
                sb.AppendLine("进程: 未找到 ffxiv_dx11.exe");
                sb.AppendLine("  -> 游戏没启动，或者程序不是以管理员身份运行");
                return sb.ToString();
            }

            sb.AppendLine($"进程: PID {process.Process.Id}");
            sb.AppendLine($"模块: 基址 0x{process.ModuleBase.ToInt64():X}  大小 {process.ModuleSize / 1024 / 1024} MB");

            try
            {
                var gameVersion = process.Process.MainModule?.FileVersionInfo;
                if (gameVersion is not null)
                {
                    sb.AppendLine($"游戏版本: {gameVersion.FileVersion}  (编译时对应 2026.10.06)");
                }
            }
            catch
            {
                // 读不到就算了
            }

            sb.AppendLine();
            sb.AppendLine($"工具编译时对应: FFXIVClientStructs 7.57 / 游戏 2026.10.06");
            sb.AppendLine("若版本对不上且特征码定位失败，说明需要更新偏移（见 README）");
            sb.AppendLine();

            var layout = new LayoutReader(process);
            layout.ResolveFunctions();

            var located = layout.TryLocate();
            sb.AppendLine($"[1] 布局引擎定位: {(located ? "成功" : "失败")}");
            sb.AppendLine($"    LayoutWorld      = 0x{layout.LayoutWorld.ToInt64():X}");
            sb.AppendLine($"    HousingStructure = 0x{layout.HousingStructure.ToInt64():X}");

            if (!located)
            {
                sb.AppendLine("    -> 特征码没命中，通常是游戏版本更新了，需要更新偏移");
                return sb.ToString();
            }

            layout.RefreshStructure();
            sb.AppendLine();
            sb.AppendLine("[2] 当前状态");
            sb.AppendLine($"    模式 Mode  = {layout.Mode}   (需要 Rotate)");
            sb.AppendLine($"    状态 State = {layout.State}   (需要 Active 或 SoftSelect)");
            sb.AppendLine($"    ActiveItem = 0x{layout.ActiveItem.ToInt64():X}");

            if (layout.Mode != LayoutMode.Rotate)
            {
                sb.AppendLine("    -> 请在游戏里进入「旋转」模式再操作");
            }

            if (layout.ActiveItem == IntPtr.Zero)
            {
                sb.AppendLine("    -> 没有选中家具。要「点击」家具，不是鼠标悬停");
            }

            sb.AppendLine();
            sb.AppendLine("[3] 坐标读写测试");

            if (layout.ActiveItem != IntPtr.Zero && layout.TryReadTransform(out var pos, out var rot))
            {
                var euler = QuatUtil.FromQ(rot);
                sb.AppendLine($"    当前坐标: X={pos.X:F3} Y={pos.Y:F3} Z={pos.Z:F3}");
                sb.AppendLine($"    当前角度: X={euler.X:F1} Y={euler.Y:F1} Z={euler.Z:F1}");

                var target = new Vector3(pos.X + 0.5f, pos.Y, pos.Z);
                var writeOk = layout.TryWriteTranslation(target);
                var readBack = layout.TryReadTransform(out var after, out _) ? after : Vector3.Zero;

                sb.AppendLine($"    写入: {(writeOk ? "WriteProcessMemory 成功" : "WriteProcessMemory 失败")}");
                sb.AppendLine($"    读回: X={readBack.X:F3}  " +
                              $"{(MathF.Abs(readBack.X - target.X) < 0.01f ? "一致，写入有效" : "不一致，写入没有落到内存")}");

                if (!writeOk || MathF.Abs(readBack.X - target.X) >= 0.01f)
                {
                    sb.AppendLine("    -> 权限问题。右键程序选「以管理员身份运行」");
                    sb.AppendLine("    -> 如果游戏以管理员启动，程序也必须管理员");
                }

                layout.TryWriteTranslation(pos);
                sb.AppendLine("    已恢复原坐标");
            }
            else
            {
                sb.AppendLine("    跳过：没有选中家具，无法测试写入");
            }

            sb.AppendLine();
            sb.AppendLine("[4] 游戏函数特征码");
            sb.AppendLine($"    SelectItem  = 0x{layout.SelectItemFunction.ToInt64():X}{(layout.SelectItemFunction == IntPtr.Zero ? "  (没找到)" : "")}");
            sb.AppendLine($"    ModelUpdate = 0x{layout.ModelUpdateFunction.ToInt64():X}{(layout.ModelUpdateFunction == IntPtr.Zero ? "  (没找到)" : "")}");

            if (layout.SelectItemFunction == IntPtr.Zero || layout.ModelUpdateFunction == IntPtr.Zero)
            {
                sb.AppendLine("    -> 游戏版本更新导致特征码失效，需要更新");
            }

            var placeAnywhere = new PlaceAnywhereService(process);
            var resolved = placeAnywhere.Resolve();

            sb.AppendLine();
            sb.AppendLine("[5] 解除家具限制");
            sb.AppendLine($"    特征码: {(resolved ? "三个都命中" : "有缺失")}");

            if (resolved)
            {
                placeAnywhere.SetState(true);
                var first = placeAnywhere.FirstPatchAddress;
                if (first != IntPtr.Zero)
                {
                    var bytes = new byte[1];
                    process.Read(first, bytes, 1);
                    sb.AppendLine($"    开关字节 = 0x{bytes[0]:X2}  (01 = 已开启)");
                }
            }
            else
            {
                sb.AppendLine("    -> 游戏版本更新导致特征码失效，需要更新");
            }

            sb.AppendLine();
            sb.AppendLine("------------------------------------------");
            sb.AppendLine("自检结束。若一切正常，把这份报告留着就行。");

            layout.Dispose();
        }
        catch (Exception ex)
        {
            sb.AppendLine("诊断过程中出现异常：");
            sb.AppendLine(ex.ToString());
        }

        return sb.ToString();
    }

    public static bool IsAdmin()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
