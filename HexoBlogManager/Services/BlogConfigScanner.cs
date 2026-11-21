using System.IO;
using System.Text;

namespace HexoBlogManager.Services;

/// <summary>配置文件在识别结果里的角色。</summary>
public enum ConfigRole
{
    /// <summary>站点主配置 _config.yml。</summary>
    Main,
    /// <summary>环境覆盖配置 _config.production.yml 之类。</summary>
    Alternate,
    /// <summary>主题配置（含主题自带的默认配置）。</summary>
    Theme
}

/// <summary>某个字段的取值状态，决定界面上怎么提示用户。</summary>
public enum ConfigFieldState
{
    /// <summary>在要写入的那个文件里读到了。</summary>
    Found,
    /// <summary>要写入的文件里没有，用的是主题自带的默认值（保存时不会主动写进去）。</summary>
    Inherited,
    /// <summary>读到了，但会被另一个文件覆盖，改这里不生效。</summary>
    Overridden,
    /// <summary>哪个文件都没有，使用默认值。</summary>
    Missing,
    /// <summary>文件读不出来（权限/编码/严重格式问题），值不可信。</summary>
    Invalid
}

/// <summary>一份候选配置文件的读取结果。</summary>
public sealed class ConfigFileSnapshot
{
    public string Path { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public ConfigRole Role { get; init; }

    /// <summary>给人看的角色说明，例如「站点主配置」。</summary>
    public string RoleText { get; init; } = string.Empty;

    /// <summary>文件是否存在。</summary>
    public bool Exists { get; set; }

    /// <summary>是否成功读入并解析。</summary>
    public bool Parsed { get; set; }

    /// <summary>是否是「保存时会写入」的那个文件。</summary>
    public bool IsWriteTarget { get; set; }

    /// <summary>解析出来的条目数。</summary>
    public int EntryCount { get; set; }

    /// <summary>文件原文（用于行级保存）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>读取失败的原因。</summary>
    public string? Error { get; set; }

    /// <summary>编码识别说明，例如「已按 GB18030 解码」。</summary>
    public string? EncodingNote { get; set; }

    /// <summary>解析过程中的可疑行提示。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>解析结果。</summary>
    public YamlFlatResult? Result { get; set; }
}

/// <summary>一个字段的解析结果。</summary>
public sealed class ConfigFieldValue
{
    public string Text { get; init; } = string.Empty;

    public ConfigFieldState State { get; init; } = ConfigFieldState.Missing;

    /// <summary>值来自哪个文件。</summary>
    public string SourceFile { get; init; } = string.Empty;

    /// <summary>值在文件里的行号（列表或继承值可能为 0）。</summary>
    public int SourceLine { get; init; }

    /// <summary>补充说明，直接展示给用户。</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>是否有可信的值可用；false 时表单应该用默认值。</summary>
    public bool HasValue => State is ConfigFieldState.Found
        or ConfigFieldState.Inherited
        or ConfigFieldState.Overridden;

    public static ConfigFieldValue Missing(string note = "") => new()
    {
        State = ConfigFieldState.Missing,
        Note = note
    };

    public static ConfigFieldValue Invalid(string note) => new()
    {
        State = ConfigFieldState.Invalid,
        Note = note
    };
}

/// <summary>
/// 一次「配置识别」的完整结果：找到了哪些文件、每个字段的值与来源、以及所有警告。
/// 界面只读这个对象，不再自己解析 YAML。
/// </summary>
public sealed class BlogConfigSnapshot
{
    public string BlogRoot { get; init; } = string.Empty;

    public DateTime ScannedAt { get; init; } = DateTime.Now;

    /// <summary>主题名（来自 _config.yml 的 theme 字段）。</summary>
    public string ThemeName { get; set; } = string.Empty;

    /// <summary>所有候选文件（含不存在的，便于在报告里说明「为什么没读到」）。</summary>
    public List<ConfigFileSnapshot> Files { get; } = new();

    /// <summary>站点配置的写入目标（一般是 _config.yml）。</summary>
    public string? MainWritePath { get; set; }

    /// <summary>主题配置的写入目标。</summary>
    public string? ThemeWritePath { get; set; }

    /// <summary>主配置写入目标的原文。</summary>
    public string MainContent { get; set; } = string.Empty;

    /// <summary>主题配置写入目标的原文。</summary>
    public string ThemeContent { get; set; } = string.Empty;

    public bool MainExists { get; set; }

    public bool ThemeExists => ThemeWritePath is not null;

    /// <summary>整个识别过程失败的原因（目录不存在等）。</summary>
    public string? Error { get; set; }

    /// <summary>整体性警告（文件级问题另在 Files 里）。</summary>
    public List<string> Warnings { get; } = new();

    internal Dictionary<string, SourceRef> MainOrigins { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, SourceRef> ThemeOrigins { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal bool MainUnreadable { get; set; }

    internal bool ThemeUnreadable { get; set; }

    /// <summary>解析站点配置里的一个字段。</summary>
    public ConfigFieldValue ResolveMain(string path)
    {
        if (MainUnreadable)
            return ConfigFieldValue.Invalid("主配置文件读不出来，字段值不可信，已使用默认值");

        if (MainOrigins.TryGetValue(path, out var source))
            return source.ToFieldValue(isTheme: false);

        return ConfigFieldValue.Missing();
    }

    /// <summary>解析主题配置里的一个字段。</summary>
    public ConfigFieldValue ResolveTheme(string path)
    {
        if (ThemeUnreadable)
            return ConfigFieldValue.Invalid("主题配置文件读不出来，字段值不可信，已使用默认值");

        if (ThemeOrigins.TryGetValue(path, out var source))
            return source.ToFieldValue(isTheme: true);

        return ConfigFieldValue.Missing();
    }

    /// <summary>
    /// 取导航菜单项。这里只认写入目标文件里的内容：
    /// 主题自带的默认菜单有二十多项，如果继承过来，用户一保存就会把整份默认菜单抄进自己的配置里。
    /// </summary>
    public List<(string Key, string Value)> GetMenu()
    {
        var target = Files.FirstOrDefault(f =>
            f.IsWriteTarget && f.Role == ConfigRole.Theme && f.Parsed && f.Result is not null);

        return target?.Result?.GetChildren("menu") ?? new List<(string, string)>();
    }

    /// <summary>写入目标里是否已经有这个键（保存时用来决定「要不要新建」）。</summary>
    public bool MainHasKey(string path)
        => Files.Any(f => f.IsWriteTarget && f.Role == ConfigRole.Main && f.Parsed && f.Result!.TryGetEntry(path, out _));

    public bool ThemeHasKey(string path)
        => Files.Any(f => f.IsWriteTarget && f.Role == ConfigRole.Theme && f.Parsed && f.Result!.TryGetEntry(path, out _));

    /// <summary>值 + 来源文件 + 状态。</summary>
    internal sealed class SourceRef
    {
        public required ConfigFileSnapshot File { get; init; }
        public required string Text { get; init; }
        public required int Line { get; init; }

        public ConfigFieldValue ToFieldValue(bool isTheme)
        {
            if (File.IsWriteTarget)
            {
                return new ConfigFieldValue
                {
                    Text = Text,
                    State = ConfigFieldState.Found,
                    SourceFile = File.Path,
                    SourceLine = Line
                };
            }

            // 主配置：值来自环境覆盖文件 → 改主配置不会生效
            if (!isTheme)
            {
                return new ConfigFieldValue
                {
                    Text = Text,
                    State = ConfigFieldState.Overridden,
                    SourceFile = File.Path,
                    SourceLine = Line,
                    Note = "环境覆盖配置的优先级更高，改 _config.yml 不会生效"
                };
            }

            // 主题配置：值来自主题自带的默认配置 → 属于继承
            return new ConfigFieldValue
            {
                Text = Text,
                State = ConfigFieldState.Inherited,
                SourceFile = File.Path,
                SourceLine = Line,
                Note = "继承自主题默认配置，保存时不会主动写入你的文件"
            };
        }
    }
}

/// <summary>
/// 配置自动识别器。
///
/// 做的事：
/// 1) 找出博客里所有可能影响当前站点配置的文件（主配置、环境覆盖配置、主题三处候选）；
/// 2) 按 Hexo 的真实优先级合并出一个「有效配置」；
/// 3) 记录每个字段的值、来源文件、行号和状态，供界面填表与解释。
///
/// Hexo 的真实规则（已对照 hexo/dist/hexo/index.js 与 load_theme_config.js 确认）：
/// · 环境覆盖配置 _config.[env].yml 会覆盖 _config.yml；
/// · 主题配置是 deepMerge(主题自带默认配置, 根目录 _config.[theme].yml)，
///   也就是说根目录那份是「覆盖层」，没写的键继续沿用主题默认值。
///
/// 整个过程不抛异常：任何一步出错都降级成「缺这个文件/这个字段」，
/// 由调用方用默认值兜底，绝不能让配置页因为读文件失败而打不开。
/// </summary>
public static class BlogConfigScanner
{
    /// <summary>扫描一个博客目录。</summary>
    public static BlogConfigSnapshot Scan(string blogRoot)
    {
        var snapshot = new BlogConfigSnapshot { BlogRoot = blogRoot ?? string.Empty };

        try
        {
            if (string.IsNullOrWhiteSpace(blogRoot) || !Directory.Exists(blogRoot))
            {
                snapshot.Error = "博客目录不存在：" + blogRoot;
                return snapshot;
            }

            // ---------- 1. 主配置 ----------
            var mainFile = LoadFirstExisting(blogRoot,
                new[] { "_config.yml", "_config.yaml" },
                ConfigRole.Main, "站点主配置");

            if (mainFile is null)
            {
                snapshot.Warnings.Add("没有找到 _config.yml，站点基础信息无法识别。");
            }
            else
            {
                mainFile.IsWriteTarget = true;
                snapshot.Files.Add(mainFile);
                snapshot.MainWritePath = mainFile.Exists ? mainFile.Path : null;
                snapshot.MainContent = mainFile.Content;
                snapshot.MainExists = mainFile.Exists;
                snapshot.MainUnreadable = mainFile.Error is not null;

                snapshot.ThemeName = mainFile.Result?.GetText("theme").Trim() ?? string.Empty;
            }

            // ---------- 2. 环境覆盖配置 ----------
            foreach (var alternate in FindAlternateConfigs(blogRoot, snapshot.ThemeName))
                snapshot.Files.Add(alternate);

            // ---------- 3. 主题配置 ----------
            if (snapshot.ThemeName.Length > 0)
            {
                var themeCandidates = FindThemeConfigs(blogRoot, snapshot.ThemeName);

                bool writeTargetAssigned = false;

                foreach (var candidate in themeCandidates)
                {
                    if (candidate.Exists && !writeTargetAssigned)
                    {
                        candidate.IsWriteTarget = true;
                        writeTargetAssigned = true;

                        snapshot.ThemeWritePath = candidate.Path;
                        snapshot.ThemeContent = candidate.Content;
                        snapshot.ThemeUnreadable = candidate.Error is not null;
                    }

                    snapshot.Files.Add(candidate);
                }

                if (!writeTargetAssigned)
                    snapshot.Warnings.Add($"主题 {snapshot.ThemeName} 的配置文件一个都没找到，主题配置将无法编辑。");
            }
            else
            {
                snapshot.Warnings.Add("_config.yml 里没有 theme 字段（或读不到），无法定位主题配置。");
            }

            // ---------- 4. 合并 ----------
            // 主配置：先 _config.yml，再各环境覆盖配置，后者覆盖前者
            BuildOrigins(snapshot.Files, ConfigRole.Main, snapshot.MainOrigins, reverse: false);
            BuildOrigins(snapshot.Files, ConfigRole.Alternate, snapshot.MainOrigins, reverse: false);

            // 主题配置：加入顺序是「根目录覆盖层 → themes 目录 → node_modules」，
            // 优先级正好相反（根目录最高），所以合并时要倒着来，让根目录的值最后写入。
            BuildOrigins(snapshot.Files, ConfigRole.Theme, snapshot.ThemeOrigins, reverse: true);

            return snapshot;
        }
        catch (Exception ex)
        {
            // 兜底：识别失败也返回一个可用的对象，界面据此提示并回退到默认值
            snapshot.Error = ex.Message;
            snapshot.Warnings.Add("配置识别过程中出现异常，已回退到默认值：" + ex.Message);
            return snapshot;
        }
    }

    // ==================================================================
    // 文件发现
    // ==================================================================

    private static ConfigFileSnapshot? LoadFirstExisting(
        string blogRoot, string[] fileNames, ConfigRole role, string roleText)
    {
        foreach (string name in fileNames)
        {
            string path = Path.Combine(blogRoot, name);

            if (File.Exists(path))
                return LoadFile(path, role, roleText);

            // 主配置不存在也要留一条记录，报告里才能说明「为什么读不到」
            if (role == ConfigRole.Main && name == fileNames[^1])
                return EmptySnapshot(path, role, roleText);
        }

        return EmptySnapshot(Path.Combine(blogRoot, fileNames[0]), role, roleText);
    }

    private static ConfigFileSnapshot EmptySnapshot(string path, ConfigRole role, string roleText) => new()
    {
        Path = path,
        FileName = Path.GetFileName(path),
        Role = role,
        RoleText = roleText,
        Exists = false,
        Parsed = false
    };

    /// <summary>
    /// 找出 _config.[env].yml 这类环境覆盖配置。
    /// 要排除主题覆盖配置（例如 _config.butterfly.yml），它的名字也符合这个模式。
    /// 判断依据很朴素：后缀正好是一个已安装的主题名，就当作主题配置。
    /// </summary>
    private static List<ConfigFileSnapshot> FindAlternateConfigs(string blogRoot, string themeName)
    {
        var result = new List<ConfigFileSnapshot>();

        try
        {
            foreach (string path in Directory.GetFiles(blogRoot, "_config.*.yml")
                         .Concat(Directory.GetFiles(blogRoot, "_config.*.yaml"))
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string suffix = GetConfigSuffix(path);

                if (suffix.Length == 0)
                    continue;

                // 主题配置，跳过
                if (themeName.Length > 0 && suffix.Equals(themeName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (LooksLikeThemeDirectory(blogRoot, suffix))
                    continue;

                result.Add(LoadFile(path, ConfigRole.Alternate, "环境覆盖配置"));
            }
        }
        catch
        {
            // 目录不可读就当作没有覆盖配置
        }

        return result;
    }

    /// <summary>主题配置的三个候选位置，按优先级从高到低。</summary>
    private static List<ConfigFileSnapshot> FindThemeConfigs(string blogRoot, string themeName)
    {
        var candidates = new List<ConfigFileSnapshot>();

        string[] paths =
        {
            Path.Combine(blogRoot, $"_config.{themeName}.yml"),
            Path.Combine(blogRoot, $"_config.{themeName}.yaml"),
            Path.Combine(blogRoot, "themes", themeName, "_config.yml"),
            Path.Combine(blogRoot, "themes", themeName, "_config.yaml"),
            Path.Combine(blogRoot, "node_modules", $"hexo-theme-{themeName}", "_config.yml")
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in paths)
        {
            if (!seen.Add(path))
                continue;

            string roleText = path.Contains(@"\node_modules\", StringComparison.OrdinalIgnoreCase)
                ? "主题默认配置（npm 包内）"
                : path.Contains(@"\themes\", StringComparison.OrdinalIgnoreCase)
                    ? "主题默认配置（themes 目录）"
                    : "主题覆盖配置（根目录）";

            candidates.Add(File.Exists(path)
                ? LoadFile(path, ConfigRole.Theme, roleText)
                : EmptySnapshot(path, ConfigRole.Theme, roleText));
        }

        return candidates;
    }

    /// <summary>从 _config.xxx.yml 里取出 xxx。</summary>
    private static string GetConfigSuffix(string path)
    {
        string name = Path.GetFileName(path);
        const string prefix = "_config.";

        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        int dot = name.LastIndexOf('.');

        // 至少要有 _config.xxx.y 这种形式
        return dot <= prefix.Length ? string.Empty : name[prefix.Length..dot];
    }

    /// <summary>这个后缀是否对应一个真实存在的主题目录（themes/x 或 node_modules/hexo-theme-x）。</summary>
    private static bool LooksLikeThemeDirectory(string blogRoot, string name)
    {
        try
        {
            return Directory.Exists(Path.Combine(blogRoot, "themes", name))
                   || Directory.Exists(Path.Combine(blogRoot, "node_modules", "hexo-theme-" + name));
        }
        catch
        {
            return false;
        }
    }

    // ==================================================================
    // 读取与解析
    // ==================================================================

    private static ConfigFileSnapshot LoadFile(string path, ConfigRole role, string roleText)
    {
        var file = new ConfigFileSnapshot
        {
            Path = path,
            FileName = Path.GetFileName(path),
            Role = role,
            RoleText = roleText,
            Exists = true
        };

        try
        {
            var (text, encodingNote, error) = ReadWithEncoding(path);

            file.EncodingNote = encodingNote;

            if (error is not null)
            {
                file.Error = error;
                return file;
            }

            file.Content = text;
            file.Result = YamlFlatReader.Parse(text);
            file.Parsed = true;
            file.EntryCount = file.Result.ValueCount;
            file.Warnings.AddRange(file.Result.Warnings);
        }
        catch (Exception ex)
        {
            file.Error = ex.Message;
        }

        return file;
    }

    /// <summary>
    /// 读取文本并识别编码。
    /// 顺序：BOM 判断 → 严格 UTF-8 校验 → 失败则按 GB18030 解码（国内博客常见 GBK 编码）。
    /// 全部失败也要返回文本，让上层能降级处理，而不是抛异常。
    /// </summary>
    private static (string Text, string? Note, string? Error) ReadWithEncoding(string path)
    {
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (string.Empty, null, "没有读取权限：" + ex.Message);
        }
        catch (IOException ex)
        {
            return (string.Empty, null, "读取失败（文件可能被占用）：" + ex.Message);
        }

        if (bytes.Length == 0)
            return (string.Empty, null, null);

        // ---------- BOM ----------
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Decode(new UTF8Encoding(false), bytes, 3), null, null);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "检测到 UTF-16 LE 编码", null);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "检测到 UTF-16 BE 编码", null);

        // ---------- 严格 UTF-8 ----------
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), null, null);
        }
        catch (DecoderFallbackException)
        {
            // 不是 UTF-8，继续尝试 GB18030
        }

        string? gb = TryDecodeGb18030(bytes);
        if (gb is not null)
            return (gb, "文件不是 UTF-8 编码，已按 GB18030（GBK）解码", null);

        // 彻底解不了：用替换字符兜底，保证界面还能显示个大概
        return (Encoding.UTF8.GetString(bytes), "文件编码无法识别，中文可能显示为乱码", null);
    }

    private static string Decode(Encoding encoding, byte[] bytes, int offset)
        => encoding.GetString(bytes, offset, bytes.Length - offset);

    private static string? TryDecodeGb18030(byte[] bytes)
    {
        try
        {
            // CodePages 提供程序随 .NET 共享框架发布，不需要额外 NuGet 包
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var gb = Encoding.GetEncoding("GB18030");
            return gb.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    // ==================================================================
    // 合并
    // ==================================================================

    /// <summary>
    /// 把某一类文件的值合并进 origins。
    /// 同一个键在多份文件里出现时，后写入的胜出；
    /// reverse 用于优先级与加入顺序相反的情况（主题配置）。
    /// </summary>
    private static void BuildOrigins(
        List<ConfigFileSnapshot> files,
        ConfigRole role,
        Dictionary<string, BlogConfigSnapshot.SourceRef> target,
        bool reverse)
    {
        var matched = files.Where(f => f.Role == role).ToList();

        if (reverse)
            matched.Reverse();

        foreach (var file in matched)
        {
            if (!file.Parsed || file.Result is null)
                continue;

            foreach (var pair in file.Result.Entries)
            {
                var entry = pair.Value;

                if (entry.IsContainer || entry.Value.Length == 0)
                    continue;

                target[entry.Path] = new BlogConfigSnapshot.SourceRef
                {
                    File = file,
                    Text = entry.Value,
                    Line = entry.Line
                };
            }

            foreach (var list in file.Result.Lists)
            {
                if (list.Value.Count == 0)
                    continue;

                target[list.Key] = new BlogConfigSnapshot.SourceRef
                {
                    File = file,
                    Text = string.Join(", ", list.Value),
                    Line = 0
                };
            }
        }
    }
}
