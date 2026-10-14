using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace HarmonicaPlayer;

// 无边框窗口外壳。
//
// 去掉系统标题栏之后，拖拽移动、双击最大化、边缘缩放、贴边（Aero Snap）与窗口投影
// 这些行为不该自己造——它们和系统输入模型耦合很深，自己实现必然在某个场景下出问题。
// 所以这里用 WindowChrome 让系统继续负责这些行为，我们只把标题栏「画」进窗口客户区。
//
// 两个已知的坑，此处都已处理：
//  1. 标题栏里的按钮必须标记 IsHitTestVisibleInChrome，否则点击会被 WindowChrome
//     当成「拖动窗口」吞掉，按钮按不动。
//  2. 最大化时 Windows 会把窗口撑到整块显示器而不是工作区，任务栏会被盖住。
//     处理办法是接管 WM_GETMINMAXINFO，把最大化尺寸限定为工作区，详见
//     ConstrainMaximizedToWorkArea 的注释。
internal static class Chrome
{
    public const double MainBarHeight = 44;
    public const double DialogBarHeight = 38;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public int Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    // Win11 的窗口圆角；旧系统会返回非零错误码，无副作用。
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static readonly Brush BarFill = Frozen(Color.FromArgb(0xE8, 0x0B, 0x10, 0x17));
    private static readonly Brush BarLine = Frozen(Color.FromRgb(0x1E, 0x2A, 0x37));
    private static readonly Brush Ink = Frozen(Color.FromRgb(0xE6, 0xED, 0xF3));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x22, 0xD3, 0xEE));
    private static readonly Brush AccentSoft = Frozen(Color.FromArgb(0x24, 0x22, 0xD3, 0xEE));
    private static readonly Brush AccentLine = Frozen(Color.FromArgb(0x66, 0x22, 0xD3, 0xEE));
    private static readonly Brush GlyphInk = Frozen(Color.FromRgb(0xD6, 0xE2, 0xEC));
    private static readonly LinearGradientBrush AccentGrad = Gradient();

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0xD3, 0xEE), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x5E, 0xEA, 0xD4), 1));
        brush.Freeze();
        return brush;
    }

    // ---------- 窗口外壳 ----------

    // captionHeight 之内的区域由 WindowChrome 交给系统处理拖动与双击最大化。
    public static void MakeFrameless(Window window, double captionHeight, bool resizable)
    {
        window.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = captionHeight,
            ResizeBorderThickness = resizable ? new Thickness(6) : new Thickness(0),
            CornerRadius = new CornerRadius(0),
            // 保留 1px 玻璃边框：完全置 0 会让 DWM 不再为窗口投影，窗口会「贴」在桌面上。
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false
        });
        if (resizable) ConstrainMaximizedToWorkArea(window);
    }

    // WindowChrome 的已知缺陷：最大化时 Windows 会把窗口撑到「整块显示器」而不是「工作区」，
    // 实测在 150% 缩放下窗口矩形为 (-10,-10)-(2570,1610)，比工作区 (0,0)-(2560,1540)
    // 多出 70px，直接把任务栏盖住。这里接管 WM_GETMINMAXINFO，把最大化尺寸与位置限定为
    // 当前显示器的工作区。因为 WindowChrome 让客户区等于整个窗口矩形，限定工作区之后
    // 内容正好铺满工作区，也就不需要再对外壳做边框内缩补偿。
    private static void ConstrainMaximizedToWorkArea(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (HwndSource.FromHwnd(new WindowInteropHelper(window).Handle) is not HwndSource source) return;
            source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message != WM_GETMINMAXINFO) return IntPtr.Zero;
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor == IntPtr.Zero) return IntPtr.Zero;
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
                var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                limits.MaxPosition.X = info.Work.Left - info.Monitor.Left;
                limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
                limits.MaxSize.X = info.Work.Right - info.Work.Left;
                limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;
                // 只改最大化的尺寸与位置，保留 MinTrackSize / MaxTrackSize（窗口最小尺寸约束在这里）。
                Marshal.StructureToPtr(limits, lParam, true);
                return IntPtr.Zero;
            });
        };
    }

    public static void EnableRoundedCorners(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }
            catch (DllNotFoundException) { /* 非 Windows：忽略。 */ }
            catch (EntryPointNotFoundException) { }
        };
    }

    // ---------- 背景环境辉光 ----------
    // 参考站点是用三处 radial-gradient 给近黑底压出层次；WPF 里叠三张径向渐变即可等效。
    public static Grid GlowLayer()
    {
        var layer = new Grid { IsHitTestVisible = false, ClipToBounds = true };
        layer.Children.Add(Glow(0.10, -0.08, 0.60, 0.55, 0x22, 0xD3, 0xEE, 0.16)); // 左上：主色
        layer.Children.Add(Glow(0.94, 0.06, 0.55, 0.50, 0x5E, 0xEA, 0xD4, 0.09)); // 右上：青绿
        layer.Children.Add(Glow(0.52, 1.12, 0.75, 0.60, 0xB3, 0x88, 0xFF, 0.10)); // 底部：紫
        return layer;
    }

    private static Rectangle Glow(double cx, double cy, double rx, double ry, byte r, byte g, byte b, double alpha)
    {
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Center = new Point(cx, cy),
            GradientOrigin = new Point(cx, cy),
            RadiusX = rx,
            RadiusY = ry
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(alpha * 255), r, g, b), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, r, g, b), 1));
        brush.Freeze();
        return new Rectangle { Fill = brush };
    }

    // ---------- 标题栏 ----------

    // 对话框外壳。对话框是独立窗口，不会继承主窗口的资源字典，所以主题也要在这里挂一遍，
    // 否则它会退回系统浅色模板（这正是之前对话框一直是浅色的原因）。
    public static Grid AttachDialog(Window window, string heading, UIElement body)
    {
        window.Resources.MergedDictionaries.Add(DarkTheme.Dictionary);
        window.SetResourceReference(Control.BackgroundProperty, "P.Page");
        window.SetResourceReference(Control.ForegroundProperty, "P.Text");
        window.FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        window.FontSize = 13;

        MakeFrameless(window, DialogBarHeight, resizable: false);
        EnableRoundedCorners(window);

        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var glow = GlowLayer();
        Grid.SetRowSpan(glow, 2); shell.Children.Add(glow);
        var bar = DialogBar(window, heading);
        Grid.SetRow(bar, 0); shell.Children.Add(bar);
        Grid.SetRow(body, 1); shell.Children.Add(body);
        return shell;
    }

    // 主窗口：左侧品牌区，中间当前曲名（由调用方传入并自行设定样式），右侧窗口按钮。
    public static Border MainBar(Window owner, string brand, string version, TextBlock titleSlot)
    {
        var brandBox = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 10, 0)
        };
        brandBox.Children.Add(Mark(17));
        brandBox.Children.Add(new TextBlock
        {
            Text = brand, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
            Foreground = Ink, VerticalAlignment = VerticalAlignment.Center
        });
        brandBox.Children.Add(new Border
        {
            Background = AccentSoft, BorderBrush = AccentLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999), Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = version, FontSize = 10.5, Foreground = Accent, VerticalAlignment = VerticalAlignment.Center }
        });
        return Bar(owner, MainBarHeight, brandBox, titleSlot, showMaximize: true);
    }

    // 自绘对话框：只留标题与最小化/关闭。
    public static Border DialogBar(Window owner, string heading)
    {
        var box = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 10, 0)
        };
        box.Children.Add(Mark(15));
        box.Children.Add(new TextBlock
        {
            Text = heading, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Ink, VerticalAlignment = VerticalAlignment.Center
        });
        return Bar(owner, DialogBarHeight, box, null, showMaximize: false);
    }

    private static Border Mark(double height) => new()
    {
        Width = 3, Height = height, CornerRadius = new CornerRadius(2), Background = AccentGrad,
        Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center
    };

    private static Border Bar(Window owner, double height, UIElement? left, UIElement? center, bool showMaximize)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (left != null) { Grid.SetColumn(left, 0); grid.Children.Add(left); }
        if (center != null) { Grid.SetColumn(center, 1); grid.Children.Add(center); }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(WindowButton(owner, "TitleBarButton", "最小化", MinimizeGlyph(), (_, _) => owner.WindowState = WindowState.Minimized));
        if (showMaximize) buttons.Children.Add(MaximizeButton(owner));
        buttons.Children.Add(WindowButton(owner, "TitleBarCloseButton", "关闭", CloseGlyph(), (_, _) => owner.Close()));
        Grid.SetColumn(buttons, 2); grid.Children.Add(buttons);

        return new Border
        {
            Height = height, Background = BarFill, BorderBrush = BarLine,
            BorderThickness = new Thickness(0, 0, 0, 1), Child = grid
        };
    }

    private static Button MaximizeButton(Window owner)
    {
        var normal = MaximizeGlyph();
        var restored = RestoreGlyph();
        restored.Visibility = Visibility.Collapsed;
        var stack = new Grid();
        stack.Children.Add(normal);
        stack.Children.Add(restored);

        var button = WindowButton(owner, "TitleBarButton", "最大化", stack, (_, _) =>
            owner.WindowState = owner.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        owner.StateChanged += (_, _) =>
        {
            bool maximized = owner.WindowState == WindowState.Maximized;
            normal.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
            restored.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
            button.ToolTip = maximized ? "向下还原" : "最大化";
        };
        return button;
    }

    private static Button WindowButton(Window owner, string styleKey, string tip, UIElement glyph, RoutedEventHandler click)
    {
        var button = new Button
        {
            Width = styleKey == "TitleBarCloseButton" ? 48 : 46,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Content = glyph,
            ToolTip = tip
        };
        if (owner.TryFindResource(styleKey) is Style style) button.Style = style;
        // 关键：标记为「可命中」，否则 WindowChrome 会把点击当成拖动窗口。
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.Click += click;
        return button;
    }

    private static UIElement MinimizeGlyph() => Glyph("M 0 0 L 11 0");

    private static UIElement MaximizeGlyph() => Glyph("M 0.5 0.5 L 11.5 0.5 L 11.5 11.5 L 0.5 11.5 Z");

    private static UIElement RestoreGlyph() =>
        Glyph("M 3 0.5 L 11.5 0.5 L 11.5 9 M 0.5 3 L 9 3 L 9 11.5 L 0.5 11.5 Z");

    private static UIElement CloseGlyph() => Glyph("M 0 0 L 11 11 M 11 0 L 0 11");

    private static Path Glyph(string data) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = GlyphInk,
        StrokeThickness = 1.25,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
}
