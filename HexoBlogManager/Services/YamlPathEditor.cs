using System.IO;
using HexoBlogManager.Models;

namespace HexoBlogManager.Services;

/// <summary>
/// YAML 配置文件的「按路径读写」工具。
///
/// 为什么不用完整的 YAML 库？
/// 因为我们要做的是「只改用户点过的那几项，其余内容和注释原样保留」。
/// 完整解析再序列化会把注释、字段顺序、缩进风格全部重写，对配置文件来说是破坏性的。
/// 这里采用行级编辑：定位到目标键所在行，只替换值，其他行一个字节都不动。
///
/// 支持的路径写法：用点分隔，例如
///   "title"                     → 顶层键
///   "nav.display_title"         → 二级
///   "footer.copyright.enable"   → 三级
/// </summary>
public static class YamlPathEditor
{
    // ==================================================================
    // 读取
    // ==================================================================

    /// <summary>该路径在文件中是否存在。</summary>
    public static bool Exists(string content, string path)
    {
        var lines = SplitLines(content);
        return FindPathLine(lines, path.Split('.')) >= 0;
    }

    /// <summary>读取标量值；不存在返回空字符串。</summary>
    public static string GetScalar(string content, string path)
    {
        var lines = SplitLines(content);
        int index = FindPathLine(lines, path.Split('.'));
        if (index < 0)
            return string.Empty;

        string line = lines[index];
        int colon = line.IndexOf(':');
        if (colon < 0)
            return string.Empty;

        return YamlMiniParser.Unquote(line[(colon + 1)..].Trim());
    }

    /// <summary>读取布尔值，兼容 true/false/yes/no/1/0。</summary>
    public static bool GetBool(string content, string path, bool defaultValue = false)
    {
        string raw = GetScalar(content, path).Trim().ToLowerInvariant();

        return raw switch
        {
            "" => defaultValue,
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => defaultValue
        };
    }

    /// <summary>读取一个映射块（例如 Butterfly 的 menu），返回子键值对列表。</summary>
    public static List<(string Key, string Value)> GetMapping(string content, string path)
    {
        var result = new List<(string, string)>();
        var lines = SplitLines(content);

        int index = FindPathLine(lines, path.Split('.'));
        if (index < 0)
            return result;

        int indent = GetIndent(lines[index]);
        int end = FindBlockEnd(lines, index, indent);

        int childIndent = -1;

        for (int i = index + 1; i < end; i++)
        {
            if (IsIgnorable(lines[i]))
                continue;

            int ind = GetIndent(lines[i]);
            if (childIndent < 0)
                childIndent = ind;

            // 只取直接子级，跳过更深层的孙节点
            if (ind != childIndent)
                continue;

            string trimmed = lines[i].Trim();
            int colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;

            string key = YamlMiniParser.Unquote(trimmed[..colon].Trim());
            string value = YamlMiniParser.Unquote(trimmed[(colon + 1)..].Trim());

            if (key.Length > 0)
                result.Add((key, value));
        }

        return result;
    }

    // ==================================================================
    // 写入（返回新内容，不落盘）
    // ==================================================================

    /// <summary>设置一个标量值。键已存在则替换该行，父级不存在会自动创建。</summary>
    public static string SetScalar(string content, string path, string value)
    {
        var lines = SplitLines(content);
        string[] keys = path.Split('.');
        string escaped = string.IsNullOrEmpty(value) ? string.Empty : FrontMatter.EscapeScalar(value);

        int index = FindPathLine(lines, keys);

        if (index >= 0)
        {
            // 已存在：保留原来的缩进和键名，只替换冒号后面的部分
            string line = lines[index];
            int colon = line.IndexOf(':');
            if (colon < 0)
                return content;

            lines[index] = escaped.Length == 0
                ? line[..(colon + 1)]
                : line[..(colon + 1)] + " " + escaped;
        }
        else if (keys.Length == 1)
        {
            // 顶层键不存在：追加到文件末尾
            lines.Add(escaped.Length == 0 ? $"{keys[0]}:" : $"{keys[0]}: {escaped}");
        }
        else
        {
            // 子键不存在：先确保父级路径存在（giscus 段可能整段都没有），再插到父块末尾
            int parentIndex = EnsurePath(lines, keys[..^1]);
            int parentIndent = GetIndent(lines[parentIndex]);
            int blockEnd = FindBlockEnd(lines, parentIndex, parentIndent);
            string childIndent = new(' ', parentIndent + 2);

            lines.Insert(blockEnd, escaped.Length == 0
                ? $"{childIndent}{keys[^1]}:"
                : $"{childIndent}{keys[^1]}: {escaped}");
        }

        return JoinLines(lines);
    }

    /// <summary>
    /// 确保一条路径存在，返回最后一级键所在行号；缺失的层级会自动补出来。
    /// 只用于写入场景（读取时不应该创建任何东西）。
    /// </summary>
    private static int EnsurePath(List<string> lines, string[] keys)
    {
        int index = FindPathLine(lines, keys);
        if (index >= 0)
            return index;

        if (keys.Length == 1)
        {
            lines.Add($"{keys[0]}:");
            return lines.Count - 1;
        }

        // 先保证父级存在，再把当前层级插到父块末尾
        int parentIndex = EnsurePath(lines, keys[..^1]);
        int parentIndent = GetIndent(lines[parentIndex]);
        int blockEnd = FindBlockEnd(lines, parentIndex, parentIndent);
        string childIndent = new(' ', parentIndent + 2);

        lines.Insert(blockEnd, $"{childIndent}{keys[^1]}:");
        return blockEnd;
    }

    /// <summary>
    /// 整体替换一个映射块（例如导航菜单）。
    /// 注意：块内的原有注释会被覆盖，这是菜单可视化的必然代价。
    /// </summary>
    public static string SetMapping(string content, string path, IReadOnlyList<(string Key, string Value)> entries)
    {
        var lines = SplitLines(content);
        string[] keys = path.Split('.');
        int index = FindPathLine(lines, keys);

        int indent;
        int blockEnd;

        if (index >= 0)
        {
            indent = GetIndent(lines[index]);
            blockEnd = FindBlockEnd(lines, index, indent);
        }
        else
        {
            // 只在顶层缺失时自动创建
            if (keys.Length > 1)
                return content;

            lines.Add($"{keys[0]}:");
            index = lines.Count - 1;
            indent = 0;
            blockEnd = lines.Count;
        }

        var block = new List<string>();
        string childIndent = new(' ', indent + 2);

        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            block.Add($"{childIndent}{EscapeKey(key)}: {value}");
        }

        // 先删掉旧块内容，再插入新块
        if (blockEnd > index + 1)
            lines.RemoveRange(index + 1, blockEnd - index - 1);

        lines.InsertRange(index + 1, block);

        return JoinLines(lines);
    }

    // ==================================================================
    // 内部工具
    // ==================================================================

    private static List<string> SplitLines(string content)
        => (content ?? string.Empty)
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();

    private static string JoinLines(List<string> lines) => string.Join("\n", lines);

    /// <summary>统计行首空格数（YAML 不允许 tab 缩进，若出现按 2 空格折算）。</summary>
    private static int GetIndent(string line)
    {
        int i = 0;
        int count = 0;

        while (i < line.Length)
        {
            if (line[i] == ' ')
            {
                count++;
                i++;
            }
            else if (line[i] == '\t')
            {
                count += 2;
                i++;
            }
            else
            {
                break;
            }
        }

        return count;
    }

    private static bool IsIgnorable(string line)
    {
        string t = line.Trim();
        return t.Length == 0 || t.StartsWith('#') || t == "---" || t == "...";
    }

    /// <summary>
    /// 按路径逐层定位，返回最后一级键所在的行号；找不到返回 -1。
    /// </summary>
    private static int FindPathLine(List<string> lines, string[] path)
    {
        int searchStart = 0;
        int searchEnd = lines.Count;
        int parentIndent = -1;
        int result = -1;

        foreach (string key in path)
        {
            result = -1;
            int childIndent = -1;

            for (int i = searchStart; i < searchEnd; i++)
            {
                string line = lines[i];

                if (IsIgnorable(line))
                    continue;

                int indent = GetIndent(line);

                // 缩进回到父级或更浅，说明已经出了当前块
                if (indent <= parentIndent)
                    break;

                if (childIndent < 0)
                    childIndent = indent;

                // 只匹配直接子级
                if (indent != childIndent)
                    continue;

                if (!KeyMatches(line, key))
                    continue;

                result = i;
                searchStart = i + 1;
                searchEnd = FindBlockEnd(lines, i, indent);
                parentIndent = indent;
                break;
            }

            if (result < 0)
                return -1;
        }

        return result;
    }

    /// <summary>该行的键名是否等于 key（支持带引号的键）。</summary>
    private static bool KeyMatches(string line, string key)
    {
        int colon = line.IndexOf(':');
        if (colon <= 0)
            return false;

        string rawKey = YamlMiniParser.Unquote(line[..colon].Trim());
        return rawKey.Equals(key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>找到某个键对应块的结束行（下一个缩进 <= 该键的行）。</summary>
    private static int FindBlockEnd(List<string> lines, int keyLine, int keyIndent)
    {
        for (int i = keyLine + 1; i < lines.Count; i++)
        {
            if (IsIgnorable(lines[i]))
                continue;

            if (GetIndent(lines[i]) <= keyIndent)
                return i;
        }

        return lines.Count;
    }

    /// <summary>YAML 键名转义：含冒号或井号时加引号。</summary>
    private static string EscapeKey(string key)
    {
        if (key.Contains(':') || key.Contains('#') || key.StartsWith('-') || key.StartsWith('?')
            || key.StartsWith('[') || key.StartsWith('{'))
            return "\"" + key.Replace("\"", "\\\"") + "\"";

        return key;
    }
}
