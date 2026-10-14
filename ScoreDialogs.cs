using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace HarmonicaPlayer;

// Injectable only for UI tests; production uses standard Windows dialogs.
public interface IScoreDialogs
{
    string? Open(Window owner);
    string? OpenMidi(Window owner) => new ScoreDialogs().OpenMidi(owner);
    string? OpenJson(Window owner) => new ScoreDialogs().OpenJson(owner);
    // 音频转谱的输入音频，以及指认附加组件环境目录。
    string? OpenAudio(Window owner) => new ScoreDialogs().OpenAudio(owner);
    string? OpenFolder(Window owner, string title) => new ScoreDialogs().OpenFolder(owner, title);
    string? Save(Window owner, string suggestedName);
    MessageBoxResult Unsaved(Window owner);
    string? SaveMidi(Window owner, string suggestedName) => new ScoreDialogs().SaveMidi(owner, suggestedName);
    // 多音轨 JSON 导入时让用户挑选音轨；取消返回 null。
    int? ChooseTrack(Window owner, IReadOnlyList<string> choices, int defaultIndex) =>
        new ScoreDialogs().ChooseTrack(owner, choices, defaultIndex);
}

public sealed class ScoreDialogs : IScoreDialogs
{
    // Scores live in a 简谱 folder beside the EXE, split by file type, so every import
    // and save dialog starts in the folder that matches the button that was pressed.
    public const string ScoreFolderName = "简谱";
    public const string TextCategory = "TXT";
    public const string MidiCategory = "MIDI";
    public const string JsonCategory = "JSON";

    // Falls back to 简谱, then to the EXE folder, when a category does not exist yet.
    public static string CategoryFolder(string category)
    {
        string root = Path.Combine(AppContext.BaseDirectory, ScoreFolderName);
        string path = Path.Combine(root, category);
        if (Directory.Exists(path)) return path;
        return Directory.Exists(root) ? root : AppContext.BaseDirectory;
    }

    public string? Open(Window owner) => Choose(owner, "TXT 简谱|*.txt", TextCategory);
    public string? OpenMidi(Window owner) => Choose(owner, "MIDI 文件|*.mid;*.midi", MidiCategory);
    public string? OpenJson(Window owner) => Choose(owner, "JSON 曲谱|*.json", JsonCategory);

    // 音频不进 简谱 目录，最后一次用过哪儿就默认开哪儿由系统自己记。
    public string? OpenAudio(Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "音频文件|*.mp3;*.wav;*.flac;*.ogg;*.m4a;*.aac;*.wma|所有文件|*.*",
            Title = "选择要转成简谱的音频"
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public string? OpenFolder(Window owner, string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }
    public string? Save(Window owner, string suggestedName)
        => SaveTo(owner, "TXT 简谱|*.txt", ".txt", TextCategory, suggestedName);
    public string? SaveMidi(Window owner, string suggestedName)
        => SaveTo(owner, "标准 MIDI 文件|*.mid", ".mid", MidiCategory, suggestedName);

    private static string? Choose(Window owner, string filter, string category)
    {
        var dialog = new OpenFileDialog { Filter = filter, InitialDirectory = CategoryFolder(category) };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    private static string? SaveTo(Window owner, string filter, string extension, string category, string suggestedName)
    {
        var dialog = new SaveFileDialog { Filter = filter, DefaultExt = extension, AddExtension = true,
            OverwritePrompt = true, FileName = suggestedName, InitialDirectory = CategoryFolder(category) };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
    public MessageBoxResult Unsaved(Window owner) => MessageBox.Show(owner,
        "曲谱、曲名、BPM或音符间隔已修改，是否保存？\n“否”放弃本次修改，“取消”返回编辑。",
        "曲谱尚未保存", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

    // 默认选中下标由调用方给出（JsonImporter.ReadTracks 的顺序里 0 就是默认音轨）。
    public int? ChooseTrack(Window owner, IReadOnlyList<string> choices, int defaultIndex)
    {
        var dialog = new TrackDialog(choices, defaultIndex) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.SelectedIndex : null;
    }
}
