namespace HexoBlogManager.Services;

/// <summary>
/// 极简 YAML 顶层键值解析器。
///
/// 为什么不用 YamlDotNet？
/// 需求明确要求「不引入复杂、重型依赖」，而我们现阶段只需要从 _config.yml 里
/// 读取 title / subtitle / author / theme 这几个**顶层标量**字段。
/// 完整 YAML 解析器（含锚点、多行文本、嵌套映射）留到模块四做配置管理时再按需增强。
///
/// 支持范围：
/// · 顶层 key: value       → 能解析
/// · key: "带引号的值"     → 能解析并去引号
/// · 缩进的子键（导航菜单等）→ 跳过，不参与顶层字段
/// · # 注释（整行 / 行尾） → 能剔除
/// · 列表项（- xxx）       → 跳过
///
/// 不支持：多行文本块（| 和 >）、锚点引用、复杂嵌套。这些在模块四会补。
/// </summary>
public static class YamlMiniParser
{
    /// <summary>解析出所有顶层键值对，键名忽略大小写。</summary>
    public static Dictionary<string, string> ParseTopLevel(string? content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(content))
            return result;

        foreach (var rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            // 顶层键必须从第 0 列开始；缩进行属于子级，跳过
            if (line[0] == ' ' || line[0] == '\t')
                continue;

            string trimmed = line.TrimStart();
            if (trimmed.StartsWith('#') || trimmed.StartsWith('-') || trimmed.StartsWith("---"))
                continue;

            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            string key = line[..colon].Trim();
            if (key.Length == 0)
                continue;

            string value = line[(colon + 1)..].Trim();
            value = StripInlineComment(value);
            value = Unquote(value);

            // 同名键只取第一次出现的（YAML 规范里重复键是错误，这里宽松处理）
            if (!result.ContainsKey(key))
                result[key] = value;
        }

        return result;
    }

    /// <summary>读取单个顶层字段，取不到时返回空字符串。</summary>
    public static string GetValue(Dictionary<string, string> map, string key)
        => map.TryGetValue(key, out var v) ? v : string.Empty;

    /// <summary>
    /// 剔除行尾注释。只有当 # 位于行首或前面是空白时才算注释，
    /// 这样 "title: My #1 Blog" 里的 #1 不会被误删。
    /// </summary>
    public static string StripInlineComment(string value)
    {
        if (value.Length == 0)
            return value;

        // 被引号包裹的值，整体保留（引号内的 # 不是注释）
        if (value[0] == '"' || value[0] == '\'')
            return value;

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '#' && (i == 0 || char.IsWhiteSpace(value[i - 1])))
                return value[..i].TrimEnd();
        }
        return value;
    }

    /// <summary>去掉成对的首尾引号（单引号或双引号），并处理常见的转义。</summary>
    public static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            char first = value[0];
            char last = value[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                string inner = value[1..^1];
                if (first == '"')
                    inner = inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
                else
                    inner = inner.Replace("''", "'");
                return inner;
            }
        }
        return value;
    }
}
