using System.IO;
using System.Windows;

namespace FF14HousingKit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 命令行诊断模式（不打开窗口）
        if (e.Args.Contains("--diag"))
        {
            var text = Diagnostics.Run();
            foreach (var path in new[]
                     {
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "diag.log"),
                         Path.Combine(AppContext.BaseDirectory, "diag.log"),
                     })
            {
                try { File.WriteAllText(path, text); } catch { }
            }

            Shutdown();
            return;
        }

        if (e.Args.Contains("--check"))
        {
            var text = Diagnostics.Run();
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "check.log"), text); } catch { }
            Shutdown();
            return;
        }

        if (e.Args.Contains("--screenshot"))
        {
            RenderPreview();
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    /// <summary>开发用：把窗口渲染成 PNG，检查布局。</summary>
    private void RenderPreview()
    {
        var window = new MainWindow
        {
            Topmost = false,
            ShowInTaskbar = false,
        };

        window.Show();
        window.UpdateLayout();

        // 让 WPF 完成一次完整渲染（用 Background 优先级，避免被定时器饿死）
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        System.Threading.Thread.Sleep(400);
        window.UpdateLayout();
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);

        if (width <= 0 || height <= 0)
            return;

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        var path = Path.Combine(AppContext.BaseDirectory, "ui-collapsed.png");
        using var stream = File.Create(path);
        encoder.Save(stream);

        window.Close();
    }
}
