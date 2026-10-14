using System.Globalization;
using System.Text;

namespace HarmonicaPlayer;

// 「曲谱编辑」按钮背后的文本变换。
//
// 刻意做成与 WPF 无关的纯字符串函数：ParserTests 可以直接断言，界面层只负责选区与替换。
//
// 两条设计原则：
//  1. 音高与时值分开改。音高操作保留时值后缀，时值操作保留音高。
//     所以按钮是「设置成某个值」而不是「追加一个符号」，反复点击不会累积出 5___、5.. 这类非法写法。
//  2. 只处理解析器能接受的音符单元。像【12】这种括号内多音、1___ 这种非法后缀、
//     或缺少配对括号的残片，一律整块原样保留并给出提示，绝不猜着改。
public static class ScoreEditor
{
    /// 音高操作。前四项对应「音高」按钮，Highest 是显式的最高do。
    public enum Pitch { OctaveDown, OctaveUp, ToggleSharp, Natural, Highest }

    /// 变换结果：新文本、实际改动的音符数、给用户的提示（已去重）。
    public readonly record struct Result(string Text, int Changed, IReadOnlyList<string> Notes);

    public static Result ApplyPitch(string text, Pitch action)
    {
        var builder = new StringBuilder();
        var notes = new List<string>();
        int changed = 0;
        foreach (var part in Split(text))
        {
            if (!part.IsNote)
            {
                builder.Append(text, part.Start, part.End - part.Start);
                Add(notes, part.Hint);
                continue;
            }
            if (!Shift(part, action, out int degree, out int octave, out bool sharp, out string? reason))
            {
                builder.Append(text, part.Start, part.End - part.Start);
                Add(notes, reason);
                continue;
            }
            if (degree == part.Degree && octave == part.Octave && sharp == part.Sharp)
            {
                builder.Append(text, part.Start, part.End - part.Start);
                continue;
            }
            builder.Append(Render(degree, octave, sharp)).Append(part.Tail);
            changed++;
        }
        return new Result(builder.ToString(), changed, notes);
    }

    public static Result ApplyBeats(string text, double beats)
    {
        if (!double.IsFinite(beats) || beats <= 0 || beats > 64)
            return new Result(text, 0, new[] { "拍数必须是大于0、不超过64的数字" });

        var builder = new StringBuilder();
        var notes = new List<string>();
        int changed = 0;
        foreach (var part in Split(text))
        {
            if (!part.IsNote)
            {
                builder.Append(text, part.Start, part.End - part.Start);
                Add(notes, part.Hint);
                continue;
            }
            // 休止符也占时间，时值同样可以改，所以这里不像音高那样跳过 0。
            if (Nearly(BeatsOf(part.Tail), beats))
            {
                builder.Append(text, part.Start, part.End - part.Start);
                continue;
            }
            builder.Append(Render(part.Degree, part.Octave, part.Sharp)).Append(RenderTail(beats));
            changed++;
        }
        return new Result(builder.ToString(), changed, notes);
    }

    /// 光标左侧紧邻的音符单元（含时值后缀）在文本中的范围，用于「没有选区时改光标处的音」。
    public static bool TryExpandCaret(string text, int caret, out int start, out int end)
    {
        start = end = 0;
        caret = Math.Clamp(caret, 0, text.Length);
        foreach (var part in Split(text))
        {
            if (!part.IsNote || caret <= part.Start || caret > part.End) continue;
            start = part.Start;
            end = part.End;
            return true;
        }
        return false;
    }

    /// 「编辑按钮实际会改动的范围」：有选区就用选区，没有选区就用光标左侧那个音符。
    /// 界面上的高亮框和改写动作都调用这一个函数，所以框住的就一定是会被改的那一段。
    public static bool TryGetTargetRange(string text, int selectionStart, int selectionLength, out int start, out int end)
    {
        start = end = 0;
        int from = Math.Clamp(selectionStart, 0, text.Length);
        if (selectionLength > 0)
        {
            int to = Math.Clamp(from + selectionLength, 0, text.Length);
            if (to <= from) return false;
            start = from;
            end = to;
            return true;
        }
        return TryExpandCaret(text, from, out start, out end);
    }

    // ---------- 音符单元扫描 ----------

    private readonly record struct Part(int Start, int End, bool IsNote,
        int Degree, int Octave, bool Sharp, string Tail, string? Hint = null);

    private static List<Part> Split(string text)
    {
        var parts = new List<Part>();
        int literal = -1;
        int i = 0;

        void Flush(int end)
        {
            if (literal >= 0 && end > literal) parts.Add(new Part(literal, end, false, 0, 0, false, ""));
            literal = -1;
        }

        while (i < text.Length)
        {
            char c = text[i];
            // 括号开头的必须是「括号内单音」（【1】【#1】（2）【【1】】）。
            // 括号里写了多个音（【12】）就整块跳过，否则里面的数字会被误当成中音处理。
            if (c is '【' or '（' or '(')
            {
                if (TryReadNote(text, i, out int length, out int degree, out int octave, out bool sharp) &&
                    TryReadTail(text, i + length, out string tail, out int tailEnd))
                {
                    Flush(i);
                    parts.Add(new Part(i, tailEnd, true, degree, octave, sharp, tail));
                    i = tailEnd;
                }
                else
                {
                    char close = c == '【' ? '】' : c == '（' ? '）' : ')';
                    int found = text.IndexOf(close, i + 1);
                    Flush(i);
                    int stop = found < 0 ? text.Length : found + 1;
                    parts.Add(new Part(i, stop, false, 0, 0, false, "",
                        "括号内写了多个音或括号不完整，需要手工修改"));
                    i = stop;
                }
                continue;
            }

            if (TryReadNote(text, i, out int len, out int deg, out int oct, out bool sh) &&
                TryReadTail(text, i + len, out string suffix, out int stopAt))
            {
                Flush(i);
                parts.Add(new Part(i, stopAt, true, deg, oct, sh, suffix));
                i = stopAt;
            }
            else
            {
                if (literal < 0) literal = i;
                i++;
            }
        }
        Flush(text.Length);
        return parts;
    }

    /// 尝试读出一个音符本体（不含时值后缀）。返回是否成功，以及占用长度。
    private static bool TryReadNote(string text, int i, out int length, out int degree, out int octave, out bool sharp)
    {
        length = 0; degree = 0; octave = 0; sharp = false;
        if (i >= text.Length) return false;

        if (text[i] == '【')
        {
            // 【【1】】是高两个八度的do，仅此一个音。
            if (i + 4 < text.Length && text[i + 1] == '【' && text[i + 2] == '1' &&
                text[i + 3] == '】' && text[i + 4] == '】')
            {
                length = 5; degree = 1; octave = 2; return true;
            }
            return TryReadBracketed(text, i, '】', 1, out length, out degree, out octave, out sharp);
        }
        if (text[i] is '（' or '(')
            return TryReadBracketed(text, i, text[i] == '（' ? '）' : ')', -1,
                out length, out degree, out octave, out sharp);

        int j = i;
        bool s = false;
        if (text[j] == '#')
        {
            s = true;
            j++;
            if (j >= text.Length) return false;
        }
        if (!IsDegree(text[j])) return false;
        length = j - i + 1; degree = text[j] - '0'; octave = 0; sharp = s;
        return true;
    }

    /// 括号内必须是单个音：可选 # 加一个 0～7 的数字，再多就不是可编辑单元。
    private static bool TryReadBracketed(string text, int i, char close, int octave,
        out int length, out int degree, out int octaveOut, out bool sharp)
    {
        length = 0; degree = 0; octaveOut = 0; sharp = false;
        int j = i + 1;
        bool s = false;
        if (j < text.Length && text[j] == '#')
        {
            s = true;
            j++;
        }
        if (j >= text.Length || !IsDegree(text[j])) return false;
        if (j + 1 >= text.Length || text[j + 1] != close) return false;
        length = j - i + 2; degree = text[j] - '0'; octaveOut = octave; sharp = s;
        return true;
    }

    private static bool IsDegree(char c) => c is >= '0' and <= '7';

    /// 读出紧跟音符的时值后缀（_ . — : 以及它们之间可能出现的空格）。
    /// 没有后缀是正常情况，返回空串并接受该音符；
    /// 后缀不合法（如 1___、1._）则返回 false，该音符整块不参与变换，避免把非法写法拆开重排。
    private static bool TryReadTail(string text, int start, out string tail, out int end)
    {
        tail = "";
        end = start;
        // 先确认后面确实有时值符号，否则就是「没有后缀」而不是「读不到」。
        int probe = start;
        while (probe < text.Length && char.IsWhiteSpace(text[probe])) probe++;
        if (probe >= text.Length || text[probe] is not ('_' or '.' or '-' or '—' or ':')) return true;

        int i = start, last = start;
        int reductions = 0;
        bool dot = false, colon = false;
        int extensions = 0;

        while (true)
        {
            int j = i;
            while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
            if (j >= text.Length) break;
            char c = text[j];
            if (c == ':')
            {
                if (colon || reductions > 0 || dot || extensions > 0) break;
                int k = j + 1;
                while (k < text.Length && (IsDegree(text[k]) || text[k] == '.')) k++;
                if (!TryReadBeats(text[(j + 1)..k], out _)) break;
                colon = true;
                i = k; last = k;
                continue;
            }
            if (c == '_')
            {
                if (colon || dot || extensions > 0 || reductions >= 2) break;
                reductions++; i = j + 1; last = i;
                continue;
            }
            if (c == '.')
            {
                if (colon || dot || extensions > 0) break;
                dot = true; i = j + 1; last = i;
                continue;
            }
            if (c is '-' or '—')
            {
                if (colon) break;
                extensions++; i = j + 1; last = i;
                continue;
            }
            break;
        }

        if (last == start) return false;
        // 后面仍跟着时值符号，说明原写法本身不合法，整块不接受。
        int rest = last;
        while (rest < text.Length && char.IsWhiteSpace(text[rest])) rest++;
        if (rest < text.Length && text[rest] is '_' or '.' or '-' or '—' or ':') return false;

        tail = text[start..last];
        end = last;
        return true;
    }

    // ---------- 拍数换算与渲染 ----------

    private static double BeatsOf(string tail)
    {
        if (tail.Length == 0) return 1;
        int colon = tail.IndexOf(':');
        if (colon >= 0) return TryReadBeats(tail[(colon + 1)..], out double value) ? value : 1;

        int reductions = 0, extensions = 0;
        bool dot = false;
        foreach (char c in tail)
        {
            if (c == '_') reductions++;
            else if (c == '.') dot = true;
            else if (c is '-' or '—') extensions++;
        }
        double beats = 1.0 / (1 << reductions);
        if (dot) beats *= 1.5;
        return beats + extensions;
    }

    private static bool TryReadBeats(string value, out double beats)
    {
        beats = 0;
        if (value.Length == 0 || value[0] == '.' || value[^1] == '.') return false;
        return double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out beats)
            && double.IsFinite(beats) && beats > 0 && beats <= 64;
    }

    /// 用最贴近手写习惯的写法表达拍数：能写符号就不写冒号。
    private static string RenderTail(double beats)
    {
        if (Nearly(beats, 1)) return "";
        if (Nearly(beats, 0.5)) return "_";
        if (Nearly(beats, 0.25)) return "__";
        if (Nearly(beats, 1.5)) return ".";
        if (Nearly(beats, 0.75)) return "_.";
        if (Nearly(beats, 0.375)) return "__.";
        int whole = (int)Math.Round(beats);
        if (whole is >= 2 and <= 16 && Nearly(beats, whole))
            return string.Concat(Enumerable.Repeat(" —", whole - 1));
        return ":" + beats.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string Render(int degree, int octave, bool sharp)
    {
        string name = (sharp ? "#" : "") + (char)('0' + degree);
        return octave switch
        {
            2 => "【【1】】",
            1 => $"【{name}】",
            -1 => $"（{name}）",
            _ => name
        };
    }

    private static bool Nearly(double a, double b) => Math.Abs(a - b) < 1e-9;

    // ---------- 音高变换 ----------

    private static bool Shift(Part part, Pitch action,
        out int degree, out int octave, out bool sharp, out string? reason)
    {
        degree = part.Degree;
        octave = part.Octave;
        sharp = part.Sharp;
        reason = null;
        // 休止符没有音高。整行批量处理时不报错，否则一句谱里几个暂停就能刷出一串提示。
        if (part.Degree == 0) return false;

        switch (action)
        {
            case Pitch.ToggleSharp:
                if (part.Octave == 2) { reason = "【【1】】不支持升半音"; return false; }
                sharp = !part.Sharp;
                return true;
            case Pitch.Natural:
                octave = 0;
                sharp = false;
                return true;
            case Pitch.OctaveUp:
                if (part.Octave == 2) { reason = "【【1】】已是最高音"; return false; }
                // 只有「高音1」能再升一层，因为程序只支持【【1】】这一个第二高八度音。
                if (part.Octave == 1 && (part.Degree != 1 || part.Sharp))
                {
                    reason = "只有高音 1 能升到【【1】】";
                    return false;
                }
                octave = part.Octave + 1;
                return true;
            case Pitch.OctaveDown:
                if (part.Octave == -1) { reason = "低音不能再降八度"; return false; }
                octave = part.Octave - 1;
                return true;
            case Pitch.Highest:
                if (part.Degree != 1 || part.Sharp) { reason = "最高音只能写成【【1】】"; return false; }
                octave = 2;
                sharp = false;
                return true;
            default:
                return false;
        }
    }

    private static void Add(List<string> notes, string? message)
    {
        if (message != null && !notes.Contains(message)) notes.Add(message);
    }
}
