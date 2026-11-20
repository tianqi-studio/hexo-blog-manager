using System.IO;

namespace HexoBlogManager.Services;

/// <summary>
/// 单个外部工具的探测结果。
/// </summary>
/// <param name="Kind">工具名称，用于界面展示（Node.js / hexo-cli）。</param>
/// <param name="Available">是否可用。</param>
/// <param name="Version">版本号字符串，探测失败时为空。</param>
/// <param name="Error">失败时的中文原因与排查建议。</param>
public sealed record ToolStatus(string Kind, bool Available, string Version, string Error);

/// <summary>
/// 运行环境自检。
///
/// 为什么要在模块一就做？
/// 「Node 没装 / hexo 不在 PATH 里」是最常见的失败原因。与其等用户点「生成」时才报错，
/// 不如在选择目录时就把环境状态摆在界面上，让用户第一时间发现问题。
///
/// 探测手段：直接执行命令看退出码，不去读注册表、不依赖任何第三方库。
/// </summary>
public sealed class EnvironmentProbe
{
    private const string NodeMissingHint =
        "没有检测到 Node.js。\n" +
        "请到 https://nodejs.org 下载 LTS 版本安装，安装时勾选「Add to PATH」，然后重启本工具。";

    private const string HexoMissingHint =
        "没有检测到 hexo 命令。\n" +
        "请打开命令提示符执行：npm install -g hexo-cli\n" +
        "安装完成后重启本工具（PATH 需要刷新才会生效）。";

    /// <summary>探测 Node.js 是否可用。</summary>
    public async Task<ToolStatus> ProbeNodeAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("node -v", null, TimeSpan.FromSeconds(15), ct)
                                            .ConfigureAwait(false);

            if (result.Success)
            {
                string version = FirstMeaningfulLine(result.StdOut);
                if (!string.IsNullOrWhiteSpace(version))
                    return new ToolStatus("Node.js", true, version, string.Empty);
            }

            return new ToolStatus("Node.js", false, string.Empty, NodeMissingHint);
        }
        catch (Exception ex)
        {
            return new ToolStatus("Node.js", false, string.Empty, $"探测 Node.js 时出错：{ex.Message}");
        }
    }

    /// <summary>
    /// 探测 hexo-cli 是否可用。
    /// 注意：hexo 依赖 node，node 不可用时这里也一定失败，界面要先显示 node 的问题。
    /// </summary>
    public async Task<ToolStatus> ProbeHexoAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("hexo version", null, TimeSpan.FromSeconds(40), ct)
                                            .ConfigureAwait(false);

            if (result.Success)
            {
                // hexo version 的输出形如：
                // hexo-cli: 4.3.2
                // os: win32 10.0.22631 undefined
                // node: 22.22.2
                string version = ExtractHexoCliVersion(result.StdOut);
                return new ToolStatus("hexo-cli", true,
                    string.IsNullOrWhiteSpace(version) ? "已安装" : version, string.Empty);
            }

            return new ToolStatus("hexo-cli", false, string.Empty, HexoMissingHint);
        }
        catch (Exception ex)
        {
            return new ToolStatus("hexo-cli", false, string.Empty, $"探测 hexo 时出错：{ex.Message}");
        }
    }

    /// <summary>
    /// 在指定博客目录下探测 hexo 能否正常执行。
    /// 这一步同时验证了两件事：
    /// 1) 该目录确实是一个可用的 Hexo 项目（有 node_modules 或全局 hexo 能识别）
    /// 2) 中文路径 / 带空格路径下命令行调用没有出问题
    /// </summary>
    public async Task<ToolStatus> ProbeHexoInBlogAsync(string blogRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blogRoot) || !Directory.Exists(blogRoot))
            return new ToolStatus("博客命令", false, string.Empty, "博客目录不存在，无法测试。");

        try
        {
            var result = await ProcessRunner.RunAsync("hexo version", blogRoot, TimeSpan.FromSeconds(40), ct)
                                            .ConfigureAwait(false);

            if (result.Success)
            {
                // 博客目录下的输出一般第一行是核心版本（hexo: 8.1.2），
                // 用 "hexo 8.1.2" 的形式展示，避免和 hexo-cli 的版本混淆。
                string version = ExtractHexoCoreVersion(result.StdOut);
                return new ToolStatus("博客命令", true,
                    string.IsNullOrWhiteSpace(version) ? "可正常执行" : version, string.Empty);
            }

            string detail = result.CombinedOutput;
            string hint = "在该博客目录下执行 hexo 命令失败。\n\n" +
                          "常见原因：\n" +
                          "· 博客还没有安装依赖：在博客目录执行 npm install\n" +
                          "· 该目录不是 Hexo 项目（缺少 package.json / node_modules）\n" +
                          "· 路径含特殊字符导致命令行解析异常\n\n" +
                          (string.IsNullOrWhiteSpace(detail) ? "" : "命令输出：\n" + detail);

            return new ToolStatus("博客命令", false, string.Empty, hint);
        }
        catch (Exception ex)
        {
            return new ToolStatus("博客命令", false, string.Empty, $"测试失败：{ex.Message}");
        }
    }

    /// <summary>从 hexo version 的输出里提取 "hexo: x.y.z"（博客本地安装的 hexo 核心版本）。</summary>
    private static string ExtractHexoCoreVersion(string output)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            // 注意不能匹配 "hexo-cli:"，所以判断冒号位置
            if (line.StartsWith("hexo:", StringComparison.OrdinalIgnoreCase))
                return "hexo " + line["hexo:".Length..].Trim();
        }
        return "可正常执行";
    }

    /// <summary>从 hexo version 的输出里提取 "hexo-cli: x.y.z" 这一行。</summary>
    private static string ExtractHexoCliVersion(string output)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("hexo-cli", StringComparison.OrdinalIgnoreCase))
                return line;
            if (line.StartsWith("hexo:", StringComparison.OrdinalIgnoreCase))
                return line;
        }
        return FirstMeaningfulLine(output);
    }

    /// <summary>取第一行非空文本（用于 node -v 这种单行输出）。</summary>
    private static string FirstMeaningfulLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        foreach (var rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length > 0)
                return line;
        }
        return string.Empty;
    }
}
