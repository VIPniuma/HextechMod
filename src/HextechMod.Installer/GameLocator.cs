using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PeakModder.HextechInstaller;

/// <summary>
/// 自动找出 PEAK 装在哪。
/// <para>
/// 思路是「顺着 Steam 找，而不是碰字符串猜路径」：从注册表拿到 Steam 安装目录，
/// 再解析每个磁盘的 <c>steamapps/libraryfolders.vdf</c> 枚举全部游戏库，
/// 在每个库里找 <c>steamapps/common/PEAK</c>。这样换盘、多库、自定义库名都能命中，
/// 也不依赖 PEAK 的 Steam AppID（那玩意儿变过一次，写死反而容易失效）。
/// </para>
/// </summary>
internal static class GameLocator
{
    private const string GameFolderName = "PEAK";

    /// <summary>PEAK 的 Unity 数据目录，用它判定一个目录是不是真的游戏根目录。</summary>
    private const string ManagedRelative = @"PEAK_Data\Managed";

    private static readonly string[] RegistrySteamKeys =
    {
        @"HKEY_CURRENT_USER\Software\Valve\Steam",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
    };

    /// <summary>返回检测到的游戏目录；找不到返回 <c>null</c>。</summary>
    public static string? Locate()
    {
        // 1) 顺着 Steam 的库列表找。
        var libraries = new List<string>();

        foreach (var steamRoot in EnumerateSteamRoots())
        {
            AddDistinct(libraries, steamRoot);

            foreach (var library in ReadLibraries(steamRoot))
            {
                AddDistinct(libraries, library);
            }
        }

        foreach (var library in libraries)
        {
            var candidate = Path.Combine(library, "steamapps", "common", GameFolderName);

            if (IsGameDirectory(candidate))
            {
                return Canonicalize(candidate);
            }
        }

        // 2) 库列表读不到（Steam 装在别处、注册表被清过）时的兜底：常见位置扫一遍。
        foreach (var drive in EnumerateDriveRoots())
        {
            foreach (var relative in new[]
                     {
                         @"Steam\steamapps\common\PEAK",
                         @"SteamLibrary\steamapps\common\PEAK",
                         @"Program Files (x86)\Steam\steamapps\common\PEAK",
                         @"Games\Steam\steamapps\common\PEAK",
                         @"Program Files\Steam\steamapps\common\PEAK",
                     })
            {
                var candidate = Path.Combine(drive, relative);

                if (IsGameDirectory(candidate))
                {
                    return Canonicalize(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>这个目录到底能不能当游戏根目录用。</summary>
    public static bool IsGameDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        // PEAK_Data/Managed 是 Unity 的托管程序集目录 —— 有它就说明这是游戏根目录，
        // 而不是随便一个叫 PEAK 的空文件夹。
        if (Directory.Exists(Path.Combine(directory, ManagedRelative)))
        {
            return true;
        }

        return File.Exists(Path.Combine(directory, "PEAK.exe"));
    }

    /// <summary>给出一个目录，尽量把它「往下」收敛成游戏根目录（用户可能选的是它的上级）。</summary>
    public static string? Normalize(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var current = new DirectoryInfo(directory);

        for (var depth = 0; current != null && depth < 3; depth++, current = current.Parent)
        {
            if (IsGameDirectory(current.FullName))
            {
                return current.FullName;
            }

            // 再顺手看一眼下级目录（用户可能选到了 steamapps）。
            try
            {
                foreach (var child in current.GetDirectories())
                {
                    if (IsGameDirectory(child.FullName))
                    {
                        return child.FullName;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 没权限遍历就算了，继续往上找。
            }
        }

        return null;
    }

    /// <summary>
    /// 把路径大小写还原成磁盘上的真实写法。
    /// Steam 写进注册表的 <c>SteamPath</c> 是整串小写的，直接显示在界面上很别扭。
    /// </summary>
    private static string Canonicalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);

            if (string.IsNullOrEmpty(root))
            {
                return full;
            }

            var segments = full.Substring(root!.Length)
                .Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            var current = root!;

            foreach (var segment in segments)
            {
                var next = Path.Combine(current, segment);

                if (!Directory.Exists(next))
                {
                    return full;
                }

                foreach (var directory in Directory.GetDirectories(current))
                {
                    if (string.Equals(Path.GetFileName(directory), segment, StringComparison.OrdinalIgnoreCase))
                    {
                        next = directory;
                        break;
                    }
                }

                current = next;
            }

            return current;
        }
        catch (Exception)
        {
            // 规范化只是为了让界面好看，失败就退回原字符串。
            return path;
        }
    }

    private static IEnumerable<string> EnumerateSteamRoots()
    {
        foreach (var key in RegistrySteamKeys)
        {
            string? path = null;

            try
            {
                // 32 位系统上注册表没有 WOW6432Node 那一层，忽略异常即可。
                path = Registry.GetValue(key, "SteamPath", null) as string
                       ?? Registry.GetValue(key, "InstallPath", null) as string;
            }
            catch (Exception)
            {
                // 注册表读不动（权限/不存在）不是致命问题，继续试下一个。
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path!.Replace('/', '\\');
            }
        }

        // 32 位进程在 64 位系统上会被重定向，这里再直接找一次默认位置。
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "Steam");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "Steam");
        }
    }

    private static IEnumerable<string> EnumerateDriveRoots()
    {
        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            yield return drive.RootDirectory.FullName;
        }
    }

    /// <summary>读 <c>steamapps/libraryfolders.vdf</c>，把每个库的根目录抠出来。</summary>
    private static IEnumerable<string> ReadLibraries(string steamRoot)
    {
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");

        if (!File.Exists(vdf))
        {
            yield break;
        }

        string content;

        try
        {
            content = File.ReadAllText(vdf);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        // 新旧两版 VDF 都是 `"path"		"D:\\SteamLibrary"` 这么一行，正则足够，
        // 没必要为了这一个文件去写一个完整的 VDF 解析器。
        foreach (Match match in Regex.Matches(content, "\"path\"\\s*\"([^\"]+)\""))
        {
            var path = match.Groups[1].Value.Replace(@"\\", @"\");

            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
    }

    private static void AddDistinct(List<string> list, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var existing in list)
        {
            if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        list.Add(value);
    }
}
