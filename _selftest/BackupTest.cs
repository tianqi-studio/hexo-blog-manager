using System.IO;
using System.Text;
using HexoBlogManager.Services;

namespace SelfTest;

/// <summary>
/// 文章历史备份的自检。
/// 重点不是"有没有生成文件"，而是三件容易出错的事：
/// 1) 备份的内容必须是「改动前」的原文，不能是改完的；
/// 2) 恢复要能真的退回，而且恢复前还要再备份一份；
/// 3) 保留上限要生效，否则长期使用会把磁盘吃满。
/// </summary>
internal static class BackupTests
{
    private delegate void CheckFn(string name, bool ok, string? detail = null);

    private const string Root = @"D:\selftest-backup";

    public static (int pass, int fail) Run()
    {
        int pass = 0;
        int fail = 0;

        void Check(string name, bool ok, string? detail = null)
        {
            if (ok)
            {
                pass++;
                Console.WriteLine($"[PASS] {name}");
            }
            else
            {
                fail++;
                Console.WriteLine($"[FAIL] {name}  → 实际：{detail}");
            }
        }

        try
        {
            Reset();

            string postsDir = Path.Combine(Root, "source", "_posts");
            Directory.CreateDirectory(postsDir);

            string postPath = Path.Combine(postsDir, "sample.md");
            File.WriteAllText(postPath,
                "---\ntitle: 原始标题\ndate: 2026-11-27 10:00:00\ntags:\n  - 测试\n---\n\n第一版正文。\n",
                new UTF8Encoding(false));

            var service = new PostService(Root);

            // 读一次，模拟"打开文章"
            var meta = service.ReadPost(postPath);

            // ---------- 1. 第一次保存：应备份改动前的原文 ----------
            service.Save(meta, "第二版正文。");

            var list1 = service.Backups.List(postPath, meta.RelativePath);
            Check("首次保存后生成 1 份备份", list1.Count == 1, list1.Count.ToString());

            string backup1 = File.ReadAllText(list1[0].FilePath);
            Check("备份内容是改动前的原文", backup1.Contains("第一版正文"), backup1);
            Check("备份里不含改动后的内容", !backup1.Contains("第二版正文"), backup1);

            // 关键：备份应当保留完整的 front-matter，而不是只剩正文
            Check("备份保留 front-matter", backup1.StartsWith("---\ntitle: 原始标题"), backup1);

            // ---------- 2. 再存一次：应有两份 ----------
            var meta2 = service.ReadPost(postPath);
            service.Save(meta2, "第三版正文。");

            var list2 = service.Backups.List(postPath, meta2.RelativePath);
            Check("再次保存后累计 2 份备份", list2.Count == 2, list2.Count.ToString());
            Check("列表按时间倒序（最新在前）",
                list2[0].Time >= list2[1].Time,
                $"{list2[0].TimeDisplay} / {list2[1].TimeDisplay}");

            // ---------- 3. 恢复：回到第二版 ----------
            var meta3 = service.ReadPost(postPath);
            bool restored = service.Backups.Restore(
                list2[0].FilePath, postPath, meta3.RelativePath, out string restoreError);

            Check("恢复操作返回成功", restored, restoreError);
            Check("恢复后正文回到第二版",
                File.ReadAllText(postPath).Contains("第二版正文"), File.ReadAllText(postPath));

            // 恢复前应当给「第三版」留了底，否则恢复错了没退路
            var list3 = service.Backups.List(postPath, meta3.RelativePath);
            Check("恢复前也备份了当前版本（份数 +1）", list3.Count == 3, list3.Count.ToString());
            Check("被覆盖的第三版仍能找回",
                list3.Any(e => File.ReadAllText(e.FilePath).Contains("第三版正文")));

            // ---------- 4. 保留上限：连存 12 次，每篇最多 10 份 ----------
            for (int i = 0; i < 12; i++)
            {
                var m = service.ReadPost(postPath);
                service.Save(m, $"批量保存 {i}。");
            }

            var list4 = service.Backups.List(postPath, meta.RelativePath);
            Check("单篇备份不超过 10 份", list4.Count <= 10, list4.Count.ToString());
            Check("裁剪后仍保留最近的历史", list4.Count >= 9, list4.Count.ToString());

            // ---------- 5. 目录整洁 ----------
            string gitignore = Path.Combine(Root, ".hexo-manager", ".gitignore");
            Check("备份目录自带 .gitignore（不污染博客仓库）", File.Exists(gitignore), gitignore);

            // 备份不能落在 source 下，否则会被 Hexo 当资源发布
            Check("备份目录不在 source 内",
                !service.Backups.BackupRoot.Contains(Path.Combine("source", "_posts"), StringComparison.OrdinalIgnoreCase),
                service.Backups.BackupRoot);

            // ---------- 6. 新建文章不产生备份（没有旧内容可备） ----------
            var fresh = new HexoBlogManager.Models.PostMeta
            {
                Title = "全新文章",
                Date = DateTime.Now,
                DateText = "2026-11-27 12:00:00"
            };

            string created = service.CreateNew("brand-new", fresh, "正文");
            var listNew = service.Backups.List(created, Path.GetFileName(created));
            Check("新建文章不生成备份", listNew.Count == 0, listNew.Count.ToString());
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("[FAIL] 备份测试异常：" + ex);
        }
        finally
        {
            ForceDelete(Root);
        }

        return (pass, fail);
    }

    private static void Reset()
    {
        ForceDelete(Root);
        Directory.CreateDirectory(Root);
    }

    /// <summary>git 会留下只读对象，普通删除会失败，先清属性再删。</summary>
    private static void ForceDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;

            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }

            Directory.Delete(dir, recursive: true);
        }
        catch { }
    }
}
