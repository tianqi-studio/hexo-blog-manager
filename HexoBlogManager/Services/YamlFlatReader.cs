using System.Text;

namespace HexoBlogManager.Services;

/// <summary>解析出来的一条记录。</summary>
public sealed class YamlEntry
{
    /// <summary>点号分隔的路径，例如 nav.display_title。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>值文本；容器（只有键没有值）为空串。</summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>值所在行号（1 开始），用于在报告里定位。</summary>
    public int Line { get; init; }

    /// <summary>是否只是一个容器键（下面还有子级）。</summary>
    public bool IsContainer { get; init; }

    /// <summary>是否来自多行文本块（| 或 &gt;）。</summary>
    public bool IsBlock { get; init; }
}

/// <summary>
/// 一份 YAML 文本的扁平解析结果：所有叶子值都变成「路径 → 值」。
///
/// 为什么是「扁平」而不是树？
/// 配置页要做的是「按字段名取值填表」，扁平字典查一次就能拿到，
/// 不用为每个字段写一套嵌套遍历；合并多份配置时也只是字典覆盖，逻辑最简单。
/// </summary>
public sealed class YamlFlatResult
{
    /// <summary>路径 → 记录。键名区分大小写（YAML 规范如此）。</summary>
    public Dictionary<string, YamlEntry> Entries { get; } = new(StringComparer.Ordinal);

    /// <summary>路径 → 列表项（形如 keywords: 下的 - a / - b）。</summary>
    public Dictionary<string, List<string>> Lists { get; } = new(StringComparer.Ordinal);

    /// <summary>解析过程中发现的可疑行，用于给用户提示（不是异常）。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>文件总行数。</summary>
    public int LineCount { get; set; }

    /// <summary>解析出的条目数（含容器与列表）。</summary>
    public int ValueCount => Entries.Count + Lists.Count;

    /// <summary>按路径取记录；先精确匹配，再忽略大小写兜底。</summary>
    public bool TryGetEntry(string path, out YamlEntry entry)
    {
        if (Entries.TryGetValue(path, out entry!))
            return true;

        foreach (var pair in Entries)
        {
            if (pair.Key.Equals(path, StringComparison.OrdinalIgnoreCase))
            {
                entry = pair.Value;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    /// <summary>按路径取文本值：标量直接返回，列表用「, 」拼接，取不到返回空串。</summary>
    public string GetText(string path)
    {
        if (TryGetEntry(path, out var entry) && entry.Value.Length > 0)
            return entry.Value;

        if (Lists.TryGetValue(path, out var items) && items.Count > 0)
            return string.Join(", ", items);

        foreach (var pair in Lists)
        {
            if (pair.Key.Equals(path, StringComparison.OrdinalIgnoreCase))
                return string.Join(", ", pair.Value);
        }

        return string.Empty;
    }

    /// <summary>取一个映射块（例如 menu）的子项，按出现顺序返回。</summary>
    public List<(string Key, string Value)> GetChildren(string path)
    {
        var result = new List<(string Key, string Value)>();
        string prefix = path + ".";

        foreach (var pair in Entries)
        {
            if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string rest = pair.Key[prefix.Length..];
            if (rest.Length == 0 || rest.Contains('.'))
                continue;   // 只要直接子级

            result.Add((rest, pair.Value.Value));
        }

        return result;
    }
}

/// <summary>
/// 容错型 YAML 扁平解析器。
///
/// 和 YamlPathEditor 的分工：
/// · YamlPathEditor 负责「写入」——定位某一行、只替换值，保住注释和顺序；
/// · 本类负责「读取」——把整份文件摊平成路径表，遇到不规范写法也尽量读出来。
///
/// 支持的写法：嵌套映射、列表项、行内列表 [a, b]、多行文本块（| 和 &gt;）、
/// 引号值、行尾注释。
///
/// 刻意「不抛异常」：配置写错是常态，解析器要能跳过坏行继续读剩下的字段，
/// 并把坏在哪一行记到 Warnings 里给用户看。
/// </summary>
public static class YamlFlatReader
{
    /// <summary>解析一份 YAML 文本。永远不抛异常。</summary>
    public static YamlFlatResult Parse(string? content)
    {
        var result = new YamlFlatResult();

        if (string.IsNullOrEmpty(content))
            return result;

        string text = content.TrimStart('\uFEFF')
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        string[] lines = text.Split('\n');
        result.LineCount = lines.Length;

        // 当前所处的层级栈：每一层记录缩进和键名
        var stack = new List<(int Indent, string Key)>();
        var definedPaths = new HashSet<string>(StringComparer.Ordinal);

        bool reportedTab = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            string trimmed = raw.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#') ||
                trimmed == "---" || trimmed == "..." || trimmed.StartsWith('%'))
                continue;

            int indent = IndentOf(raw, out bool hasTab);

            if (hasTab && !reportedTab)
            {
                result.Warnings.Add($"第 {i + 1} 行用了 Tab 缩进（YAML 规范不允许），已按 2 个空格处理");
                reportedTab = true;
            }

            // 缩进回退，说明离开了更深的层级
            while (stack.Count > 0 && indent <= stack[^1].Indent)
                stack.RemoveAt(stack.Count - 1);

            // ---------- 列表项 ----------
            if (trimmed == "-" || trimmed.StartsWith("- "))
            {
                if (stack.Count == 0)
                {
                    result.Warnings.Add($"第 {i + 1} 行是列表项但没有上级键，已跳过");
                    continue;
                }

                string parent = JoinPath(stack);
                string item = trimmed.Length > 2 ? trimmed[2..].Trim() : string.Empty;
                item = YamlMiniParser.Unquote(YamlMiniParser.StripInlineComment(item));

                if (!result.Lists.TryGetValue(parent, out var list))
                {
                    list = new List<string>();
                    result.Lists[parent] = list;
                }

                list.Add(item);
                continue;
            }

            // ---------- 键值 ----------
            int colon = IndexOfKeyColon(trimmed);
            if (colon <= 0)
            {
                result.Warnings.Add($"第 {i + 1} 行缺少「键: 值」结构，已跳过：{Shorten(trimmed)}");
                continue;
            }

            string key = YamlMiniParser.Unquote(trimmed[..colon].Trim());
            if (key.Length == 0)
            {
                result.Warnings.Add($"第 {i + 1} 行的键名为空，已跳过：{Shorten(trimmed)}");
                continue;
            }

            string path = stack.Count > 0 ? JoinPath(stack) + "." + key : key;
            string valuePart = trimmed[(colon + 1)..].Trim();

            // ---------- 容器（只有键） ----------
            if (valuePart.Length == 0)
            {
                AddEntry(result, definedPaths, new YamlEntry
                {
                    Path = path,
                    Value = string.Empty,
                    Line = i + 1,
                    IsContainer = true
                });

                stack.Add((indent, key));
                continue;
            }

            // ---------- 多行文本块 ----------
            if (IsBlockMarker(valuePart))
            {
                var (blockText, nextIndex) = ReadBlockScalar(lines, i, indent, valuePart.StartsWith('>'));

                AddEntry(result, definedPaths, new YamlEntry
                {
                    Path = path,
                    Value = blockText,
                    Line = i + 1,
                    IsBlock = true
                });

                i = nextIndex;
                continue;
            }

            // ---------- 普通标量 ----------
            if (IsUnbalancedQuote(valuePart))
                result.Warnings.Add($"第 {i + 1} 行的引号没有闭合，值可能不完整：{Shorten(valuePart)}");

            string value = YamlMiniParser.Unquote(YamlMiniParser.StripInlineComment(valuePart));
            value = ExpandInlineList(value);

            AddEntry(result, definedPaths, new YamlEntry
            {
                Path = path,
                Value = value,
                Line = i + 1
            });
        }

        return result;
    }

    // ==================================================================
    // 内部工具
    // ==================================================================

    private static void AddEntry(YamlFlatResult result, HashSet<string> defined, YamlEntry entry)
    {
        if (!defined.Add(entry.Path))
        {
            // 重复键：YAML 规范视为错误，Hexo 用的 js-yaml 默认会直接报 duplicated mapping key。
            // 本工具不报错，按后出现的值显示，并提示用户去修。
            result.Warnings.Add($"第 {entry.Line} 行重复定义了 {entry.Path}（Hexo 解析时可能直接报错，本工具按后出现的值显示）");
        }

        result.Entries[entry.Path] = entry;
    }

    private static string JoinPath(List<(int Indent, string Key)> stack)
    {
        var sb = new StringBuilder();
        foreach (var item in stack)
        {
            if (sb.Length > 0) sb.Append('.');
            sb.Append(item.Key);
        }
        return sb.ToString();
    }

    /// <summary>统计行首缩进；出现 Tab 时按 2 个空格折算并标记。</summary>
    private static int IndentOf(string line, out bool hasTab)
    {
        hasTab = false;
        int indent = 0;

        foreach (char c in line)
        {
            if (c == ' ')
            {
                indent++;
            }
            else if (c == '\t')
            {
                indent += 2;
                hasTab = true;
            }
            else
            {
                break;
            }
        }

        return indent;
    }

    /// <summary>
    /// 找「键值分隔冒号」：必须在引号外，且后面是空白或行尾，
    /// 这样 url: https://a.com 里的冒号不会被误判。
    /// </summary>
    private static int IndexOfKeyColon(string text)
    {
        char quote = '\0';

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                continue;
            }

            if (c == ':' && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
                return i;
        }

        return -1;
    }

    private static bool IsBlockMarker(string value)
        => value is "|" or "|-" or "|+" or ">" or ">-" or ">+";

    /// <summary>读多行文本块，返回文本和「块最后一行」的下标。</summary>
    private static (string Text, int NextIndex) ReadBlockScalar(string[] lines, int keyLine, int keyIndent, bool folded)
    {
        var parts = new List<string>();
        int blockIndent = -1;
        int i = keyLine + 1;

        for (; i < lines.Length; i++)
        {
            string raw = lines[i];

            if (raw.Trim().Length == 0)
            {
                parts.Add(string.Empty);
                continue;
            }

            int indent = IndentOf(raw, out _);
            if (indent <= keyIndent)
                break;

            if (blockIndent < 0)
                blockIndent = indent;

            parts.Add(raw.Length >= blockIndent ? raw[blockIndent..] : raw.Trim());
        }

        while (parts.Count > 0 && parts[^1].Length == 0)
            parts.RemoveAt(parts.Count - 1);

        string text = folded
            ? string.Join(" ", parts.Where(p => p.Length > 0))
            : string.Join("\n", parts);

        return (text.Trim(), i - 1);
    }

    /// <summary>引号成对检查（只看是不是以引号开头却没在同处收尾）。</summary>
    private static bool IsUnbalancedQuote(string value)
    {
        if (value.Length < 2)
            return value.Length == 1 && (value[0] == '"' || value[0] == '\'');

        char first = value[0];
        if (first != '"' && first != '\'')
            return false;

        return value[^1] != first;
    }

    /// <summary>把 [a, b, c] 这种行内列表展开成「a, b, c」，表单里更好读。</summary>
    private static string ExpandInlineList(string value)
    {
        if (value.Length < 2 || value[0] != '[' || value[^1] != ']')
            return value;

        string inner = value[1..^1];
        var items = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';

        foreach (char c in inner)
        {
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                sb.Append(c);
                continue;
            }

            if (c == ',')
            {
                items.Add(YamlMiniParser.Unquote(sb.ToString().Trim()));
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        if (sb.ToString().Trim().Length > 0)
            items.Add(YamlMiniParser.Unquote(sb.ToString().Trim()));

        return string.Join(", ", items.Where(x => x.Length > 0));
    }

    private static string Shorten(string text, int max = 40)
        => text.Length <= max ? text : text[..max] + "…";
}
