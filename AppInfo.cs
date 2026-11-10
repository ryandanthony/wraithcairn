using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Wraithcairn;

/// <summary>Version (stamped by GitVersion in releases) and branding assets.</summary>
public static class AppInfo
{
    /// <summary>e.g. "0.1.0" in a release, "0.0.0-dev" for local builds.</summary>
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+')[0]
        ?? "0.0.0-dev";

    private static readonly Lazy<WindowIcon?> IconLazy = new(() => Load(s => new WindowIcon(s), "icon-256.png"));

    public static WindowIcon? WindowIcon => IconLazy.Value;

    private static T? Load<T>(Func<Stream, T> create, string file)
        where T : class
    {
        try
        {
            using Stream stream = AssetLoader.Open(new Uri($"avares://wraithcairn/assets/{file}"));
            return create(stream);
        }
        catch
        {
            return null;
        }
    }

    public static async Task ShowAboutAsync(Window owner)
    {
        StackPanel body = new()
        {
            Margin = new Thickness(24),
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        if (Load(s => new Bitmap(s), "icon-64.png") is { } bitmap)
        {
            body.Children.Add(new Image { Source = bitmap, Width = 64, Height = 64 });
        }

        body.Children.Add(new TextBlock
        {
            Text = "wraithcairn",
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        body.Children.Add(new TextBlock
        {
            Text = $"version {Version}",
            Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        body.Children.Add(new TextBlock
        {
            Text = "Split-pane terminal on Ghostty's libghostty-vt",
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        body.Children.Add(new TextBlock
        {
            Text = "github.com/ryandanthony/wraithcairn",
            Foreground = Brushes.Gray,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        Window dialog = new()
        {
            Title = "About wraithcairn",
            Content = body,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = owner.Background,
            Icon = WindowIcon,
        };

        await dialog.ShowDialog(owner);
    }
}
