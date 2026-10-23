using System.IO;
using System.Text.Json;

namespace FF14HousingKit;

/// <summary>本地设置，存在 %APPDATA%\HammondHousingTool\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>窗口不透明度，0.4 ~ 1.0。</summary>
    public double Opacity { get; set; } = 0.85;

    /// <summary>空白区域鼠标穿透（点击直接落到游戏）。</summary>
    public bool ClickThrough { get; set; } = true;
}

public static class SettingsStore
{
    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HammondHousingTool");

    private static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
                return new AppSettings();

            var json = File.ReadAllText(SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // 保存失败不影响使用
        }
    }
}
