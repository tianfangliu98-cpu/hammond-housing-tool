using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FF14HousingKit.Game;
using FF14HousingKit.Memory;

namespace FF14HousingKit;

public partial class MainWindow
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private const double TitleBarHeight = 54;

    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly ObservableCollection<FurnitureRow> rows = new();

    private GameProcess? process;
    private HousingReader? reader;
    private LayoutReader? layout;
    private PlaceAnywhereService? placeAnywhere;
    private List<FurnitureEntry> furniture = new();

    private bool suppress;
    private bool dirty;
    private bool clickThrough = true;
    private DateTime lastEdit = DateTime.MinValue;
    private DateTime lastModelRefresh = DateTime.MinValue;
    private IntPtr lastPickedItem;
    private bool listExpanded;

    private readonly DispatcherTimer autoAttachTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        suppress = true;
        InitializeComponent();

        // 恢复上次设置
        var settings = SettingsStore.Load();
        OpacitySlider.Value = Math.Clamp(settings.Opacity * 100, 40, 100);
        ApplyOpacity();

        clickThrough = settings.ClickThrough;

        suppress = false;

        FurnitureGrid.ItemsSource = rows;

        timer.Tick += (_, _) => OnTick();
        Closed += (_, _) => { autoAttachTimer.Stop(); Shutdown(); };

        // 自动等待游戏启动：每 2 秒检查一次，找到就自动连上
        autoAttachTimer.Tick += (_, _) => AutoAttachTick();
        autoAttachTimer.Start();

        // 鼠标穿透需要拦截窗口命中测试
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(WndProc);
        };

        StatusText.Text = Diagnostics.IsAdmin()
            ? "等待游戏启动…"
            : "请以管理员身份运行（否则读不了游戏）";

        UpdatePinVisual();
        UpdateClickThroughVisual();
    }

    /// <summary>
    /// 鼠标穿透：标题栏以下的整块区域返回 HTTRANSPARENT，点击直接落到游戏。
    /// 标题栏保持正常交互，所以既能拖窗口，也能点「穿透」按钮关回来。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST || !clickThrough)
            return IntPtr.Zero;

        var lp = lParam.ToInt64();
        var screenX = (short)(lp & 0xFFFF);
        var screenY = (short)((lp >> 16) & 0xFFFF);

        Point point;
        try
        {
            point = PointFromScreen(new Point(screenX, screenY));
        }
        catch
        {
            return IntPtr.Zero;
        }

        // 标题栏不穿透
        if (point.Y >= 0 && point.Y < TitleBarHeight)
            return IntPtr.Zero;

        handled = true;
        return new IntPtr(HTTRANSPARENT);
    }

    private void ClickThrough_Click(object sender, RoutedEventArgs e)
    {
        clickThrough = !clickThrough;
        UpdateClickThroughVisual();
        SaveSettings();
    }

    private void UpdateClickThroughVisual()
    {
        if (clickThrough)
        {
            ClickThroughButton.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
            ClickThroughButton.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x33, 0x14, 0xB8, 0xA6));
            ClickThroughButton.FontWeight = FontWeights.SemiBold;
            StatusText.Text = "鼠标穿透已开（标题栏可点，再点「穿透」关闭）";
        }
        else
        {
            ClickThroughButton.Foreground = (System.Windows.Media.Brush)FindResource("SubTextBrush");
            ClickThroughButton.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xFF, 0x22, 0x31, 0x44));
            ClickThroughButton.FontWeight = FontWeights.Normal;
        }
    }

    private void SaveSettings()
    {
        SettingsStore.Save(new AppSettings
        {
            Opacity = OpacitySlider.Value / 100.0,
            ClickThrough = clickThrough,
        });
    }

    private void ApplyOpacity()
    {
        Opacity = OpacitySlider.Value / 100.0;
        OpacityText.Text = $"{(int)OpacitySlider.Value}%";
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (suppress)
            return;

        ApplyOpacity();
        SaveSettings();
    }

    // ================= 窗口拖动 =================

    /// <summary>整个窗口空白处都能拖动，只有输入框/按钮这类控件除外。</summary>
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1)
            return;

        if (IsInteractive(e.OriginalSource as DependencyObject))
            return;

        try
        {
            DragMove();
        }
        catch
        {
            // 鼠标已经松开时 DragMove 会抛，忽略
        }
    }

    private static bool IsInteractive(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.TextBox
                or System.Windows.Controls.Primitives.RepeatButton
                or System.Windows.Controls.Primitives.ToggleButton
                or System.Windows.Controls.Primitives.Thumb
                or System.Windows.Controls.Button
                or System.Windows.Controls.CheckBox
                or System.Windows.Controls.Slider
                or System.Windows.Controls.ScrollViewer
                or System.Windows.Controls.DataGrid
                or System.Windows.Controls.Primitives.ScrollBar)
            {
                return true;
            }

            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        UpdatePinVisual();
    }

    /// <summary>置顶按钮：开启时金色高亮 + 实心底，关闭时灰字。</summary>
    private void UpdatePinVisual()
    {
        if (Topmost)
        {
            PinButton.Foreground = (System.Windows.Media.Brush)FindResource("GoldBrush");
            PinButton.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x33, 0xC9, 0xA2, 0x27));
            PinButton.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            PinButton.Foreground = (System.Windows.Media.Brush)FindResource("SubTextBrush");
            PinButton.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xFF, 0x22, 0x31, 0x44));
            PinButton.FontWeight = FontWeights.Normal;
        }
    }

    private bool collapsed;

    private void Collapse_Click(object sender, RoutedEventArgs e)
    {
        collapsed = !collapsed;

        ContentPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.Content = collapsed ? "▼" : "▲";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ================= 连接 =================

    /// <summary>每 2 秒自动尝试连接；游戏关掉后也会自动重新等。</summary>
    private void AutoAttachTick()
    {
        if (process is not null && process.IsAlive)
            return;

        if (process is not null)
            Shutdown();      // 游戏退出了，清理旧句柄

        TryConnect(silent: true);
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => TryConnect(silent: false);

    private void TryConnect(bool silent)
    {
        try
        {
            if (process is not null)
                Shutdown();

            process = GameProcess.TryAttach();
            if (process is null)
            {
                StatusText.Text = Diagnostics.IsAdmin()
                    ? "等待游戏启动…"
                    : "未找到游戏：请以管理员身份运行";
                StatusDot.Fill = System.Windows.Media.Brushes.Gray;
                return;
            }

            reader = new HousingReader(process);
            reader.TryLocate();

            layout = new LayoutReader(process);
            layout.TryLocate();
            layout.ResolveFunctions();

            placeAnywhere = new PlaceAnywhereService(process);
            placeAnywhere.Resolve();

            StatusText.Text = $"PID {process.Process.Id}";
            StatusDot.Fill = System.Windows.Media.Brushes.LimeGreen;

            timer.Start();
            RefreshList();
        }
        catch (Exception ex)
        {
            StatusText.Text = "失败：" + ex.Message;
        }
    }

    // ================= 定时 =================

    private void OnTick()
    {
        if (layout is null || process is null || !process.IsAlive)
            return;

        try
        {
            if (!layout.RefreshStructure())
            {
                ModeText.Text = "模式：未进入布置界面";
                StateText.Text = "状态：—";
                return;
            }

            ModeText.Text = $"模式：{layout.Mode}";
            StateText.Text = layout.State == ItemState.Hover
                ? "状态：Hover（请点击家具选中）"
                : $"状态：{layout.State}";

            if (!layout.CanEdit)
                return;

            var idleMs = (DateTime.Now - lastEdit).TotalMilliseconds;

            if (AutoApplyToggle.IsChecked == true && dirty)
            {
                if (idleMs < 150)
                {
                    // 按住中：每帧都重写一次坐标，压住游戏自身的每帧覆盖，
                    // 否则只会在松手那一下「嗖」地跳过去
                    WriteTransformOnly();

                    if ((DateTime.Now - lastModelRefresh).TotalMilliseconds > 50)
                    {
                        layout.RefreshModel(out _);
                        lastModelRefresh = DateTime.Now;
                    }
                }
                else
                {
                    // 停手了：做一次完整应用（含模型刷新）
                    Apply(silent: true);
                }
            }

            if (!AnyInputFocused() && idleMs > 300)
                ReadCurrentValues();

            if ((DateTime.Now - lastModelRefresh).TotalMilliseconds > 100)
            {
                layout.RefreshModel(out _);
                lastModelRefresh = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "异常：" + ex.Message;
        }
    }

    // ================= 坐标 =================

    private void XMinus_Click(object sender, RoutedEventArgs e) => Nudge(XBox, -1);
    private void XPlus_Click(object sender, RoutedEventArgs e) => Nudge(XBox, 1);
    private void YMinus_Click(object sender, RoutedEventArgs e) => Nudge(YBox, -1);
    private void YPlus_Click(object sender, RoutedEventArgs e) => Nudge(YBox, 1);
    private void ZMinus_Click(object sender, RoutedEventArgs e) => Nudge(ZBox, -1);
    private void ZPlus_Click(object sender, RoutedEventArgs e) => Nudge(ZBox, 1);
    private void RotMinus_Click(object sender, RoutedEventArgs e) => Nudge(RotBox, -1);
    private void RotPlus_Click(object sender, RoutedEventArgs e) => Nudge(RotBox, 1);

    private void Coord_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => MarkDirty();

    private void MarkDirty()
    {
        if (suppress)
            return;

        dirty = true;
        lastEdit = DateTime.Now;
    }

    private void Nudge(System.Windows.Controls.TextBox box, int direction)
    {
        var step = Parse(StepBox, 0.05f);
        if (step <= 0)
            step = 0.05f;

        var value = Parse(box, 0f) + step * direction;

        suppress = true;
        box.Text = value.ToString("F3", CultureInfo.InvariantCulture);
        suppress = false;

        dirty = true;
        lastEdit = DateTime.Now;
    }

    /// <summary>只写坐标/旋转，不刷模型（便宜，可以每帧调）。</summary>
    private void WriteTransformOnly()
    {
        if (layout is null || !layout.CanEdit)
            return;

        if (layout.State == ItemState.SoftSelect && layout.ActiveItem != lastPickedItem)
        {
            layout.PickUpActiveItem(out _);
            lastPickedItem = layout.ActiveItem;
        }

        layout.TryWriteTranslation(new Vector3(
            Parse(XBox, 0f), Parse(YBox, 0f), Parse(ZBox, 0f)));
        layout.TryWriteYaw(Parse(RotBox, 0f));
    }

    private void ReadCurrentValues()
    {
        if (layout is null || !layout.TryReadTransform(out var pos, out var rot))
            return;

        Render(XBox, pos.X.ToString("F3", CultureInfo.InvariantCulture));
        Render(YBox, pos.Y.ToString("F3", CultureInfo.InvariantCulture));
        Render(ZBox, pos.Z.ToString("F3", CultureInfo.InvariantCulture));

        var euler = QuatUtil.FromQ(rot);
        Render(RotBox, euler.Y.ToString("F1", CultureInfo.InvariantCulture));
    }

    private void Render(System.Windows.Controls.TextBox box, string text)
    {
        if (box.IsKeyboardFocusWithin)
            return;

        suppress = true;
        box.Text = text;
        suppress = false;
    }

    private void Apply(bool silent)
    {
        if (layout is null || !layout.CanEdit)
            return;

        var x = Parse(XBox, 0f);
        var y = Parse(YBox, 0f);
        var z = Parse(ZBox, 0f);

        WriteTransformOnly();

        // 停手后补一次模型刷新
        layout.RefreshModel(out _);
        lastModelRefresh = DateTime.Now;

        dirty = false;

        if (!silent)
            StatusText.Text = $"已应用 ({x:F2}, {y:F2}, {z:F2})";
    }

    private static float Parse(System.Windows.Controls.TextBox box, float fallback) =>
        float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private bool AnyInputFocused() =>
        XBox.IsKeyboardFocusWithin || YBox.IsKeyboardFocusWithin ||
        ZBox.IsKeyboardFocusWithin || RotBox.IsKeyboardFocusWithin ||
        StepBox.IsKeyboardFocusWithin;

    // ================= 清单 =================

    private void RefreshList()
    {
        if (reader is null)
            return;

        try
        {
            var kind = reader.Detect();
            if (kind is HousingKind.Indoor or HousingKind.Outdoor)
            {
                furniture = reader.ReadFurniture();

                rows.Clear();
                foreach (var item in furniture)
                {
                    rows.Add(new FurnitureRow
                    {
                        Index = item.Index,
                        Id = item.Id,
                        X = item.Position.X.ToString("F2", CultureInfo.InvariantCulture),
                        Y = item.Position.Y.ToString("F2", CultureInfo.InvariantCulture),
                        Z = item.Position.Z.ToString("F2", CultureInfo.InvariantCulture),
                        Rot = item.Rotation.ToString("F2", CultureInfo.InvariantCulture),
                        Stain = item.Stain,
                    });
                }

                UpdateListButton();
            }
        }
        catch
        {
            // 列表失败不影响主流程
        }
    }

    private void ListToggle_Click(object sender, RoutedEventArgs e)
    {
        listExpanded = !listExpanded;
        ListPanel.Visibility = listExpanded ? Visibility.Visible : Visibility.Collapsed;
        UpdateListButton();
    }

    private void UpdateListButton() =>
        ListToggle.Content = (listExpanded ? "▼   房屋家具清单" : "▶   房屋家具清单") + $"（{furniture.Count} 件）";

    // ================= 解除限制 =================

    private void PlaceAnywhere_Changed(object sender, RoutedEventArgs e)
    {
        if (placeAnywhere is null)
            return;

        var state = PlaceAnywhereCheck.IsChecked == true;

        if (!placeAnywhere.IsResolved && !placeAnywhere.Resolve())
        {
            StatusText.Text = "解除限制：找不到特征码";
            PlaceAnywhereCheck.IsChecked = false;
            return;
        }

        placeAnywhere.SetState(state);
        StatusText.Text = state ? "已解除家具限制" : "已恢复限制";
    }

    // ================= 自检 =================

    private void Diag_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            text = Diagnostics.Run();
        }
        catch (Exception ex)
        {
            text = "诊断出错：" + ex;
        }

        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            File.WriteAllText(Path.Combine(desktop, "diag.log"), text);
        }
        catch
        {
            // 忽略
        }

        var viewer = new Window
        {
            Title = "自检结果",
            Width = 680,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = (System.Windows.Media.Brush)FindResource("BgBrush"),
        };

        var root = new System.Windows.Controls.DockPanel { Margin = new Thickness(12) };

        var bar = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var copy = new System.Windows.Controls.Button
        {
            Content = "复制到剪贴板",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 12, 0),
        };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(text); copy.Content = "已复制"; }
            catch { copy.Content = "复制失败"; }
        };

        bar.Children.Add(copy);
        bar.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "发给工具作者即可定位问题（已存到桌面 diag.log）",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)FindResource("SubTextBrush"),
        });

        var body = new System.Windows.Controls.TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            Background = (System.Windows.Media.Brush)FindResource("FieldBrush"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10),
        };

        System.Windows.Controls.DockPanel.SetDock(bar, System.Windows.Controls.Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(body);
        viewer.Content = root;
        viewer.ShowDialog();
    }

    // ================= 生命周期 =================

    private void Shutdown()
    {
        timer.Stop();
        layout?.Dispose();
        layout = null;
        process?.Dispose();
        process = null;
        reader = null;
        placeAnywhere = null;
        lastPickedItem = IntPtr.Zero;
    }
}

public sealed class FurnitureRow
{
    public int Index { get; init; }
    public uint Id { get; init; }
    public string X { get; init; } = "";
    public string Y { get; init; } = "";
    public string Z { get; init; } = "";
    public string Rot { get; init; } = "";
    public byte Stain { get; init; }
}
