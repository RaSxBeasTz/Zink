using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Windows.System;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class ZinkVpnPage : Page
    {
        private const string CredentialResourceName = "ZinkVpn";

        private readonly ZinkVpnService _vpnService = new();
        private readonly PasswordVault _passwordVault = new();
        private bool _isBusy;
        private ZinkVpnConnectionState _connectionState = ZinkVpnConnectionState.Unknown;

        public ZinkVpnPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LoadSavedCredentials();
            _ = RefreshStateAsync();
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isBusy)
                return;

            await SetBusyAsync(async () =>
            {
                DiagnosticText.Visibility = Visibility.Collapsed;
                DiagnosticText.Text = "";

                StatusText.Text = _connectionState == ZinkVpnConnectionState.Connected
                    ? "Disconnecting Zink VPN..."
                    : "Connecting Zink VPN...";

                ZinkVpnCommandResult result;
                if (_connectionState == ZinkVpnConnectionState.Connected)
                {
                    result = await _vpnService.DisconnectAsync();
                }
                else
                {
                    var username = VpnUsernameBox.Text?.Trim();
                    var password = VpnPasswordBox.Password;
                    result = await _vpnService.ConnectAsync(ZinkVpnService.DefaultProfileName, username, password);

                    if (result.Success && SaveCredentialsCheckBox.IsChecked == true)
                        SaveCredentials(username, password);
                }

                StatusText.Text = result.Message;

                if (!string.IsNullOrWhiteSpace(result.Detail))
                {
                    DiagnosticText.Text = result.Detail;
                    DiagnosticText.Visibility = Visibility.Visible;
                }

                if (result.Success)
                {
                    await RefreshStateCoreAsync();
                }
            });
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshStateAsync();
        }

        private async void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            await Launcher.LaunchUriAsync(new Uri("ms-settings:network-vpn"));
        }

        private async Task RefreshStateAsync()
        {
            if (_isBusy)
                return;

            await SetBusyAsync(async () =>
            {
                await RefreshStateCoreAsync();
            });
        }

        private async Task RefreshStateCoreAsync()
        {
            _connectionState = await _vpnService.GetConnectionStateAsync();
            UpdateUiForState();
        }

        private async Task SetBusyAsync(Func<Task> action)
        {
            _isBusy = true;
            ConnectButton.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            VpnUsernameBox.IsEnabled = false;
            VpnPasswordBox.IsEnabled = false;
            SaveCredentialsCheckBox.IsEnabled = false;

            try
            {
                await action();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"VPN error: {ex.Message}";
                DiagnosticText.Text = ex.ToString();
                DiagnosticText.Visibility = Visibility.Visible;
                _connectionState = ZinkVpnConnectionState.Unknown;
                UpdateUiForState();
            }
            finally
            {
                _isBusy = false;
                ConnectButton.IsEnabled = true;
                RefreshButton.IsEnabled = true;
                VpnUsernameBox.IsEnabled = true;
                VpnPasswordBox.IsEnabled = true;
                SaveCredentialsCheckBox.IsEnabled = true;
            }
        }

        private void LoadSavedCredentials()
        {
            try
            {
                var credentials = _passwordVault.FindAllByResource(CredentialResourceName);
                if (credentials.Count == 0)
                    return;

                var credential = credentials[0];
                credential.RetrievePassword();
                VpnUsernameBox.Text = credential.UserName;
                VpnPasswordBox.Password = credential.Password;
                SaveCredentialsCheckBox.IsChecked = true;
            }
            catch
            {
            }
        }

        private void SaveCredentials(string? username, string? password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return;

            try
            {
                try
                {
                    var existingCredentials = _passwordVault.FindAllByResource(CredentialResourceName);
                    foreach (var existingCredential in existingCredentials)
                    {
                        _passwordVault.Remove(existingCredential);
                    }
                }
                catch
                {
                }

                _passwordVault.Add(new PasswordCredential(CredentialResourceName, username, password));
            }
            catch
            {
            }
        }

        private void UpdateUiForState()
        {
            switch (_connectionState)
            {
                case ZinkVpnConnectionState.Connected:
                    StatusText.Text = "Zink VPN is connected.";
                    DiagnosticText.Visibility = Visibility.Collapsed;
                    ConnectButtonText.Text = "Disconnect";
                    break;

                case ZinkVpnConnectionState.Disconnected:
                    StatusText.Text = "Zink VPN is disconnected.";
                    ConnectButtonText.Text = "Connect";
                    break;

                default:
                    StatusText.Text = "VPN status is unavailable.";
                    ConnectButtonText.Text = "Connect";
                    break;
            }
        }
    }
}
