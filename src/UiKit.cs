using System;
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
            b.Padding = new Thickness(0, 10, 0, 10);
            b.BorderBrush = Br("LineBrush");
            b.BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1);
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
            Root = sv;
        }

        public virtual void Update(SystemSample s) { }
        public virtual void Shown() { }
    }
}
