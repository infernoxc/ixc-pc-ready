using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace InfernoXC
{
    /// <summary>Small helpers to build consistent UI in code.</summary>
    public static class UK
    {
        public static Style S(string key) { return (Style)Application.Current.Resources[key]; }
        public static Brush Br(string key) { return (Brush)Application.Current.Resources[key]; }

        public static TextBlock T(string text, string style)
        {
            TextBlock t = new TextBlock();
            t.Text = text;
            t.Style = S(style);
            return t;
        }

        public static Button Btn(string text, bool primary, RoutedEventHandler h)
        {
            Button b = new Button();
            b.Content = text;
            b.Style = S(primary ? "PrimaryBtn" : "GhostBtn");
            b.Margin = new Thickness(0, 0, 8, 0);
            if (h != null) b.Click += h;
            return b;
        }

        /// <summary>A button that steps through a fixed list of choices each time it is clicked. Used instead of a drop-down so it matches the dark theme.</summary>
        public static Button Cycle(string label, string[] options, int start, Action<int> changed)
        {
            Button b = new Button();
            b.Style = S("GhostBtn");
            b.Margin = new Thickness(0, 0, 8, 0);
            int[] idx = new int[] { Math.Max(0, Math.Min(options.Length - 1, start)) };
            b.Content = label + options[idx[0]];
            b.Click += delegate
            {
                idx[0] = (idx[0] + 1) % options.Length;
                b.Content = label + options[idx[0]];
                if (changed != null) changed(idx[0]);
            };
            return b;
        }

        public static ToggleButton Sw(bool on)
        {
            ToggleButton t = new ToggleButton();
            t.Style = S("Switch");
            t.IsChecked = on;
            return t;
        }

        public static Border Card(UIElement c)
        {
            Border b = new Border();
            b.Style = S("Card");
            b.Child = c;
            b.Margin = new Thickness(0, 0, 0, 12);
            return b;
        }

        public static Expander Section(string title, string sub, UIElement content, bool expanded, ToggleButton master)
        {
            Expander e = new Expander();
            e.Style = S("Section");
            Grid h = new Grid();
            h.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            h.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel sp = new StackPanel();
            sp.Children.Add(T(title, "H2"));
            if (!string.IsNullOrEmpty(sub))
            {
                TextBlock m = T(sub, "Muted");
                m.Margin = new Thickness(0, 2, 0, 0);
                sp.Children.Add(m);
                e.Tag = m;
            }
            h.Children.Add(sp);
            if (master != null)
            {
                Grid.SetColumn(master, 1);
                h.Children.Add(master);
            }
            e.Header = h;
            e.Content = content;
            e.IsExpanded = expanded;
            return e;
        }

        /// <summary>A list row: title + detail on the left, optional right-hand text, optional control (switch / button).</summary>
        public static Border Row(string title, string detail, TextBlock right, UIElement ctl, bool last)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel sp = new StackPanel();
            TextBlock a = T(title, "Body");
            a.FontWeight = FontWeights.SemiBold;
            a.TextTrimming = TextTrimming.CharacterEllipsis;
            a.TextWrapping = TextWrapping.NoWrap;
            sp.Children.Add(a);
            if (!string.IsNullOrEmpty(detail))
            {
                TextBlock d = T(detail, "Muted");
                d.Margin = new Thickness(0, 2, 0, 0);
                sp.Children.Add(d);
            }
            sp.Margin = new Thickness(0, 0, 14, 0);
            g.Children.Add(sp);
            if (right != null)
            {
                right.VerticalAlignment = VerticalAlignment.Center;
                right.Margin = new Thickness(0, 0, 16, 0);
                right.TextAlignment = TextAlignment.Right;
                Grid.SetColumn(right, 1);
                g.Children.Add(right);
            }
            if (ctl != null)
            {
                FrameworkElement fe = ctl as FrameworkElement;
                if (fe != null) fe.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(ctl, 2);
                g.Children.Add(ctl);
            }
            Border b = new Border();
            b.Child = g;
            b.Padding = new Thickness(10, 10, 10, 10);
            b.BorderBrush = Br("LineBrush");
            b.BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1);
            return b;
        }

        /// <summary>Small coloured label. level: Low / Medium / High (risk) or Good / Warn / Bad / Info.</summary>
        public static Border Chip(string text, string level)
        {
            string key = level == "Low" || level == "Good" ? "GoodBrush" : level == "Medium" || level == "Warn" ? "WarnBrush" : level == "High" || level == "Bad" ? "BadBrush" : "MutedBrush";
            SolidColorBrush c = (SolidColorBrush)Br(key);
            Border b = new Border();
            b.CornerRadius = new CornerRadius(3);
            b.Padding = new Thickness(6, 1, 6, 2);
            b.Margin = new Thickness(0, 0, 6, 0);
            b.Background = new SolidColorBrush(Color.FromArgb(40, c.Color.R, c.Color.G, c.Color.B));
            b.BorderBrush = new SolidColorBrush(Color.FromArgb(120, c.Color.R, c.Color.G, c.Color.B));
            b.BorderThickness = new Thickness(1);
            TextBlock t = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = c, VerticalAlignment = VerticalAlignment.Center };
            b.Child = t;
            return b;
        }

        /// <summary>Safe / Caution / Risky chip for a risk word, plus an optional recommendation chip.</summary>
        public static StackPanel RiskChips(string risk, string rec)
        {
            StackPanel p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            if (!string.IsNullOrEmpty(risk)) p.Children.Add(Chip(risk == "Low" ? "SAFE" : risk == "Medium" ? "CAUTION" : "RISKY", risk));
            if (!string.IsNullOrEmpty(rec)) p.Children.Add(Chip(rec.ToUpperInvariant(), rec.StartsWith("Recommended") || rec.StartsWith("Noticeable") || rec.StartsWith("Stability") || rec.StartsWith("Aim") ? "Good" : rec.StartsWith("Not") || rec.StartsWith("Avoid") ? "Bad" : "Info"));
            return p;
        }

        /// <summary>Like Row, but the title line is followed by coloured risk and recommendation chips.</summary>
        public static Border Row(string title, string detail, string risk, string rec, TextBlock right, UIElement ctl, bool last)
        {
            Border b = Row(title, detail, right, ctl, last);
            Grid g = (Grid)b.Child;
            StackPanel sp = (StackPanel)g.Children[0];
            sp.Children.Add(RiskChips(risk, rec));
            return b;
        }

        public static TextBlock Right(string text)
        {
            TextBlock t = T(text, "Muted");
            t.TextWrapping = TextWrapping.NoWrap;
            return t;
        }

        /// <summary>Run work on a thread-pool thread, then deliver the result on the UI thread.</summary>
        public static void Bg(Func<object> work, Action<object> done)
        {
            System.Windows.Threading.Dispatcher d = Application.Current.Dispatcher;
            ThreadPool.QueueUserWorkItem(delegate
            {
                object r = null;
                try { r = work(); } catch (Exception ex) { Log.Err(ex); }
                d.BeginInvoke(new Action(delegate
                {
                    try { if (done != null) done(r); } catch (Exception ex) { Log.Err(ex); }
                }));
            });
        }

        public static void UI(Action a)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(delegate
            {
                try { a(); } catch (Exception ex) { Log.Err(ex); }
            }));
        }
    }

    /// <summary>Lays children out in as many equal columns as fit (each at least MinItemWidth), wrapping to new rows. Used so the dashboard works from phone-narrow to ultrawide.</summary>
    public class FlowGrid : Panel
    {
        public double MinItemWidth = 200, Gap = 12;
        int Cols(double w, int n)
        {
            int c = (int)Math.Floor((w + Gap) / (MinItemWidth + Gap));
            if (c < 1) c = 1; if (c > n) c = n;
            return c;
        }
        protected override Size MeasureOverride(Size avail)
        {
            double w = double.IsInfinity(avail.Width) ? 800 : avail.Width;
            int n = InternalChildren.Count; if (n == 0) return new Size(0, 0);
            int cols = Cols(w, n); double cw = (w - Gap * (cols - 1)) / cols;
            double total = 0, rowH = 0;
            for (int i = 0; i < n; i++)
            {
                UIElement c = InternalChildren[i];
                c.Measure(new Size(cw, double.PositiveInfinity));
                rowH = Math.Max(rowH, c.DesiredSize.Height);
                if (i % cols == cols - 1 || i == n - 1) { total += rowH + (i == n - 1 ? 0 : Gap); rowH = 0; }
            }
            return new Size(w, total);
        }
        protected override Size ArrangeOverride(Size fin)
        {
            int n = InternalChildren.Count; if (n == 0) return fin;
            int cols = Cols(fin.Width, n); double cw = (fin.Width - Gap * (cols - 1)) / cols;
            double y = 0; int start = 0;
            while (start < n)
            {
                int end = Math.Min(n, start + cols); double h = 0;
                for (int i = start; i < end; i++) h = Math.Max(h, InternalChildren[i].DesiredSize.Height);
                for (int i = start; i < end; i++) InternalChildren[i].Arrange(new Rect((i - start) * (cw + Gap), y, cw, h));
                y += h + Gap; start = end;
            }
            return fin;
        }
        /// <summary>Replaces a UniformGrid / Grid in its parent with a FlowGrid holding the same children.</summary>
        public static FlowGrid Replace(Panel old, double minWidth, double gap)
        {
            FlowGrid f = new FlowGrid { MinItemWidth = minWidth, Gap = gap, Margin = old.Margin };
            UIElement[] kids = old.Children.Cast<UIElement>().ToArray();
            old.Children.Clear();
            foreach (UIElement k in kids) { FrameworkElement fe = k as FrameworkElement; if (fe != null) fe.Margin = new Thickness(0); Grid.SetColumn(k, 0); f.Children.Add(k); }
            Panel parent = (Panel)old.Parent;
            int idx = parent.Children.IndexOf(old);
            parent.Children.RemoveAt(idx);
            parent.Children.Insert(idx, f);
            return f;
        }
    }

    public abstract class PageBase : IPage
    {
        public FrameworkElement Root { get; private set; }
        protected StackPanel Body;

        protected PageBase(string title, string sub)
        {
            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Body = new StackPanel();
            Body.Margin = new Thickness(32, 28, 32, 28);
            Body.Children.Add(UK.T(title, "H1"));
            TextBlock s = UK.T(sub, "Muted");
            s.Margin = new Thickness(0, 4, 0, 18);
            Body.Children.Add(s);
            sv.Content = Body;
            sv.SizeChanged += delegate { double w = sv.ActualWidth; double m = w < 560 ? 12 : w < 800 ? 20 : 32; Body.Margin = new Thickness(m, w < 560 ? 16 : 26, m, 24); };
            Root = sv;
        }

        public virtual void Update(SystemSample s) { }
        public virtual void Shown() { }
    }
}
