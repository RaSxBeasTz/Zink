using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Globalization;
using Windows.Storage;

namespace Zink.Pages
{
    public sealed partial class AppThemePage : Page
    {
        private bool _loaded;
        private bool _loadingGlass;
        private readonly DispatcherTimer _glassApplyTimer;
        private global::Windows.UI.Color _pendingGlassColor;
        private static readonly global::Windows.UI.Color DefaultGlassColor =
            global::Windows.UI.Color.FromArgb(255, 6, 64, 74);
        private static readonly global::Windows.UI.Color LegacyDefaultGlassColor =
            global::Windows.UI.Color.FromArgb(255, 59, 117, 130);
        private static readonly global::Windows.UI.Color PreviousEmeraldDefaultGlassColor =
            global::Windows.UI.Color.FromArgb(255, 14, 143, 61);
        private static readonly global::Windows.UI.Color PreviousNeonDefaultGlassColor =
            global::Windows.UI.Color.FromArgb(255, 56, 255, 102);

        public AppThemePage()
        {
            InitializeComponent();

            _glassApplyTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _glassApplyTimer.Tick += GlassApplyTimer_Tick;

            LoadThemeSelection();
            LoadGlassSelection();
        }

        private void LoadThemeSelection()
        {
            try
            {
                var value = ApplicationData.Current.LocalSettings.Values["Zink.Theme"] as string ?? "Dark";
                ApplySelectionUI(value);
                _loaded = true;
            }
            catch
            {
                _loaded = true;
            }
        }

        private void ApplySelectionUI(string value)
        {
            // Update current label
            CurrentThemeText.Text = value switch
            {
                "Light" => "Current: Light",
                "Dark" => "Current: Zink default",
                _ => "Current: Use system setting"
            };

            // Reset borders to default
            ResetTileBorders();

            // Highlight selected tile (accent border)
            var selected = value switch
            {
                "Light" => LightThemeButton,
                "Dark" => DarkThemeButton,
                _ => SystemThemeButton
            };

            try
            {
                selected.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Resources["SelectedStrokeBrush"];
                selected.BorderThickness = new Thickness(2);
            }
            catch
            {
                // If accent resource isn't found, keep default border.
            }
        }

        private void ResetTileBorders()
        {
            var borderBrush = (Microsoft.UI.Xaml.Media.Brush)Resources["ThemeBorderBrush"];

            LightThemeButton.BorderBrush = borderBrush;
            DarkThemeButton.BorderBrush = borderBrush;
            SystemThemeButton.BorderBrush = borderBrush;

            LightThemeButton.BorderThickness = new Thickness(1);
            DarkThemeButton.BorderThickness = new Thickness(1);
            SystemThemeButton.BorderThickness = new Thickness(1);
        }

        private void SaveAndApply(string value)
        {
            if (!_loaded) return;

            try
            {
                ApplicationData.Current.LocalSettings.Values["Zink.Theme"] = value;
            }
            catch { }

            var theme = value switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };

            try
            {
                if (App.MainWindow is MainWindow mw)
                {
                    mw.ApplyAppTheme(theme);
                }
            }
            catch { }

            ApplySelectionUI(value);
        }

        private void LoadGlassSelection()
        {
            _loadingGlass = true;
            var value = ColorToHex(DefaultGlassColor);

            try
            {
                value = ApplicationData.Current.LocalSettings.Values["Zink.GlassTint"] as string ?? value;
            }
            catch { }

            if (!TryParseHexColor(value, out var color) || IsLegacyDefaultGlassColor(color))
            {
                color = DefaultGlassColor;
            }

            GlassColorPicker.Color = color;
            ApplyGlassColor(color, false);
            _loadingGlass = false;
        }

        private void SystemThemeButton_Click(object sender, RoutedEventArgs e)
        {
            SaveAndApply("Default");
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Frame.CanGoBack)
                {
                    Frame.GoBack();
                }
                else
                {
                    Frame.Navigate(typeof(AppCustomizationPage));
                }
            }
            catch { }
        }

        private void LightThemeButton_Click(object sender, RoutedEventArgs e)
        {
            SaveAndApply("Light");
        }

        private void DarkThemeButton_Click(object sender, RoutedEventArgs e)
        {
            SaveAndApply("Dark");
        }

        private void PresetColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string hex)
                return;

            if (!TryParseHexColor(hex, out var color))
                return;

            _glassApplyTimer.Stop();
            GlassColorPicker.Color = color;
            ApplyGlassColor(color, true, true);
        }

        private void ResetGlassColor_Click(object sender, RoutedEventArgs e)
        {
            _glassApplyTimer.Stop();
            GlassColorPicker.Color = DefaultGlassColor;
            SaveAndApply("Dark");
            ApplyGlassColor(DefaultGlassColor, true, true);
        }

        private void GlassColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (_loadingGlass) return;

            ApplyGlassColor(args.NewColor, false, false);
            _pendingGlassColor = args.NewColor;

            _glassApplyTimer.Stop();
            _glassApplyTimer.Start();
        }

        private void GlassApplyTimer_Tick(object? sender, object e)
        {
            _glassApplyTimer.Stop();

            try
            {
                if (App.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.ApplyGlassTint(_pendingGlassColor, true);
                }
            }
            catch { }
        }

        private void ApplyGlassColor(global::Windows.UI.Color color, bool save, bool tintCurrentPage = false)
        {
            var hex = ColorToHex(color);
            CurrentGlassText.Text = $"Glass colour: {hex}";

            SetBrush("ThemePanelBrush", WithAlpha(color, 88));
            SetBrush("ThemeCardBrush", WithAlpha(color, 56));
            PageGradientStart.Color = global::Windows.UI.Color.FromArgb(144, 0, 8, 20);
            PageGradientMiddle.Color = WithAlpha(Darken(color, 26), 120);
            PageGradientEnd.Color = WithAlpha(color, 128);

            try
            {
                if (save && App.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.ApplyGlassTint(color, tintCurrentPage);

                    if (tintCurrentPage)
                    {
                        _glassApplyTimer.Stop();
                    }
                }
            }
            catch { }
        }

        private void SetBrush(string key, global::Windows.UI.Color color)
        {
            try
            {
                if (Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
                {
                    brush.Color = color;
                }
            }
            catch { }
        }

        private static global::Windows.UI.Color WithAlpha(global::Windows.UI.Color color, byte alpha)
        {
            return global::Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private static global::Windows.UI.Color Darken(global::Windows.UI.Color color, byte amount)
        {
            return global::Windows.UI.Color.FromArgb(
                color.A,
                (byte)Math.Max(0, color.R - amount),
                (byte)Math.Max(0, color.G - amount),
                (byte)Math.Max(0, color.B - amount));
        }

        private static string ColorToHex(global::Windows.UI.Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        private static bool TryParseHexColor(string? hex, out global::Windows.UI.Color color)
        {
            color = DefaultGlassColor;

            if (string.IsNullOrWhiteSpace(hex))
                return false;

            var value = hex.Trim().TrimStart('#');
            if (value.Length == 8)
                value = value.Substring(2);

            if (value.Length != 6)
                return false;

            if (!byte.TryParse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
                !byte.TryParse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
                !byte.TryParse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                return false;
            }

            color = global::Windows.UI.Color.FromArgb(255, r, g, b);
            return true;
        }

        private static bool IsLegacyDefaultGlassColor(global::Windows.UI.Color color)
        {
            return color.R == LegacyDefaultGlassColor.R &&
                color.G == LegacyDefaultGlassColor.G &&
                color.B == LegacyDefaultGlassColor.B ||
                color.R == PreviousEmeraldDefaultGlassColor.R &&
                color.G == PreviousEmeraldDefaultGlassColor.G &&
                color.B == PreviousEmeraldDefaultGlassColor.B ||
                color.R == PreviousNeonDefaultGlassColor.R &&
                color.G == PreviousNeonDefaultGlassColor.G &&
                color.B == PreviousNeonDefaultGlassColor.B;
        }
    }
}
