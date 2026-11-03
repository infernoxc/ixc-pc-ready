using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace InfernoXC
{
    /// <summary>Dark, theme-matched replacement for MessageBox so dialogs never flash a white box with mismatched fonts.</summary>
    public static class Dlg
    {
        public static MessageBoxResult Show(string text, string title, MessageBoxButton buttons, MessageBoxImage icon)
        {
            MessageBoxResult result = buttons == MessageBoxButton.YesNo || buttons == MessageBoxButton.YesNoCancel ? MessageBoxResult.No : MessageBoxResult.OK;
            try
            {
                Window w = new Window();
                w.Title = title;
                w.Background = UK.Br("BgBrush");
                w.SizeToContent = SizeToContent.WidthAndHeight;
                w.MaxWidth = 560; w.MinWidth = 340; w.MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 80);
                w.ResizeMode = ResizeMode.NoResize;
                w.ShowInTaskbar = false;
                w.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
                Window owner = Application.Current == null ? null : Application.Current.MainWindow;
                if (owner != null && owner.IsVisible) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
                else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

                Grid g = new Grid { Margin = new Thickness(22, 20, 22, 18) };
                g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                string key = icon == MessageBoxImage.Error || icon == MessageBoxImage.Warning ? (icon == MessageBoxImage.Error ? "BadBrush" : "WarnBrush") : "AccentBrush";
                Border bar = new Border { Width = 4, Background = UK.Br(key), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2, 14, 2) };
                TextBlock t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = UK.Br("TextBrush"), FontSize = 13.5, MaxWidth = 470, LineHeight = 20 };
                ScrollViewer sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = t };
                DockPanel dp = new DockPanel();
                DockPanel.SetDock(bar, Dock.Left);
                dp.Children.Add(bar); dp.Children.Add(sv);
                g.Children.Add(dp);

                StackPanel bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
                Grid.SetRow(bp, 1);
                Action<string, MessageBoxResult, bool> add = delegate(string label, MessageBoxResult r, bool primary)
                {
                    Button b = UK.Btn(label, primary, delegate { result = r; w.Close(); });
                    b.Margin = new Thickness(8, 0, 0, 0); b.MinWidth = 76;
                    if (primary) b.IsDefault = true;
                    bp.Children.Add(b);
                };
                if (buttons == MessageBoxButton.YesNo) { add("No", MessageBoxResult.No, false); add("Yes", MessageBoxResult.Yes, true); }
                else if (buttons == MessageBoxButton.YesNoCancel) { add("Cancel", MessageBoxResult.Cancel, false); add("No", MessageBoxResult.No, false); add("Yes", MessageBoxResult.Yes, true); }
                else if (buttons == MessageBoxButton.OKCancel) { add("Cancel", MessageBoxResult.Cancel, false); add("OK", MessageBoxResult.OK, true); }
                else add("OK", MessageBoxResult.OK, true);
                g.Children.Add(bp);
                w.Content = g;
                w.KeyDown += delegate(object s, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Escape) w.Close(); };
                w.ShowDialog();
            }
            catch (Exception ex) { Log.Err(ex); return MessageBox.Show(text, title, buttons, icon); }
            return result;
        }
    }
}
