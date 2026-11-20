using System.Text;
using HexoBlogManager.Models;

namespace HexoBlogManager.Services;

/// <summary>
/// front-matter 的原始结构。
///
/// 设计目标：**保真**。
/// 用户的文章里可能有 cover、description、katex、copyright 等我们不管的字段，
/// 编辑保存时绝不能把它们弄丢。所以这里不只解析值，还把每个键的「原始文本块」存下来，
/// 保存时原样回写，只替换我们管理的 7 个字段。
/// </summary>
public sealed class FrontMatterBlock
{
    /// <summary>文件里是否存在 front-matter。</summary>
    public bool Exists { get; set; }

    /// <summary>键的原始出现顺序。</summary>
    public List<string> Keys { get; } = new();

    /// <summary>键 → 该键的原始行（第一行是 "key: value"，后续是缩进子行）。</summary>
    public Dictionary<string, List<string>> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>front-matter 结束标记之后的正文。</summary>
    public string Body { get; set; } = string.Empty;
}

/// <summary>
/// front-matter 解析与重建。
///
/// 支持的值形态：
/// · 标量：title: 你好
/// · 列表（多行）：tags:\n  - A\n  - B
/// · 列表（行内）：tags: [A, B]
/// · 布尔：top: true / false / 1 / 0
/// · 多行文本（| 或 >）：原样保留，不做语义处理
/// </summary>
public static class FrontMatter
{
    /// <summary>本工具负责编辑的字段（其余字段原样保留）。</summary>
    private static readonly string[] ManagedKeys =
        { "title", "date", "updated", "tags", "categories", "top", "comments" };

    private static readonly HashSet<string> ManagedSet =
        new(ManagedKeys, StringComparer.OrdinalIgnoreCase);

    // ==================================================================
    // 解析
    // ==================================================================

    public static FrontMatterBlock Parse(string? content)
    {
        var result = new FrontMatterBlock();

        if (string.IsNullOrEmpty(content))
            return result;

        // 去掉 UTF-8 BOM，统一换行，避免 \r 混进值里
        content = content.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');

        string[] lines = content.Split('\n');

        // 允许 front-matter 之前有空白行
        int first = 0;
        while (first < lines.Length && lines[first].Trim().Length == 0)
            first++;

        if (first >= lines.Length || lines[first].Trim() != "---")
        {
            // 没有 front-matter，全文都是正文
            result.Body = content;
            return result;
        }

        int bodyStart = -1;
        for (int i = first + 1; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t == "---" || t == "...")
            {
                bodyStart = i + 1;
                break;
            }
        }

        if (bodyStart < 0)
        {
            // 只有开头 --- 没有结尾，按无 front-matter 处理，避免把正文吃掉
            result.Body = content;
            return result;
        }

        result.Exists = true;

        string? currentKey = null;

        for (int i = first + 1; i < bodyStart - 1; i++)
        {
            string line = lines[i];

            if (line.Trim().Length == 0)
            {
                if (currentKey is not null)
                    result.Blocks[currentKey].Add(line);
                continue;
            }

            // 顶层键：第 0 列不是空白，且不是列表项
            bool isTopLevel = line[0] != ' ' && line[0] != '\t'
                              && !line.TrimStart().StartsWith("-", StringComparison.Ordinal);

            if (isTopLevel)
            {
                int colon = line.IndexOf(':');
                if (colon > 0)
                {
                    string key = line[..colon].Trim();
                    currentKey = key;

                    if (!result.Blocks.ContainsKey(key))
                    {
                        result.Keys.Add(key);
                        result.Blocks[key] = new List<string>();
                    }

                    result.Blocks[key].Add(line);
                    continue;
                }
            }

            // 子行（缩进 / 列表项 / 续行）归入当前键
            if (currentKey is not null)
                result.Blocks[currentKey].Add(line);
        }

        result.Body = string.Join("\n", lines.Skip(bodyStart));
        return result;
    }

    // ==================================================================
    // 取值
    // ==================================================================

    /// <summary>取标量值；键不存在或为空返回空字符串。</summary>
    public static string GetScalar(FrontMatterBlock fm, string key)
    {
        if (fm is null || !fm.Blocks.TryGetValue(key, out var block) || block.Count == 0)
            return string.Empty;

        string first = block[0];
        int colon = first.IndexOf(':');
        if (colon < 0)
            return string.Empty;

        string value = first[(colon + 1)..].Trim();

        // 多行文本块的标记符，不是真实值
        if (value is "|" or ">" or "|-" or ">-" or "|+" or ">+")
            return string.Empty;

        return YamlMiniParser.Unquote(value);
    }

    /// <summary>取列表值，兼容多行列表与行内 [a, b] 两种写法。</summary>
    public static List<string> GetList(FrontMatterBlock fm, string key)
    {
        var list = new List<string>();

        if (fm is null || !fm.Blocks.TryGetValue(key, out var block) || block.Count == 0)
            return list;

        string first = block[0];
        int colon = first.IndexOf(':');
        string inline = colon >= 0 ? first[(colon + 1)..].Trim() : string.Empty;

        // 行内写法：tags: [a, b]
        if (inline.StartsWith('[') && inline.EndsWith(']'))
        {
            string inner = inline[1..^1];
            foreach (string part in inner.Split(','))
            {
                string item = YamlMiniParser.Unquote(part.Trim());
                if (item.Length > 0)
                    list.Add(item);
            }
            return list;
        }

        // 多行写法
        for (int i = 1; i < block.Count; i++)
        {
            string line = block[i].Trim();
            if (!line.StartsWith('-'))
                continue;

            string item = YamlMiniParser.Unquote(line.TrimStart('-').Trim());
            if (item.Length > 0)
                list.Add(item);
        }

        // 兜底：单值标量写法，例如 categories: 未分类、tags: 教程
        // Hexo 完全支持这种写法，必须当成「只有一个元素的列表」处理
        if (list.Count == 0 && inline.Length > 0)
        {
            string single = YamlMiniParser.Unquote(inline);
            if (single.Length > 0 && single != "[]")
                list.Add(single);
        }

        return list;
    }

    /// <summary>取布尔值，兼容 true/false/yes/no/1/0。</summary>
    public static bool GetBool(FrontMatterBlock fm, string key, bool defaultValue)
    {
        string raw = GetScalar(fm, key).Trim().ToLowerInvariant();
        return raw switch
        {
            "" => defaultValue,
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => defaultValue
        };
    }

    /// <summary>
    /// 取置顶状态。Butterfly 的 top 通常是数字（越大越靠前），也接受 true/false。
    /// 只要是正数或真值就算置顶。
    /// </summary>
    public static bool GetTop(FrontMatterBlock fm)
    {
        string raw = GetScalar(fm, "top").Trim();
        if (raw.Length == 0)
            return false;

        if (double.TryParse(raw, out double number))
            return number > 0;

        return raw.Equals("true", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    // ==================================================================
    // 重建
    // ==================================================================

    /// <summary>
    /// 根据当前元信息重新生成完整 Markdown 文本（front-matter + 正文）。
    /// 未被管理的字段按原始行原样回写，保持用户原有写法与顺序。
    /// </summary>
    public static string Build(PostMeta meta, FrontMatterBlock existing)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) 先按原顺序输出：托管字段用新值，其他字段原样
        foreach (string key in existing.Keys)
        {
            if (ManagedSet.Contains(key))
            {
                AppendManaged(sb, key, meta);
                written.Add(key);
            }
            else if (existing.Blocks.TryGetValue(key, out var block))
            {
                foreach (string line in block)
                    sb.Append(line).Append('\n');
            }
        }

        // 2) 补齐原来没有的托管字段，顺序固定
        foreach (string key in ManagedKeys)
        {
            if (!written.Contains(key))
                AppendManaged(sb, key, meta);
        }

        sb.Append("---\n");

        // 正文：保留用户原有开头换行习惯
        string body = meta.Body ?? string.Empty;
        sb.Append(body);

        return sb.ToString();
    }

    private static void AppendManaged(StringBuilder sb, string key, PostMeta meta)
    {
        switch (key.ToLowerInvariant())
        {
            case "title":
                sb.Append("title: ").Append(EscapeScalar(meta.Title)).Append('\n');
                break;

            case "date":
                // 用户原来写的日期格式能解析就沿用原文本，否则用标准格式
                sb.Append("date: ")
                  .Append(meta.Date.HasValue
                      ? PostMeta.FormatDate(meta.Date.Value)
                      : EscapeScalar(meta.DateText))
                  .Append('\n');
                break;

            case "updated":
                if (meta.Updated.HasValue)
                {
                    sb.Append("updated: ").Append(PostMeta.FormatDate(meta.Updated.Value)).Append('\n');
                }
                else if (!string.IsNullOrWhiteSpace(meta.DateText))
                {
                    sb.Append("updated: ").Append(PostMeta.FormatDate(meta.Date ?? DateTime.Now)).Append('\n');
                }
                break;

            case "tags":
                AppendList(sb, "tags", meta.Tags);
                break;

            case "categories":
                AppendList(sb, "categories", meta.Categories);
                break;

            case "top":
                sb.Append("top: ").Append(meta.Top ? "true" : "false").Append('\n');
                break;

            case "comments":
                sb.Append("comments: ").Append(meta.Comments ? "true" : "false").Append('\n');
                break;
        }
    }

    private static void AppendList(StringBuilder sb, string key, List<string> values)
    {
        var cleaned = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();

        if (cleaned.Count == 0)
        {
            sb.Append(key).Append(": []\n");
            return;
        }

        sb.Append(key).Append(":\n");
        foreach (string value in cleaned)
            sb.Append("  - ").Append(EscapeScalar(value)).Append('\n');
    }

    /// <summary>
    /// YAML 标量转义：只有当值可能被误解析时才加双引号。
    /// 需要加引号的情况：空值、首尾空格、包含 ": "、以特殊符号开头、包含 # 等。
    /// </summary>
    public static string EscapeScalar(string? value)
    {
        value ??= string.Empty;

        if (value.Length == 0)
            return "\"\"";

        bool needQuote =
            value != value.Trim()
            || value.Contains(": ", StringComparison.Ordinal)
            || value.EndsWith(':')
            || value.Contains(" #", StringComparison.Ordinal)
            || value.StartsWith('#')
            || value.StartsWith('&') || value.StartsWith('*')
            || value.StartsWith('!') || value.StartsWith('|')
            || value.StartsWith('>') || value.StartsWith('%')
            || value.StartsWith('@') || value.StartsWith('`')
            || value is "true" or "false" or "null" or "yes" or "no" or "~"
            || double.TryParse(value, out _);

        if (!needQuote)
            return value;

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
