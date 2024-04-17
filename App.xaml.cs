using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Serilog;
using Serilog.Events;

// ReSharper disable PossibleNullReferenceException

namespace AzurePRMate {

    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application, IDisposable {

        protected ContextMenu IconContextMenu;

        private readonly AzurePrService _azurePrService;
        private readonly int _reminderFrequency;
        private readonly int _fetchFrequency;

        private Timer _timer;
        private DateTime _lastReminderTime;
        private TaskbarIcon _taskbarIcon;

        public App() {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File("prMate.log", LogEventLevel.Information)
                .CreateLogger();
            var config = new ConfigurationBuilder().AddJsonFile("config.json").AddUserSecrets<App>().Build();
            this._azurePrService = new AzurePrService(config);
            this._reminderFrequency = config.GetValue("ReminderFrequencyInMinutes", 30);
            this._fetchFrequency = config.GetValue("FetchFrequencyInSeconds", 180);
        }

        public void Dispose() {
            this._taskbarIcon?.Dispose();
            this._timer?.Dispose();
        }

        protected override void OnStartup(StartupEventArgs _) {
            this._taskbarIcon = (this.MainWindow!.Content as TaskbarIcon);
            this._taskbarIcon!.TrayBalloonTipClicked += this.OnBalloonClick;
            this._taskbarIcon.TrayMouseDoubleClick += this.OnTaskbarIconDoubleClick;

            var runAtStartUpMenuItem = this._taskbarIcon.ContextMenu.Items.GetItemAt(1) as MenuItem;
            runAtStartUpMenuItem.IsChecked = this.WillRunOnStartUp();

            this._timer = new Timer(async _ => {
                try {
                    await CheckAzure();
                } catch (Exception e) {
                    Log.Error(e, "Failed to refresh with Azure");
                    this.Dispatcher.Invoke(() => {
                        this._taskbarIcon.ToolTipText = $"Azure PRMate (error)";
                    });
                }
            }, null, 0, this._fetchFrequency * 1_000);
        }

        private void OnContextMenuOpened(object sender, RoutedEventArgs e) {
            var runAtStartUpMenuItem = this._taskbarIcon.ContextMenu.Items.GetItemAt(1) as MenuItem;
            runAtStartUpMenuItem.IsChecked = this.WillRunOnStartUp();
        }

        private void OnStartUpMenuItemClick(object sender, RoutedEventArgs e) {
            var key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            if (this.WillRunOnStartUp()) {
                key.DeleteValue("AzurePRMate");
            } else {
                key.SetValue("AzurePRMate", Process.GetCurrentProcess().MainModule.FileName);
            }
        }

        private void OnOpenAzureDevOpsMenuItemClick(object sender, RoutedEventArgs e) {
            this.OpenBrowser("https://dev.azure.com/PebblePad/_pulls");
        }

        private void OnExitMenuItemClick(object sender, RoutedEventArgs e) {
            this.Dispose();
            Environment.Exit(0);
        }

        private void OnBalloonClick(object sender, RoutedEventArgs e) {
            this.OpenBrowser("https://dev.azure.com/PebblePad/_pulls");
        }

        private void OnTaskbarIconDoubleClick(object sender, RoutedEventArgs e) {
            this.OpenBrowser("https://dev.azure.com/PebblePad/_pulls");
        }

        private async Task CheckAzure() {
            this.Dispatcher.Invoke(() => {
                this._taskbarIcon.ToolTipText += $" (fetching...)";
            });

            await this._azurePrService.RunAutomationsAsync();
            var count = await this._azurePrService.GetPullRequestCountAsync();

            this.Dispatcher.Invoke(() => {
                this._taskbarIcon.IconSource = BitmapFrame.Create(new Uri($"pack://application:,,,/AzurePRMate;component/Icons/devops{(count > 0 ? "-new" : "")}.ico"));
                this._taskbarIcon.ToolTipText = $"Azure PRMate ({count} awaiting)";
                if (count > 0 && this._lastReminderTime < DateTime.UtcNow.AddMinutes(-this._reminderFrequency)) {
                    this._taskbarIcon.ShowBalloonTip("Pull Requests waiting", $"You have {count} pull request(s) awaiting your attention. Click the Azure icon to view them.", BalloonIcon.Info);
                    this._lastReminderTime = DateTime.UtcNow;
                }
            });
        }

        private void OpenBrowser(string url) {
            try {
                Process.Start(url);
            } catch {
                // hack because of this: https://github.com/dotnet/corefx/issues/10361
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                    url = url.Replace("&", "^&");
                    Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
                } else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                    Process.Start("xdg-open", url);
                } else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
                    Process.Start("open", url);
                } else {
                    throw;
                }
            }
        }

        private bool WillRunOnStartUp() {
            var key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            var value = (string) key.GetValue("AzurePRMate", null);
            return !string.IsNullOrWhiteSpace(value);
        }


    }
}
