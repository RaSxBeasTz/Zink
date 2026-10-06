using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Zink.Services.Social;

namespace Zink.Pages.Social
{
    public sealed partial class ProfilePage : Page
    {
        private long _userId;
        private ProfileDetails _details = new();

        public ProfilePage()
        {
            this.InitializeComponent();
            Loaded += ProfilePage_Loaded;
        }

        private async void ProfilePage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var me = await SocialManager.Instance.Api.GetMeAsync();
                _userId = me.UserId;
                _details = await ProfileDetailsStore.Instance.GetAsync(_userId);

                if (_details.SignedUpAtUtc == null && me.SignedUpAtUtc != null)
                    _details.SignedUpAtUtc = me.SignedUpAtUtc;
                if (string.IsNullOrWhiteSpace(_details.AvatarPath) && !string.IsNullOrWhiteSpace(me.AvatarUrl))
                    _details.AvatarPath = me.AvatarUrl;
                if (_details.SocialLinks.Count == 0 && me.SocialLinks.Count > 0)
                    _details.SocialLinks = me.SocialLinks.ToList();

                EmailText.Text = $"Email: {me.Email}";
                UsernameText.Text = $"Username: {me.Username}";
                SignedUpText.Text = _details.SignedUpAtUtc.HasValue
                    ? $"Signed up to Zink: {_details.SignedUpAtUtc.Value.LocalDateTime:MMMM d, yyyy}"
                    : "Signed up to Zink: Not available";
                DisplayNameBox.Text = me.DisplayName;
                LoadAvatar();
                RenderSocialLinks();
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_userId <= 0)
                    throw new InvalidOperationException("Your profile is not ready yet.");

                _details.SocialLinks = ReadSocialLinks();
                await SocialManager.Instance.Api.UpdateProfileAsync(DisplayNameBox.Text.Trim(), null);
                await ProfileDetailsStore.Instance.SaveAsync(_userId, _details);
                StatusText.Text = "Profile updated.";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void ChooseAvatarButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_userId <= 0)
                    throw new InvalidOperationException("Your profile is not ready yet.");

                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".gif");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));

                var file = await picker.PickSingleFileAsync();
                if (file == null)
                    return;

                _details.AvatarPath = await ProfileDetailsStore.Instance.SaveAvatarAsync(_userId, file);
                await ProfileDetailsStore.Instance.SaveAsync(_userId, _details);
                LoadAvatar();
                StatusText.Text = "Profile picture updated.";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void AddSocialLinkButton_Click(object sender, RoutedEventArgs e)
        {
            var links = ReadSocialLinks();
            links.Add(new SocialLink { Service = "", Url = "" });
            _details.SocialLinks = links;
            RenderSocialLinks();
        }

        private void LoadAvatar()
        {
            if (!string.IsNullOrWhiteSpace(_details.AvatarPath) &&
                (Uri.TryCreate(_details.AvatarPath, UriKind.Absolute, out var uri) || File.Exists(_details.AvatarPath)))
            {
                AvatarImage.Source = new BitmapImage(uri ?? new Uri(_details.AvatarPath));
            }
        }

        private void RenderSocialLinks()
        {
            SocialLinksPanel.Children.Clear();
            foreach (var link in _details.SocialLinks)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var serviceBox = new TextBox { Text = link.Service, PlaceholderText = "Platform", MinHeight = 42, CornerRadius = new CornerRadius(12) };
                var urlBox = new TextBox { Text = link.Url, PlaceholderText = "https://...", MinHeight = 42, CornerRadius = new CornerRadius(12) };
                var removeButton = new Button { Content = "Remove", Padding = new Thickness(12, 0, 12, 0), MinHeight = 42, CornerRadius = new CornerRadius(21) };
                removeButton.Click += (_, _) =>
                {
                    _details.SocialLinks = ReadSocialLinks();
                    var index = SocialLinksPanel.Children.IndexOf(row);
                    if (index >= 0)
                        _details.SocialLinks.RemoveAt(index);
                    RenderSocialLinks();
                };

                Grid.SetColumn(urlBox, 1);
                Grid.SetColumn(removeButton, 2);
                row.Children.Add(serviceBox);
                row.Children.Add(urlBox);
                row.Children.Add(removeButton);
                SocialLinksPanel.Children.Add(row);
            }
        }

        private List<SocialLink> ReadSocialLinks()
        {
            var links = new List<SocialLink>();
            foreach (var row in SocialLinksPanel.Children.OfType<Grid>())
            {
                var service = (row.Children[0] as TextBox)?.Text.Trim() ?? "";
                var url = (row.Children[1] as TextBox)?.Text.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(service) && string.IsNullOrWhiteSpace(url))
                    continue;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new InvalidOperationException($"Enter a valid http or https URL for {service}." );
                links.Add(new SocialLink { Service = string.IsNullOrWhiteSpace(service) ? "Social link" : service, Url = url });
            }
            return links;
        }
        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await SocialManager.Instance.Api.LogoutAsync();
                Frame.Navigate(typeof(LoginPage));
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
        }
    }
}