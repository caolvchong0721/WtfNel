using System.Windows;
using System.Windows.Media.Animation;

namespace WtfNel.Ui.Controls;

/// <summary>CSS-style cubic-bezier easing used for the Material 3 motion tokens.</summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    public static readonly DependencyProperty X1Property = DependencyProperty.Register(nameof(X1), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(0.2));
    public static readonly DependencyProperty Y1Property = DependencyProperty.Register(nameof(Y1), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(0.0));
    public static readonly DependencyProperty X2Property = DependencyProperty.Register(nameof(X2), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(0.0));
    public static readonly DependencyProperty Y2Property = DependencyProperty.Register(nameof(Y2), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(1.0));

    public CubicBezierEase()
    {
        EasingMode = EasingMode.EaseIn;
    }

    public CubicBezierEase(double x1, double y1, double x2, double y2) : this()
    {
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
    }

    /// <summary>MD3 emphasized decelerate: elements entering the screen.</summary>
    public static CubicBezierEase EmphasizedDecelerate { get; } = Frozen(0.05, 0.7, 0.1, 1.0);

    /// <summary>MD3 emphasized accelerate: elements leaving the screen.</summary>
    public static CubicBezierEase EmphasizedAccelerate { get; } = Frozen(0.3, 0.0, 0.8, 0.15);

    /// <summary>MD3 standard: small utility transitions that stay on screen.</summary>
    public static CubicBezierEase Standard { get; } = Frozen(0.2, 0.0, 0.0, 1.0);

    public double X1 { get => (double)GetValue(X1Property); set => SetValue(X1Property, value); }
    public double Y1 { get => (double)GetValue(Y1Property); set => SetValue(Y1Property, value); }
    public double X2 { get => (double)GetValue(X2Property); set => SetValue(X2Property, value); }
    public double Y2 { get => (double)GetValue(Y2Property); set => SetValue(Y2Property, value); }

    protected override double EaseInCore(double normalizedTime)
    {
        double x = Math.Clamp(normalizedTime, 0, 1);
        double x1 = X1, y1 = Y1, x2 = X2, y2 = Y2;
        double t = x;
        for (int i = 0; i < 8; i++)
        {
            double error = Bezier(t, x1, x2) - x;
            if (Math.Abs(error) < 1e-5) break;
            double slope = Slope(t, x1, x2);
            if (Math.Abs(slope) < 1e-6) break;
            t = Math.Clamp(t - error / slope, 0, 1);
        }
        double lo = 0, hi = 1;
        for (int i = 0; i < 20 && Math.Abs(Bezier(t, x1, x2) - x) > 1e-5; i++)
        {
            if (Bezier(t, x1, x2) < x) lo = t; else hi = t;
            t = (lo + hi) / 2;
        }
        return Bezier(t, y1, y2);
    }

    protected override Freezable CreateInstanceCore() => new CubicBezierEase();

    private static double Bezier(double t, double p1, double p2)
    {
        double u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }

    private static double Slope(double t, double p1, double p2)
    {
        double u = 1 - t;
        return 3 * u * u * p1 + 6 * u * t * (p2 - p1) + 3 * t * t * (1 - p2);
    }

    private static CubicBezierEase Frozen(double x1, double y1, double x2, double y2)
    {
        var ease = new CubicBezierEase(x1, y1, x2, y2);
        ease.Freeze();
        return ease;
    }
}
