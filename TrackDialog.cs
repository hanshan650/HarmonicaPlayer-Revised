using System.Windows;
using System.Windows.Controls;

namespace HarmonicaPlayer;

// 多音轨 JSON 导入时让用户挑选要演奏的音轨；关闭或取消返回 null（= 取消本次导入）。
public sealed class TrackDialog : Window
{
    public int SelectedIndex { get; private set; }
    public TrackDialog(IReadOnlyList<string> choices, int defaultIndex)
    {
        Title = "选择要演奏的音轨"; Width = 600; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        Content = Chrome.AttachDialog(this, "选择要演奏的音轨", panel);
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = "这首曲谱有多条音轨（分声部编排，例如主旋律、伴奏、不同演奏者）。软件每次只演奏一条，"
                 + "默认是编号最小的一条——通常就是主旋律。请用“本机试听”确认旋律是否正确；选错时可以重新导入换选。" });
        var list = new ListBox { Margin = new Thickness(0, 12, 0, 12), Height = 220, SelectedIndex = defaultIndex };
        foreach (string choice in choices) list.Items.Add(choice);
        panel.Children.Add(list);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var play = new Button { Content = "演奏这条音轨", IsDefault = true, Margin = new Thickness(4), Padding = new Thickness(10, 6, 10, 6) };
        var cancel = new Button { Content = "取消导入", IsCancel = true, Margin = new Thickness(4), Padding = new Thickness(10, 6, 10, 6) };
        play.Click += (_, _) => { SelectedIndex = Math.Max(0, list.SelectedIndex); DialogResult = true; };
        row.Children.Add(play); row.Children.Add(cancel); panel.Children.Add(row);
    }
}
