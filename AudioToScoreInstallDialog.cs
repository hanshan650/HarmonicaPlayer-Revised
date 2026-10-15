using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HarmonicaPlayer;

// 应用内安装「音频转谱」附加组件。
//
// 面向的是普通玩家而不是开发者，所以刻意不提供任何选项：装到哪、装哪个版本、走哪个源，
// 全部由程序判断；用户只需要决定"装"还是"不装"。唯一的例外是安装位置会明说出来，
// 让用户知道这十几个 GB 被占到了哪里。
//
// 装完的目录正好落在 AudioToScore 的候选位置（EXE 旁的「音频转谱」），
// 所以关掉这个窗口之后主程序不需要任何额外配置就能认出它。
public sealed class AudioToScoreInstallDialog : Window
{
    private readonly string root;
    private readonly bool gpu;

    private readonly ProgressBar bar = new()
    {
        Minimum = 0, Maximum = 100, Height = 8, Margin = new Thickness(0, 0, 0, 10),
    };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox log = new()
    {
        IsReadOnly = true, Height = 150, Margin = new Thickness(0, 10, 0, 12),
        FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 12,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap,
    };
    private readonly StackPanel actions = new()
    {
        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
    };
    private readonly TextBlock detail = new()
    {
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
    };

    private CancellationTokenSource? cts;

    /// 装好了就是 true，调用方据此决定要不要接着往下走。
    public bool Installed { get; private set; }

    /// 用户选了「我已经装好了」：调用方应该转去让他指认目录。
    public bool WantPickFolder { get; private set; }

    public AudioToScoreInstallDialog(string baseDirectory)
    {
        root = AudioToScoreInstaller.DefaultRoot(baseDirectory);
        gpu = AudioToScoreInstaller.DetectNvidiaGpu();

        Title = "安装「音频转谱」";
        Width = 680;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(18) };
        Content = Chrome.AttachDialog(this, "安装「音频转谱」", panel);

        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "音频转谱能把歌曲、哼唱或口琴录音自动写成简谱，再交给软件演奏。"
                 + "它需要的运行环境与模型体积很大，不随程序本体一起提供，需要现在联网下载一次。",
        });

        var plan = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        plan.Children.Add(Line("需要下载", AudioToScoreInstaller.Human(AudioToScoreInstaller.DownloadBytes(gpu))
            + (gpu ? "" : "（未检测到 NVIDIA 显卡，装 CPU 版，下载量小很多但转谱较慢）")));
        plan.Children.Add(Line("安装后占用", AudioToScoreInstaller.Human(AudioToScoreInstaller.DiskBytes)));
        plan.Children.Add(Line("安装位置", root));
        plan.Children.Add(Line("加速方式", gpu ? "已检测到 NVIDIA 显卡，安装 GPU 加速版" : "用 CPU 转谱"));
        panel.Children.Add(plan);

        if (!AudioToScoreInstaller.HasEnoughDisk(root, out string diskReason) && diskReason.Length > 0)
            detail.Text = "⚠ " + diskReason;

        status.Text = "中途可以取消，已经下好的部分不会白费，下次接着装。";
        panel.Children.Add(status);
        panel.Children.Add(log);
        log.Visibility = Visibility.Collapsed;
        panel.Children.Add(detail);
        panel.Children.Add(actions);
        SetActions();

        Closing += OnClosing;
    }

    private static TextBlock Line(string label, string value) => new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 1, 0, 1),
        Text = label + "　" + value,
    };

    /// 按钮随状态重建：计划态是「开始安装」，安装态是「中止」，结束后回到关闭。
    private void SetActions()
    {
        actions.Children.Clear();
        if (cts is null)
        {
            bool blocked = detail.Text.StartsWith("⚠", StringComparison.Ordinal);
            if (Installed)
            {
                actions.Children.Add(Button("完成", isDefault: true, onClick: () => { DialogResult = true; }));
            }
            else
            {
                var start = Button("开始安装", isDefault: true, onClick: StartInstall);
                start.IsEnabled = !blocked;
                actions.Children.Add(start);
                // 少数人已经自己装过（比如从仓库里跑了 setup.ps1），给他们一条不重复下载的路。
                actions.Children.Add(Button("我已经装好了", onClick: () =>
                {
                    WantPickFolder = true;
                    DialogResult = false;
                }));
                actions.Children.Add(Button("以后再说", isCancel: true, onClick: () => { DialogResult = false; }));
            }
        }
        else
        {
            actions.Children.Add(Button("中止安装", isCancel: true, onClick: Cancel));
        }
    }

    private static Button Button(string text, bool isDefault = false, bool isCancel = false, Action? onClick = null)
    {
        var button = new Button
        {
            Content = text, Margin = new Thickness(4), Padding = new Thickness(14, 6, 14, 6),
            IsDefault = isDefault, IsCancel = isCancel,
        };
        if (onClick != null) button.Click += (_, _) => onClick();
        return button;
    }

    private void Cancel() => cts?.Cancel();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 关窗口等于取消：安装器会杀掉子进程，留下已下好的部分供下次续用。
        cts?.Cancel();
    }

    private async void StartInstall()
    {
        if (cts != null) return;
        cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;
        Installed = false;
        detail.Text = "";
        log.Visibility = Visibility.Visible;
        log.Clear();
        bar.Value = 0;
        status.Text = "正在准备…";
        SetActions();

        void Report(int percent, string text) => Dispatcher.Invoke(() =>
        {
            bar.Value = percent;
            if (text.Length > 0) status.Text = percent + "%　" + text;
        });
        void Write(string line) => Dispatcher.Invoke(() =>
        {
            log.AppendText(line + "\n");
            log.ScrollToEnd();
        });

        try
        {
            await Task.Run(() => AudioToScoreInstaller.InstallAsync(root, gpu, Report, Write, token), token);
            Installed = true;
            bar.Value = 100;
            status.Text = "安装完成。现在可以开始转谱了。";
        }
        catch (OperationCanceledException)
        {
            status.Text = "已中止。已经下好的部分保留着，下次安装会接着用。";
        }
        catch (Exception e)
        {
            status.Text = "安装失败。";
            detail.Text = e.Message;
            Write("");
            Write("× " + e.Message);
        }
        finally
        {
            cts.Dispose();
            cts = null;
            SetActions();
        }
    }
}
