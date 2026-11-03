using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace InfernoXC
{
    /// <summary>
    /// Circular gauge drawn directly, no animation timers: it only redraws when a new reading arrives,
    /// so it costs nothing while the window is idle or minimised.
    /// </summary>
    public class Gauge : FrameworkElement
    {
        double _frac;
        string _big = "-", _label = "", _small = "";
        Brush _brush;
        bool _ok;
        double _shown;

        public void Set(string label, double value, double max, string big, string small, Brush brush, bool ok)
        {
            _label = label; _big = big; _small = small; _ok = ok;
            _brush = brush;
            _frac = ok && max > 0 ? Math.Max(0, Math.Min(1, value / max)) : 0;
            InvalidateVisual();
        }

        static Point Pt(Point c, double r, double deg)
        {
            double a = deg * Math.PI / 180.0;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        static Geometry Arc(Point c, double r, double start, double sweep)
        {
            if (sweep < 0.6) sweep = 0.6;
            if (sweep > 269.9) sweep = 269.9;
            StreamGeometry g = new StreamGeometry();
            using (StreamGeometryContext ctx = g.Open())
            {
                ctx.BeginFigure(Pt(c, r, start), false, false);
                ctx.ArcTo(Pt(c, r, start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        FormattedText Txt(string s, double size, Brush b, bool bold)
        {
            Typeface tf = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
            return new FormattedText(s ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 20 || h < 20) return;
            double size = Math.Min(w, h);
            Point c = new Point(w / 2, h / 2 + size * 0.04);
            double r = size / 2 - 18;
            double thick = Math.Max(6, size * 0.055);

            Brush line = (Brush)Application.Current.Resources["LineBrush"];
            Brush text = (Brush)Application.Current.Resources["TextBrush"];
            Brush muted = (Brush)Application.Current.Resources["MutedBrush"];

            Pen track = new Pen(line, thick); track.StartLineCap = PenLineCap.Round; track.EndLineCap = PenLineCap.Round;
            dc.DrawGeometry(null, track, Arc(c, r, 135, 269.9));
            if (_ok && _brush != null && _frac > 0.001)
            {
                Pen p = new Pen(_brush, thick); p.StartLineCap = PenLineCap.Round; p.EndLineCap = PenLineCap.Round;
                dc.DrawGeometry(null, p, Arc(c, r, 135, 269.9 * _frac));
            }

            FormattedText big = Txt(_big, size * 0.20, _ok ? text : muted, true);
            dc.DrawText(big, new Point(c.X - big.Width / 2, c.Y - big.Height / 2 - size * 0.075));
            FormattedText lab = Txt(_label, size * 0.07, muted, false);
            lab.MaxTextWidth = size * 0.62; lab.MaxLineCount = 1; lab.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(lab, new Point(c.X - lab.Width / 2, c.Y + size * 0.085));
            if (!string.IsNullOrEmpty(_small))
            {
                FormattedText sm = Txt(_small, size * 0.056, muted, false);
                sm.MaxTextWidth = size * 0.46; sm.MaxLineCount = 2; sm.TextAlignment = TextAlignment.Center; sm.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(sm, new Point(c.X - size * 0.23, c.Y + size * 0.165));
            }
        }
    }
}
