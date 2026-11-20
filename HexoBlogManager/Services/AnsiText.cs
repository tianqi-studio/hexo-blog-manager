using System.Text;
using System.Windows.Media;

namespace HexoBlogManager.Services;

/// <summary>一段带颜色的文本。</summary>
/// <param name="Text">文字内容。</param>
/// <param name="Foreground">前景色；null 表示使用默认色。</param>
/// <param name="Bold">是否加粗（ANSI 的 1 码）。</param>
public sealed record AnsiSegment(string Text, Color? Foreground, bool Bold);

/// <summary>
/// ANSI 转义序列处理工具。
///
/// 背景：Hexo / npm 在输出到管道时仍会带 ANSI 颜色码，
/// 例如 "\u001b[32mINFO\u001b[39m"。如果直接显示会变成 "[32mINFO[39m" 这种乱码。
/// 这里做两件事：
/// · Parse：拆成「文字 + 颜色」的片段，让界面按 Hexo 原本的配色渲染；
/// · Strip：去掉所有转义码，得到纯文本（用于日志分级判断和复制到剪贴板）。
///
/// 只处理 SGR 序列（...m），其他控制序列（光标移动等）直接丢弃。
/// </summary>
public static class AnsiText
{
    /// <summary>16 色标准调色板（参考 VS Code 终端配色，适合深色背景）。</summary>
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x1E, 0x1E, 0x1E), // 30 黑（深底上用它几乎不可见，保持与背景一致）
        Color.FromRgb(0xCD, 0x31, 0x31), // 31 红
        Color.FromRgb(0x0D, 0xBC, 0x79), // 32 绿
        Color.FromRgb(0xE5, 0xE5, 0x10), // 33 黄
        Color.FromRgb(0x24, 0x72, 0xC8), // 34 蓝
        Color.FromRgb(0xBC, 0x3F, 0xBC), // 35 洋红
        Color.FromRgb(0x11, 0xA8, 0xCD), // 36 青
        Color.FromRgb(0xE5, 0xE5, 0xE5), // 37 白
        Color.FromRgb(0x66, 0x66, 0x66), // 90 亮黑（灰）
        Color.FromRgb(0xF1, 0x4C, 0x4C), // 91 亮红
        Color.FromRgb(0x23, 0xD1, 0x8B), // 92 亮绿
        Color.FromRgb(0xF5, 0xF5, 0x43), // 93 亮黄
        Color.FromRgb(0x3B, 0x8E, 0xEA), // 94 亮蓝
        Color.FromRgb(0xD6, 0x70, 0xD6), // 95 亮洋红
        Color.FromRgb(0x29, 0xB8, 0xDB), // 96 亮青
        Color.FromRgb(0xFF, 0xFF, 0xFF)  // 97 亮白
    };

    /// <summary>是否包含 ANSI 转义字符。</summary>
    public static bool ContainsAnsi(string? text)
        => !string.IsNullOrEmpty(text) && text.Contains('\u001b');

    /// <summary>去掉所有 ANSI 转义码，返回纯文本。</summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('\u001b'))
            return text ?? string.Empty;

        var sb = new StringBuilder(text.Length);
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\u001b')
            {
                // 跳过 ESC [ ... <终止字符> 形式的控制序列
                if (i + 1 < text.Length && text[i + 1] == '[')
                {
                    int j = i + 2;
                    while (j < text.Length && !char.IsLetter(text[j]))
                        j++;

                    // 跳过终止字符
                    if (j < text.Length)
                        j++;

                    i = j;
                    continue;
                }

                // 其他 ESC 序列（如 ESC ] ... BEL），跳过 ESC 及其后一个字符
                i += 2;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 把一行文本按 ANSI 颜色码拆成多个片段。
    /// </summary>
    /// <param name="text">原始文本（可能含 ANSI 码）。</param>
    /// <param name="defaultColor">没有指定颜色时使用的默认色。</param>
    public static List<AnsiSegment> Parse(string? text, Color defaultColor)
    {
        var segments = new List<AnsiSegment>();

        if (string.IsNullOrEmpty(text))
            return segments;

        if (!text.Contains('\u001b'))
        {
            segments.Add(new AnsiSegment(text, null, false));
            return segments;
        }

        var buffer = new StringBuilder();
        Color? current = null;     // null 表示用默认色
        bool bold = false;
        int i = 0;

        void Flush()
        {
            if (buffer.Length == 0) return;
            segments.Add(new AnsiSegment(buffer.ToString(), current, bold));
            buffer.Clear();
        }

        while (i < text.Length)
        {
            char c = text[i];

            if (c == '\u001b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                int end = i + 2;
                while (end < text.Length && !char.IsLetter(text[end]))
                    end++;

                if (end >= text.Length)
                {
                    // 不完整的序列，原样保留剩余部分
                    buffer.Append(text[i..]);
                    break;
                }

                char terminator = text[end];

                if (terminator == 'm')
                {
                    Flush();
                    string param = text[(i + 2)..end];
                    ApplySgr(param, ref current, ref bold);
                }

                // 非 SGR 序列（如清屏、移动光标）直接丢弃
                i = end + 1;
                continue;
            }

            buffer.Append(c);
            i++;
        }

        Flush();

        if (segments.Count == 0)
            segments.Add(new AnsiSegment(string.Empty, null, false));

        return segments;
    }

    /// <summary>解析 SGR 参数串，例如 "1;32"、"0"、""。</summary>
    private static void ApplySgr(string parameters, ref Color? current, ref bool bold)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            // "\u001b[m" 等同于重置
            current = null;
            bold = false;
            return;
        }

        string[] parts = parameters.Split(';');

        foreach (string part in parts)
        {
            if (!int.TryParse(part, out int code))
                continue;

            switch (code)
            {
                case 0:
                    current = null;
                    bold = false;
                    break;

                case 1:
                    bold = true;
                    break;

                case 22:
                    bold = false;
                    break;

                case 39:
                    current = null;
                    break;

                default:
                    if (code is >= 30 and <= 37)
                        current = Palette[code - 30];
                    else if (code is >= 90 and <= 97)
                        current = Palette[code - 90 + 8];
                    // 背景色（40-49）在日志里用不上，忽略
                    break;
            }
        }
    }
}
