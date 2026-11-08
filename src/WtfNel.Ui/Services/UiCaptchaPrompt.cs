using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WtfNel.Core.Auth.Login;

namespace WtfNel.Ui.Services;

/// <summary>Shows 4399 captchas as in-window dialogs: type the text, drag the jigsaw slider, or click the characters in order.</summary>
public sealed class UiCaptchaPrompt : ICaptchaPrompt
{
    public string Image(byte[] image) =>
        OnUi(() => ImageAsync(image)) ?? throw new OperationCanceledException("已取消验证码输入");

    public int Jigsaw(byte[] background, byte[] piece) =>
        OnUi(() => JigsawAsync(background, piece)) ?? throw new OperationCanceledException("已取消滑块验证");

    public IReadOnlyList<(int X, int Y)> Click(byte[] image, string text) =>
        OnUi(() => ClickAsync(image, text)) ?? throw new OperationCanceledException("已取消点选验证");

    private static T OnUi<T>(Func<Task<T>> show)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) throw new InvalidOperationException("验证码提示不能在界面线程上同步等待");
        return dispatcher.Invoke(show).GetAwaiter().GetResult();
    }

    private static async Task<string?> ImageAsync(byte[] data)
    {
        var bitmap = Load(data);
        var box = new TextBox { Tag = "输入图片中的字符", Margin = new Thickness(0, 14, 0, 0) };
        box.Loaded += (_, _) => box.Focus();
        var body = new StackPanel
        {
            Children =
            {
                Hint("4399 要求输入验证码。"),
                Frame(new Image { Source = bitmap, Height = Math.Min(bitmap.PixelHeight * 2, 90), Stretch = Stretch.Uniform }),
                box,
            },
        };
        var pick = await Dialogs.ShowAsync("输入验证码", body, ["取消", "确定"], DialogKind.Warning,
            validate: i => i != 1 || box.Text.Trim().Length > 0);
        return pick == 1 ? box.Text.Trim() : null;
    }

    private static async Task<int?> JigsawAsync(byte[] backgroundData, byte[] pieceData)
    {
        var background = Load(backgroundData);
        var piece = Load(pieceData);
        int width = background.PixelWidth, height = background.PixelHeight;
        var canvas = new Canvas { Width = width, Height = height, ClipToBounds = true };
        canvas.Children.Add(new Image { Source = background, Width = width, Height = height, Stretch = Stretch.Fill });
        var pieceImage = new Image { Source = piece, Width = piece.PixelWidth, Height = piece.PixelHeight, Stretch = Stretch.Fill };
        canvas.Children.Add(pieceImage);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = piece.PixelWidth < width ? width - piece.PixelWidth : width,
            Margin = new Thickness(0, 14, 0, 0),
            SmallChange = 1,
            LargeChange = 10,
        };
        var readout = Hint("位移 0 px");
        slider.ValueChanged += (_, e) =>
        {
            Canvas.SetLeft(pieceImage, e.NewValue);
            readout.Text = $"位移 {(int)Math.Round(e.NewValue)} px";
        };
        slider.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Left) { slider.Value -= 1; e.Handled = true; }
            if (e.Key == Key.Right) { slider.Value += 1; e.Handled = true; }
        };

        var body = new StackPanel
        {
            Children =
            {
                Hint("拖动滑块，把拼图块移到缺口处。方向键可以 1 像素微调。"),
                Frame(new Viewbox { Child = canvas, MaxWidth = 460, Stretch = Stretch.Uniform }),
                slider,
                readout,
            },
        };
        var pick = await Dialogs.ShowAsync("滑块验证", body, ["取消", "提交"], DialogKind.Warning, width: 520);
        return pick == 1 ? (int)Math.Round(slider.Value) : null;
    }

    private static async Task<IReadOnlyList<(int X, int Y)>?> ClickAsync(byte[] data, string text)
    {
        var bitmap = Load(data);
        var words = text.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int need = Math.Max(words.Length, 1);
        var points = new List<(int X, int Y)>();

        var canvas = new Canvas { Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, Background = Brushes.Transparent, Cursor = Cursors.Cross, ClipToBounds = true };
        canvas.Children.Add(new Image { Source = bitmap, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight, Stretch = Stretch.Fill, IsHitTestVisible = false });
        var progress = Hint("");

        void Refresh() => progress.Text = points.Count < need
            ? $"下一个：「{(points.Count < words.Length ? words[points.Count] : text)}」  （{points.Count}/{need}）"
            : $"已选 {points.Count}/{need}，可以提交了";

        canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (points.Count >= need) return;
            var p = e.GetPosition(canvas);
            points.Add(((int)Math.Round(p.X), (int)Math.Round(p.Y)));
            var marker = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = Dialogs.Res("Accent"),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
                Child = new TextBlock { Text = points.Count.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Canvas.SetLeft(marker, p.X - 12);
            Canvas.SetTop(marker, p.Y - 12);
            canvas.Children.Add(marker);
            Refresh();
        };

        var reset = new Button { Content = "重选", Style = (Style)Application.Current.FindResource("Btn.Ghost"), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(10, 4, 10, 4) };
        reset.Click += (_, _) =>
        {
            points.Clear();
            while (canvas.Children.Count > 1) canvas.Children.RemoveAt(canvas.Children.Count - 1);
            Refresh();
        };
        Refresh();

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(reset, Dock.Right);
        footer.Children.Add(reset);
        footer.Children.Add(progress);

        var body = new StackPanel
        {
            Children =
            {
                Hint($"按顺序点击：{string.Join("  ", words.Length > 0 ? words : [text])}"),
                Frame(new Viewbox { Child = canvas, MaxWidth = 460, Stretch = Stretch.Uniform }),
                footer,
            },
        };
        var pick = await Dialogs.ShowAsync("点选验证", body, ["取消", "提交"], DialogKind.Warning, width: 520,
            validate: i =>
            {
                if (i != 1 || points.Count >= need) return true;
                Dialogs.Toast($"还差 {need - points.Count} 个字没点", DialogKind.Warning);
                return false;
            });
        return pick == 1 ? points : null;
    }

    private static BitmapImage Load(byte[] data)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(data);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12.5,
        Foreground = Dialogs.Res("TextSecondary"),
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static Border Frame(UIElement child) => new()
    {
        CornerRadius = new CornerRadius(8),
        BorderBrush = Dialogs.Res("Stroke"),
        BorderThickness = new Thickness(1),
        Background = Dialogs.Res("SubtleFill"),
        Padding = new Thickness(6),
        HorizontalAlignment = HorizontalAlignment.Center,
        Child = child,
    };
}
