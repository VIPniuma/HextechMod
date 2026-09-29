using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace PeakModder.HextechInstaller;

/// <summary>
/// 深色主题（2026-09-14 先做了浅色，2026-09-19 用户要求改回深色）。
/// 真正的「磨砂」由 <see cref="Frost.EnableAcrylic(System.Windows.Window)"/> 在窗口句柄上
/// 开启系统的深色亚克力模糊；这里提供叠在上面的半透明暗蓝灰底色与浅色控件。
/// <para>
/// 颜色名沿用浅色时代的命名（PanelBackground 之类没改），但语义现在是「暗蓝灰半透明」。
/// 所有界面都引用 Theme.*，换肤只动这一处。
/// </para>
/// </summary>
internal static class Theme
{
    // ── 面板底色（全部带 alpha，叠在系统磨砂上；深色主题：暗蓝灰半透明）────────────────
    public static readonly SolidColorBrush PanelBackground = Frozen("#C81E222B");
    public static readonly SolidColorBrush PanelBackgroundLight = Frozen("#E2242933");
    public static readonly SolidColorBrush PanelHighlight = Frozen("#E22C3340");
    public static readonly SolidColorBrush HeaderBackground = Frozen("#B31E222B");
    public static readonly SolidColorBrush SidebarBackground = Frozen("#A61A1E26");
    public static readonly SolidColorBrush InsetBackground = Frozen("#661A1E26");
    public static readonly SolidColorBrush Divider = Frozen("#FF2E3540");
    public static readonly SolidColorBrush Outline = Frozen("#FF3A4250");

    // ── 文字与强调色（深色主题：浅色字）─────────────────────────────────────────
    public static readonly SolidColorBrush TextPrimary = Frozen("#FFF2F5FA");
    public static readonly SolidColorBrush TextMuted = Frozen("#FFAEB6C2");
    public static readonly SolidColorBrush TextDim = Frozen("#FF7A828F");
    public static readonly SolidColorBrush Accent = Frozen("#FFD97706");
    public static readonly SolidColorBrush AccentHover = Frozen("#FFF59E0B");
    public static readonly SolidColorBrush Success = Frozen("#FF3DDC8C");
    public static readonly SolidColorBrush Warning = Frozen("#FFE0902B");
    public static readonly SolidColorBrush Danger = Frozen("#FFF06A6A");

    public static readonly FontFamily Font = new("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

    private const string ButtonTemplateXaml =
        "<ControlTemplate TargetType='Button'" +
        " xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
        " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
        "  <Border x:Name='Root' CornerRadius='16' Background='{TemplateBinding Background}' Padding='24,0'" +
        "          BorderBrush='#14FFFFFF' BorderThickness='0,0,0,1'>" +
        "    <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'" +
        "                      TextElement.Foreground='{TemplateBinding Foreground}' />" +
        "  </Border>" +
        "  <ControlTemplate.Triggers>" +
        "    <Trigger Property='IsMouseOver' Value='True'>" +
        "      <Setter TargetName='Root' Property='Opacity' Value='0.86' />" +
        "    </Trigger>" +
        "    <Trigger Property='IsPressed' Value='True'>" +
        "      <Setter TargetName='Root' Property='Opacity' Value='0.68' />" +
        "    </Trigger>" +
        "    <Trigger Property='IsEnabled' Value='False'>" +
        "      <Setter TargetName='Root' Property='Opacity' Value='0.36' />" +
        "    </Trigger>" +
        "  </ControlTemplate.Triggers>" +
        "</ControlTemplate>";

    private const string CheckBoxTemplateXaml =
        "<ControlTemplate TargetType='CheckBox'" +
        " xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
        " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
        "  <StackPanel Orientation='Horizontal' Background='Transparent'>" +
        "    <Border x:Name='Box' Width='17' Height='17' CornerRadius='8'" +
        "            Background='#402C3340' BorderBrush='#FF5A6373' BorderThickness='1'" +
        "            VerticalAlignment='Center'>" +
        "      <Path x:Name='Tick' Data='M 0,5 L 4.5,9.5 L 11.5,1.5' Stroke='#FFD97706' StrokeThickness='2.2'" +
        "            StrokeStartLineCap='Round' StrokeEndLineCap='Round' Visibility='Collapsed'" +
        "            HorizontalAlignment='Center' VerticalAlignment='Center' />" +
        "    </Border>" +
        "    <ContentPresenter Margin='9,0,0,0' VerticalAlignment='Center'" +
        "                      TextElement.Foreground='{TemplateBinding Foreground}' />" +
        "  </StackPanel>" +
        "  <ControlTemplate.Triggers>" +
        "    <Trigger Property='IsChecked' Value='True'>" +
        "      <Setter TargetName='Box' Property='BorderBrush' Value='#FFD97706' />" +
        "    </Trigger>" +
        "    <Trigger Property='IsMouseOver' Value='True'>" +
        "      <Setter TargetName='Box' Property='BorderBrush' Value='#FF8A94A3' />" +
        "    </Trigger>" +
        "    <Trigger Property='IsEnabled' Value='False'>" +
        "      <Setter Property='Opacity' Value='0.4' />" +
        "    </Trigger>" +
        "  </ControlTemplate.Triggers>" +
        "</ControlTemplate>";

    /// <summary>日志框的滚动条：浅色界面配一根灰色细条。</summary>
    private const string ScrollBarStyleXaml =
        "<Style TargetType='ScrollBar'" +
        " xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
        " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
        "  <Setter Property='Width' Value='9' />" +
        "  <Setter Property='Background' Value='Transparent' />" +
        "  <Setter Property='Template'>" +
        "    <Setter.Value>" +
        "      <ControlTemplate TargetType='ScrollBar'>" +
        "        <Grid Background='Transparent'>" +
        "          <Track x:Name='PART_Track' IsDirectionReversed='True'>" +
        "            <Track.DecreaseRepeatButton>" +
        "              <RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False' />" +
        "            </Track.DecreaseRepeatButton>" +
        "            <Track.IncreaseRepeatButton>" +
        "              <RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False' />" +
        "            </Track.IncreaseRepeatButton>" +
        "            <Track.Thumb>" +
        "              <Thumb>" +
        "                <Thumb.Template>" +
        "                  <ControlTemplate TargetType='Thumb'>" +
        "                    <Border Background='#FF616B7A' CornerRadius='4' Margin='1,2' />" +
        "                  </ControlTemplate>" +
        "                </Thumb.Template>" +
        "              </Thumb>" +
        "            </Track.Thumb>" +
        "          </Track>" +
        "        </Grid>" +
        "      </ControlTemplate>" +
        "    </Setter.Value>" +
        "  </Setter>" +
        "</Style>";

    private const string FlatButtonTemplateXaml =
        "<ControlTemplate TargetType='Button'" +
        " xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
        " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
        "  <Border x:Name='Root' Background='{TemplateBinding Background}' CornerRadius='12'>" +
        "    <ContentPresenter x:Name='Body' HorizontalAlignment='Center' VerticalAlignment='Center'" +
        "                      TextElement.Foreground='{TemplateBinding Foreground}' />" +
        "  </Border>" +
        "  <ControlTemplate.Triggers>" +
        "    <Trigger Property='IsMouseOver' Value='True'>" +
        "      <Setter TargetName='Root' Property='Background' Value='#FFD64545' />" +
        "      <Setter TargetName='Body' Property='TextElement.Foreground' Value='#FFFFFFFF' />" +
        "    </Trigger>" +
        "    <Trigger Property='IsPressed' Value='True'>" +
        "      <Setter TargetName='Root' Property='Opacity' Value='0.72' />" +
        "    </Trigger>" +
        "  </ControlTemplate.Triggers>" +
        "</ControlTemplate>";

    private static ControlTemplate? _buttonTemplate;
    private static ControlTemplate? _checkBoxTemplate;
    private static ControlTemplate? _flatButtonTemplate;
    private static Style? _scrollBarStyle;

    /// <summary>模板是共享的，解析一次就够了。</summary>
    private static ControlTemplate ButtonTemplate =>
        _buttonTemplate ??= (ControlTemplate)XamlReader.Parse(ButtonTemplateXaml);

    private static ControlTemplate CheckBoxTemplate =>
        _checkBoxTemplate ??= (ControlTemplate)XamlReader.Parse(CheckBoxTemplateXaml);

    public static Style ScrollBarStyle =>
        _scrollBarStyle ??= (Style)XamlReader.Parse(ScrollBarStyleXaml);

    /// <summary>把一个容器里所有滚动条都换成细条版本。</summary>
    public static void SlimScrollBars(FrameworkElement element)
        => element.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), ScrollBarStyle);

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static TextBlock Label(
        string text,
        double size,
        Brush? foreground = null,
        FontWeight? weight = null)
    {
        return new TextBlock
        {
            Text = InstallerLocalization.T(text),
            FontFamily = Font,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = foreground ?? TextPrimary,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static Border Rounded(Brush background, double radius, Thickness? padding = null)
    {
        return new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(radius),
            Padding = padding ?? new Thickness(0),
        };
    }

    public static Button Button(string text, Brush background, Brush foreground, double height = 44, double minWidth = 0)
    {
        var button = new Button
        {
            Content = InstallerLocalization.T(text),
            Template = ButtonTemplate,
            Background = background,
            Foreground = foreground,
            BorderThickness = new Thickness(0),
            Height = height,
            MinWidth = minWidth,
            FontFamily = Font,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = null,
        };

        return button;
    }

    /// <summary>主操作：琥珀色实心。</summary>
    public static Button PrimaryButton(string text, double minWidth = 160)
        => Button(text, Accent, Brushes.White, 46, minWidth);

    /// <summary>次操作：浅灰底 + 危险色文字。</summary>
    public static Button DangerButton(string text, double minWidth = 130)
        => Button(text, PanelHighlight, Danger, 46, minWidth);

    /// <summary>小号次要按钮（浏览… 之类）。</summary>
    public static Button SubtleButton(string text, double minWidth = 0)
        => Button(text, PanelHighlight, TextPrimary, 34, minWidth);

    /// <summary>
    /// 分段选择器（语言切换这类「几选一」用）：圆角容器里并排几颗扁平按钮，选中那颗点亮成主题色。
    /// <para>
    /// 切换语言会整个重建窗口，所以选中态不需要动态刷新 —— 传对 selectedIndex 就行。
    /// 按钮沿用 <see cref="ButtonTemplate"/> 的圆角外观，未选中的做成透明底、悬停才浮起来，
    /// 免得像个突兀的灰方块贴在卡片里。
    /// </para>
    /// </summary>
    public static Border Segmented(IReadOnlyList<string> options, int selectedIndex, Action<int> onSelected)
    {
        var container = new Border
        {
            Background = InsetBackground,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var selected = i == selectedIndex;

            var button = new Button
            {
                Content = options[i],
                Template = ButtonTemplate,
                Background = selected ? Accent : Brushes.Transparent,
                Foreground = selected ? Brushes.White : TextMuted,
                BorderThickness = new Thickness(0),
                Height = 34,
                MinWidth = 98,
                Padding = new Thickness(14, 0, 14, 0),
                FontFamily = Font,
                FontSize = 13.5,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                Cursor = System.Windows.Input.Cursors.Hand,
                FocusVisualStyle = null,
                Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0),
            };

            var idle = button.Background;
            var hovered = selected ? AccentHover : PanelHighlight;

            button.MouseEnter += (_, _) => button.Background = hovered;
            button.MouseLeave += (_, _) => button.Background = idle;
            button.Click += (_, _) => onSelected(index);

            row.Children.Add(button);
        }

        container.Child = row;
        return container;
    }

    public static CheckBox CheckBox(string text, double fontSize = 13.5)
        => new()
        {
            Content = InstallerLocalization.T(text),
            Template = CheckBoxTemplate,
            Foreground = TextMuted,
            FontFamily = Font,
            FontSize = fontSize,
            Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = null,
        };

    /// <summary>标题栏右上角的关闭按钮：平时透明，悬停变红。</summary>
    public static Button CloseButton(string glyph)
        => new()
        {
            Content = glyph,
            Template = _flatButtonTemplate ??= (ControlTemplate)XamlReader.Parse(FlatButtonTemplateXaml),
            Background = Brushes.Transparent,
            Foreground = TextMuted,
            BorderThickness = new Thickness(0),
            Width = 56,
            Height = 44,
            FontFamily = Font,
            FontSize = 14,
            Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = null,
        };
}

/// <summary>
/// 给无边框 WPF 窗口开系统的「亚克力磨砂」背景（Win10 1803+ / Win11）。
/// 老系统开不上就静默放弃 —— 窗口底色本身是半透明白，没有模糊也能看。
/// </summary>
internal static class Frost
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private enum WindowCompositionAttribute
    {
        WCA_ACCENT_POLICY = 19,
    }

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// 给窗口叠一层「深色磨砂」。GradientColor 是 ABGR —— 暗蓝灰（RGB 0x1E,0x22,0x2B）约 78% 不透明。
    /// 失败（老系统 / 被组策略关了）完全无感，界面照样是半透明暗底。
    /// </summary>
    public static void EnableAcrylic(System.Windows.Window window, byte alpha = 0xC8)
    {
        try
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(window);

            if (helper.Handle == IntPtr.Zero)
            {
                return;
            }

            var accent = new AccentPolicy
            {
                AccentState = (int)AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                // AccentFlags = 2：让磨砂也盖住标题栏区域（窗口本来就无边框）。
                AccentFlags = 2,
                // ABGR：高字节 alpha，随后 B=2B、G=22、R=1E。
                GradientColor = ((uint)alpha << 24) | 0x002B221E,
            };

            var size = Marshal.SizeOf(accent);
            var pointer = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(accent, pointer, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = pointer,
                SizeOfData = size,
            };

            try
            {
                _ = SetWindowCompositionAttribute(helper.Handle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch (Exception)
        {
            // 开不上就算了：半透明暗底本身就是兜底方案。
        }
    }
}
