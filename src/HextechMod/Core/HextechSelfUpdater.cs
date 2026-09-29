using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace PeakModder.HextechMod;

/// <summary>
/// 游戏内自更新的「落盘 + 布雷」阶段。
/// <para>
/// 插件 dll 正被游戏进程加载着，文件锁死，运行中换不掉自己 —— 唯一可行的顺序是：
/// 先把新版 dll 写到目标 dll 旁边（.update 后缀），再拉起一个独立的 PowerShell 脚本，
/// 脚本轮询等游戏进程退出，然后原地把 .update 顶替目标 dll、重新启动游戏、自删。
/// 游戏侧只需要在脚本拉起后 <see cref="Application.Quit"/>，剩下的全归脚本。
/// </para>
/// <para>
/// 脚本用 PowerShell 而不是 cmd 批处理：游戏路径可能含中文，cmd 按系统代码页读批处理文件，
/// UTF-8 写出来的中文路径会乱码；PowerShell 的 .ps1 按 UTF-8 BOM 识别，写盘时带上 BOM 就稳。
/// </para>
/// </summary>
internal static class HextechSelfUpdater
{
    /// <summary>
    /// 把新版 dll 写到目标旁边，并布好「等退出 → 替换 → 重启」的脚本。
    /// 全部就绪返回 true；任何一步失败返回 false 并给出原因（调用方弹提示，玩家还能走安装器）。
    /// </summary>
    public static bool StageAndArm(byte[] data, out string error)
    {
        error = string.Empty;

        var assemblyPath = typeof(HextechSelfUpdater).Assembly.Location;
        var gamePath = GameExecutablePath();

        if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
        {
            error = "找不到当前模组 dll 的位置";
            return false;
        }

        if (string.IsNullOrEmpty(gamePath))
        {
            error = "找不到游戏主程序的位置";
            return false;
        }

        var pendingPath = assemblyPath + ".update";

        try
        {
            File.WriteAllBytes(pendingPath, data);
        }
        catch (Exception e)
        {
            error = Localization.T("新版 dll 写不进去（目录没有写权限？）：{0}", e.Message);
            return false;
        }

        // 重启方式：优先走 Steam 协议（steam://rungameid/<appid>）。
        // 以前脚本直接 Start-Process 游戏主程序 —— 启动上下文和玩家从 Steam 点开完全不同
        // （缺 SteamAppId 等环境变量），Steamworks 初始化失败时游戏可能在**启动早期自退**，
        // 玩家看到的就是「自更新后第一次打开游戏，所有 mod 都没加载」（2026-09-23 用户反馈；
        // 本机实测：不带 Steam 上下文直启 PEAK，链式加载器刚跑完游戏就退了）。
        var steamUri = FindSteamRunGameIdUri(gamePath);

        // 等「老进程死透」用的两个抓手：
        //  - 自己的 PID（按进程名等会误判：名字对不上会直接跳过等待，重名/第二实例会白等）；
        //  - BepInEx 的日志文件（进程退了但句柄还没释放时，新实例一上来 BepInEx 打不开它 → 整条
        //    链式加载器直接挂掉，表现就是「更新后第一次进游戏所有 mod 都不加载」——必须等它可独占打开）。
        var bepInExLog = BepInExLogPath(assemblyPath);
        var selfPid = Process.GetCurrentProcess().Id;

        var scriptPath = pendingPath + ".ps1";

        try
        {
            File.WriteAllText(
                scriptPath,
                BuildScript(assemblyPath, pendingPath, gamePath, steamUri, selfPid, bepInExLog),
                new UTF8Encoding(true));
        }
        catch (Exception e)
        {
            TryDelete(pendingPath);
            error = Localization.T("更新脚本写不进去：{0}", e.Message);
            return false;
        }

        if (!Launch(scriptPath))
        {
            TryDelete(pendingPath);
            TryDelete(scriptPath);
            error = "更新脚本没能启动";
            return false;
        }

        return true;
    }

    /// <summary>游戏主程序的全路径（重启要用）。拿不到就更新不了，返回 null。</summary>
    private static string? GameExecutablePath()
    {
        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 从游戏自身路径反查 Steam 的 rungameid URI（steam://rungameid/&lt;appid&gt;）。
    /// 游戏一定装在某个库的 <c>steamapps\common\&lt;游戏目录&gt;</c> 下，往上两级就是它所属的
    /// <c>steamapps</c>，里面的 <c>appmanifest_&lt;appid&gt;.acf</c> 文件名自带 AppID；
    /// 只要清单里的 installdir 和游戏目录名对上就是它。**不写死 AppID**（PEAK 换过一次）。
    /// 找不到（Steam 清单被清 / 目录结构异常）返回 null，脚本回退成直接启动。
    /// </summary>
    private static string? FindSteamRunGameIdUri(string gamePath)
    {
        try
        {
            var gameDir = Path.GetDirectoryName(gamePath);
            var common = Path.GetDirectoryName(gameDir);
            var steamApps = Path.GetDirectoryName(common);

            if (string.IsNullOrEmpty(gameDir) || string.IsNullOrEmpty(steamApps) || !Directory.Exists(steamApps))
            {
                return null;
            }

            var gameFolderName = Path.GetFileName(gameDir);

            foreach (var manifest in Directory.GetFiles(steamApps, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(manifest);

                    if (System.Text.RegularExpressions.Regex.IsMatch(
                            text,
                            "\"installdir\"\\s+\"" + System.Text.RegularExpressions.Regex.Escape(gameFolderName) + "\"",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        var fileName = Path.GetFileNameWithoutExtension(manifest);

                        if (fileName.StartsWith("appmanifest_", StringComparison.OrdinalIgnoreCase))
                        {
                            var id = fileName.Substring("appmanifest_".Length);
                            var digits = id.Length > 0;

                            foreach (var c in id)
                            {
                                if (c < '0' || c > '9')
                                {
                                    digits = false;
                                    break;
                                }
                            }

                            if (digits)
                            {
                                return "steam://rungameid/" + id;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // 单个清单读不出来就跳过，不影响别的候选。
                }
            }
        }
        catch (Exception)
        {
            // 整条探测失败就按「没有 Steam」处理，回退直接启动。
        }

        return null;
    }

    /// <summary>
    /// BepInEx 日志文件的路径（…\BepInEx\LogOutput.log）。插件 dll 在 …\BepInEx\plugins\ 下，
    /// 往上两级就是 BepInEx 目录。拿不到返回 null（脚本就跳过「等句柄释放」那一步）。
    /// </summary>
    private static string? BepInExLogPath(string assemblyPath)
    {
        try
        {
            var plugins = Path.GetDirectoryName(assemblyPath);
            var bepInEx = Path.GetDirectoryName(plugins);

            if (string.IsNullOrEmpty(bepInEx))
            {
                return null;
            }

            var candidate = Path.Combine(bepInEx, "LogOutput.log");

            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>等游戏退出 → 换 dll → 重启游戏（走 Steam）→ 自删，就这一条流水线。</summary>
    private static string BuildScript(
        string targetPath, string pendingPath, string gamePath, string? steamUri, int selfPid, string? bepInExLog)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "hextech_update.log");

        return string.Join('\n',
            "$target = '" + Escape(targetPath) + "'",
            "$pending = '" + Escape(pendingPath) + "'",
            "$game = '" + Escape(gamePath) + "'",
            "$steamUri = '" + Escape(steamUri) + "'",
            "$log = '" + Escape(logPath) + "'",
            "$selfPid = " + selfPid.ToString(CultureInfo.InvariantCulture),
            "$probe = '" + Escape(bepInExLog) + "'",
            "$name = [IO.Path]::GetFileNameWithoutExtension($game)",
            "function Log($m) { try { Add-Content -LiteralPath $log -Value (\"[{0}] {1}\" -f (Get-Date -Format 'HH:mm:ss.fff'), $m) } catch { } }",
            "try {",
            "  Log ('start; target=' + $target + '; pid=' + $selfPid)",
            // 1) 等**自己这个**游戏进程彻底退出。按 PID 等，不按进程名 —— 名字对不上会直接跳过等待。
            "  while (Get-Process -Id $selfPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }",
            "  Log 'game process gone'",
            // 2) 等老进程把文件句柄真正放掉：进程退出后 BepInEx 的 LogOutput.log 还会被占一小会儿，
            //    这时候拉起新实例，BepInEx 打不开日志 → 整条链式加载器挂掉 → **所有 mod 都不加载**
            //    （2026-09-23 玩家反馈「自更新后第一次进游戏 mod 全没影」的元凶）。
            //    这里等它能被独占打开为止，最多 30 秒；等不到也继续（不能卡死更新）。
            "  if ($probe -ne '') {",
            "    $until = (Get-Date).AddSeconds(30)",
            "    $released = $false",
            "    while ((Get-Date) -lt $until) {",
            "      $fs = $null",
            "      try { $fs = [IO.File]::Open($probe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None); $released = $true; break }",
            "      catch { Start-Sleep -Milliseconds 300 }",
            "      finally { if ($fs -ne $null) { $fs.Dispose() } }",
            "    }",
            "    Log ('log handle released: ' + $released)",
            "  }",
            "  Start-Sleep -Seconds 2",
            "  Log 'settled'",
            // 2) 重试替换：Unity 退出到旧 dll 文件锁真正释放之间可能有延迟，
            //    一次性 Move-Item 会因共享冲突失败，必须重试直到目标就位（最多 60s）。
            "  $deadline = (Get-Date).AddSeconds(60)",
            "  while ($true) {",
            "    try { Move-Item -Force -LiteralPath $pending -Destination $target -ErrorAction Stop; break }",
            "    catch { if ((Get-Date) -gt $deadline) { Log ('move failed: ' + $_.Exception.Message); throw } Start-Sleep -Milliseconds 500 }",
            "  }",
            "  Log 'dll replaced'",
            // 3) 重启。**优先走 Steam**：直接 Start-Process 主程序的启动上下文和玩家平时点开不同
            //    （缺 SteamAppId 等环境变量），Steamworks 初始化失败会让游戏在启动早期自退，
            //    表现就是「更新后第一次打开游戏 mod 全没影」。Steam 协议失败再回退直接启动。
            //    另外玩家要是等不及自己把游戏开起来了（进程又出现了），就不要再拉第二个实例。
            "  if (Get-Process -Name $name -ErrorAction SilentlyContinue) {",
            "    Log 'game is running again (player started it); skip relaunch'",
            "  } elseif ($steamUri -ne '') {",
            "    try { Start-Process -FilePath $steamUri; Log ('relaunched via ' + $steamUri) }",
            "    catch { Log ('steam relaunch failed: ' + $_.Exception.Message); Start-Process -FilePath $game -WorkingDirectory (Split-Path -Parent $game); Log 'relaunched via exe (fallback)' }",
            "  } else {",
            "    Start-Process -FilePath $game -WorkingDirectory (Split-Path -Parent $game)",
            "    Log 'relaunched via exe'",
            "  }",
            "} finally {",
            // 无论成败都自删脚本；失败时 .update 会留下（无害，下次更新会覆盖），日志里有原因可查。
            "  Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue",
            "}");
    }

    /// <summary>PowerShell 单引号字符串里的单引号要翻倍，路径里出现单引号时才不会断句。</summary>
    private static string Escape(string? path)
    {
        return (path ?? string.Empty).Replace("'", "''");
    }

    private static bool Launch(string scriptPath)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            };

            Process.Start(start);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // 删不掉就删不掉，一个 .update 残留不影响任何功能。
        }
    }
}
