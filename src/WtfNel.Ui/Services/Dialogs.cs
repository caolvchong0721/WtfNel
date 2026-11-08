using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using WtfNel.Ui.Controls;

namespace WtfNel.Ui.Services;

public enum DialogKind { None, Info, Success, Warning, Error, Question }

/// <summary>In-window WinUI-style content dialogs and info toasts, animated with Material 3 motion.</summary>
public static class Dialogs
{
    private static readonly SemaphoreSlim Queue = new(1, 1);
    private static Grid? _layer;
    private static Panel? _toasts;

    public static void Attach(Grid layer, Panel toasts)
    {
        _layer = layer;
        _toasts = toasts;
    }

    /// <summary>Shows a modal dialog and returns the index of the clicked button, or -1 when dismissed.</summary>
    public static async Task<int> ShowAsync(string title, object body, string[] buttons, DialogKind kind = DialogKind.None,
        int primary = -1, bool danger = false, double width = 448, Func<int, bool>? validate = null, bool dismissible = true)
    {
        if (_layer is null) return -1;
        await Queue.WaitAsync();
        try
        {
            return await ShowCoreAsync(title, body, buttons, primary < 0 ? buttons.Length - 1 : primary, danger, width, validate, dismissible);
        }
        finally
        {
            Queue.Release();
        }
    }

    public static Task AlertAsync(string title, string message, DialogKind kind = DialogKind.Info) =>
        ShowAsync(title, message, ["确定"], kind);

    public static async Task<bool> ConfirmAsync(string title, string message, string confirm = "确定", bool danger = false) =>
        await ShowAsync(title, message, ["取消", confirm], DialogKind.Question, danger: danger) == 1;

    public static async Task<string?> PromptAsync(string title, string hint, string initial = "")
    {
        var box = new TextBox { Text = initial, Tag = hint };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        var result = await ShowAsync(title, box, ["取消", "确定"], validate: i => i != 1 || box.Text.Trim().Length > 0);
        return result == 1 ? box.Text.Trim() : null;
    }

    public static void Toast(string message, DialogKind kind = DialogKind.Info)
    {
        if (_toasts is null) return;
        var (icon, brush) = Visual(kind);
        var shift = new TranslateTransform(0, 24);
        var card = new Border
        {
            Background = SoftOf(kind),
            BorderBrush = Res("Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 11, 16, 12),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 400,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.1 },
            RenderTransform = shift,
            Opacity = 0,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new SvgIcon { Data = icon, Foreground = brush, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) },
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 340, Foreground = Res("TextPrimary"), FontSize = 14 },
                },
            },
        };
        _toasts.Children.Add(card);
        card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, Ms(200)) { EasingFunction = CubicBezierEase.Standard });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Ms(500)) { EasingFunction = CubicBezierEase.EmphasizedDecelerate });

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, Ms(200)) { EasingFunction = CubicBezierEase.EmphasizedAccelerate };
            fade.Completed += (_, _) => _toasts.Children.Remove(card);
            card.BeginAnimation(UIElement.OpacityProperty, fade);
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, Ms(200)) { EasingFunction = CubicBezierEase.EmphasizedAccelerate });
        };
        timer.Start();
    }

    private static Task<int> ShowCoreAsync(string title, object body, string[] buttons, int primary, bool danger,
        double width, Func<int, bool>? validate, bool dismissible)
    {
        var layer = _layer!;
        var done = new TaskCompletionSource<int>();
        var previousFocus = Keyboard.FocusedElement as IInputElement;

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            FontFamily = (FontFamily)Application.Current.FindResource("DisplayFont"),
            Foreground = Res("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };

        UIElement content = body as UIElement ?? new ScrollViewer
        {
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = body?.ToString() ?? "",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                LineHeight = 20,
                Foreground = Res("TextPrimary"),
            },
        };

        var row = new UniformGrid { Rows = 1, Columns = buttons.Length };
        var footer = new Border
        {
            Background = Res("DialogFooter"),
            BorderBrush = Res("Stroke"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 8, 8),
            Padding = new Thickness(24),
            Child = row,
        };
        var scale = new ScaleTransform(0.92, 0.92);
        var card = new Border
        {
            Width = width,
            MaxWidth = 720,
            Background = Res("CardBg"),
            BorderBrush = Res("Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 48, ShadowDepth = 12, Direction = 270, Opacity = 0.2 },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
            Opacity = 0,
            Child = new StackPanel
            {
                Children =
                {
                    new StackPanel { Margin = new Thickness(24), Children = { heading, content } },
                    footer,
                },
            },
        };
        var scrim = new Border { Background = Res("Overlay"), Opacity = 0 };
        var root = new Grid { Children = { scrim, card } };

        void Close(int index)
        {
            if (done.Task.IsCompleted) return;
            if (index >= 0 && validate is not null && !validate(index)) return;
            done.TrySetResult(index);
            root.IsHitTestVisible = false;
            var exit = new DoubleAnimation(0, Ms(150)) { EasingFunction = CubicBezierEase.EmphasizedAccelerate };
            exit.Completed += (_, _) =>
            {
                layer.Children.Remove(root);
                if (layer.Children.Count == 0) layer.Visibility = Visibility.Collapsed;
                previousFocus?.Focus();
            };
            var shrink = new DoubleAnimation(0.96, Ms(150)) { EasingFunction = CubicBezierEase.EmphasizedAccelerate };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, Ms(150)) { EasingFunction = CubicBezierEase.EmphasizedAccelerate });
            scrim.BeginAnimation(UIElement.OpacityProperty, exit);
        }

        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Content = buttons[i],
                Margin = new Thickness(i == 0 ? 0 : 4, 0, i == buttons.Length - 1 ? 0 : 4, 0),
                IsDefault = i == primary,
                IsCancel = dismissible && buttons.Length > 1 && i == 0,
            };
            if (i == primary) button.Style = (Style)Application.Current.FindResource(danger ? "Btn.Danger" : "Btn.Primary");
            button.Click += (_, _) => Close(index);
            row.Children.Add(button);
        }

        if (dismissible)
        {
            root.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                Close(-1);
            };
        }

        layer.Children.Add(root);
        layer.Visibility = Visibility.Visible;
        scrim.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(200)) { EasingFunction = CubicBezierEase.Standard });
        card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(200)) { EasingFunction = CubicBezierEase.Standard });
        var grow = new DoubleAnimation(0.92, 1, Ms(450)) { EasingFunction = CubicBezierEase.EmphasizedDecelerate };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        card.Loaded += (_, _) =>
        {
            if (card.IsKeyboardFocusWithin) return;
            if (row.Children.Count > 0) ((UIElement)row.Children[Math.Clamp(primary, 0, row.Children.Count - 1)]).Focus();
        };
        return done.Task;
    }

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static (Geometry Icon, Brush Brush) Visual(DialogKind kind) => kind switch
    {
        DialogKind.Success => (Geo("I.Check"), Res("Success")),
        DialogKind.Warning => (Geo("I.Alert"), Res("Warning")),
        DialogKind.Error => (Geo("I.Alert"), Res("Danger")),
        _ => (Geo("I.Info"), Res("Accent")),
    };

    private static Brush SoftOf(DialogKind kind) => kind switch
    {
        DialogKind.Success => Res("SuccessSoft"),
        DialogKind.Warning => Res("WarningSoft"),
        DialogKind.Error => Res("DangerSoft"),
        _ => Res("AccentSoft"),
    };

    internal static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    internal static Geometry Geo(string key) => (Geometry)Application.Current.FindResource(key);
}
