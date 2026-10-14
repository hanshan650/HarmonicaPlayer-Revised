using HarmonicaPlayer;

static class ScoreEditorTests
{
    public static void Run()
    {
        int count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); count++; }

        // 每次变换的结果都应当仍能被解析器接受，且拍数符合预期——这是最有价值的一条约束。
        void CheckPlayable(string text, double[] beats, string name)
        {
            try
            {
                var parsed = ScoreParser.Parse(text);
                Check(parsed.Select(n => n.Beats).SequenceEqual(beats), name + "（拍数）");
            }
            catch (FormatException e) { throw new Exception("FAIL: " + name + "（结果无法解析：" + text + " -> " + e.Message + "）"); }
        }

        // ---------- 音高：八度 ----------
        var up = ScoreEditor.ApplyPitch("1", ScoreEditor.Pitch.OctaveUp);
        Check(up.Text == "【1】" && up.Changed == 1, "中音升八度变高音");
        Check(ScoreEditor.ApplyPitch("【1】", ScoreEditor.Pitch.OctaveUp).Text == "【【1】】", "高音1升到最高do");
        var refuse = ScoreEditor.ApplyPitch("【2】", ScoreEditor.Pitch.OctaveUp);
        Check(refuse.Text == "【2】" && refuse.Changed == 0 && refuse.Notes.Count == 1, "高音2不能再升八度并给出提示");
        Check(ScoreEditor.ApplyPitch("（1）", ScoreEditor.Pitch.OctaveUp).Text == "1", "低音升八度回中音");
        Check(ScoreEditor.ApplyPitch("【【1】】", ScoreEditor.Pitch.OctaveUp).Changed == 0, "最高音不再上升");

        var down = ScoreEditor.ApplyPitch("1", ScoreEditor.Pitch.OctaveDown);
        Check(down.Text == "（1）", "中音降八度变低音");
        Check(ScoreEditor.ApplyPitch("【3】", ScoreEditor.Pitch.OctaveDown).Text == "3", "高音降八度回中音");
        Check(ScoreEditor.ApplyPitch("【【1】】", ScoreEditor.Pitch.OctaveDown).Text == "【1】", "最高音降八度变高音1");
        Check(ScoreEditor.ApplyPitch("（2）", ScoreEditor.Pitch.OctaveDown).Changed == 0, "低音不能再降");

        // ---------- 音高：升号 / 还原 / 最高do ----------
        Check(ScoreEditor.ApplyPitch("1", ScoreEditor.Pitch.ToggleSharp).Text == "#1", "加升号");
        Check(ScoreEditor.ApplyPitch("#1", ScoreEditor.Pitch.ToggleSharp).Text == "1", "再点一次去掉升号");
        Check(ScoreEditor.ApplyPitch("【1】", ScoreEditor.Pitch.ToggleSharp).Text == "【#1】", "升号写在括号内");
        Check(ScoreEditor.ApplyPitch("【【1】】", ScoreEditor.Pitch.ToggleSharp).Changed == 0, "最高音不接受升号");

        Check(ScoreEditor.ApplyPitch("【#1】", ScoreEditor.Pitch.Natural).Text == "1", "还原去掉升降与括号");
        Check(ScoreEditor.ApplyPitch("（2）", ScoreEditor.Pitch.Natural).Text == "2", "还原低音");
        Check(ScoreEditor.ApplyPitch("3", ScoreEditor.Pitch.Natural).Changed == 0, "已是中音时还原无改动");

        Check(ScoreEditor.ApplyPitch("1", ScoreEditor.Pitch.Highest).Text == "【【1】】", "指定最高do");
        Check(ScoreEditor.ApplyPitch("#1", ScoreEditor.Pitch.Highest).Changed == 0, "升号音不能作最高do");
        Check(ScoreEditor.ApplyPitch("2", ScoreEditor.Pitch.Highest).Changed == 0, "非1不能作最高do");

        // ---------- 音高：批量、休止符与保留时值 ----------
        Check(ScoreEditor.ApplyPitch("123", ScoreEditor.Pitch.ToggleSharp).Text == "#1#2#3", "连写数字逐个处理");
        Check(ScoreEditor.ApplyPitch("1 2_ 3__", ScoreEditor.Pitch.ToggleSharp).Text == "#1 #2_ #3__", "批量处理保留时值符号");
        Check(ScoreEditor.ApplyPitch("1_ 【2】.", ScoreEditor.Pitch.OctaveDown).Text == "（1）_ 2.", "升降八度保留时值");
        var rest = ScoreEditor.ApplyPitch("0 0_ 1", ScoreEditor.Pitch.ToggleSharp);
        Check(rest.Text == "0 0_ #1" && rest.Notes.Count == 0, "休止符静默跳过且不报错");
        Check(ScoreEditor.ApplyPitch("0 0_", ScoreEditor.Pitch.ToggleSharp).Changed == 0, "全是休止符时无改动");

        // ---------- 时值 ----------
        Check(ScoreEditor.ApplyBeats("1", 0.5).Text == "1_", "设为半拍");
        Check(ScoreEditor.ApplyBeats("1", 0.25).Text == "1__", "设为四分之一拍");
        Check(ScoreEditor.ApplyBeats("1", 0.75).Text == "1_.", "设为四分之三拍");
        Check(ScoreEditor.ApplyBeats("1", 1.5).Text == "1.", "设为附点一拍半");
        Check(ScoreEditor.ApplyBeats("1", 2).Text == "1 —", "设为两拍用延长符");
        Check(ScoreEditor.ApplyBeats("1", 3).Text == "1 — —", "设为三拍");
        Check(ScoreEditor.ApplyBeats("1", 1.25).Text == "1:1.25", "非惯用拍数用冒号写法");
        Check(ScoreEditor.ApplyBeats("1", 0.375).Text == "1__.", "设为四分之一拍加附点");
        Check(ScoreEditor.ApplyBeats("1", 1).Text == "1", "设为一拍去掉时值符号");

        Check(ScoreEditor.ApplyBeats("1_", 1).Text == "1", "去掉减时线");
        Check(ScoreEditor.ApplyBeats("1.", 0.5).Text == "1_", "附点改成半拍");
        Check(ScoreEditor.ApplyBeats("1 — —", 1).Text == "1", "延长符全部去掉");
        Check(ScoreEditor.ApplyBeats("1 — —", 4).Text == "1 — — —", "在现有基础上重新指定");
        Check(ScoreEditor.ApplyBeats("1:1.25", 0.25).Text == "1__", "冒号写法改回符号");
        Check(ScoreEditor.ApplyBeats("1 .", 2).Text == "1 —", "符号间夹空格也能识别");
        Check(ScoreEditor.ApplyBeats("0_", 0.5).Changed == 0, "休止符同样支持时值且幂等");

        // ---------- 幂等与非法写法 ----------
        Check(ScoreEditor.ApplyBeats("1_", 0.5).Changed == 0, "同值重复设置无改动");
        Check(ScoreEditor.ApplyBeats("1", 0).Text == "1" && ScoreEditor.ApplyBeats("1", 0).Notes.Count == 1, "拒绝0拍");
        Check(ScoreEditor.ApplyBeats("1", 65).Changed == 0, "拒绝超过64拍");

        var dirty = ScoreEditor.ApplyBeats("1___ 2", 0.5);
        Check(dirty.Text == "1___ 2_" && dirty.Changed == 1, "非法写法整块跳过、不影响其他音符");
        Check(ScoreEditor.ApplyPitch("【12】", ScoreEditor.Pitch.OctaveUp).Text == "【12】", "括号内多音不参与音高变换");
        var grouped = ScoreEditor.ApplyBeats("【12】 3", 0.5);
        Check(grouped.Text == "【12】 3_" && grouped.Changed == 1 && grouped.Notes.Count == 1,
            "括号内多音不由按钮改时值，但会提示且不影响其他音符");
        Check(ScoreEditor.ApplyPitch("1 2", ScoreEditor.Pitch.OctaveUp).Text == "【1】 【2】", "空格分隔正常");

        // ---------- 结果必须仍可被解析 ----------
        CheckPlayable(ScoreEditor.ApplyPitch("1 2_ 【3】 （4） #5", ScoreEditor.Pitch.OctaveUp).Text,
            new[] { 1.0, .5, 1.0, 1.0, 1.0 }, "升八度后仍可解析");
        CheckPlayable(ScoreEditor.ApplyBeats("1 2_ 【3】 （4） 0", 0.25).Text,
            new[] { .25, .25, .25, .25, .25 }, "批量改时值后仍可解析");
        CheckPlayable(ScoreEditor.ApplyBeats("1", 5).Text, new[] { 5.0 }, "五拍写法可解析");
        var highest = ScoreEditor.ApplyPitch("【【1】】:1.25", ScoreEditor.Pitch.Natural);
        Check(highest.Text == "1:1.25", "最高音还原为中音并保留冒号时值");
        CheckPlayable(highest.Text, new[] { 1.25 }, "最高音还原后仍可解析");
        CheckPlayable(ScoreEditor.ApplyPitch("【【1】】", ScoreEditor.Pitch.OctaveDown).Text,
            new[] { 1.0 }, "最高音降八度后仍可解析");

        // ---------- 光标定位 ----------
        Check(ScoreEditor.TryExpandCaret("1 2_ 3", 1, out int s1, out int e1) && s1 == 0 && e1 == 1, "光标在音符后取该音符");
        Check(ScoreEditor.TryExpandCaret("1 2_ 3", 4, out int s2, out int e2) && s2 == 2 && e2 == 4, "取到带时值符号的完整单元");
        Check(ScoreEditor.TryExpandCaret("1 2_ 3", 6, out int s3, out int e3) && s3 == 5 && e3 == 6, "行尾音符");
        Check(!ScoreEditor.TryExpandCaret("1 2", 2, out _, out _), "光标悬在音符之前时不猜目标");

        Console.WriteLine($"PASS: ScoreEditor {count} tests");
    }
}
