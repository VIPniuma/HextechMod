using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PeakModder.HextechConfigurator;

/// <summary>主窗口骨架：数值表与视图切换。卡片墙见 Views，服务器交互见 Server。</summary>
internal sealed partial class MainWindow : Window
{
    private const string ServerUrl = "http://175.178.43.129/balance.json";
    private const string ScpTarget = "root@175.178.43.129:/www/wwwroot/hextech/balance.json";

    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "hextech_publish");

    private static readonly string LocalBackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HextechConfigurator", "hextech_balance.json");

    private readonly Dictionary<string, double> _values = new();
    private readonly Grid _root = new();
    private readonly TextBlock _status = new();

    private int _version;
    private bool _busy;
    private ModuleDef? _currentModule;

    public MainWindow()
    {
        Title = "海克斯平衡配置器";
        Width = 1000;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.PanelBackground;
        FontFamily = Theme.Font;

        foreach (var module in Schema.Modules)
        {
            foreach (var field in module.Fields)
            {
                _values[field.Key] = field.Default;
            }
        }

        BuildMainView();
        Content = _root;

        LoadFromServer();
    }

    private double Get(BalanceField field)
    {
        return _values.TryGetValue(field.Key, out var value) ? value : field.Default;
    }

    private void Set(BalanceField field, double value)
    {
        _values[field.Key] = Clamp(value, field.Min, field.Max);
    }

    private bool IsModified(BalanceField field)
    {
        return Math.Abs(Get(field) - field.Default) > 0.000001;
    }

    private int ModifiedCount(ModuleDef module)
    {
        var count = 0;
        foreach (var field in module.Fields)
        {
            if (IsModified(field))
            {
                count++;
            }
        }
        return count;
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    private static string FormatValue(double value, bool isInt)
    {
        if (isInt)
        {
            return Math.Round(value).ToString(CultureInfo.InvariantCulture);
        }
        return Math.Abs(value - Math.Round(value)) < 0.0001
            ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>状态栏更新（颜色随语义）。</summary>
    private void SetStatus(string text, Brush? color)
    {
        _status.Text = text;
        _status.Foreground = color ?? Theme.TextMuted;
    }
}

/// <summary>主界面：三选一式的词条方块墙。</summary>
internal sealed partial class MainWindow
{
    private void BuildMainView()
    {
        // 返回主界面 = 离开编辑页。不清掉的话，点「拉取服务器」后会被
        // ApplyServerConfig 当成「还在编辑页」又跳回去（2026-09-14 用户实测）。
        _currentModule = null;

        var view = new Grid { Margin = new Thickness(18) };
        view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = BuildTopBar();
        Grid.SetRow(top, 0);
        view.Children.Add(top);

        // ⚠️ _status 是跨视图复用的字段控件：二次 BuildMainView 前必须先从旧视图摘下来，
        // 否则「指定的元素已经是另一个元素的逻辑子元素」直接炸启动（2026-09-14 修过一次）。
        if (_status.Parent is Panel previousStatusParent)
        {
            previousStatusParent.Children.Remove(_status);
        }

        _status.Margin = new Thickness(2, 8, 0, 8);
        Grid.SetRow(_status, 1);
        view.Children.Add(_status);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var wrap = new WrapPanel();
        foreach (var module in Schema.Modules)
        {
            wrap.Children.Add(BuildModuleCard(module));
        }

        scroll.Content = wrap;
        Grid.SetRow(scroll, 2);
        view.Children.Add(scroll);

        _root.Children.Clear();
        _root.Children.Add(view);
    }

    private FrameworkElement BuildTopBar()
    {
        var top = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = Theme.Label("海克斯平衡配置器", 18, Theme.TextPrimary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 0);
        top.Children.Add(title);

        var fetch = Theme.SubtleButton("拉取服务器", 104);
        fetch.Click += (_, _) => LoadFromServer();
        Grid.SetColumn(fetch, 2);
        top.Children.Add(fetch);

        var upload = Theme.PrimaryButton("保存并上传到服务器", 190);
        upload.Click += (_, _) => Upload();
        Grid.SetColumn(upload, 3);
        top.Children.Add(upload);

        return top;
    }

    private FrameworkElement BuildModuleCard(ModuleDef module)
    {
        var modified = ModifiedCount(module);

        var card = Theme.Rounded(Brushes.White, 14, new Thickness(14, 10, 14, 10));
        card.Width = 240;
        card.Margin = new Thickness(0, 0, 12, 12);
        card.Cursor = Cursors.Hand;
        card.BorderBrush = modified > 0 ? Theme.Accent : Theme.Outline;
        card.BorderThickness = new Thickness(modified > 0 ? 2 : 1);

        var stack = new StackPanel();
        stack.Children.Add(Theme.Label(module.Title, 15, Theme.TextPrimary, FontWeights.SemiBold));
        stack.Children.Add(new TextBlock
        {
            Text = module.Subtitle,
            FontSize = 11.5,
            Foreground = Theme.TextDim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        });
        stack.Children.Add(new TextBlock
        {
            Text = modified > 0
                ? "已改 " + modified + " / " + module.Fields.Count + " 项"
                : module.Fields.Count + " 项参数 · 未改动",
            FontSize = 11,
            Foreground = modified > 0 ? Theme.Accent : Theme.TextDim,
            Margin = new Thickness(0, 6, 0, 0),
        });

        card.Child = stack;

        var captured = module;
        card.MouseLeftButtonDown += (_, _) => ShowDetail(captured);

        return card;
    }
}

/// <summary>编辑页：单个词条的数值编辑（数字框 + 加减步进，不用滑条）。</summary>
internal sealed partial class MainWindow
{
    private void ShowDetail(ModuleDef module)
    {
        _currentModule = module;

        var view = new Grid { Margin = new Thickness(18) };
        view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var back = Theme.SubtleButton("← 返回", 90);
        back.Click += (_, _) => BuildMainView();
        Grid.SetColumn(back, 0);
        top.Children.Add(back);

        var title = Theme.Label(module.Title, 17, Theme.TextPrimary, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        top.Children.Add(title);

        var reset = Theme.SubtleButton("恢复默认", 104);
        reset.Click += (_, _) =>
        {
            foreach (var field in module.Fields)
            {
                Set(field, field.Default);
            }
            ShowDetail(module);
            SetStatus("已恢复默认（点主界面「保存并上传到服务器」才会生效）。", Theme.Warning);
        };
        Grid.SetColumn(reset, 2);
        top.Children.Add(reset);

        Grid.SetRow(top, 0);
        view.Children.Add(top);

        var subtitle = Theme.Label(module.Subtitle + " · 改完点主界面「保存并上传到服务器」", 12.5, Theme.TextDim);
        subtitle.Margin = new Thickness(2, 0, 0, 10);
        Grid.SetRow(subtitle, 1);
        view.Children.Add(subtitle);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var stack = new StackPanel();
        foreach (var field in module.Fields)
        {
            stack.Children.Add(BuildFieldRow(field));
        }

        scroll.Content = stack;
        Grid.SetRow(scroll, 2);
        view.Children.Add(scroll);

        _root.Children.Clear();
        _root.Children.Add(view);
    }

    private FrameworkElement BuildFieldRow(BalanceField field)
    {
        var card = Theme.Rounded(Brushes.White, 12, new Thickness(14, 8, 14, 8));
        card.Margin = new Thickness(0, 0, 0, 8);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = Theme.Label(field.Label, 13, Theme.TextPrimary);
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var minus = Theme.SubtleButton("−", 40);
        minus.Height = 30;
        minus.Margin = new Thickness(0, 0, 6, 0);
        minus.Click += (_, _) => Adjust(field, -field.Step);
        Grid.SetColumn(minus, 1);
        row.Children.Add(minus);

        var box = Theme.NumberBox(FormatValue(Get(field), field.IsInt));
        box.LostFocus += (_, _) => CommitBox(field, box);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitBox(field, box);
            }
        };
        Grid.SetColumn(box, 2);
        row.Children.Add(box);

        var plus = Theme.SubtleButton("＋", 40);
        plus.Height = 30;
        plus.Margin = new Thickness(6, 0, 12, 0);
        plus.Click += (_, _) => Adjust(field, field.Step);
        Grid.SetColumn(plus, 3);
        row.Children.Add(plus);

        var range = Theme.Label(
            FieldHint(field),
            11.5,
            IsModified(field) ? Theme.Accent : Theme.TextDim);
        range.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(range, 4);
        row.Children.Add(range);

        card.Child = row;
        return card;
    }

    private string FieldHint(BalanceField field)
    {
        var range = "范围 " + FormatValue(field.Min, field.IsInt) + " ~ " + FormatValue(field.Max, field.IsInt);

        return IsModified(field)
            ? range + " · 默认 " + FormatValue(field.Default, field.IsInt) + "（已改动）"
            : range + " · 默认 " + FormatValue(field.Default, field.IsInt);
    }

    private void Adjust(BalanceField field, double delta)
    {
        Set(field, Get(field) + delta);

        if (_currentModule != null)
        {
            ShowDetail(_currentModule);
        }
    }

    private void CommitBox(BalanceField field, TextBox box)
    {
        if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            box.Text = FormatValue(Get(field), field.IsInt);
            return;
        }

        Set(field, value);

        if (_currentModule != null)
        {
            ShowDetail(_currentModule);
        }
    }
}

/// <summary>服务器交互：拉取配置回填、生成 JSON 并经 scp 上传。</summary>
internal sealed partial class MainWindow
{
    private void LoadFromServer()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetStatus("正在拉取服务器配置…", null);

        ThreadPool.QueueUserWorkItem(_ =>
        {
            string? text = null;
            string? error = null;

            try
            {
                ServicePointManager.Expect100Continue = false;

                var request = (HttpWebRequest)WebRequest.Create(ServerUrl);
                request.Method = "GET";
                request.Timeout = 6000;
                request.ReadWriteTimeout = 6000;
                request.Proxy = null;

                using var response = request.GetResponse();
                using var reader = new StreamReader(response.GetResponseStream()!);
                text = reader.ReadToEnd();
            }
            catch (Exception exception)
            {
                error = exception.Message;
            }

            Dispatcher.BeginInvoke(() =>
            {
                _busy = false;

                if (text == null)
                {
                    SetStatus("拉取失败：" + error + "（可以继续改，上传时会覆盖服务器）", Theme.Warning);
                    return;
                }

                ApplyServerConfig(text);
            });
        });
    }

    private void ApplyServerConfig(string text)
    {
        var versionMatch = Regex.Match(text, "\"version\"\\s*:\\s*(\\d+)");
        _version = versionMatch.Success ? int.Parse(versionMatch.Groups[1].Value) : 0;

        var applied = 0;

        foreach (var module in Schema.Modules)
        {
            foreach (var field in module.Fields)
            {
                var match = Regex.Match(
                    text,
                    "\"" + Regex.Escape(field.Key) + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)");

                if (!match.Success)
                {
                    continue;
                }

                Set(field, double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
                applied++;
            }
        }

        if (_currentModule != null)
        {
            ShowDetail(_currentModule);
        }
        else
        {
            BuildMainView();
        }

        SetStatus(
            applied > 0
                ? "已加载服务器配置 v" + _version + "：" + applied + " 项覆盖已应用到界面（带绿边的卡片就是被服务器改过的）。"
                : "已加载服务器配置 v" + _version + "：上面还没有配置任何覆盖，所有词条都是默认数值，你改完点「保存并上传」就有了。",
            Theme.Success);
    }

    private void Upload()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetStatus("正在生成并上传配置…", null);

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var overrides = new List<string>();

                foreach (var module in Schema.Modules)
                {
                    foreach (var field in module.Fields)
                    {
                        if (IsModified(field))
                        {
                            overrides.Add("\"" + field.Key + "\":"
                                          + Get(field).ToString("0.####", CultureInfo.InvariantCulture));
                        }
                    }
                }

                var json = "{\"version\":" + (_version + 1) + ",\"overrides\":{"
                           + string.Join(",", overrides) + "}}";

                Directory.CreateDirectory(Path.GetDirectoryName(LocalBackupPath)!);
                File.WriteAllText(LocalBackupPath, json);

                var start = new ProcessStartInfo
                {
                    FileName = "scp",
                    Arguments = "-i \"" + KeyPath + "\" -o StrictHostKeyChecking=no -o BatchMode=yes \""
                                + LocalBackupPath + "\" " + ScpTarget,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                var stdOut = new StringBuilder();
                var stdErr = new StringBuilder();

                using var process = new Process { StartInfo = start, EnableRaisingEvents = true };

                // 两条流都异步读，避免 scp 把某一侧缓冲区写满后互相卡死（那种死锁会一直拖到超时）。
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        stdOut.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        stdErr.AppendLine(e.Data);
                    }
                };

                if (!process.Start())
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _busy = false;
                        SetStatus("上传失败：没能启动 scp（确认本机装了 Git/OpenSSH 且在 PATH 里）。", Theme.Danger);
                    });
                    return;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 等进程退出；超时（多半是网络/密钥问题）就杀掉并当失败，绝不直接读 ExitCode ——
                // 进程没退出时读 ExitCode 会抛 InvalidOperationException，而且发生在 UI 线程上 → 整个程序闪退。
                if (!process.WaitForExit(25000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // 杀不掉也无所谓，下面照常当超时处理。
                    }

                    Dispatcher.BeginInvoke(() =>
                    {
                        _busy = false;
                        SetStatus("上传超时（25 秒没响应，多半是网络或密钥问题）。本地备份已存：" + LocalBackupPath,
                            Theme.Danger);
                    });
                    return;
                }

                var output = stdErr.ToString();
                var exitCode = process.ExitCode;

                Dispatcher.BeginInvoke(() =>
                {
                    _busy = false;

                    if (exitCode == 0)
                    {
                        _version++;
                        SetStatus("已上传 v" + _version + "（" + overrides.Count + " 项改动）· 玩家重启游戏后生效。",
                            Theme.Success);
                        BuildMainView();
                    }
                    else
                    {
                        SetStatus("上传失败：" + output.Trim() + "（本地已存 " + LocalBackupPath + "）", Theme.Danger);
                    }
                });
            }
            catch (Exception exception)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _busy = false;
                    SetStatus("上传失败：" + exception.Message, Theme.Danger);
                });
            }
        });
    }
}
