using System.IO;
using System.Text;
using HexoBlogManager.Services;

namespace SelfTest;

/// <summary>
/// 配置备份与还原的自检。
///
/// 之前只做了「保存前生成 .bak」，没有还原入口，也没有任何测试盯着这块。
/// 这里补上：备份不能覆盖、时间要从文件名正确解析、还原要能退回去、
/// 还原前还要先给当前文件留底。
/// </summary>
internal static class ConfigBackupTests
{
    private const string Root = @"D:\selftest-config-backup";

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
            ForceDelete(Root);
            Directory.CreateDirectory(Root);

            string configPath = Path.Combine(Root, "_config.yml");
            File.WriteAllText(configPath, "title: 第一版\ntheme: butterfly\n", new UTF8Encoding(false));

            var service = new ConfigService(Root);

            // ---------- 1. 备份 ----------
            string first = service.Backup(configPath);
            Check("备份文件已生成", File.Exists(first), first);
            Check("备份内容与原文件一致",
                File.ReadAllText(first) == File.ReadAllText(configPath));

            var list1 = service.ListBackups(configPath);
            Check("列表能读到 1 份备份", list1.Count == 1, list1.Count.ToString());
            Check("备份时间接近当前时间",
                Math.Abs((list1[0].Time - DateTime.Now).TotalMinutes) < 2,
                list1[0].Time.ToString("O"));

            // ---------- 2. 同一秒连续备份不能互相覆盖 ----------
            string second = service.Backup(configPath);
            string third = service.Backup(configPath);

            Check("同一秒内多次备份生成不同文件", first != second && second != third,
                $"{Path.GetFileName(first)} / {Path.GetFileName(second)} / {Path.GetFileName(third)}");
            Check("同秒备份总数正确", service.ListBackups(configPath).Count == 3,
                service.ListBackups(configPath).Count.ToString());

            // ---------- 3. 时间解析（含 -1 后缀） ----------
            string fakeOld = Path.Combine(Root, "_config.yml.20200101-010101.bak");
            File.WriteAllText(fakeOld, "title: 很旧的版本\n");

            string fakeSuffix = Path.Combine(Root, "_config.yml.20200102-020202-1.bak");
            File.WriteAllText(fakeSuffix, "title: 带后缀的版本\n");

            var list2 = service.ListBackups(configPath);
            var oldEntry = list2.FirstOrDefault(e => e.FilePath == fakeOld);
            var suffixEntry = list2.FirstOrDefault(e => e.FilePath == fakeSuffix);

            Check("从文件名解析出 2020-01-01",
                oldEntry is not null && oldEntry.Time.Date == new DateTime(2020, 1, 1),
                oldEntry?.Time.ToString("O") ?? "未找到");
            Check("带 -1 后缀的时间也能解析",
                suffixEntry is not null && suffixEntry.Time.Date == new DateTime(2020, 1, 2),
                suffixEntry?.Time.ToString("O") ?? "未找到");
            Check("列表按时间倒序，新备份在前",
                list2[0].Time >= list2[^1].Time,
                $"{list2[0].Time:O} / {list2[^1].Time:O}");

            // ---------- 4. 还原 ----------
            File.WriteAllText(configPath, "title: 改坏的版本\n", new UTF8Encoding(false));

            bool restored = service.RestoreBackup(oldEntry!.FilePath, configPath, out string error);
            Check("还原操作返回成功", restored, error);
            Check("还原后内容回到 2020 那份",
                File.ReadAllText(configPath).Contains("很旧的版本"), File.ReadAllText(configPath));

            // 还原前应该把「改坏的版本」也备份下来，否则还原错了没退路
            var afterRestore = service.ListBackups(configPath);
            Check("还原前备份了被覆盖的内容",
                afterRestore.Any(e => File.ReadAllText(e.FilePath).Contains("改坏的版本")),
                afterRestore.Count.ToString());

            // ---------- 5. 异常分支 ----------
            bool failed = service.RestoreBackup(
                Path.Combine(Root, "不存在的备份.bak"), configPath, out string missingError);

            Check("备份不存在时返回失败", !failed);
            Check("备份不存在时给出中文提示", missingError.Contains("不存在"), missingError);

            // ---------- 6. 清理旧备份 ----------
            for (int i = 0; i < 8; i++)
                service.Backup(configPath);

            int before = service.ListBackups(configPath).Count;
            service.CleanOldBackups(configPath, keep: 5);
            int after = service.ListBackups(configPath).Count;

            Check("清理前备份超过上限", before > 5, before.ToString());
            Check("清理后只保留 5 份", after == 5, after.ToString());
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("[FAIL] 配置备份测试异常：" + ex);
        }
        finally
        {
            ForceDelete(Root);
        }

        return (pass, fail);
    }

    private static void ForceDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { }
    }
}
