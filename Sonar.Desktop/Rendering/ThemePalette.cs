using System.Windows.Media;

namespace Sonar.Rendering;

/// <summary>与 XAML 主题一致的颜色/字体令牌，供后台构建 FlowDocument 使用。</summary>
public static class ThemePalette
{
    public static readonly Brush Bg = Make("#0A0A0C");
    public static readonly Brush Card = Make("#141418");
    public static readonly Brush CardHover = Make("#1D1D23");
    public static readonly Brush Border = Make("#26262E");
    public static readonly Brush Text = Make("#E9E9ED");
    public static readonly Brush TextDim = Make("#9A9AA6");
    public static readonly Brush TextMuted = Make("#66666F");
    public static readonly Brush Accent = Make("#E11D2E");
    public static readonly Brush AccentHover = Make("#F52B3D");
    public static readonly Brush AccentSoft = Make("#2A0D11");
    public static readonly Brush AccentBorder = Make("#5E1A22");
    public static readonly Brush CodeBg = Make("#1B1B21");
    public static readonly Brush Success = Make("#3DDC84");
    public static readonly Brush Warning = Make("#FFB020");
    public static readonly Brush Error = Make("#FF4D5E");

    public static readonly FontFamily UiFont = new("Microsoft YaHei UI");
    public static readonly FontFamily MonoFont = new("Cascadia Mono");

    public static Brush Make(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex)!;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
