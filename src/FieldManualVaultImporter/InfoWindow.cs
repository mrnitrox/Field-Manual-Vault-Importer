using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FieldManualVaultImporter;

/// <summary>The About and Help windows: a heading, a few paragraphs or numbered steps, and Close.</summary>
public sealed class InfoWindow : Window
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    private readonly StackPanel _body = new() { Margin = new Thickness(24, 0, 24, 8) };

    private InfoWindow(Window owner, string title, string heading, string? subheading)
    {
        Owner = owner;
        Title = title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("Bg");

        var head = new DockPanel { Margin = new Thickness(24, 20, 24, 14) };
        var icon = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/FieldManualVaultImporter;component/Assets/app.png")),
            Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = heading, FontSize = 18, FontWeight = FontWeights.SemiBold });
        if (subheading != null) titles.Children.Add(new TextBlock { Text = subheading, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0) });
        head.Children.Add(titles);

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        var footer = new Border
        {
            Background = (Brush)FindResource("Bg.Chrome"), BorderBrush = (Brush)FindResource("Border"),
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 10, 16, 10), Child = close,
        };

        var panel = new DockPanel { Background = (Brush)FindResource("Bg") };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(head);
        panel.Children.Add(footer);
        panel.Children.Add(_body);
        Content = panel;
    }

    private void Paragraph(string text, bool muted = false) =>
        _body.Children.Add(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), LineHeight = 19,
            Foreground = (Brush)FindResource(muted ? "Fg.Muted" : "Fg"),
        });

    private void Step(int number, string title, string text)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var badge = new Border
        {
            Background = (Brush)FindResource("Accent"), CornerRadius = new CornerRadius(11), Width = 22, Height = 22,
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = number.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        var text2 = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        text2.Inlines.Add(new System.Windows.Documents.Run(title + "  ") { FontWeight = FontWeights.SemiBold });
        text2.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = (Brush)FindResource("Fg.Muted") });
        row.Children.Add(text2);
        _body.Children.Add(row);
    }

    public static void ShowAbout(Window owner)
    {
        var w = new InfoWindow(owner, "About", "Field Manual Vault Importer", $"Version {Version}");
        w.Paragraph("Downloads the public-domain U.S. military publications of the T.REX ARMS Field Manual Vault "
                    + "(trex-arms.com/field-manual-vault) to a folder of your choice, as PDF, EPUB or both, sorted by branch and "
                    + "publication type.");
        w.Paragraph("It reads the site as a visitor does, one page at a time, and can pause and resume at any point, even after a "
                    + "crash or a power cut. If the site asks for a browser check, you complete it yourself; the program never does.");
        w.Paragraph("Not affiliated with T.REX ARMS. The publications are U.S. government works in the public domain.", muted: true);
        w.ShowDialog();
    }

    public static void ShowHelp(Window owner)
    {
        var w = new InfoWindow(owner, "Help", "How to use it", null);
        w.Step(1, "Destination.", "Browse… to a folder with enough free space. The files go to <folder>\\Field Manual Vault\\<Branch>\\<Type>\\.");
        w.Step(2, "Collections.", "Open the list and tick the collections you want: Current Doctrine, Recent Archive, Lessons Learned, Historic.");
        w.Step(3, "File types.", "Tick PDF (the original document), EPUB (reflowing text for phones and e-readers) or both.");
        w.Step(4, "Start.", "First the catalog is read, about one page a second (roughly 45 minutes for 2,600 publications); the total size "
                            + "then appears. After that the files download one at a time.");
        w.Step(5, "Pause, Resume, Cancel.", "Pause stops at once and Resume carries on from there. Cancel keeps everything downloaded so far. "
                                            + "It's safe to close the program: the next time, choose the same folder and click \"Resume it\".");
        w.Step(6, "Browser check.", "If the site asks for one, click \"Open the site to verify\", complete the check in the window that "
                                    + "opens, and the download continues by itself.");
        w.Paragraph("Don't delete vault-download.json or the .part files in the Field Manual Vault folder while a run is unfinished: they "
                    + "are how it resumes.", muted: true);
        w.ShowDialog();
    }
}
