using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PeakModder.HextechConfigurator;

/// <summary>配置器的轻量主题（与安装器同一套浅色视觉，独立一份避免互相牵连）。</summary>
internal static class Theme
{
    public static readonly FontFamily Font = new("Microsoft YaHei UI");

    public static readonly Color AccentColor = Color.FromRgb(0x27, 0xA0, 0x5B);
    public static readonly Brush Accent = new SolidColorBrush(AccentColor);
    public static readonly Brush TextPrimary = new SolidColorBrush(Color.FromRgb(0x1F, 0x27, 0x33));
    public static readonly Brush TextMuted = new SolidColorBrush(Color.FromRgb(0x66, 0x70, 0x85));
    public static readonly Brush TextDim = new SolidColorBrush(Color.FromRgb(0x98, 0xA2, 0xB3));
    public static readonly Brush PanelBackground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8));
    public static readonly Brush PanelBackgroundLight = new SolidColorBrush(Color.FromRgb(0xF7, 0xF9, 0xFC));
    public static readonly Brush Outline = new SolidColorBrush(Color.FromRgb(0xE3, 0xE8, 0xF0));
    public static readonly Brush Success = Accent;
    public static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(0xE6, 0xA2, 0x3C));
    public static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xF5, 0x6C, 0x6C));

    static Theme()
    {
        Accent.Freeze();
        TextPrimary.Freeze();
        TextMuted.Freeze();
        TextDim.Freeze();
        PanelBackground.Freeze();
        PanelBackgroundLight.Freeze();
        Outline.Freeze();
        Success.Freeze();
        Warning.Freeze();
        Danger.Freeze();
    }

    public static Label Label(string text, double size, Brush color, FontWeight? weight = null)
    {
        return new Label
        {
            Content = text,
            FontFamily = Font,
            FontSize = size,
            Foreground = color,
            FontWeight = weight ?? FontWeights.Normal,
            Padding = new Thickness(0),
        };
    }

    /// <summary>圆角卡片。</summary>
    public static Border Rounded(Brush background, double cornerRadius, Thickness padding)
    {
        return new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(cornerRadius),
            Padding = padding,
        };
    }

    /// <summary>主按钮（实心绿）。</summary>
    public static Button PrimaryButton(string text, double minWidth)
    {
        return new Button
        {
            Content = text,
            FontFamily = Font,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Background = Accent,
            BorderThickness = new Thickness(0),
            MinWidth = minWidth,
            Height = 36,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
    }

    /// <summary>次按钮（浅色描边）。</summary>
    public static Button SubtleButton(string text, double minWidth)
    {
        return new Button
        {
            Content = text,
            FontFamily = Font,
            FontSize = 13,
            Foreground = TextPrimary,
            Background = Brushes.White,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            MinWidth = minWidth,
            Height = 36,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
    }

    /// <summary>数字输入框（右对齐，交给调用方做解析与钳制）。</summary>
    public static TextBox NumberBox(string text)
    {
        return new TextBox
        {
            Text = text,
            FontFamily = Font,
            FontSize = 13,
            Foreground = TextPrimary,
            Background = Brushes.White,
            BorderBrush = Outline,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Height = 30,
        };
    }
}
