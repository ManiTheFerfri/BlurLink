using System.Windows;
using System.Windows.Controls;
using BlurLink.Contracts;
using BlurLink.Desktop;
using BlurLink.Desktop.ViewModels;
using BlurLink.Desktop.Views;
using Xunit;

namespace BlurLink.Core.Tests;

[CollectionDefinition("ui", DisableParallelization = true)]
public sealed class UiTestCollection
{
}

/// <summary>
/// Instantiates every view with a real view-model on an STA thread and
/// forces layout. Catches XAML parse errors and throwing bindings
/// (e.g. TwoWay on read-only properties) without launching the app.
/// Runs isolated: real windows must never share the run with other tests.
/// </summary>
[Collection("ui")]
public sealed class ViewSmokeTests
{
    private static void RunOnSta(Action action)
    {
        // Real windows on a background STA thread are timing-sensitive under
        // parallel test load; one retry separates flakes from real breaks.
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            last = null;
            Exception? captured = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(60)))
            {
                throw new XamlParseException("View smoke test timed out", new TimeoutException());
            }

            last = captured;
            if (last is null)
            {
                return;
            }

            Thread.Sleep(500);
        }

        throw new XamlParseException("View smoke test failed", last!);
    }

    private static void Render(FrameworkElement view)
    {
        view.Measure(new Size(1000, 700));
        view.Arrange(new Rect(0, 0, 1000, 700));
        view.UpdateLayout();
    }

    /// <summary>Walks up from the test binary to the repo root (BlurLink.sln).</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BlurLink.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repo root (BlurLink.sln).");
    }

    [Fact]
    public void AllViews_RenderWithoutThrowing()
    {
        RunOnSta(() =>
        {
            // Host the shared theme like App.xaml does, so StaticResources resolve.
            // Loaded as loose XAML; the converter namespace is assembly-qualified
            // in memory (BlurLink.dll is already referenced by the test project).
            var themePath = Path.Combine(FindRepoRoot(), "src", "BlurLink.Desktop", "Themes", "Theme.xaml");
            var themeXaml = File.ReadAllText(themePath).Replace(
                "clr-namespace:BlurLink.Desktop.Converters\"",
                "clr-namespace:BlurLink.Desktop.Converters;assembly=BlurLink\"",
                StringComparison.Ordinal);
            var theme = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(themeXaml);
            var app = new Application();
            app.Resources.MergedDictionaries.Add(theme);

            var vm = new MainViewModel();
            try
            {
                var join = new JoinView { DataContext = vm.Join };
                var host = new HostView { DataContext = vm.Host };
                var settings = new SettingsView { DataContext = vm.Settings };

                Render(join);
                Render(host);
                Render(settings);

                // Exercise the host tab's two visual states: off (prerequisite
                // message + no players) and running with a player listed.
                vm.Host.ApplyStatus(new IpcStatusResponse { HostActive = false });
                Render(host);
                vm.Host.ApplyStatus(new IpcStatusResponse
                {
                    HostActive = true,
                    HostForwardsHeard = 3,
                    HostRepliesForwarded = 1,
                    HostBroadcastReplies = 1,
                    Filter = "(inbound && ip && udp && udp.DstPort == 47811)",
                    HostPlayers =
                    {
                        new HostPlayerStatus
                        {
                            OverlayIp = "25.1.2.3",
                            LanIp = "192.168.1.50",
                            BlurSourcePort = 51234,
                            ForwardsHeard = 3,
                            RepliesForwarded = 1,
                            InFilter = true,
                        },
                        new HostPlayerStatus { OverlayIp = "25.4.5.6", InFilter = false },
                    },
                });
                Render(host);

                // Exercise per-state visuals: running + lost.
                vm.Join.BridgeRunning = true;
                Render(join);
                vm.Join.HelperLost = true;
                Render(join);
                vm.Join.BridgeRunning = false;
                vm.Join.HelperLost = false;
                foreach (var view in new[] { "Join", "Host", "Settings" })
                {
                    vm.CurrentView = view;
                }

                // MainWindow hosts styles the views don't (sidebar status card).
                // Show() forces full style application, like a real launch.
                var win = new MainWindow();
                try
                {
                    win.Show();
                    Render(win);
                }
                finally
                {
                    win.Close();
                }
            }
            finally
            {
                vm.Host.Dispose();
                vm.Join.Dispose();
            }
        });
    }

    private sealed class XamlParseException : Exception
    {
        public XamlParseException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
