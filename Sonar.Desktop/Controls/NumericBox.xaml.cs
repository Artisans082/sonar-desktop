using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Sonar.Controls;

public partial class NumericBox : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(decimal), typeof(NumericBox),
        new FrameworkPropertyMetadata(0m, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(decimal), typeof(NumericBox),
        new PropertyMetadata(0m, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(decimal), typeof(NumericBox),
        new PropertyMetadata(100m, OnRangeChanged));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(decimal), typeof(NumericBox),
        new PropertyMetadata(1m));

    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(
        nameof(Decimals), typeof(int), typeof(NumericBox),
        new PropertyMetadata(0, OnFormatChanged));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(NumericBox),
        new PropertyMetadata("", OnFormatChanged));

    public event EventHandler? ValueChanged;

    public NumericBox()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshText();
    }

    public decimal Value
    {
        get => (decimal)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public decimal Minimum
    {
        get => (decimal)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public decimal Maximum
    {
        get => (decimal)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public decimal Step
    {
        get => (decimal)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public int Decimals
    {
        get => (int)GetValue(DecimalsProperty);
        set => SetValue(DecimalsProperty, value);
    }

    public string Suffix
    {
        get => (string)GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    public int IntValue => (int)Math.Round(Value);
    public double DoubleValue => (double)Value;

    public void SetValueSilently(decimal value)
    {
        var clamped = Math.Clamp(value, Minimum, Maximum);
        if (Value == clamped)
        {
            RefreshText();
            return;
        }
        SetCurrentValue(ValueProperty, clamped);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not NumericBox box) return;
        box.RefreshText();
        box.ValueChanged?.Invoke(box, EventArgs.Empty);
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not NumericBox box) return;
        box.SetCurrentValue(ValueProperty,
            Math.Clamp(box.Value, box.Minimum, box.Maximum));
    }

    private static void OnFormatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not NumericBox box) return;
        box.SuffixText.Text = box.Suffix;
        box.RefreshText();
    }

    private void RefreshText()
    {
        if (ValueBox.IsKeyboardFocusWithin) return;
        ValueBox.Text = Format(Value);
        SuffixText.Text = Suffix;
    }

    private string Format(decimal value) =>
        Decimals <= 0
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("F" + Decimals, CultureInfo.InvariantCulture);

    private void CommitText()
    {
        if (decimal.TryParse(ValueBox.Text.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed))
        {
            SetCurrentValue(ValueProperty, Math.Clamp(parsed, Minimum, Maximum));
        }
        RefreshText();
    }

    private void OnValueBoxLostFocus(object sender, RoutedEventArgs e) => CommitText();

    private void OnValueBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            Nudge(Step);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            Nudge(-Step);
            e.Handled = true;
        }
    }

    private void Nudge(decimal delta)
    {
        SetCurrentValue(ValueProperty, Math.Clamp(Value + delta, Minimum, Maximum));
        RefreshText();
    }

    private void OnUpClick(object sender, RoutedEventArgs e) => Nudge(Step);

    private void OnDownClick(object sender, RoutedEventArgs e) => Nudge(-Step);

    /// <summary>输入框获得焦点 / 失焦时切换边框颜色。</summary>
    public void SetFocusVisual(bool focused)
    {
        Box.BorderBrush = focused
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("BorderBrush");
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SetFocusVisual(true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!IsKeyboardFocusWithin) SetFocusVisual(false);
    }
}
