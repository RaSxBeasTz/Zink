using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zink.Services.NativeCalling;
using Zink.Services.Social;

namespace Zink.Pages.Social
{
    public sealed partial class FriendsPage : Page
    {
        private List<FriendDto> _friends = new();
        private readonly DispatcherTimer _presenceRefreshTimer = new()
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        private bool _isLoadingFriends;

        public FriendsPage()
        {
            this.InitializeComponent();
            Loaded += FriendsPage_Loaded;
            Unloaded += FriendsPage_Unloaded;
            _presenceRefreshTimer.Tick += PresenceRefreshTimer_Tick;
        }

        private async void FriendsPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadFriendsAsync();
            _presenceRefreshTimer.Start();
        }

        private void FriendsPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _presenceRefreshTimer.Stop();
        }

        private async void PresenceRefreshTimer_Tick(object? sender, object e)
        {
            await LoadFriendsAsync();
        }

        private async Task LoadFriendsAsync()
        {
            if (_isLoadingFriends)
                return;

            _isLoadingFriends = true;
            try
            {
                StatusText.Text = "";
                _friends = await SocialManager.Instance.Api.GetFriendsAsync();
                var friendRows = _friends.Select(CreateFriendPresenceViewModel).ToList();

                FriendsList.ItemsSource = null;
                FriendsList.ItemsSource = friendRows;

                UpdateFriendHubCounts(friendRows);
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
            finally
            {
                _isLoadingFriends = false;
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "";

                var query = SearchBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(query))
                {
                    StatusText.Text = "Type a username or display name to search.";
                    return;
                }

                var results = await SocialManager.Instance.Api.SearchUsersAsync(query);
                ShowSearchResultsFlyout((FrameworkElement)sender, results);
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void ShowSearchResultsFlyout(FrameworkElement anchor, IReadOnlyList<UserSummaryDto> results)
        {
            var panel = new StackPanel
            {
                Padding = new Thickness(14),
                Spacing = 10,
                Width = 430
            };

            panel.Children.Add(new TextBlock
            {
                Text = results.Count == 0 ? "No search results" : $"Search results ({results.Count})",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold
            });

            if (results.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Try another username or display name.",
                    TextWrapping = TextWrapping.Wrap
                });
            }
            else
            {
                var resultsPanel = new StackPanel
                {
                    Spacing = 8
                };

                foreach (var user in results.Take(12))
                    resultsPanel.Children.Add(CreateSearchResultRow(user));

                panel.Children.Add(new ScrollViewer
                {
                    Content = resultsPanel,
                    MaxHeight = 380,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                });
            }

            new Flyout
            {
                Content = panel
            }.ShowAt(anchor);
        }

        private UIElement CreateSearchResultRow(UserSummaryDto user)
        {
            var row = new Grid
            {
                ColumnSpacing = 10
            };

            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = new Grid
            {
                Width = 42,
                Height = 42
            };
            avatar.Children.Add(new Ellipse { Fill = MakeBrush(0xFF, 0x27, 0x31, 0x3D) });
            avatar.Children.Add(new FontIcon
            {
                Glyph = "\uE77B",
                FontSize = 17,
                Foreground = MakeBrush(0xFF, 0xFF, 0xFF, 0xFF),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            var namePanel = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            namePanel.Children.Add(new TextBlock
            {
                Text = user.Username,
                FontWeight = FontWeights.SemiBold
            });
            namePanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(user.DisplayName) ? "No display name" : user.DisplayName,
                Opacity = 0.72,
                FontSize = 12
            });

            var addButton = CreateFlyoutIconButton("\uE710", "Add friend");
            addButton.Tag = user.Username;
            addButton.Click += AddFriendButton_Click;

            var blockButton = CreateFlyoutIconButton("\uE8F8", "Block");
            blockButton.Tag = user.UserId;
            blockButton.Click += BlockButton_Click;

            Grid.SetColumn(avatar, 0);
            Grid.SetColumn(namePanel, 1);
            Grid.SetColumn(addButton, 2);
            Grid.SetColumn(blockButton, 3);

            row.Children.Add(avatar);
            row.Children.Add(namePanel);
            row.Children.Add(addButton);
            row.Children.Add(blockButton);

            return new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(16),
                Background = MakeBrush(0x2C, 0xFF, 0xFF, 0xFF),
                BorderBrush = MakeBrush(0x26, 0xFF, 0xFF, 0xFF),
                BorderThickness = new Thickness(1),
                Child = row
            };
        }

        private Button CreateFlyoutIconButton(string glyph, string tooltip)
        {
            var button = new Button
            {
                Width = 38,
                Height = 38,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(19),
                Content = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 15
                }
            };

            ToolTipService.SetToolTip(button, tooltip);
            return button;
        }

        private async void AddFriendButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "";

                var button = (Button)sender;
                var username = button.Tag?.ToString();

                if (string.IsNullOrWhiteSpace(username))
                    throw new Exception("Invalid username.");

                await SocialManager.Instance.Api.AddFriendByUsernameAsync(username);

                StatusText.Text = "Friend added.";

                await LoadFriendsAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void BlockButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "Block not implemented yet.";
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void RefreshFriendsButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadFriendsAsync();
        }

        private void FriendRequestsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(FriendRequestsPage));
        }

        private void ProfileButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ProfilePage));
        }

        private void MessageButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "";

                var button = (Button)sender;
                var targetUserId = long.Parse(button.Tag?.ToString() ?? "0");

                if (targetUserId <= 0)
                    throw new Exception("Invalid target user.");

                var friend = _friends.FirstOrDefault(f => f.UserId == targetUserId);
                Frame.Navigate(typeof(MessagesPage), new MessagesPageArgs
                {
                    TargetUserId = targetUserId,
                    Username = friend?.Username ?? $"User {targetUserId}",
                    DisplayName = friend?.DisplayName ?? ""
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void VoiceCallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "";

                var button = (Button)sender;
                var targetUserId = long.Parse(button.Tag?.ToString() ?? "0");

                if (targetUserId <= 0)
                    throw new Exception("Invalid target user.");

                await NativeCallCoordinator.Instance.StartOutgoingAsync(targetUserId, false);

                StatusText.Text = "Calling...";

                Frame.Navigate(typeof(CallPage), new CallPageArgs
                {
                    TargetUserId = targetUserId,
                    IsScreenShare = false
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void ShareScreenButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "";

                var button = (Button)sender;
                var targetUserId = long.Parse(button.Tag?.ToString() ?? "0");

                if (targetUserId <= 0)
                    throw new Exception("Invalid target user.");

                await NativeCallCoordinator.Instance.StartOutgoingAsync(targetUserId, true);

                StatusText.Text = "Starting screen share call...";

                Frame.Navigate(typeof(CallPage), new CallPageArgs
                {
                    TargetUserId = targetUserId,
                    IsScreenShare = true
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void UpdateFriendHubCounts(IReadOnlyList<FriendPresenceViewModel> friendRows)
        {
            TotalFriendsCountText.Text = friendRows.Count.ToString();
            OnlineFriendsCountText.Text = friendRows.Count(f => f.PresenceText == "Online").ToString();
            AwayFriendsCountText.Text = friendRows.Count(f => f.PresenceText == "Away").ToString();
            BusyFriendsCountText.Text = friendRows.Count(f => f.PresenceText == "Busy").ToString();
        }

        private static FriendPresenceViewModel CreateFriendPresenceViewModel(FriendDto friend)
        {
            var state = ResolvePresence(friend);

            return new FriendPresenceViewModel
            {
                UserId = friend.UserId,
                Username = friend.Username,
                DisplayName = string.IsNullOrWhiteSpace(friend.DisplayName) ? friend.Username : friend.DisplayName,
                PresenceText = state.Text,
                PresenceDetail = state.Detail,
                PresenceBrush = state.Brush
            };
        }

        private static (string Text, string Detail, SolidColorBrush Brush) ResolvePresence(FriendDto friend)
        {
            var raw = (friend.Presence ?? "").Trim();
            var normalized = raw.ToLowerInvariant();

            if (friend.IsOnline || normalized.Contains("online"))
                return ("Online", "Online now", MakeBrush(0xFF, 0x4C, 0xDB, 0x7F));

            if (normalized.Contains("away") || normalized.Contains("idle"))
                return ("Away", "Idle or away", MakeBrush(0xFF, 0xFF, 0xC8, 0x57));

            if (normalized.Contains("busy") || normalized.Contains("dnd") || normalized.Contains("do not disturb"))
                return ("Busy", "Do not disturb", MakeBrush(0xFF, 0xFF, 0x5C, 0x5C));

            if (!string.IsNullOrWhiteSpace(raw) && !normalized.Contains("offline"))
                return (raw, "Custom status", MakeBrush(0xFF, 0x7D, 0xB7, 0xFF));

            return ("Offline", "Not currently connected", MakeBrush(0xFF, 0x7F, 0x87, 0x93));
        }

        private static SolidColorBrush MakeBrush(byte a, byte r, byte g, byte b)
        {
            return new SolidColorBrush(ColorHelper.FromArgb(a, r, g, b));
        }

        private sealed class FriendPresenceViewModel
        {
            public long UserId { get; set; }
            public string Username { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string PresenceText { get; set; } = "";
            public string PresenceDetail { get; set; } = "";
            public SolidColorBrush PresenceBrush { get; set; } = MakeBrush(0xFF, 0x7F, 0x87, 0x93);
        }
    }
}
