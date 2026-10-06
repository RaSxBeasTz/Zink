using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace Zink.Pages.Social
{
    public sealed partial class FriendProfilePage : Page
    {
        public FriendProfilePage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is not FriendProfilePageArgs profile)
                return;

            DisplayNameText.Text = profile.DisplayName;
            UsernameText.Text = profile.Username;
            AddedText.Text = $"Added: {profile.AddedText}";
            SignedUpText.Text = $"Signed up to Zink: {profile.SignedUpText}";

            if (!string.IsNullOrWhiteSpace(profile.AvatarUrl))
                AvatarImage.Source = new BitmapImage(new System.Uri(profile.AvatarUrl));
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
        }
    }
}
