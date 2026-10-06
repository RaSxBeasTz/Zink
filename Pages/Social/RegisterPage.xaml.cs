using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Zink.Services.Social;

namespace Zink.Pages.Social
{
    public sealed partial class RegisterPage : Page
    {
        public RegisterPage()
        {
            this.InitializeComponent();
        }

        private async void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetBusy(true);
                StatusText.Text = "";

                var username = UsernameBox.Text.Trim();
                var displayName = DisplayNameBox.Text.Trim();
                var email = EmailBox.Text.Trim();
                var password = PasswordBox.Password;

                if (string.IsNullOrWhiteSpace(username))
                    throw new InvalidOperationException("Enter a username.");

                if (string.IsNullOrWhiteSpace(password))
                    throw new InvalidOperationException("Enter a password.");

                var auth = await SocialManager.Instance.Api.RegisterAsync(
                    new RegisterRequest(email, password, username, displayName));

                var profileDetails = await ProfileDetailsStore.Instance.GetAsync(auth.UserId);
                profileDetails.SignedUpAtUtc ??= DateTimeOffset.UtcNow;
                await ProfileDetailsStore.Instance.SaveAsync(auth.UserId, profileDetails);

                StatusText.Text = $"Registered and logged in as {auth.Username}.";

                try
                {
                    await SocialManager.Instance.Realtime.ConnectAsync();
                }
                catch
                {
                    StatusText.Text += " Realtime connection will retry when available.";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(LoginPage));
        }

        private void DeveloperSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(DeveloperSettingsPage));
        }

        private void SetBusy(bool isBusy)
        {
            BusyRing.IsActive = isBusy;
            BusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
