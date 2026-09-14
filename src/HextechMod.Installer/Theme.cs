using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace PeakModder.HextechInstaller;

/// <summary>
/// 磨砂白浅色主题（2026-09-14 用户要求：半透明磨砂白 + 浅色界面）。
/// 真正的「磨砂」由 <see cref="Frost.EnableAcrylic(System.Windows.Window)"/> 在窗口句柄上
/// 开启系统的亚克力模糊；这里只提供叠在上面的半透明白色底色与浅色控件。
/// <para>
/// ⚠️ 旧版是照 mod 的深色面板抄的，这次整体换成浅色 —— 颜色名没改
/// （PanelBackground 之类沿用），但语义从「深棕」变成了「半透明白」。
/// </para>
/// </summary>
internal static class Theme
{
    // ── 面板底色（全部带 alpha，叠在系统磨砂上）────────────────
    public static readonly SolidColorBrush PanelBackground = Frozen("#C8F7F9FC");
    public static readonly SolidColorBrush PanelBackgroundLight = Frozen("#D9FFFFFF");
    public static readonly SolidColorBrush PanelHighlight = Frozen("#F2FFFFFF");
    public static readonly SolidColorBrush HeaderBackground = Frozen("#B3FFFFFF");
    public static readonly SolidColorBrush SidebarBackground = Frozen("#A6FFFFFF");
    public static readonly SolidColorBrush InsetBackground = Frozen("#E6F3F6FB");
    public static readonly SolidColorBrush Divider = Frozen("#FFE1E6EE");
    public static readonly SolidColorBrush Outline = Frozen("#FFD5DCE6");

    // ── 文字与强调色 ──────────────────────────────────────────
    public static readonly SolidColorBrush TextPrimary = Frozen("#FF1A2233");
    public static readonly SolidColorBrush TextMuted = Frozen("#FF525E6E");
    public static readonly SolidColorBrush TextDim = Frozen("#FF8B95A3");
    public static readonly SolidColorBrush Accent = Frozen("#FFD97706");
    public static readonly SolidColorBrush AccentHover = Frozen("#FFF59E0B");
    public static readonly SolidColorBrush Success = Frozen("#FF1F9D55");
    public static readonly SolidColorBrush Warning = Frozen("#FFB45309");
    public static readonly SolidColorBrush Danger = Frozen("#FFD64545");

    public static readonly FontFamily Font = new("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

    private const string ButtonTemplateXaml =
        "<ControlTemplate TargetType='Button'" +
        " xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
        " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
        "  <Border x:Name='Root' CornerRadius='16' Background='{TemplateBinding Background}' Padding='24,0'" +
        "          BorderBrush='#14000000' BorderThickness='0,0,0,1'>" +
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
        "            Background='#66FFFFFF' BorderBrush='#FFB9C2CF' BorderThickness='1'" +
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
        "                    <Border Background='#FFC2CAD6' CornerRadius='4' Margin='1,2' />" +
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
            Text = text,
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
            Content = text,
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

    public static CheckBox CheckBox(string text, double fontSize = 13.5)
        => new()
        {
            Content = text,
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
    /// 给窗口叠一层「白色磨砂」。GradientColor 是 ABGR —— 白色 60% 不透明 = 0x99FFFFFF。
    /// 失败（老系统 / 被组策略关了）完全无感，界面照样是半透明白底。
    /// </summary>
    public static void EnableAcrylic(System.Windows.Window window, byte alpha = 0x99)
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
                GradientColor = ((uint)alpha << 24) | 0x00FFFFFF,
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
            // 开不上就算了：半透明白底本身就是兜底方案。
        }
    }
}
