using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using IODirectory = System.IO.Directory;
using IOPath = System.IO.Path;

namespace PeakModder.HextechInstaller;

/// <summary>
/// 安装器主窗口（2026-09-14 重做）：磨砂白浅色界面 + 左侧导航，三页 ——
/// ① 主页（作者信息与使用指引）② 前置框架（装/卸 BepInEx）③ 模组仓库（服务器清单逐个安装）。
/// 日志与进度条三页共享。
/// <para>
/// 界面纯代码搭建（不写 .xaml）。⚠️ 往卡片 Border 里塞内容时**必须记得 card.Child = grid**
/// —— 仓库卡片第一版就是漏了这一句，整页看起来是空的（2026-09-14 修）。
/// </para>
/// </summary>
internal sealed class MainWindow : Window
{
    private sealed class StatusRow
    {
        public Ellipse Dot = null!;
        public TextBlock Detail = null!;
    }

    /// <summary>左侧导航的一项：底板 + 高亮条 + 文字，切换页时改状态。</summary>
    private sealed class NavItem
    {
        public NavItem(Border root, Border bar, TextBlock text)
        {
            Root = root;
            Bar = bar;
            Text = text;
        }

        public Border Root { get; }

        public Border Bar { get; }

        public TextBlock Text { get; }
    }

    private readonly InstallEngine _engine;
    private readonly CancellationTokenSource _cts = new();

    private StatusRow _bepInExRow = new();

    private TextBlock _pathText = null!;
    private TextBlock _hint = null!;
    private RichTextBox _log = null!;
    private Border _progressTrack = null!;
    private Border _progressFill = null!;
    private TextBlock _progressLabel = null!;
    private Button _frameworkButton = null!;
    private Button _uninstallButton = null!;
    private Button _browseButton = null!;

    // ── 主页头像 ──
    private Image _avatarImage = null!;
    private FrameworkElement _avatarFallback = null!;

    // ── 仓库页 ──
    private StackPanel _repoList = null!;
    private TextBlock _repoHint = null!;
    private Button _refreshRepoButton = null!;

    // ── 导航 ──
    private readonly List<NavItem> _navItems = new();
    private readonly List<FrameworkElement> _pages = new();
    private int _currentPage;

    private string? _gameDirectory;
    private bool _busy;
    private bool _repoLoaded;

    /// <summary>启动加载页覆盖层（Loaded 1.2 秒后淡出移除）。</summary>
    private Border? _loadingOverlay;

    /// <summary>仓库清单（主模组 + 服务器 repository.json 里的其它模组）。</summary>
    private readonly List<RepoMod> _repoMods = new();

    private const string AuthorQQ = "1330144749";

    public MainWindow()
    {
        _engine = new InstallEngine(AppendLog);

        // 窗口图标用内嵌的 app.ico（exe 文件图标由 csproj 的 ApplicationIcon 负责，两处都设置）。
        try
        {
            var iconStream = typeof(Program).Assembly
                .GetManifestResourceStream("PeakModder.HextechInstaller.app.ico");

            if (iconStream != null)
            {
                Icon = BitmapFrame.Create(iconStream);
            }
        }
        catch (Exception)
        {
            // 图标加载失败不该挡住启动，顶多显示默认图标。
        }

        Build();

        // 等窗口显示出来再检测，日志才有地方写；仓库清单在检测之后异步跑，
        // 服务器慢或者断网都不会卡住界面。
        Loaded += OnLoaded;
        SourceInitialized += (_, _) => Frost.EnableAcrylic(this);
    }

    // ── 界面 ─────────────────────────────────────────────────────

    private void Build()
    {
        Title = "小王同学模组安装器";
        Width = 860;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = Theme.Font;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var topBar = BuildTopBar();
        Grid.SetRow(topBar, 0);
        shell.Children.Add(topBar);

        var body = BuildBody();
        Grid.SetRow(body, 1);
        shell.Children.Add(body);

        var shellBorder = new Border
        {
            Background = Theme.PanelBackground,
            CornerRadius = new CornerRadius(22),
            BorderBrush = Theme.Outline,
            BorderThickness = new Thickness(1),
            Child = shell,
        };

        // 启动加载页：盖在主界面上的同圆角覆盖层，Loaded 后至少停留 1.2 秒再淡出露出主界面
        // （2026-09-14 用户要求：不要独立漂浮窗口，就在本窗口里做加载动画；动画用开源库 LoadingIndicators.WPF）。
        _loadingOverlay = BuildLoadingOverlay();

        var root = new Grid();
        root.Children.Add(shellBorder);
        root.Children.Add(_loadingOverlay);

        Content = root;

        var loadingTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200),
        };
        loadingTimer.Tick += (_, _) =>
        {
            loadingTimer.Stop();
            FadeOutLoadingOverlay();
        };
        loadingTimer.Start();

        // 安装到一半把窗口关了的话，别让后台任务继续往已关闭的窗口上写日志。
        Closing += (_, _) => _cts.Cancel();
    }

    /// <summary>启动加载页：与窗口同圆角的覆盖层 —— 旋转弧（开源库）+ 应用名 + 版本 + 提示。</summary>
    private Border BuildLoadingOverlay()
    {
        var spinner = new MahApps.Metro.Controls.ProgressRing
        {
            IsActive = true,
            IsLarge = true,
            Width = 52,
            Height = 52,
            Foreground = Theme.Accent,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var title = new TextBlock
        {
            Text = "小王同学模组安装器",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var subtitle = new TextBlock
        {
            Text = "v" + (version == null ? "?" : version.ToString(3)),
            FontSize = 11.5,
            Foreground = Theme.TextDim,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
        };

        var hint = new TextBlock
        {
            Text = "正在加载，请稍候…",
            FontSize = 12,
            Foreground = Theme.TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(spinner);
        stack.Children.Add(title);
        stack.Children.Add(subtitle);
        stack.Children.Add(hint);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC)),
            CornerRadius = new CornerRadius(22),
            Child = stack,
        };
    }

    /// <summary>加载页淡出后从视觉树移除（不再参与布局，也挡不住下面的界面）。</summary>
    private void FadeOutLoadingOverlay()
    {
        var overlay = _loadingOverlay;

        if (overlay == null)
        {
            return;
        }

        _loadingOverlay = null;

        var animation = new DoubleAnimation(1d, 0d, TimeSpan.FromMilliseconds(320));

        animation.Completed += (_, _) =>
        {
            if (overlay.Parent is Grid root)
            {
                root.Children.Remove(overlay);
            }
        };

        overlay.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>顶栏：拖动区 + 标题 + 关闭。</summary>
    private FrameworkElement BuildTopBar()
    {
        var bar = new Grid { Height = 44, Background = Brushes.Transparent };

        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        var title = Theme.Label("小王同学模组安装器", 13, Theme.TextMuted);
        title.Margin = new Thickness(20, 0, 0, 0);
        bar.Children.Add(title);

        var close = Theme.CloseButton("✕");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => Close();
        bar.Children.Add(close);

        return bar;
    }

    private FrameworkElement BuildBody()
    {
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(192) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = BuildSidebar();
        Grid.SetColumn(sidebar, 0);
        body.Children.Add(sidebar);

        var content = BuildContent();
        Grid.SetColumn(content, 1);
        body.Children.Add(content);

        return body;
    }

    /// <summary>左侧导航栏：logo + 三个页签 + 底部版本号。</summary>
    private FrameworkElement BuildSidebar()
    {
        var sidebar = new Border
        {
            Background = Theme.SidebarBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 0, 1, 0),
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 0 logo
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 1 导航
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 3 版本

        var logo = new StackPanel { Margin = new Thickness(20, 14, 12, 10), Orientation = Orientation.Horizontal };
        logo.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 12,0 L 22.4,6 L 22.4,18 L 12,24 L 1.6,18 L 1.6,6 Z"),
            Fill = Theme.Accent,
            Stretch = Stretch.Uniform,
            Width = 26,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var logoTexts = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        logoTexts.Children.Add(Theme.Label("小王同学", 16, Theme.TextPrimary, FontWeights.Bold));
        logoTexts.Children.Add(Theme.Label("模组安装器", 11.5, Theme.TextDim));
        logo.Children.Add(logoTexts);

        Grid.SetRow(logo, 0);
        grid.Children.Add(logo);

        var nav = new StackPanel { Margin = new Thickness(12, 6, 12, 0) };
        nav.Children.Add(BuildNavItem(0, "主页"));
        nav.Children.Add(BuildNavItem(1, "前置框架"));
        nav.Children.Add(BuildNavItem(2, "模组仓库"));

        Grid.SetRow(nav, 1);
        grid.Children.Add(nav);

        var version = Theme.Label("v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0"), 11.5, Theme.TextDim);
        version.Margin = new Thickness(20, 0, 0, 12);
        Grid.SetRow(version, 3);
        grid.Children.Add(version);

        sidebar.Child = grid;
        return sidebar;
    }

    private FrameworkElement BuildNavItem(int index, string title)
    {
        var root = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(13),
            Height = 42,
            Margin = new Thickness(0, 4, 0, 4),
            Cursor = Cursors.Hand,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bar = new Border
        {
            Width = 3.5,
            Height = 18,
            CornerRadius = new CornerRadius(2),
            Background = Theme.Accent,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(bar, 0);
        grid.Children.Add(bar);

        var text = Theme.Label(title, 14, Theme.TextMuted);
        Grid.SetColumn(text, 1);
        text.Margin = new Thickness(10, 0, 0, 0);
        grid.Children.Add(text);

        root.Child = grid;
        root.MouseLeftButtonDown += (_, _) => SwitchPage(index);

        _navItems.Add(new NavItem(root, bar, text));
        return root;
    }

    /// <summary>右侧内容列：共享的游戏目录条 + 三页 + 日志 + 进度。</summary>
    private FrameworkElement BuildContent()
    {
        var content = new Grid { Margin = new Thickness(16, 10, 16, 10) };

        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // 0 游戏目录条
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });// 1 页面
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // 2 日志
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // 3 进度

        // ── 游戏目录条（三页共享，检测到目录这件事对每一页都有意义）──
        var dirBar = Theme.Rounded(Theme.PanelBackgroundLight, 14, new Thickness(14, 8, 14, 8));
        dirBar.BorderBrush = Theme.Divider;
        dirBar.BorderThickness = new Thickness(1);

        var dirGrid = new Grid();
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dirLabel = Theme.Label("游戏目录", 13, Theme.TextMuted);
        dirLabel.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(dirLabel, 0);
        dirGrid.Children.Add(dirLabel);

        _pathText = Theme.Label("正在查找…", 13, Theme.TextMuted);
        _pathText.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_pathText, 1);
        dirGrid.Children.Add(_pathText);

        _browseButton = Theme.SubtleButton("浏览…", 76);
        _browseButton.Height = 30;
        _browseButton.Margin = new Thickness(10, 0, 0, 0);
        _browseButton.Click += OnBrowseClicked;
        Grid.SetColumn(_browseButton, 2);
        dirGrid.Children.Add(_browseButton);

        _refreshRepoButton = Theme.SubtleButton("刷新仓库", 84);
        _refreshRepoButton.Height = 30;
        _refreshRepoButton.Margin = new Thickness(8, 0, 0, 0);
        _refreshRepoButton.Click += async (_, _) => await LoadRepositoryAsync();
        Grid.SetColumn(_refreshRepoButton, 3);
        dirGrid.Children.Add(_refreshRepoButton);

        dirBar.Child = dirGrid;
        Grid.SetRow(dirBar, 0);
        content.Children.Add(dirBar);

        // ── 三个页面 ──
        var host = new Grid();

        _pages.Add(BuildHomePage());
        _pages.Add(BuildFrameworkPage());
        _pages.Add(BuildRepoPage());

        for (var i = 0; i < _pages.Count; i++)
        {
            _pages[i].Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            host.Children.Add(_pages[i]);
        }

        Grid.SetRow(host, 1);
        content.Children.Add(host);

        // ── 日志（三页共享，套一层圆角边框）──
        _log = new RichTextBox
        {
            IsReadOnly = true,
            Background = Brushes.Transparent,
            Foreground = Theme.TextPrimary,
            BorderThickness = new Thickness(0),
            FontFamily = Theme.Font,
            FontSize = 12.5,
            Padding = new Thickness(14, 8, 10, 8),
            Height = 118,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsDocumentEnabled = false,
            Cursor = Cursors.Arrow,
        };

        _log.Document.Blocks.Clear();
        _log.Document.PagePadding = new Thickness(0);
        Theme.SlimScrollBars(_log);

        var logFrame = new Border
        {
            Background = Theme.InsetBackground,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Child = _log,
        };

        Grid.SetRow(logFrame, 2);
        content.Children.Add(logFrame);

        // ── 进度（三页共享）──
        _progressTrack = new Border
        {
            Background = Theme.PanelBackgroundLight,
            CornerRadius = new CornerRadius(2.5),
            Height = 5,
            ClipToBounds = true,
            Margin = new Thickness(0, 10, 0, 0),
        };

        _progressFill = new Border
        {
            Background = Theme.Accent,
            CornerRadius = new CornerRadius(2.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
        };

        _progressTrack.Child = _progressFill;

        _progressLabel = Theme.Label("就绪", 12, Theme.TextDim);
        _progressLabel.Margin = new Thickness(0, 5, 0, 4);

        var progressGrid = new Grid();
        progressGrid.Children.Add(_progressTrack);
        progressGrid.Children.Add(_progressLabel);

        Grid.SetRow(_progressTrack, 0);
        Grid.SetRow(_progressLabel, 1);
        Grid.SetRow(progressGrid, 3);
        content.Children.Add(progressGrid);

        return content;
    }

    /// <summary>主页：作者卡片（QQ 头像 / 昵称 / 号码）+ 三步使用指引。</summary>
    private FrameworkElement BuildHomePage()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        Theme.SlimScrollBars(scroll);

        var stack = new StackPanel();

        // ── 作者卡 ──
        var authorCard = Theme.Rounded(Theme.PanelBackgroundLight, 18, new Thickness(20, 16, 20, 16));
        authorCard.Margin = new Thickness(0, 0, 0, 12);

        var authorGrid = new Grid();
        authorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        authorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        authorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // QQ 头像（OnLoaded 里异步加载，失败显示占位「王」）。
        // ⚠️ 不能在构造函数里就开跑：那时窗口的同步上下文还没装好，await 回来会落在
        // 线程池线程上，给 Image.Source 赋值会抛「跨线程访问」异常 —— 头像永远变不回来（2026-09-14 修）。
        var avatarHost = new Grid { Width = 72, Height = 72, VerticalAlignment = VerticalAlignment.Center };

        _avatarFallback = new Border
        {
            Background = Theme.Accent,
            CornerRadius = new CornerRadius(999),
            Child = new TextBlock
            {
                Text = "王",
                FontFamily = Theme.Font,
                FontSize = 30,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        avatarHost.Children.Add(_avatarFallback);

        _avatarImage = new Image { Width = 72, Height = 72, Visibility = Visibility.Collapsed };
        _avatarImage.Clip = new EllipseGeometry(new Rect(0, 0, 72, 72));

        avatarHost.Children.Add(_avatarImage);

        Grid.SetColumn(avatarHost, 0);
        authorGrid.Children.Add(avatarHost);

        var authorTexts = new StackPanel
        {
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        authorTexts.Children.Add(Theme.Label("小王同学", 19, Theme.TextPrimary, FontWeights.Bold));
        authorTexts.Children.Add(new TextBlock
        {
            Text = "模组作者 · QQ " + AuthorQQ,
            FontFamily = Theme.Font,
            FontSize = 13,
            Foreground = Theme.TextMuted,
            Margin = new Thickness(0, 5, 0, 0),
        });

        Grid.SetColumn(authorTexts, 1);
        authorGrid.Children.Add(authorTexts);

        var copyButton = Theme.SubtleButton("复制 QQ", 88);
        copyButton.Height = 34;
        copyButton.VerticalAlignment = VerticalAlignment.Center;
        copyButton.Click += (_, _) => CopyAuthorQQ();
        Grid.SetColumn(copyButton, 2);
        authorGrid.Children.Add(copyButton);

        authorCard.Child = authorGrid;
        stack.Children.Add(authorCard);

        // ── 使用指引 ──
        var guideCard = Theme.Rounded(Theme.PanelBackgroundLight, 18, new Thickness(20, 16, 20, 16));
        guideCard.Child = BuildGuide();
        stack.Children.Add(guideCard);

        scroll.Content = stack;
        return scroll;
    }

    /// <summary>三步指引：装前置 → 挑模组 → 进游戏。</summary>
    private FrameworkElement BuildGuide()
    {
        var stack = new StackPanel();

        stack.Children.Add(Theme.Label("三步开玩", 15, Theme.TextPrimary, FontWeights.Bold));

        stack.Children.Add(BuildGuideStep("1", "装前置", "「前置框架」页一键安装。"));
        stack.Children.Add(BuildGuideStep("2", "挑模组", "「模组仓库」页点「安装」。"));
        stack.Children.Add(BuildGuideStep("3", "进游戏", "启动 PEAK。"));

        return stack;
    }

    private FrameworkElement BuildGuideStep(string number, string title, string detail)
    {
        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(999),
            Background = Theme.Accent,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = number,
                FontFamily = Theme.Font,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        Grid.SetColumn(badge, 0);
        row.Children.Add(badge);

        var texts = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Theme.Label(title, 14, Theme.TextPrimary, FontWeights.SemiBold));
        texts.Children.Add(new TextBlock
        {
            Text = detail,
            FontFamily = Theme.Font,
            FontSize = 12.5,
            Foreground = Theme.TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);

        return row;
    }

    /// <summary>前置框架页：框架状态 + 装/卸。</summary>
    private FrameworkElement BuildFrameworkPage()
    {
        var stack = new StackPanel();

        var card = Theme.Rounded(Theme.PanelBackgroundLight, 18, new Thickness(20, 16, 20, 16));
        card.Margin = new Thickness(0, 0, 0, 12);

        var cardStack = new StackPanel();

        cardStack.Children.Add(Theme.Label("BepInEx 框架", 15, Theme.TextPrimary, FontWeights.Bold));
        cardStack.Children.Add(new TextBlock
        {
            Text = "所有模组的运行前提。没有它，任何模组都不会被游戏加载。",
            FontFamily = Theme.Font,
            FontSize = 12.5,
            Foreground = Theme.TextMuted,
            Margin = new Thickness(0, 4, 0, 10),
        });

        _bepInExRow = new StatusRow();
        cardStack.Children.Add(BuildStatusCard(_bepInExRow));

        _hint = new TextBlock
        {
            FontFamily = Theme.Font,
            FontSize = 12.5,
            Foreground = Theme.TextDim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Text = "正在自动检测 PEAK 的安装位置…",
        };

        cardStack.Children.Add(_hint);
        card.Child = cardStack;
        stack.Children.Add(card);

        // ── 按钮行（这页只管框架：卸载按钮就是卸框架，不再有勾选框）──
        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var note = Theme.Label("模组的安装 / 卸载在「模组仓库」页。", 12.5, Theme.TextDim);
        note.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(note, 0);
        buttons.Children.Add(note);

        _uninstallButton = Theme.DangerButton("卸载框架", 120);
        _uninstallButton.Click += OnUninstallClicked;
        Grid.SetColumn(_uninstallButton, 1);
        buttons.Children.Add(_uninstallButton);

        _frameworkButton = Theme.PrimaryButton("安装 BepInEx 框架", 190);
        _frameworkButton.Margin = new Thickness(12, 0, 0, 0);
        _frameworkButton.Click += OnInstallFrameworkClicked;
        Grid.SetColumn(_frameworkButton, 2);
        buttons.Children.Add(_frameworkButton);

        stack.Children.Add(buttons);

        return stack;
    }

    /// <summary>模组仓库页：提示 + 列表。</summary>
    private FrameworkElement BuildRepoPage()
    {
        var grid = new Grid();

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // 0 提示
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });   // 1 列表

        _repoHint = Theme.Label("正在从服务器读取仓库…", 12.5, Theme.TextDim);
        _repoHint.Margin = new Thickness(4, 0, 0, 8);
        Grid.SetRow(_repoHint, 0);
        grid.Children.Add(_repoHint);

        _repoList = new StackPanel();

        var scroll = new ScrollViewer
        {
            Content = _repoList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 2, 0),
        };

        Theme.SlimScrollBars(scroll);

        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        return grid;
    }

    /// <summary>
    /// 仓库里一张模组卡片：名称 / 在线版本 / 本机状态 / 说明 + 安装、卸载按钮。
    /// ⚠️ 最后必须 card.Child = grid —— 第一版漏了这句，整张卡片渲染成空的。
    /// </summary>
    private FrameworkElement BuildRepoCard(RepoMod mod)
    {
        var card = Theme.Rounded(Theme.PanelBackgroundLight, 16, new Thickness(16, 12, 16, 12));
        card.Margin = new Thickness(0, 0, 0, 10);
        card.MinHeight = 76;

        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = mod.Name,
            FontFamily = Theme.Font,
            FontSize = 15.5,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        });

        titleRow.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(mod.Published) ? " v" + mod.Version : " v" + mod.Version + "（" + mod.Published + "）",
            FontFamily = Theme.Font,
            FontSize = 13,
            Foreground = Theme.TextMuted,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        left.Children.Add(titleRow);

        var status = new TextBlock
        {
            Text = "正在检查本机状态…",
            FontFamily = Theme.Font,
            FontSize = 13,
            Foreground = Theme.TextMuted,
            Margin = new Thickness(0, 4, 0, 0),
        };

        left.Children.Add(status);

        if (!string.IsNullOrWhiteSpace(mod.Notes))
        {
            left.Children.Add(new TextBlock
            {
                Text = mod.Notes.Replace("\r", string.Empty).Replace("\n", " / "),
                FontFamily = Theme.Font,
                FontSize = 12.5,
                Foreground = Theme.TextDim,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 12, 0),
            });
        }

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var installButton = Theme.PrimaryButton("安装", 92);
        installButton.Height = 38;
        installButton.FontSize = 13.5;
        installButton.Margin = new Thickness(0, 0, 8, 0);
        installButton.Click += async (_, _) => await InstallRepoModAsync(mod);
        buttons.Children.Add(installButton);

        var uninstallButton = Theme.DangerButton("卸载", 78);
        uninstallButton.Height = 38;
        uninstallButton.FontSize = 13.5;
        uninstallButton.Click += (_, _) => UninstallRepoMod(mod);
        buttons.Children.Add(uninstallButton);

        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        card.Child = grid;

        // 状态行与按钮文案：知道本机装没装之后再刷一遍。
        card.Tag = new RepoCardState(mod, status, installButton, uninstallButton);
        return card;
    }

    private sealed class RepoCardState
    {
        public RepoCardState(RepoMod mod, TextBlock status, Button installButton, Button uninstallButton)
        {
            Mod = mod;
            Status = status;
            InstallButton = installButton;
            UninstallButton = uninstallButton;
        }

        public RepoMod Mod { get; }

        public TextBlock Status { get; }

        public Button InstallButton { get; }

        public Button UninstallButton { get; }
    }

    private static Border BuildStatusCard(StatusRow row)
    {
        var card = Theme.Rounded(Theme.InsetBackground, 14, new Thickness(14, 8, 14, 8));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = Theme.TextDim,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(row.Dot, 0);
        grid.Children.Add(row.Dot);

        var title = Theme.Label("当前状态", 13.5, Theme.TextPrimary);
        title.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        row.Detail = new TextBlock
        {
            FontFamily = Theme.Font,
            FontSize = 13.5,
            Foreground = Theme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(row.Detail, 2);
        grid.Children.Add(row.Detail);

        card.Child = grid;
        return card;
    }

    private void SwitchPage(int index)
    {
        if (index < 0 || index >= _pages.Count)
        {
            return;
        }

        _currentPage = index;

        for (var i = 0; i < _pages.Count; i++)
        {
            _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        for (var i = 0; i < _navItems.Count; i++)
        {
            var active = i == index;
            _navItems[i].Root.Background = active ? Theme.PanelHighlight : Brushes.Transparent;
            _navItems[i].Bar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            _navItems[i].Text.Foreground = active ? Theme.TextPrimary : Theme.TextMuted;
            _navItems[i].Text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        if (index == 2 && !_repoLoaded && !_busy)
        {
            _ = LoadRepositoryAsync();
        }
    }

    // ── 主页：头像与 QQ ──────────────────────────────────────────

    /// <summary>
    /// 拉作者的 QQ 头像（q1/q2/q3 三个源逐个试）；拿不到就保留占位「王」，不打扰任何流程。
    /// ⚠️ 抓取与解码都在后台线程（ConfigureAwait(false) + Freeze），更新 UI 必须走 Dispatcher ——
    /// 窗口构造函数阶段同步上下文尚未安装，直接赋值会跨线程爆炸。
    /// </summary>
    private async Task LoadAvatarAsync()
    {
        var hosts = new[] { "q1.qlogo.cn", "q2.qlogo.cn", "q3.qlogo.cn" };
        var dispatcher = _avatarImage.Dispatcher;

        foreach (var host in hosts)
        {
            try
            {
                byte[] bytes;

                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(8);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("HextechModInstaller/1.0");

                    bytes = await client
                        .GetByteArrayAsync("https://" + host + "/g?b=qq&nk=" + AuthorQQ + "&s=100")
                        .ConfigureAwait(false);
                }

                if (bytes == null || bytes.Length == 0)
                {
                    continue;
                }

                // 解码在后台线程做，Freeze 之后才能跨线程交给 UI。
                var image = new BitmapImage();

                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();

                await dispatcher.InvokeAsync(() =>
                {
                    _avatarImage.Source = image;
                    _avatarImage.Visibility = Visibility.Visible;
                    _avatarFallback.Visibility = Visibility.Collapsed;
                });

                return;
            }
            catch (Exception exception)
            {
                // 这个源没拉到（超时 / 解码失败），换下一个；全都失败就留「王」字占位。
                AppendLog("头像加载失败（" + host + "）：" + exception.Message, LogLevel.Warn);
            }
        }
    }

    private void CopyAuthorQQ()
    {
        try
        {
            Clipboard.SetText(AuthorQQ);
        }
        catch (Exception)
        {
            // 剪贴板被别的进程占着就算了。
            return;
        }

        FlashStatus("作者 QQ 已复制：" + AuthorQQ);
    }

    /// <summary>在进度条标签上闪一条提示，1.6 秒后恢复。</summary>
    private void FlashStatus(string message)
    {
        var previous = _progressLabel.Text;
        _progressLabel.Text = message;
        _progressLabel.Foreground = Theme.Success;

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _progressLabel.Text = previous;
            _progressLabel.Foreground = Theme.TextDim;
        };
        timer.Start();
    }

    // ── 流程 ─────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DetectGame();
        _ = LoadAvatarAsync();
        await LoadRepositoryAsync();
    }

    /// <summary>
    /// 拉仓库清单（version.json 主模组 + repository.json 其它模组），重建仓库页卡片。
    /// 失败只记一行日志 —— 离线、服务器维护都不该挡着框架安装。
    /// </summary>
    private async Task LoadRepositoryAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "正在读取仓库…");
        _repoHint.Text = "正在从服务器读取仓库…";
        _repoHint.Foreground = Theme.TextDim;

        try
        {
            _repoMods.Clear();
            _repoMods.AddRange(await ModRepository.FetchAsync(_cts.Token).ConfigureAwait(true));

            _repoLoaded = true;

            AppendLog(
                _repoMods.Count == 0
                    ? "仓库清单是空的（服务器还没上架任何模组）。"
                    : $"仓库共 {_repoMods.Count} 个模组：{string.Join("、", DescribeRepoMods())}",
                LogLevel.Success);

            _repoHint.Text = _repoMods.Count == 0
                ? "仓库暂时是空的。"
                : "点「安装」把模组放进游戏的 BepInEx\\plugins（框架没装会自动先补框架）。";
            _repoHint.Foreground = Theme.TextDim;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            _repoHint.Text = "仓库读取失败（可以点「刷新仓库」重试；不影响装框架）。";
            _repoHint.Foreground = Theme.Warning;
            AppendLog(
                "读取模组仓库失败：" + UpdateFeed.Redact(exception.Message),
                LogLevel.Warn);
        }
        finally
        {
            RebuildRepoList();
            RefreshStatus();
            SetBusy(false);
        }
    }

    private IEnumerable<string> DescribeRepoMods()
    {
        foreach (var mod in _repoMods)
        {
            yield return mod.Name + " v" + mod.Version;
        }
    }

    /// <summary>按最新清单重建仓库卡片，并把每张卡的本机状态刷一遍。</summary>
    private void RebuildRepoList()
    {
        _repoList.Children.Clear();

        if (_gameDirectory == null)
        {
            _repoHint.Text = "先在上方指定游戏目录，再安装模组。";
            return;
        }

        foreach (var mod in _repoMods)
        {
            var card = BuildRepoCard(mod);
            _repoList.Children.Add(card);
            RefreshRepoCard(card);
        }
    }

    private void RefreshRepoCard(FrameworkElement card)
    {
        if (card.Tag is not RepoCardState state || _gameDirectory == null)
        {
            return;
        }

        var status = _engine.QueryModFile(_gameDirectory, RepoFileName(state.Mod), state.Mod.Version);
        state.Status.Text = status.Detail;
        state.Status.Foreground = status.Installed ? Theme.Success : Theme.TextMuted;

        var upToDate = status.Installed && status.Detail.IndexOf("已是最新", StringComparison.Ordinal) >= 0;
        state.InstallButton.Content = status.Installed ? (upToDate ? "重装" : "更新") : "安装";
        state.UninstallButton.Visibility = status.Installed ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>仓库条目的 dll 字段要么是文件名要么是完整地址，落盘文件名取最后一段。</summary>
    private static string RepoFileName(RepoMod mod)
    {
        var name = (mod.Dll ?? string.Empty).Trim();

        if (Uri.TryCreate(name, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.AbsolutePath))
        {
            name = IOPath.GetFileName(uri.AbsolutePath);
        }

        return string.IsNullOrWhiteSpace(name) ? "mod-" + mod.Id + ".dll" : name;
    }

    private async Task InstallRepoModAsync(RepoMod mod)
    {
        if (_gameDirectory == null || _busy)
        {
            return;
        }

        if (!EnsureGameClosed())
        {
            return;
        }

        SetBusy(true, $"准备安装 {mod.Name}…");
        AppendLog($"── 安装 {mod.Name} v{mod.Version} ──", LogLevel.Info);

        var progress = new Progress<InstallProgress>(OnProgress);

        try
        {
            await _engine
                .InstallModAsync(_gameDirectory, mod.ToRelease(), RepoFileName(mod), progress, _cts.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AppendLog("操作已取消。", LogLevel.Warn);
        }
        catch (Exception exception)
        {
            AppendLog(exception.Message, LogLevel.Error);
            MessageBox.Show(this, exception.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
            RefreshAllRepoCards();
        }
    }

    private void UninstallRepoMod(RepoMod mod)
    {
        if (_gameDirectory == null || _busy)
        {
            return;
        }

        if (!EnsureGameClosed())
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"确定卸载 {mod.Name} 吗？（只删这一个模组，保留框架和其它模组）",
            "确认卸载",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, $"正在卸载 {mod.Name}…");

        try
        {
            var removed = _engine.UninstallModFile(_gameDirectory, RepoFileName(mod));

            AppendLog(
                removed ? $"{mod.Name} 已卸载。" : "没有找到它的插件文件，可能已经卸过了。",
                removed ? LogLevel.Success : LogLevel.Warn);
        }
        catch (Exception exception)
        {
            AppendLog(exception.Message, LogLevel.Error);
            MessageBox.Show(this, exception.Message, "卸载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
            RefreshAllRepoCards();
        }
    }

    private void RefreshAllRepoCards()
    {
        foreach (var child in _repoList.Children)
        {
            if (child is FrameworkElement element)
            {
                RefreshRepoCard(element);
            }
        }
    }

    private async void OnInstallFrameworkClicked(object sender, RoutedEventArgs e)
    {
        if (_gameDirectory == null || _busy)
        {
            return;
        }

        // 游戏还在跑的话，装到一半文件写不进去 —— 先挡住。
        if (!EnsureGameClosed())
        {
            return;
        }

        SetBusy(true, "准备安装框架…");
        AppendLog("── 安装 BepInEx 框架 ──", LogLevel.Info);

        var progress = new Progress<InstallProgress>(OnProgress);

        try
        {
            await _engine.InstallFrameworkAsync(_gameDirectory, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("操作已取消。", LogLevel.Warn);
        }
        catch (Exception exception)
        {
            AppendLog(exception.Message, LogLevel.Error);
            MessageBox.Show(this, exception.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
        }
    }

    private void DetectGame()
    {
        AppendLog("正在查找 PEAK 的安装位置…", LogLevel.Info);

        var found = GameLocator.Locate();

        if (found == null)
        {
            _gameDirectory = null;
            _pathText.Text = "未检测到，请手动选择 PEAK.exe";
            _pathText.Foreground = Theme.TextDim;
            _hint.Text = "没有自动找到 PEAK，请点上面的「浏览…」手动指定游戏目录。";
            _hint.Foreground = Theme.Warning;
            AppendLog("自动检测失败，请手动选择游戏目录。", LogLevel.Warn);
        }
        else
        {
            ApplyGameDirectory(found);
        }

        RefreshStatus();
        UpdateButtons();
    }

    private void ApplyGameDirectory(string directory)
    {
        _gameDirectory = directory;
        _pathText.Text = directory;
        _pathText.Foreground = Theme.TextPrimary;

        // 提示行统一交给 RefreshStatus 按当前状态写，免得两边各写一句互相盖掉。
        AppendLog("游戏目录：" + directory, LogLevel.Success);
    }

    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 PEAK.exe",
            Filter = "PEAK 游戏程序|PEAK.exe|可执行文件|*.exe",
            CheckFileExists = true,
        };

        if (_gameDirectory != null && IODirectory.Exists(_gameDirectory))
        {
            dialog.InitialDirectory = _gameDirectory;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var directory = IOPath.GetDirectoryName(dialog.FileName);

        // 用户可能选到了 steamapps 一层，往回收敛成真正的游戏根目录。
        var normalized = GameLocator.Normalize(directory);

        if (normalized == null)
        {
            _hint.Text = "这个目录看起来不是 PEAK 的安装目录，请选到 PEAK.exe 那一层。";
            _hint.Foreground = Theme.Danger;
            AppendLog("选中的目录里没有找到 PEAK_Data，请重新选择。", LogLevel.Error);
            return;
        }

        ApplyGameDirectory(normalized);
        RefreshStatus();
        UpdateButtons();
        RefreshAllRepoCards();
    }

    /// <summary>
    /// 有 PEAK 进程挡着就先处理掉，返回 false 表示这次操作不该继续。
    /// <para>
    /// 分两种情况：真能看见游戏窗口的，只能让玩家自己退；只剩没有窗口的残留进程的，
    /// 问一句要不要顺手结束掉 —— 后者是玩家最常撞上的卡点：游戏上次退出时卡住了，
    /// 任务管理器默认视图里根本看不到它，玩家只会觉得「我明明没开游戏」。
    /// </para>
    /// </summary>
    private bool EnsureGameClosed()
    {
        var running = InstallEngine.FindRunningGames(_gameDirectory);

        if (running.Count == 0)
        {
            return true;
        }

        // 只要还有一个带窗口的进程就是在真的玩游戏，不替玩家做决定。
        foreach (var game in running)
        {
            if (!game.HasWindow)
            {
                continue;
            }

            var where = string.IsNullOrWhiteSpace(game.Path)
                ? string.Empty
                : Environment.NewLine + game.Path;

            AppendLog("检测到 PEAK 正在运行，本次操作已阻止。", LogLevel.Warn);
            MessageBox.Show(
                this,
                "检测到 PEAK 正在运行，请先完全退出游戏再操作。" + where,
                "游戏正在运行",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return false;
        }

        var answer = MessageBox.Show(
            this,
            "检测到 PEAK 的残留进程（没有游戏窗口，多半是上次退出时卡住了）。" + Environment.NewLine + Environment.NewLine +
            "继续操作需要先结束它，现在结束吗？",
            "结束残留进程",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            AppendLog("残留的 PEAK 进程还在，操作已取消。", LogLevel.Warn);
            return false;
        }

        foreach (var game in running)
        {
            if (game.TryKill(5000))
            {
                AppendLog($"已结束残留进程 PEAK.exe（PID {game.Id}）。", LogLevel.Success);
            }
            else
            {
                AppendLog($"结束 PEAK.exe（PID {game.Id}）失败，请在任务管理器里手动结束。", LogLevel.Error);
            }
        }

        // 结束完再看一眼：没走干净就别硬着头皮去写文件。
        if (InstallEngine.FindRunningGames(_gameDirectory).Count > 0)
        {
            MessageBox.Show(
                this,
                "残留进程没能结束掉（可能被安全软件拦着）。请在任务管理器 → 详细信息里手动结束 PEAK.exe 后再试。",
                "无法结束进程",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            return false;
        }

        return true;
    }

    /// <summary>
    /// 卸载 BepInEx 框架（前置框架页专用）。框架目录里的模组会一并删除 ——
    /// 没有框架它们本来也跑不起来；不是本安装器装的框架则拒绝删除。
    /// </summary>
    private void OnUninstallClicked(object sender, RoutedEventArgs e)
    {
        if (_gameDirectory == null || _busy)
        {
            return;
        }

        // 卸载同理：dll 被游戏占着就删不掉。
        if (!EnsureGameClosed())
        {
            return;
        }

        // 删框架会把别人装的 mod 一起废掉，先问一句。
        var foreign = InstallEngine.FindForeignPlugins(_gameDirectory);

        if (foreign.Count > 0)
        {
            var names = string.Join("、", foreign.Count > 5
                ? new[] { string.Join("、", System.Linq.Enumerable.Take(foreign, 5)) + " 等 " + foreign.Count + " 个" }
                : foreign);

            var answer = MessageBox.Show(
                this,
                "plugins 目录里除了本安装器装的模组，还有别的插件：" + Environment.NewLine + names + Environment.NewLine + Environment.NewLine +
                "移除 BepInEx 框架会让这些插件一起失效，确定继续吗？",
                "确认移除框架",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }
        else
        {
            var answer = MessageBox.Show(
                this,
                "确定卸载 BepInEx 框架吗？" + Environment.NewLine +
                "框架目录里的模组（含仓库装的）会一并删除。",
                "确认卸载框架",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        SetBusy(true, "正在卸载框架…");
        AppendLog("── 卸载 BepInEx 框架 ──", LogLevel.Info);

        try
        {
            _engine.Uninstall(_gameDirectory, removeBepInEx: true);
            MessageBox.Show(this, "卸载完成。", "小王同学模组安装器", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            AppendLog(exception.Message, LogLevel.Error);
            MessageBox.Show(this, exception.Message, "卸载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
            RefreshAllRepoCards();
        }
    }

    private void OnProgress(InstallProgress progress)
    {
        var ratio = progress.Ratio;

        if (ratio < 0)
        {
            ratio = 0;
        }
        else if (ratio > 1)
        {
            ratio = 1;
        }

        _progressFill.Width = _progressTrack.ActualWidth * ratio;
        _progressLabel.Text = progress.Message;
    }

    private void RefreshStatus()
    {
        if (_gameDirectory == null)
        {
            ApplyRow(_bepInExRow, false, "需要先指定游戏目录", Theme.TextDim);
            return;
        }

        var bepInEx = _engine.QueryBepInEx(_gameDirectory);
        ApplyRow(_bepInExRow, bepInEx.Installed, bepInEx.Detail, bepInEx.Installed ? Theme.Success : Theme.Warning);

        // Mod Manager 把框架托管在 profile 里时，游戏目录里看不到 BepInEx 文件夹，
        // 而那份框架从 Steam 直接启动是加载不到的 —— 这是玩家最常撞上的坑：
        // 状态行说「已安装」、从 Steam 启动却没效果。说明白，并告诉他点安装能修好。
        if (!bepInEx.Installed && InstallEngine.HasModManagerBepInEx(_gameDirectory))
        {
            _hint.Text =
                "检测到雷霆商店 / r2modman 的 BepInEx（在它的 profile 里）：那份只有从 Mod Manager "
                + "启动才生效，从 Steam 启动等于没装。点「安装 BepInEx 框架」会把框架补到游戏目录。";
            _hint.Foreground = Theme.Warning;
        }
        else if (bepInEx.Installed)
        {
            _hint.Text = "框架已就绪。模组去「模组仓库」页安装。";
            _hint.Foreground = Theme.Success;
        }
        else
        {
            _hint.Text = "游戏目录已就绪。";
            _hint.Foreground = Theme.Success;
        }
    }

    private static void ApplyRow(StatusRow row, bool installed, string detail, Brush color)
    {
        row.Dot.Fill = color;
        row.Detail.Text = detail;
        row.Detail.Foreground = installed ? color : Theme.TextMuted;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;

        if (message != null)
        {
            _progressLabel.Text = message;
        }

        if (!busy)
        {
            _progressFill.Width = 0;
            _progressLabel.Text = "就绪";
            _progressLabel.Foreground = Theme.TextDim;
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var ready = !_busy && _gameDirectory != null;

        _frameworkButton.IsEnabled = ready;
        _uninstallButton.IsEnabled = ready;
        _browseButton.IsEnabled = !_busy;
        _refreshRepoButton.IsEnabled = !_busy;
    }

    private void AppendLog(string message, LogLevel level)
    {
        // 安装逻辑跑在后台线程，日志得 marshal 回 UI 线程再动控件。
        if (!Dispatcher.CheckAccess())
        {
            // 装到一半把窗口关了的话，Dispatcher 已经拆了，别再往上排队。
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                Dispatcher.Invoke(() => AppendLog(message, level));
            }
            catch (TaskCanceledException)
            {
                // 窗口正好在这一刻关掉，日志丢了就丢了。
            }
            catch (InvalidOperationException)
            {
                // 同上：Dispatcher 已不可用。
            }

            return;
        }

        Brush color;
        string prefix;

        switch (level)
        {
            case LogLevel.Success:
                color = Theme.Success;
                prefix = "✓  ";
                break;
            case LogLevel.Warn:
                color = Theme.Warning;
                prefix = "!  ";
                break;
            case LogLevel.Error:
                color = Theme.Danger;
                prefix = "✕  ";
                break;
            default:
                color = Theme.TextMuted;
                prefix = "·  ";
                break;
        }

        _log.Document.Blocks.Add(new Paragraph(new Run(prefix + message) { Foreground = color })
        {
            Margin = new Thickness(0, 0, 0, 4),
            LineHeight = 18,
        });

        // 日志留最后 200 条就够了，多了只是占内存。
        while (_log.Document.Blocks.Count > 200)
        {
            _log.Document.Blocks.Remove(_log.Document.Blocks.FirstBlock);
        }

        _log.ScrollToEnd();
    }
}
