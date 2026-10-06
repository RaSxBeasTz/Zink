using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class XboxPage : Page
    {
        private Storyboard? _achievementStoryboard;
        private string? _lastAchievementSignature;
        private DateTimeOffset _lastAchievementTime;

        public XboxPage()
        {
            this.InitializeComponent();
            InitializeXboxWebView();
        }

        private async void InitializeXboxWebView()
        {
            // Choose a folder in LocalAppData for persistence of cookies, local storage, etc.
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZinkXboxWebView2Data");

            // Create the CoreWebView2Environment with your chosen userDataFolder
            // (leave browserExecutableFolder null to auto-find the runtime, and options null for default)
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: null);

            // Initialize the WebView2 itself with that environment
            await XboxWebView.EnsureCoreWebView2Async(env);

            XboxWebView.CoreWebView2.WebMessageReceived += XboxWebView_WebMessageReceived;
            await XboxWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(AchievementObserverScript);

            // Now point it at the Xbox web app
            XboxWebView.Source = new Uri("https://www.xbox.com/play");
        }

        private void XboxWebView_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                using JsonDocument message = JsonDocument.Parse(args.TryGetWebMessageAsString());
                JsonElement root = message.RootElement;
                if (!root.TryGetProperty("source", out JsonElement source) ||
                    source.GetString() != "zink-xbox-achievement")
                {
                    return;
                }

                string title = ReadText(root, "title", "Xbox achievement");
                string score = ReadText(root, "score", "G");
                string description = ReadText(root, "description", "Xbox achievement added to your profile");
                ShowAchievement(title, score, description);
            }
            catch
            {
                // Ignore unrelated or malformed messages from the hosted page.
            }
        }

        private static string ReadText(JsonElement root, string property, string fallback)
        {
            if (!root.TryGetProperty(property, out JsonElement value))
                return fallback;

            string? text = value.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }

        public void ShowAchievement(string title, string score = "G", string description = "Xbox achievement added to your profile")
        {
            string signature = $"{title}|{score}";
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_lastAchievementSignature == signature && now - _lastAchievementTime < TimeSpan.FromSeconds(12))
                return;

            _lastAchievementSignature = signature;
            _lastAchievementTime = now;
            AchievementUnlockSoundService.Play();

            DispatcherQueue.TryEnqueue(() =>
            {
                _achievementStoryboard?.Stop();
                AchievementTitleText.Text = title;
                AchievementScoreText.Text = score;
                AchievementDescriptionText.Text = description;
                AchievementOverlay.Visibility = Visibility.Visible;

                _achievementStoryboard = BuildAchievementStoryboard();
                _achievementStoryboard.Completed += (_, _) => AchievementOverlay.Visibility = Visibility.Collapsed;
                _achievementStoryboard.Begin();
            });
        }

        private Storyboard BuildAchievementStoryboard()
        {
            var storyboard = new Storyboard();
            var duration = new Duration(TimeSpan.FromMilliseconds(520));
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var opacity = new DoubleAnimationUsingKeyFrames();
            opacity.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
            opacity.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(520), Value = 1, EasingFunction = easing });
            opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(4300), Value = 1 });
            opacity.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(4750), Value = 0, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
            Storyboard.SetTarget(opacity, AchievementOverlay);
            Storyboard.SetTargetProperty(opacity, "Opacity");

            var translate = new DoubleAnimation
            {
                From = 42,
                To = 0,
                Duration = duration,
                EasingFunction = easing
            };
            Storyboard.SetTarget(translate, AchievementTransform);
            Storyboard.SetTargetProperty(translate, "TranslateY");

            var scaleX = new DoubleAnimation { From = 0.94, To = 1, Duration = duration, EasingFunction = easing };
            Storyboard.SetTarget(scaleX, AchievementTransform);
            Storyboard.SetTargetProperty(scaleX, "ScaleX");

            var scaleY = new DoubleAnimation { From = 0.94, To = 1, Duration = duration, EasingFunction = easing };
            Storyboard.SetTarget(scaleY, AchievementTransform);
            Storyboard.SetTargetProperty(scaleY, "ScaleY");

            storyboard.Children.Add(opacity);
            storyboard.Children.Add(translate);
            storyboard.Children.Add(scaleX);
            storyboard.Children.Add(scaleY);
            return storyboard;
        }

        private const string AchievementObserverScript = """
            (() => {
              if (window.__zinkAchievementObserverInstalled) return;
              window.__zinkAchievementObserverInstalled = true;
              const seen = new Map();
              const achievementPattern = /achievement\s+(unlocked|earned)|unlocked\s+achievement/i;
              const scorePattern = /\b(\d{1,5})\s*G\b/i;

              function clean(value) {
                return (value || '').replace(/\s+/g, ' ').trim();
              }

              function inspect(node) {
                if (!(node instanceof Element)) return;
                const text = clean(node.innerText || node.textContent);
                if (!text || text.length > 700 || !achievementPattern.test(text)) return;
                const key = text.slice(0, 250);
                const now = Date.now();
                if (seen.has(key) && now - seen.get(key) < 12000) return;
                seen.set(key, now);

                const lines = (node.innerText || text).split(/\r?\n/).map(clean).filter(Boolean);
                const scoreMatch = text.match(scorePattern);
                const title = lines.find(line =>
                  !achievementPattern.test(line) && !scorePattern.test(line) && line.length > 2 && line.length < 100
                ) || 'Xbox achievement';

                chrome.webview.postMessage(JSON.stringify({
                  source: 'zink-xbox-achievement',
                  title,
                  score: scoreMatch ? `${scoreMatch[1]} G` : 'G',
                  description: 'Xbox achievement added to your profile'
                }));
              }

              const observer = new MutationObserver(records => {
                for (const record of records) {
                  for (const node of record.addedNodes) inspect(node);
                }
              });

              const start = () => {
                if (!document.documentElement) return setTimeout(start, 100);
                observer.observe(document.documentElement, { childList: true, subtree: true });
              };
              start();
            })();
            """;
    }
}
