using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace HarmonicaPlayer;

public static class Program
{
    [STAThread]
    public static void Main() => SingleInstance.Run();
}

public sealed partial class PlayerWindow : Window
{
    // 对外版本号：与 HarmonicaPlayer.csproj 的 Version、Build-Release.ps1 的 $version 保持一致。
    internal const string VersionLabel = "1.2";

    private readonly TextBox songTitle = new() { Text = "未命名曲谱" };
    private readonly TextBlock documentInfo = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Muted, FontSize = 12 };
    private readonly TextBlock documentWarning = new() { TextWrapping = TextWrapping.Wrap, Background = WarnFill, Foreground = WarnInk, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button saveScore = new() { Content = "保存 TXT", Margin = new Thickness(5) };
    private readonly Button saveAs = new() { Content = "另存为", Margin = new Thickness(5) };
    private readonly Button newScore = new() { Content = "新建", Margin = new Thickness(5) };
    private readonly ComboBox trackChoice = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel trackGroup = new() { Visibility = Visibility.Collapsed };
    private TrackSource? trackSource;
    private string? appliedTrack;
    private bool suppressTrackChange;

    // 导入后的多音轨来源：用于在界面上随时换轨（同一文件按另一条轨重新导入）。
    private sealed record TrackSource(bool Midi, string Path, IReadOnlyList<string> Labels, IReadOnlyList<string> Descriptions);
    private readonly IScoreDialogs scoreDialogs;
    private string? documentPath;
    private bool legacyDocument, documentBusy;
    private string[] extraHeaders = Array.Empty<string>();
    private (string Title, string Bpm, string Body, string Gap) cleanDocument;
    private long saveGeneration;
    private bool DocumentDirty => cleanDocument != (songTitle.Text, bpm.Text, score.Text, gap.Text);
    // 自绘标题栏里的曲名（无边框后系统标题栏不可见；Title 仍保留给任务栏使用）。
    private readonly TextBlock chromeTitle = new();
    private void UpdateDocumentTitle()
    {
        Title = $"口琴简谱播放器 {VersionLabel} — {songTitle.Text}{(DocumentDirty ? " *" : "")}";
        documentInfo.Text = documentPath ?? "尚未保存";
        documentInfo.ToolTip = documentPath;
        chromeTitle.Text = $"{songTitle.Text}{(DocumentDirty ? " *" : "")}";
    }
    private void MarkDocumentClean() { cleanDocument = (songTitle.Text, bpm.Text, score.Text, gap.Text); UpdateDocumentTitle(); }
    // 导入/保存的提示条：没有内容时整条收起，避免留下一根空色带占位。
    private void SetDocumentWarning(string text)
    {
        documentWarning.Text = text;
        documentWarning.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
    private readonly TextBox score = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 230, Text = "1234567 【1234567】 （1234567） #1 #2 #4 #5 #6 111 000" };
    // 曲谱编辑按钮：只改「选中部分」（没有选区时改光标左侧那个音符）。
    // 变换逻辑在 ScoreEditor 里（纯字符串函数，有单元测试），这里只管选区与替换。
    private readonly List<Control> editControls = new();
    private readonly TextBlock editHint = new()
    {
        Text = "在下面的谱面里选中音符（或把光标停在某个音符后面），再点上面的按钮；只改选中的部分，可以 Ctrl+Z 撤销。",
        TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 5, 0, 0)
    };
    private readonly TextBox gap = new() { Text = "20", Width = 75 };
    private readonly CheckBox dry = new() { Content = "仅日志测试（不发送键鼠、不发声）", IsChecked = true, Margin = new Thickness(0, 10, 0, 10) };
    private readonly TextBlock status = new() { Text = "就绪：先导入谱面，快捷键状态见上方。", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox log = new() { IsReadOnly = true, Height = 110, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    // 日志栏容器：勾选“仅日志测试”时展开，取消勾选（准备实际演奏）时收起。
    private readonly StackPanel logHost = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button start = new() { Content = "开始 F6", Margin = new Thickness(5), Padding = new Thickness(15, 6, 15, 6) };
    private readonly Button import = new() { Content = "导入 TXT", Margin = new Thickness(5), Padding = new Thickness(15, 6, 15, 6) };
    private readonly Button importMidi = new() { Content = "导入 MIDI", Margin = new Thickness(5), Padding = new Thickness(15, 6, 15, 6) };
    private readonly Button importJson = new() { Content = "导入 JSON", Margin = new Thickness(5), Padding = new Thickness(15, 6, 15, 6) };
    private readonly TextBox bpm = new() { Text = "120", Width = 75 };
    private readonly TextBox preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button refresh = new() { Content = "预览 / 检查", Margin = new Thickness(5), Padding = new Thickness(12, 6, 12, 6) };
    private readonly TextBlock alert = new() { TextWrapping = TextWrapping.Wrap, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = AlertInk };
    private readonly Border alertBox = new() { Background = AlertFill, BorderBrush = Danger, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 6, 0, 6), Visibility = Visibility.Collapsed };
    private string? scoreIssue, hotkeyIssue, operationIssue;
    private int? errorPosition;
    private bool uiBusy;
    private readonly Button locateError = new() { Content = "定位曲谱错误", Margin = new Thickness(4) };
    private readonly TextBlock startReason = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted };

    // 曲库：启动时自动读取 简谱 的三个分类文件夹，点一下就能导入，不必逐个手动选文件。
    private readonly ListBox libraryList = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBox libraryFilter = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock libraryHint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, FontSize = 12 };
    private readonly Button libraryRefresh = new() { Content = "刷新曲库" };
    private LibraryEntry[] libraryEntries = Array.Empty<LibraryEntry>();
    private LibraryEntry[] visibleEntries = Array.Empty<LibraryEntry>();
    private bool suppressLibraryChange;
    private sealed record LibraryEntry(string Path, string Kind, string Title);

    // 统一配色与间距（深色科技风；控件模板见 DarkTheme.cs）。
    // 视觉手法参考 wu.wujidj.cn：近黑底 + 环境辉光、胶囊按钮 + 同色辉光、渐变标题、
    // 卡片微渐变 + 悬停上浮。主色仍用原来的青，只借用它的表达方式。
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));   // 主色：青
    private static readonly Brush AccentInk = new SolidColorBrush(Color.FromRgb(0x04, 0x1E, 0x26)); // 主色按钮上的深色字
    private static readonly Brush AccentSoft = new SolidColorBrush(Color.FromArgb(0x24, 0x22, 0xD3, 0xEE));
    private static readonly Brush AccentLine = new SolidColorBrush(Color.FromArgb(0x66, 0x22, 0xD3, 0xEE));
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xE1, 0x4D, 0x63));
    private static readonly Brush Page = new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x13));     // 窗口底
    private static readonly Brush FieldFill = new SolidColorBrush(Color.FromRgb(0x16, 0x1E, 0x28));// 次级按钮
    private static readonly Brush TextMain = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8B, 0x9A, 0xAB));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x23, 0x2F, 0x3D));
    private static readonly Brush Line2 = new SolidColorBrush(Color.FromRgb(0x2F, 0x3E, 0x50));     // 悬停时的描边

    // 主色/危险色/卡片底/标题都用渐变，避免大片纯色显得平。
    private static readonly LinearGradientBrush AccentGrad = Grad(0x22, 0xD3, 0xEE, 0x5E, 0xEA, 0xD4);
    private static readonly LinearGradientBrush DangerGrad = Grad(0xE1, 0x4D, 0x63, 0xC3, 0x34, 0x50);
    private static readonly LinearGradientBrush CardGrad = Grad(0x18, 0x20, 0x2B, 0x0F, 0x16, 0x1E, vertical: true);
    private static readonly LinearGradientBrush TitleGrad = Grad(0xF2, 0xFB, 0xFF, 0x22, 0xD3, 0xEE);

    private static LinearGradientBrush Grad(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2, bool vertical = false)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = vertical ? new Point(0, 1) : new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(r1, g1, b1), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(r2, g2, b2), 1));
        brush.Freeze();
        return brush;
    }

    // 参考站的按钮都压着一层同色辉光；WPF 里用 ShadowDepth=0 的投影等效。
    private static DropShadowEffect Glow(Color color, double opacity) =>
        new() { Color = color, ShadowDepth = 0, BlurRadius = 16, Opacity = opacity };
    private static readonly Brush WarnFill = new SolidColorBrush(Color.FromRgb(0x2A, 0x22, 0x10));
    private static readonly Brush WarnInk = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly Brush AlertFill = new SolidColorBrush(Color.FromRgb(0x2A, 0x14, 0x18));
    private static readonly Brush AlertInk = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0xA6));

    // 分区标签做成胶囊（参考站 .lbl 的手法）：主色淡底 + 主色描边 + 主色文字，
    // 比原来的「竖条 + 粗体标题」更接近参考站的表达。
    private static Border Pill(string text, double fontSize = 12.5)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border { Width = 3, Height = 13, CornerRadius = new CornerRadius(2),
            Background = Accent, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = fontSize,
            Foreground = Accent, VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Background = AccentSoft, BorderBrush = AccentLine, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3, 13, 4),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12), Child = row
        };
    }

    // 卡片：小标题用胶囊标签，卡片本身用微渐变底 + 悬停上浮与描边提亮。
    private static Border Card(string title, params UIElement[] content)
    {
        var stack = new StackPanel();
        stack.Children.Add(Pill(title));
        foreach (var item in content) stack.Children.Add(item);

        var card = new Border { Background = CardGrad, BorderBrush = Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12), Child = stack };
        var lift = new TranslateTransform();
        card.RenderTransform = lift;
        card.MouseEnter += (_, _) => { card.BorderBrush = Line2; lift.Y = -2; };
        card.MouseLeave += (_, _) => { card.BorderBrush = Line; lift.Y = 0; };
        return card;
    }

    private static void StyleButton(Button button, bool primary = false, bool danger = false)
    {
        button.Padding = new Thickness(16, 7, 16, 7);
        button.Margin = new Thickness(0, 0, 8, 8);
        button.MinWidth = 96;
        button.BorderThickness = new Thickness(1);
        if (primary || danger)
        {
            // 主/危险按钮：胶囊渐变底 + 同色辉光，对应参考站的 .btn-primary。
            button.BorderBrush = Brushes.Transparent;
            button.Background = primary ? AccentGrad : DangerGrad;
            button.Foreground = primary ? AccentInk : Brushes.White;
            button.FontWeight = FontWeights.SemiBold;
            button.Effect = Glow(primary ? Color.FromRgb(0x22, 0xD3, 0xEE) : Color.FromRgb(0xE1, 0x4D, 0x63),
                primary ? 0.42 : 0.34);
        }
        else
        {
            // 次级按钮：幽灵样式（半透明白底 + 亮一档的描边），对应参考站的 .btn-ghost。
            button.BorderBrush = Line2;
            button.Background = FieldFill;
            button.Foreground = TextMain;
            button.FontWeight = FontWeights.Normal;
        }
    }

    // 一行“标签 + 输入框”，让速度/间隔这类小输入框对齐。
    private static StackPanel Row(string label, UIElement input, double labelWidth = 132)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(new TextBlock { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(input);
        return row;
    }

    // ---------- 曲谱编辑按钮 ----------

    private Button Chip(string label, string tip, Action click)
    {
        var button = new Button
        {
            Content = label, ToolTip = tip, FontSize = 12.5,
            Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6)
        };
        button.Click += (_, _) => click();
        editControls.Add(button);
        return button;
    }

    private static WrapPanel ChipRow(string label, params UIElement[] chips)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        row.Children.Add(new TextBlock
        {
            Text = label, Foreground = Muted, FontSize = 12, Width = 34,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 6)
        });
        foreach (var chip in chips) row.Children.Add(chip);
        return row;
    }

    // 曲谱编辑的三组按钮。都走同一套「改选中部分」的逻辑，所以可以任意组合使用：
    // 例如先框选一整句再点 ＋八度，就把整句移高一个八度，时值符号原样保留。
    private UIElement BuildScoreTools()
    {
        var custom = new TextBox
        {
            Text = "1.25", Width = 58, Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 0, 6, 6), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "填写任意拍数（大于0、不超过64），点右侧“应用”"
        };
        editControls.Add(custom);

        var pitch = ChipRow("音高",
            Chip("−八度", "低一个八度：中音 1 → 低音（1），高音【1】→ 中音 1",
                () => EditScore(t => ScoreEditor.ApplyPitch(t, ScoreEditor.Pitch.OctaveDown))),
            Chip("＋八度", "高一个八度：中音 1 → 高音【1】，高音【1】→ 最高音【【1】】",
                () => EditScore(t => ScoreEditor.ApplyPitch(t, ScoreEditor.Pitch.OctaveUp))),
            Chip("＃升半音", "加或去掉升号；升号写在括号内，如【1】→【#1】",
                () => EditScore(t => ScoreEditor.ApplyPitch(t, ScoreEditor.Pitch.ToggleSharp))),
            Chip("还原", "去掉升号与高低音括号，回到中音",
                () => EditScore(t => ScoreEditor.ApplyPitch(t, ScoreEditor.Pitch.Natural))),
            Chip("最高do", "写成【【1】】，全曲只有这一个音；时值符号写在括号外",
                () => EditScore(t => ScoreEditor.ApplyPitch(t, ScoreEditor.Pitch.Highest))));

        var beats = ChipRow("时值",
            Chip("1拍", "整拍：去掉现有的时候符号", () => EditScore(t => ScoreEditor.ApplyBeats(t, 1))),
            Chip("½拍", "八分音符，写作 5_", () => EditScore(t => ScoreEditor.ApplyBeats(t, 0.5))),
            Chip("¼拍", "十六分音符，写作 5__", () => EditScore(t => ScoreEditor.ApplyBeats(t, 0.25))),
            Chip("¾拍", "半拍加附点，写作 5_.", () => EditScore(t => ScoreEditor.ApplyBeats(t, 0.75))),
            Chip("1.5拍", "附点：一拍加附点，写作 5.，共一点五拍", () => EditScore(t => ScoreEditor.ApplyBeats(t, 1.5))),
            Chip("2拍", "延长一拍，写作 5 —", () => EditScore(t => ScoreEditor.ApplyBeats(t, 2))),
            custom,
            Chip("应用", "按左边填的数字设置总拍数；不是常见值时写成冒号写法，如 5:1.25",
                () =>
                {
                    if (!double.TryParse(custom.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value))
                    {
                        editHint.Text = "请输入 0 到 64 之间的拍数，例如 1.25。";
                        return;
                    }
                    EditScore(t => ScoreEditor.ApplyBeats(t, value));
                }));

        var insert = ChipRow("插入",
            Chip("休止符 0", "插入休止符：不发声但占时间，可再用上面的时值按钮改长短", () => InsertAtCaret(" 0")),
            Chip("小节线 |", "插入小节线，只作视觉分隔，不增加停顿", () => InsertAtCaret(" | ")));

        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        box.Children.Add(pitch);
        box.Children.Add(beats);
        box.Children.Add(insert);
        // 提示紧跟在按钮组下面：点完按钮眼睛就在这一带，反馈一定看得见。
        box.Children.Add(editHint);
        return box;
    }

    // 取出选区（没有选区就取光标左侧的音符），交给 transform 改写后写回。
    // 写回用 SelectedText 而不是直接赋 Text：前者走编辑管道，Ctrl+Z 仍然能撤销。
    private void EditScore(Func<string, ScoreEditor.Result> transform)
    {
        if (uiBusy) return;
        string whole = score.Text;
        int start = score.SelectionStart;
        int length = score.SelectionLength;
        if (length == 0)
        {
            if (!ScoreEditor.TryExpandCaret(whole, start, out start, out int end))
            {
                editHint.Text = "请先选中要修改的音符；也可以把光标停在某个音符后面再点。";
                return;
            }
            length = end - start;
        }

        string selected = whole.Substring(start, length);
        var result = transform(selected);
        if (result.Text == selected)
        {
            editHint.Text = result.Notes.Count > 0
                ? string.Join("；", result.Notes)
                : "选中的音符已经是这个值，没有改动。";
            return;
        }

        score.Focus();
        score.Select(start, length);
        score.SelectedText = result.Text;
        score.Select(start, result.Text.Length);
        editHint.Text = $"已修改 {result.Changed} 个音符" +
            (result.Notes.Count > 0 ? "；" + string.Join("；", result.Notes) : "。");
    }

    // 插入类按钮：插在光标处，不覆盖已有内容。
    private void InsertAtCaret(string text)
    {
        if (uiBusy) return;
        score.Focus();
        int at = score.SelectionStart;
        score.Select(at, 0);
        score.SelectedText = text;
        score.Select(at + text.Length, 0);
        editHint.Text = $"已在光标处插入 {text.Trim()}。";
    }

    // 原来这里用 DwmSetWindowAttribute 把「系统标题栏」染成深色。改为无边框自绘标题栏后，
    // 系统标题栏已不存在，这段逻辑随之作废，一并删除（窗口底色与圆角改由 Chrome.cs 负责）。

    private void UpdateAvailability()
    {
        bool blockedByHotkey = dry.IsChecked != true && hotkeys?.StopReady != true;
        start.IsEnabled = !uiBusy && scoreIssue == null && !blockedByHotkey;
        startReason.Text = scoreIssue != null ? "无法开始：请先修正曲谱或速度、音符间隔。" :
            blockedByHotkey ? "无法开始游戏演奏：请先修复停止快捷键。" : "";
        locateError.IsEnabled = !uiBusy && errorPosition != null;
        listen.IsEnabled = listenFromCursor.IsEnabled = exportMidi.IsEnabled = !uiBusy && scoreIssue == null;
    }
    // 勾选“仅日志测试”时展开日志栏，取消勾选（准备实际演奏）时收起。
    private void UpdateLogVisibility() =>
        logHost.Visibility = dry.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    private void LocateError()
    {
        if (uiBusy || errorPosition is not int position) return;
        position = Math.Clamp(position, 0, score.Text.Length);
        score.Focus(); score.Select(position, position < score.Text.Length ? 1 : 0);
        score.ScrollToLine(score.GetLineIndexFromCharacterIndex(position));
    }
    private void UpdateAlert()
    {
        var issues = new[] { operationIssue, hotkeyIssue, scoreIssue }.Where(x => !string.IsNullOrWhiteSpace(x));
        alert.Text = string.Join("\n\n", issues);
        alertBox.Visibility = alert.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateAvailability();
    }
    private void ReportIssue(string message) { operationIssue = message; UpdateAlert(); }
    private CancellationTokenSource? cancellation;
    private Task? running;
    public bool IsClosing => closing;
    private SettingsWriter settingsWriter = null!;
    private readonly string settingsPath;
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly NativeInput output = new();
    private HwndSource? source;
    private IntPtr hwnd;
    private bool allowClose, closing, editingHotkeys, beginning;
    private HotkeyController? hotkeys;
    private AppSettings settings = new();
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TextBlock settingsStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock hotkeyStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly Button configure = new() { Content = "自定义快捷键", Margin = new Thickness(5), Padding = new Thickness(10, 6, 10, 6) };
    private readonly Button retry = new() { Content = "重试注册", Margin = new Thickness(5), Padding = new Thickness(10, 6, 10, 6) };
    private readonly Button stop = new() { Content = "停止 F8", Margin = new Thickness(5), Padding = new Thickness(15, 6, 15, 6) };

    public PlayerWindow(string? settingsFile = null, IScoreDialogs? dialogs = null, Action<AppSettings>? writeSettings = null, ILocalAudioPlayer? audioPlayer = null)
    {
        Resources.MergedDictionaries.Add(DarkTheme.Dictionary);
        scoreDialogs = dialogs ?? new ScoreDialogs();
        localAudio = audioPlayer ?? new LocalAudioPlayer();
        settingsPath = settingsFile ?? SettingsStore.DefaultPath;
        Title = $"口琴简谱播放器 {VersionLabel}"; Width = 1060; Height = 880; MinWidth = 880; MinHeight = 640;
        Background = Page; Foreground = TextMain; FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"); FontSize = 13.5;

        // 去系统边框：拖拽、双击最大化、边缘缩放、贴边仍由系统负责（见 Chrome.cs），
        // 标题栏与背景辉光改在窗口内自绘。结构是三层：辉光铺满，标题栏一行，内容一行。
        Chrome.MakeFrameless(this, Chrome.MainBarHeight, resizable: true);
        Chrome.EnableRoundedCorners(this);
        chromeTitle.FontSize = 12.5; chromeTitle.Foreground = Muted;
        chromeTitle.HorizontalAlignment = HorizontalAlignment.Center;
        chromeTitle.VerticalAlignment = VerticalAlignment.Center;
        chromeTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        chromeTitle.Margin = new Thickness(16, 0, 16, 0);
        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var glow = Chrome.GlowLayer();
        Grid.SetRowSpan(glow, 2); shell.Children.Add(glow);
        var titleBar = Chrome.MainBar(this, "口琴简谱播放器", VersionLabel, chromeTitle);
        Grid.SetRow(titleBar, 0); shell.Children.Add(titleBar);

        var panel = new StackPanel { Margin = new Thickness(18) };
        var root = new DockPanel { Margin = new Thickness(8) };
        var fixedHeader = new StackPanel();
        var alertPanel = new StackPanel();
        alertPanel.Children.Add(alert);
        var alertActions = new WrapPanel();
        var fixHotkeys = new Button { Content = "修改快捷键", Margin = new Thickness(4) };
        var retryHotkeys = new Button { Content = "重试注册", Margin = new Thickness(4) };
        var dismissIssue = new Button { Content = "清除上次操作提示", Margin = new Thickness(4) };
        fixHotkeys.Click += (_, _) => ConfigureHotkeys();
        retryHotkeys.Click += (_, _) => ApplyHotkeys();
        dismissIssue.Click += (_, _) => { operationIssue = null; UpdateAlert(); };
        locateError.Click += (_, _) => LocateError();
        alertActions.Children.Add(locateError);
        alertActions.Children.Add(fixHotkeys); alertActions.Children.Add(retryHotkeys); alertActions.Children.Add(dismissIssue);
        alertPanel.Children.Add(alertActions); alertBox.Child = alertPanel;
        fixedHeader.Children.Add(alertBox);
        status.FontSize = 19; status.FontWeight = FontWeights.Bold;
        status.Foreground = AccentGrad;
        status.Margin = new Thickness(12, 4, 12, 8);
        fixedHeader.Children.Add(status);
        DockPanel.SetDock(fixedHeader, Dock.Top); root.Children.Add(fixedHeader);
        // 左侧曲库 + 分隔条 + 右侧编辑器：拖动分隔条可以左右调整曲库宽度。
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(276), MinWidth = 160 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
        var library = BuildLibrary();
        Grid.SetColumn(library, 0); body.Children.Add(library);
        var splitter = new GridSplitter
        {
            Width = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext, ResizeDirection = GridResizeDirection.Columns,
            Background = Line, ShowsPreview = false, Cursor = Cursors.SizeWE,
            ToolTip = "左右拖动可以调整曲库栏的宽度"
        };
        Grid.SetColumn(splitter, 1); body.Children.Add(splitter);
        var editorScroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(editorScroll, 2); body.Children.Add(editorScroll);
        root.Children.Add(body);
        Grid.SetRow(root, 1); shell.Children.Add(root);
        Content = shell;
        panel.Children.Add(new TextBlock { Text = "单音简谱播放器", FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = TitleGrad, Margin = new Thickness(0, 0, 0, 2) });
        panel.Children.Add(new TextBlock
        {
            Text = "【高音】 （低音） 【【1】】最高do；5:1.25 指定总拍数；# 升半音；0 休止，-或—延长一拍，_ 半拍，__ 四分之一拍，. 附点。",
            Margin = new Thickness(0, 4, 0, 12), TextWrapping = TextWrapping.Wrap, Foreground = Muted
        });
        var playRow = new WrapPanel { Orientation = Orientation.Horizontal };
        playRow.Children.Add(start); playRow.Children.Add(stop); playRow.Children.Add(refresh);
        logHost.Children.Add(new TextBlock { Text = "日志（仅日志测试时展开）", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        logHost.Children.Add(log);
        panel.Children.Add(Card("演奏", playRow, startReason, dry,
            new TextBlock
            {
                Text = "实际演奏请取消“仅日志测试”。开始后直接倒计时3秒，不再弹窗；请保持游戏口琴界面在前台，除停止键外不要操作鼠标键盘。",
                TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 0, 0, 8)
            }, logHost));
        UpdateLogVisibility();
        var importRow = new WrapPanel { Orientation = Orientation.Horizontal };
        importRow.Children.Add(import); importRow.Children.Add(importMidi); importRow.Children.Add(importJson);
        var fileRow = new WrapPanel { Orientation = Orientation.Horizontal };
        fileRow.Children.Add(newScore); fileRow.Children.Add(saveScore); fileRow.Children.Add(saveAs);
        trackGroup.Children.Add(new TextBlock { Text = "演奏音轨（多音轨曲谱导入后可随时切换）：", TextWrapping = TextWrapping.Wrap });
        trackGroup.Children.Add(trackChoice);
        panel.Children.Add(Card("曲谱文件", importRow, fileRow, trackGroup));
        var listening = new StackPanel();
        AddListeningControls(listening);
        panel.Children.Add(Card("本机试听", listening));
        panel.Children.Add(Card("曲谱编辑",
            new TextBlock { Text = "曲名（与文件名独立）：", Margin = new Thickness(0, 0, 0, 4) }, songTitle,
            documentInfo, documentWarning,
            new TextBlock { Text = "谱面（TXT → 单音简谱）", Margin = new Thickness(0, 10, 0, 6), Foreground = Muted },
            BuildScoreTools(), score,
            Row("速度（BPM）：", bpm),
            Row("音符间隔／留白(ms)：", gap),
            new TextBlock { Text = "解析预览（每个音的拍数和时间；不是图片识谱）", Margin = new Thickness(0, 8, 0, 4), Foreground = Muted }, preview));
        var shortcuts = new StackPanel { Orientation = Orientation.Horizontal };
        shortcuts.Children.Add(configure); shortcuts.Children.Add(retry);
        panel.Children.Add(Card("快捷键与设置", shortcuts, hotkeyStatus, settingsStatus));
        StyleButton(start, primary: true); StyleButton(stop, danger: true); StyleButton(refresh);
        StyleButton(import); StyleButton(importMidi); StyleButton(importJson);
        StyleButton(newScore); StyleButton(saveScore); StyleButton(saveAs); StyleButton(configure); StyleButton(retry);
        settings = SettingsStore.Load(settingsPath, out var loadWarning);
        settingsWriter = new SettingsWriter(writeSettings ?? (value => SettingsStore.Save(settingsPath, value)),
            writeSettings == null && loadWarning == null && File.Exists(settingsPath) ? settings : null);
        bpm.Text = settings.Bpm.ToString(); gap.Text = settings.Gap.ToString();
        volumeSlider.Value = settings.PreviewVolume; localAudio.Volume = settings.PreviewVolume;
        settingsStatus.Text = loadWarning ?? "设置会自动保存；每次启动默认开启“仅日志测试”。";
        saveTimer.Tick += async (_, _) => { saveTimer.Stop(); await SaveCurrentSettingsAsync(); };
        previewTimer.Tick += (_, _) => { previewTimer.Stop(); Preview(); };
        configure.Click += (_, _) => ConfigureHotkeys();
        retry.Click += (_, _) => ApplyHotkeys();
        import.Click += async (_, _) => await Import(); start.Click += async (_, _) => await Begin();
        importMidi.Click += async (_, _) => await ImportMidi();
        importJson.Click += async (_, _) => await ImportJson();
        trackChoice.SelectionChanged += async (_, _) => await SwitchTrack();
        saveScore.Click += async (_, _) => await DocumentOperation(() => SaveScoreAsync(false));
        saveAs.Click += async (_, _) => await DocumentOperation(() => SaveScoreAsync(true));
        newScore.Click += async (_, _) => await DocumentOperation(async () =>
        {
            if (!await ConfirmUnsavedAsync()) return false;
            ApplyDocument(new ScoreDocument("未命名曲谱", 120, "")); return true;
        });
        stop.Click += (_, _) => Stop();
        refresh.Click += (_, _) => Preview();
        score.TextChanged += (_, _) => QueuePreview();
        bpm.TextChanged += (_, _) => QueuePreview();
        gap.TextChanged += (_, _) => QueuePreview();
        foreach (var box in new[] { bpm, gap })
            box.TextChanged += (_, _) => QueueSave();
        foreach (var box in new[] { songTitle, bpm, score, gap })
            box.TextChanged += (_, _) => UpdateDocumentTitle();
        MarkDocumentClean();
        dry.Checked += (_, _) => { UpdateLogVisibility(); UpdateAvailability(); };
        dry.Unchecked += (_, _) => { UpdateLogVisibility(); UpdateAvailability(); };
        SetBusy(false); Preview();
        RefreshLibrary();
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(hwnd); source.AddHook(Hook);
            hotkeys = new HotkeyController(new NativeHotkeys(hwnd));
            ApplyHotkeys();
        };
        Closing += (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            if (documentBusy) { ReportIssue("正在读写曲谱，请完成后再关闭。"); return; }
            closing = true;
            saveTimer.Stop(); previewTimer.Stop();
            cancellation?.Cancel(); // Cancel first; do not put disk I/O before it.
            SetBusy(true); stop.IsEnabled = false;
            status.Text = "正在停止演奏并关闭…";
            // Always leave the initial Closing event before calling Close again,
            // even when there is no playback task or every await completes inline.
            Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(() => _ = CloseAfterStopAsync()));
        };
    }
    private async Task CloseAfterStopAsync()
    {
        try
        {
            Task? playback = running;
            if (playback != null)
            {
                if (await Task.WhenAny(playback, Task.Delay(1500)) != playback)
                    status.Text = "正在等待演奏任务停止，窗口仍可响应…";
                try { await playback; }
                catch (Exception e) { status.Text = "演奏结束异常：" + e.Message; }
            }
            // No background note sender remains before final cleanup begins.
            string? releaseError = await Task.Run(output.Release);
            if (releaseError != null)
            {
                // Do not silently exit with keys still held. A second close retries.
                status.Text = "按键释放失败，请手动按下并松开相关键后再关闭：" + releaseError; ReportIssue(status.Text);
                return;
            }
            if (!await ConfirmUnsavedAsync()) { status.Text = "已取消关闭。"; return; }
            Task save = SaveCurrentSettingsAsync(true);
            if (await Task.WhenAny(save, Task.Delay(1500)) != save)
                status.Text = "正在保存最后的设置，请稍候（窗口仍可响应）…";
            await save;
            hotkeys?.Suspend(); source?.RemoveHook(Hook);
            allowClose = true; Close();
        }
        catch (Exception e) { status.Text = "关闭未完成，请重试：" + e.Message; ReportIssue(status.Text); }
        finally
        {
            if (!allowClose)
            {
                closing = false; stop.IsEnabled = true; SetBusy(false);
                Preview(); QueueSave(); // A cancelled close must restore validation/debouncing.
            }
        }
    }
    private void QueuePreview()
    {
        if (closing) return;
        previewTimer.Stop(); previewTimer.Start();
    }

    private IntPtr Hook(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message == 0x0312)
        {
            // Discard queued messages from old registrations after editing.
            uint packed = unchecked((uint)l.ToInt64());
            uint key = packed >> 16, modifiers = packed & 0xFFFF;
            if (!editingHotkeys && w.ToInt32() == 1 && hotkeys?.StartReady == true &&
                key == settings.Start.Key && modifiers == settings.Start.Modifiers) _ = Begin(key);
            if (!editingHotkeys && w.ToInt32() == 2 && hotkeys?.StopReady == true &&
                key == settings.Stop.Key && modifiers == settings.Stop.Modifiers) Stop();
            handled = true;
        }
        return IntPtr.Zero;
    }
    private void QueueSave()
    {
        if (closing) return;
        ++saveGeneration; // Invalidate completion even during the debounce interval.
        saveTimer.Stop(); saveTimer.Start();
    }
    private async Task SaveCurrentSettingsAsync(bool preserveValidTiming = false)
    {
        long generation = ++saveGeneration;
        AppSettings next = settings with { PreviewVolume = (int)volumeSlider.Value };
        string? invalid = null;
        if (!int.TryParse(bpm.Text, out int tempo) || !int.TryParse(gap.Text, out int silence))
            invalid = "部分数值尚未填写完整，保留上次有效设置。";
        else
        {
            var candidate = next with { Bpm = tempo, Gap = silence };
            try { candidate.Validate(); next = candidate; }
            catch (FormatException e) { invalid = e.Message; }
        }
        if (invalid != null && !preserveValidTiming)
        { settingsStatus.Text = invalid; return; }
        settings = next; // Update the in-memory snapshot before asynchronous disk work.
        string? error = await settingsWriter.SaveAsync(next);
        if (generation != saveGeneration) return; // Never replace a newer edit/error's status.
        settingsStatus.Text = error != null ? "设置未保存：" + error :
            invalid ?? "设置已保存（快捷键、速度、音符间隔和试听音量）。";
    }
    private void ApplyHotkeys()
    {
        if (hotkeys == null || cancellation != null || closing || beginning || editingHotkeys || documentBusy) return;
        hotkeys.Apply(settings.Start, settings.Stop);
        start.Content = "开始 " + settings.Start.Label; stop.Content = "停止 " + settings.Stop.Label;
        hotkeyStatus.Text = hotkeys.Describe(settings.Start, settings.Stop);
        hotkeyIssue = hotkeys.StartReady && hotkeys.StopReady ? null :
            hotkeyStatus.Text + "\n常见原因：其他播放器、录屏或键盘工具占用了热键。点击“自定义快捷键”换一个组合，或关闭占用程序后点“重试注册”。管理员权限不能解除热键占用。";
        UpdateAlert();
    }
    private void ConfigureHotkeys()
    {
        if (hotkeys == null || cancellation != null || closing || beginning || editingHotkeys || documentBusy) return;
        editingHotkeys = true; hotkeys.Suspend();
        try
        {
            var dialog = new HotkeyDialog(settings.Start, settings.Stop) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                settings = settings with { Start = dialog.Start, Stop = dialog.Stop };
                _ = SaveCurrentSettingsAsync(true);
            }
        }
        finally { editingHotkeys = false; ApplyHotkeys(); }
    }
    private async Task WaitForRelease(uint triggerKey, CancellationToken token)
    {
        var timeout = Stopwatch.StartNew();
        while (NativeHotkeys.Held(triggerKey))
        {
            token.ThrowIfCancellationRequested();
            if (timeout.Elapsed.TotalSeconds > 15)
                throw new InvalidOperationException("等待松开快捷键超时，已取消演奏。");
            status.Text = $"请松开启动键及 Ctrl / Alt / Shift / Win；{settings.Stop.Label} 可停止。";
            await Task.Delay(20, token);
        }
        token.ThrowIfCancellationRequested();
    }
    private async Task DocumentOperation(Func<Task<bool>> action)
    {
        if (cancellation != null || beginning || editingHotkeys || closing || documentBusy) return;
        documentBusy = true; SetBusy(true);
        try { await action(); }
        catch (Exception e) { ReportIssue("曲谱操作失败：" + e.Message); }
        finally { documentBusy = false; SetBusy(false); }
    }
    private Task Import() => LoadScore(null);
    private Task LoadScore(string? path) => DocumentOperation(async () =>
    {
        path ??= scoreDialogs.Open(this);
        if (path == null) return false;
        int fallback = int.TryParse(bpm.Text, out int value) && value is >= 20 and <= 300 ? value : settings.Bpm;
        var result = await ScoreDocumentReader.ReadAsync(path, fallback);
        if (!await ConfirmUnsavedAsync()) return false;
        // Saving the current document may have updated the very file selected for import.
        result = await ScoreDocumentReader.ReadAsync(path, fallback);
        ApplyDocument(result.Document);
        SetDocumentWarning(string.Join("\n", result.Warnings));
        status.Text = scoreIssue == null ? "已导入，检查通过；请确认速度（BPM）。" : "已导入，请修正顶部提示的错误。";
        SelectLibraryFile(path);
        return true;
    });
    private Task ImportMidi() => LoadMidi(null);
    private Task LoadMidi(string? path) => DocumentOperation(async () =>
    {
        path ??= scoreDialogs.OpenMidi(this);
        if (path == null) return false;
        // 多轨 MIDI 同样先选一条要演奏的轨道（默认音符最多的一条）。
        var tracks = await Task.Run(() => MidiImporter.ReadTracks(path));
        int? index = null;
        if (tracks.Count > 1)
        {
            int? choice = scoreDialogs.ChooseTrack(this, tracks.Select(t => t.Describe()).ToArray(), 0);
            if (choice == null) return false;
            index = tracks[choice.Value].Index;
        }
        var result = await Task.Run(() => MidiImporter.Read(path, index));
        if (!await ConfirmUnsavedAsync()) return false;
        ApplyDocument(new ScoreDocument(result.Title, result.Bpm, result.Body));
        ShowTrackChoice(Source(true, tracks.Count, path, tracks.Select(t => t.Index.ToString()).ToArray(),
            tracks.Select(t => t.Describe()).ToArray()), index?.ToString());
        SetDocumentWarning(string.Join("\n", result.Warnings));
        status.Text = Imported(result.LoweredFromBpm, "MIDI");
        SelectLibraryFile(path);
        return true;
    });
    private Task ImportJson() => LoadJson(null);
    private Task LoadJson(string? path) => DocumentOperation(async () =>
    {
        path ??= scoreDialogs.OpenJson(this);
        if (path == null) return false;
        // 多音轨是分声部编排：先选一条要演奏的音轨（默认编号最小的一条）。
        var tracks = await Task.Run(() => JsonImporter.ReadTracks(path));
        string? label = null;
        if (tracks.Count > 1)
        {
            int? choice = scoreDialogs.ChooseTrack(this, tracks.Select(t => t.Describe()).ToArray(), 0);
            if (choice == null) return false;
            label = tracks[choice.Value].Label;
        }
        var result = await Task.Run(() => JsonImporter.Read(path, label));
        if (!await ConfirmUnsavedAsync()) return false;
        ApplyDocument(new ScoreDocument(result.Title, result.Bpm, result.Body));
        ShowTrackChoice(Source(false, tracks.Count, path, tracks.Select(t => t.Label).ToArray(),
            tracks.Select(t => t.Describe()).ToArray()), label);
        SetDocumentWarning(string.Join("\n", result.Warnings));
        status.Text = Imported(result.LoweredFromBpm, "JSON");
        SelectLibraryFile(path);
        return true;
    });
    // 单轨曲谱不需要下拉框。
    private static TrackSource? Source(bool midi, int count, string path, string[] labels, string[] descriptions)
        => count > 1 ? new TrackSource(midi, path, labels, descriptions) : null;
    // 多音轨导入后把可选音轨放进下拉框；导入 TXT、新建或换曲谱时清空。
    private void ShowTrackChoice(TrackSource? source, string? applied)
    {
        suppressTrackChange = true;
        try
        {
            trackSource = source;
            appliedTrack = applied;
            trackGroup.Visibility = source == null ? Visibility.Collapsed : Visibility.Visible;
            trackChoice.ItemsSource = source?.Descriptions;
            trackChoice.SelectedIndex = source == null ? -1 : Math.Max(0, source.Labels.ToList().IndexOf(applied ?? ""));
            trackChoice.IsEnabled = !uiBusy && source != null;
        }
        finally { suppressTrackChange = false; }
    }
    // 换轨＝用同一个源文件按另一条轨重新导入；未保存的修改会照常询问。
    private async Task SwitchTrack()
    {
        if (suppressTrackChange || uiBusy || trackSource is not { } source) return;
        int index = trackChoice.SelectedIndex;
        if (index < 0 || index >= source.Labels.Count) return;
        string label = source.Labels[index];
        if (label == appliedTrack) return;
        await DocumentOperation(async () =>
        {
            if (!await ConfirmUnsavedAsync()) { ShowTrackChoice(source, appliedTrack); return false; }
            string title, body;
            int importedBpm;
            string[] importedWarnings;
            if (source.Midi && int.TryParse(label, out int trackIndex))
            {
                var midi = await Task.Run(() => MidiImporter.Read(source.Path, trackIndex));
                title = midi.Title; importedBpm = midi.Bpm; body = midi.Body; importedWarnings = midi.Warnings;
            }
            else
            {
                var json = await Task.Run(() => JsonImporter.Read(source.Path, label));
                title = json.Title; importedBpm = json.Bpm; body = json.Body; importedWarnings = json.Warnings;
            }
            ApplyDocument(new ScoreDocument(title, importedBpm, body));
            ShowTrackChoice(source, label);
            SetDocumentWarning(string.Join("\n", importedWarnings));
            status.Text = $"已换轨：{source.Descriptions[index]}";
            return true;
        });
    }

    // 曲库侧栏：自动读取 简谱\TXT、简谱\MIDI、简谱\JSON，点一下即导入。
    private Border BuildLibrary()
    {
        libraryFilter.ToolTip = "按曲名筛选";
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = Pill("曲库", 13);
        title.ToolTip = "自动读取 EXE 同级的“简谱”文件夹：TXT / MIDI / JSON 三种曲谱";
        Grid.SetRow(title, 0); grid.Children.Add(title);
        Grid.SetRow(libraryFilter, 1); grid.Children.Add(libraryFilter);
        // 不再套外层 ScrollViewer：让 ListBox 自己滚动，鼠标停在列表任意位置都能用滚轮。
        libraryList.Margin = new Thickness(0, 0, 0, 10);
        ScrollViewer.SetVerticalScrollBarVisibility(libraryList, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(libraryList, ScrollBarVisibility.Disabled);
        Grid.SetRow(libraryList, 2); grid.Children.Add(libraryList);
        var footer = new StackPanel();
        footer.Children.Add(libraryHint);
        libraryRefresh.HorizontalAlignment = HorizontalAlignment.Left;
        StyleButton(libraryRefresh);
        footer.Children.Add(libraryRefresh);
        Grid.SetRow(footer, 3); grid.Children.Add(footer);
        // 列表项外观（内边距、悬停、选中）统一交给 DarkTheme 的隐式 ListBoxItem 样式。
        libraryList.SelectionChanged += async (_, _) => await LibrarySelectionChanged();
        libraryFilter.TextChanged += (_, _) => ApplyLibraryFilter();
        libraryRefresh.Click += (_, _) =>
        {
            RefreshLibrary();
            status.Text = $"曲库已刷新：{libraryEntries.Length} 首。";
        };
        return new Border
        {
            Background = CardGrad, BorderBrush = Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 14, 14, 8), Margin = new Thickness(0, 0, 8, 0), Child = grid
        };
    }

    private void RefreshLibrary()
    {
        var entries = new List<LibraryEntry>();
        foreach (string category in new[] { ScoreDialogs.TextCategory, ScoreDialogs.MidiCategory, ScoreDialogs.JsonCategory })
        {
            string folder = Path.Combine(AppContext.BaseDirectory, ScoreDialogs.ScoreFolderName, category);
            if (!Directory.Exists(folder)) continue;
            foreach (string file in Directory.EnumerateFiles(folder).OrderBy(Path.GetFileName, StringComparer.CurrentCulture))
                entries.Add(new(file, category, Path.GetFileNameWithoutExtension(file)));
        }
        libraryEntries = entries.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ApplyLibraryFilter();
    }

    private void ApplyLibraryFilter()
    {
        string filter = libraryFilter.Text.Trim();
        visibleEntries = filter.Length == 0
            ? libraryEntries
            : libraryEntries.Where(e => e.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        suppressLibraryChange = true;
        try { libraryList.ItemsSource = visibleEntries.Select(e => $"{e.Title}    【{e.Kind}】").ToArray(); }
        finally { suppressLibraryChange = false; }
        libraryHint.Text = libraryEntries.Length == 0
            ? "还没有曲谱：点“导入 TXT / MIDI / JSON”添加，或把曲谱放进 简谱\\TXT、简谱\\MIDI、简谱\\JSON 后刷新。"
            : $"共 {visibleEntries.Length} / {libraryEntries.Length} 首；选中即导入，不必再选文件。";
    }

    // 选中即导入；走的是与手动导入完全相同的流程（含多音轨选轨弹窗与未保存询问）。
    private async Task LibrarySelectionChanged()
    {
        if (suppressLibraryChange || uiBusy) return;
        int index = libraryList.SelectedIndex;
        if (index < 0 || index >= visibleEntries.Length) return;
        var entry = visibleEntries[index];
        await (entry.Kind switch
        {
            ScoreDialogs.MidiCategory => LoadMidi(entry.Path),
            ScoreDialogs.JsonCategory => LoadJson(entry.Path),
            _ => LoadScore(entry.Path),
        });
    }

    // 在曲库里高亮当前文件（不在库里的外部文件则取消高亮）。
    private void SelectLibraryFile(string path)
    {
        RefreshLibrary();
        suppressLibraryChange = true;
        try
        {
            libraryList.SelectedIndex = Array.FindIndex(visibleEntries,
                e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        }
        finally { suppressLibraryChange = false; }
    }
    // Imported scores may need a slower tempo before they pass the shared minimum-hold rule.
    private string Imported(int? loweredFromBpm, string kind) => loweredFromBpm is int original
        ? $"已导入 {kind}（已转为单音简谱）；为保证能按住每个音，速度已从 {original} BPM 自动调整为 {bpm.Text} BPM，可直接试听／演奏。"
        : scoreIssue == null
            ? $"已导入 {kind}（已转为单音简谱），请确认速度（BPM）。"
            : $"已导入 {kind}，请修正顶部提示的错误。";
    private void ApplyDocument(ScoreDocument document)
    {
        documentPath = document.SourcePath; legacyDocument = document.IsLegacy;
        extraHeaders = document.ExtraHeaders ?? Array.Empty<string>();
        songTitle.Text = document.Title; bpm.Text = document.Bpm.ToString(); score.Text = document.ScoreText;
        gap.Text = document.Gap.ToString();
        SetDocumentWarning(""); operationIssue = null;
        ShowTrackChoice(null, null);   // 换曲谱即清空音轨下拉；导入 JSON/MIDI 会随后重新填入。
        MarkDocumentClean(); Preview();
    }
    private async Task<bool> ConfirmUnsavedAsync()
    {
        if (!DocumentDirty) return true;
        return scoreDialogs.Unsaved(this) switch
        {
            MessageBoxResult.No => true,
            MessageBoxResult.Yes => await SaveScoreAsync(false),
            _ => false
        };
    }
    private async Task<bool> SaveScoreAsync(bool forceSaveAs)
    {
        try
        {
            var document = new ScoreDocument(songTitle.Text, PlaybackValidation.ParseBpm(bpm.Text), score.Text,
                documentPath, legacyDocument, extraHeaders, PlaybackValidation.ParseGap(gap.Text));
            document.Validate(gap.Text); // Validate before asking for a destination.
            string? path = documentPath;
            if (forceSaveAs || legacyDocument || path == null)
            {
                string name = string.Concat(songTitle.Text.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                path = scoreDialogs.Save(this, name + (legacyDocument ? "-新版" : "") + ".txt");
                if (path == null) return false;
            }
            await ScoreDocumentWriter.SaveAsync(path, document, gap.Text);
            documentPath = Path.GetFullPath(path); legacyDocument = false;
            SetDocumentWarning(string.Join("\n", ScoreDocumentReader.Parse(
                ScoreDocumentWriter.Serialize(document, gap.Text), documentPath, document.Bpm).Warnings));
            MarkDocumentClean(); operationIssue = null; UpdateAlert();
            SelectLibraryFile(path);
            status.Text = "曲谱已保存（新格式，含曲名、BPM和音符间隔）。";
            return true;
        }
        catch (Exception e) { ReportIssue("曲谱未保存：" + e.Message); return false; }
    }
    private void Stop()
    {
        if (closing) return; // Shutdown owns final input cleanup; do not race it.
        cancellation?.Cancel();
        if (cancellation != null) status.Text = listening ? "正在停止试听…" : "正在停止并释放输入…";
        else { var error = output.Release(); status.Text = error ?? "已停止。"; if (error != null) ReportIssue("释放按键失败：" + error); }
    }
    private async Task Begin(uint triggerKey = 0)
    {
        if (cancellation != null || closing || beginning || editingHotkeys || documentBusy) return;
        beginning = true; SetBusy(true);
        operationIssue = null; UpdateAlert();
        try
        {
            var notes = ScoreParser.Parse(score.Text);
            double ms = Timing();
            int silence = ValidateGap(notes, ms);
            scoreIssue = null; errorPosition = null; UpdateAlert();
            bool simulation = dry.IsChecked == true;
            if (!simulation && hotkeys?.StopReady != true)
                throw new InvalidOperationException($"停止键 {settings.Stop.Label} 不可用，禁止真实演奏。请点击“自定义快捷键”或“重试注册”。");
            cancellation = new CancellationTokenSource();
            if (closing || cancellation.IsCancellationRequested) return;
            _ = SaveCurrentSettingsAsync();
            SetBusy(true); log.Clear();
            running = Run(notes, ms, silence, simulation, triggerKey, cancellation.Token);
            await running;
        }
        catch (FormatException) { Preview(); status.Text = "无法开始，请查看顶部提示。"; }
        catch (Exception e)
        {
            status.Text = "无法开始，请查看顶部提示。";
            if (dry.IsChecked != true && hotkeys?.StopReady != true) UpdateAlert();
            else ReportIssue(e.Message);
        }
        finally
        {
            cancellation?.Cancel(); // Invalidate queued progress from the completed run.
            cancellation?.Dispose(); cancellation = null; running = null; beginning = false; SetBusy(false);
        }
    }
    private int ValidateGap(List<ScoreNote> notes, double ms)
        => PlaybackValidation.ValidateGap(gap.Text, notes, ms);
    private double Timing()
    {
        return 60000.0 / PlaybackValidation.ParseBpm(bpm.Text);
    }
    private void Preview()
    {
        if (cancellation != null || closing) return;
        try
        {
            var notes = ScoreParser.Parse(score.Text);
            double ms = Timing();
            var hold = PlaybackValidation.CheckGap(gap.Text, notes, ms);
            double total = 0;
            var lines = new StringBuilder();
            foreach (var note in notes)
            {
                if (lines.Length < 30000)
                    lines.AppendLine($"{total / 1000:0.###}s  {note.Label}  {note.Beats:0.###}拍  ({note.Beats * ms:0.##}ms)");
                total += note.Beats * ms;
            }
            scoreIssue = null; errorPosition = null; UpdateAlert();
            // 短音不再拦截演奏，只在预审里提示哪些音可能发声不足。
            string shortHold = hold.TooShort == 0 ? "" :
                $"注意：有{hold.TooShort}个音短于可按住下限（{PlaybackValidation.MinimumHoldMs}ms），演奏时会按能按住的最长时间触发，" +
                "游戏里可能不发声；可减小“音符间隔”或降低速度。\n";
            preview.Text = shortHold + $"共{notes.Count}个音/休止，{notes.Sum(n => n.Beats):0.###}拍，预计{total / 1000:0.###}秒（不含倒计时）\n" + lines;
        }
        catch (Exception e) { errorPosition = (e as ScoreFormatException)?.Position; scoreIssue = "谱面／设置错误：" + e.Message; preview.Text = scoreIssue; UpdateAlert(); }
    }
    private void SetBusy(bool busy)
    {
        busy |= closing || documentBusy;
        uiBusy = busy;
        import.IsEnabled = importMidi.IsEnabled = importJson.IsEnabled = refresh.IsEnabled = gap.IsEnabled = dry.IsEnabled = !busy;
        bpm.IsEnabled = !busy;
        trackChoice.IsEnabled = !busy && trackSource != null;
        configure.IsEnabled = retry.IsEnabled = !busy;
        score.IsReadOnly = busy;
        songTitle.IsReadOnly = busy;
        foreach (var control in editControls) control.IsEnabled = !busy;
        saveScore.IsEnabled = saveAs.IsEnabled = newScore.IsEnabled = !busy;
        UpdateAvailability();
    }
    private async Task Run(List<ScoreNote> notes, double ms, int silence, bool simulation, uint triggerKey, CancellationToken token)
    {
        string result = simulation ? "日志测试完成：未发送键鼠，不会发声。实际演奏请取消“仅日志测试”。" : "演奏完成。";
        bool failed = false;
        try
        {
            await WaitForRelease(triggerKey, token);
            for (int n = 3; n > 0; n--)
            { status.Text = $"{(simulation ? "日志测试" : "游戏演奏")}：{n}秒后开始，请切到目标窗口…"; await Task.Delay(1000, token); }
            // Check again after countdown: the user may have used Alt+Tab to switch windows.
            await WaitForRelease(triggerKey, token);
            var target = NativeInput.GetForegroundWindow();
            NativeInput.GetWindowThreadProcessId(target, out uint pid);
            if (!simulation && (target == IntPtr.Zero || pid == (uint)Environment.ProcessId))
                throw new InvalidOperationException("倒计时结束时仍在播放器窗口，已取消。请在3秒倒计时内切到游戏口琴界面，或在游戏内按开始快捷键。");
            await Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                void Check()
                {
                    token.ThrowIfCancellationRequested();
                    if (!simulation && NativeInput.GetForegroundWindow() != target)
                        throw new InvalidOperationException("目标窗口失去焦点，已停止。演奏期间请勿切换窗口；切回游戏后重新开始。");
                }
                void Wait(double until)
                {
                    while (true)
                    {
                        Check(); double remaining = until - clock.Elapsed.TotalMilliseconds;
                        if (remaining <= 0) return;
                        if (remaining > 2) token.WaitHandle.WaitOne((int)Math.Min(5, Math.Max(1, remaining - 1)));
                        else Thread.SpinWait(100);
                    }
                }
                var initialError = simulation ? null : output.Release();
                if (initialError != null) throw new InvalidOperationException(initialError);
                double nextStart = 0;
                for (int i = 0; i < notes.Count; i++)
                {
                    var note = notes[i];
                    double noteMs = note.Beats * ms;
                    double begin = nextStart;
                    Wait(begin); Check();
                    // 严重超时直接停止，避免恢复后瞬间补发积压音符。
                    if (clock.Elapsed.TotalMilliseconds - begin > Math.Min(50, noteMs / 4.0))
                        throw new InvalidOperationException("调度延迟过大，已停止；请降低后台负载后重试。");
                    int index = i;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (closing || token.IsCancellationRequested) return;
                        status.Text = $"{index + 1}/{notes.Count}：{note.Label} / {note.Beats:0.###}拍";
                        if (log.LineCount > 150) log.Clear();
                        log.AppendText($"{begin:0}ms  {note.Label}  {note.Beats:0.###}拍 ({noteMs:0.##}ms)\n"); log.ScrollToEnd();
                    }));
                    if (note.Degree != 0)
                    {
                        if (!simulation) output.Modifiers(note);
                        Wait(begin + PlaybackValidation.PreparationMs); Check();
                        if (!simulation) output.NoteOn(note);
                        Wait(begin + noteMs - silence);
                        var error = simulation ? null : output.Release();
                        if (error != null) throw new InvalidOperationException(error);
                    }
                    Wait(begin + noteMs);
                    nextStart = begin + noteMs;
                }
            }, token);
        }
        catch (OperationCanceledException) { result = "已停止。"; }
        catch (Exception e) { failed = true; result = "已停止：" + e.Message; }
        finally
        {
            // 工作任务已退出，再做最终释放，避免释放之后仍有旧任务按键。
            var error = simulation ? null : await Task.Run(output.Release);
            if (!closing)
            {
                status.Text = error == null ? result : result + " 释放失败，请手动按下并松开相关键：" + error;
                if (failed || error != null) ReportIssue(status.Text);
            }
        }
    }
}
