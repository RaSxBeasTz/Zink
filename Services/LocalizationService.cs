using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Storage;
using Windows.System.UserProfile;

namespace Zink.Services
{
    public sealed record AppLanguage(string Tag, string DisplayName, string SearchTerms = "");

    public static class LocalizationService
    {
        private const string SettingKey = "Zink.Language";
        public const string SystemLanguageTag = "system";

        public static event EventHandler? LanguageChanged;
        private static readonly HttpClient TranslationClient = new() { Timeout = TimeSpan.FromSeconds(12) };
        private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> TranslationCache = new(StringComparer.Ordinal);
        private sealed class TranslationState
        {
            public bool IsUpdating { get; set; }
            public bool IsWatched { get; set; }
        }

        private static readonly ConditionalWeakTable<DependencyObject, TranslationState> ElementStates = new();
        private static readonly ConcurrentDictionary<string, string> DownloadedTranslationSources = new(StringComparer.Ordinal);

        public static IReadOnlyList<AppLanguage> Languages { get; } = BuildLanguageList();

        private static IReadOnlyList<AppLanguage> BuildLanguageList()
        {
            var specificCultures = CultureInfo.GetCultures(CultureTypes.SpecificCultures);
            var languages = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .Where(culture => !string.IsNullOrWhiteSpace(culture.Name) && culture.Name != "iv")
                .GroupBy(culture => culture.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(culture =>
                {
                    var relatedCultures = specificCultures.Where(specific => IsChildCultureOf(specific, culture)).ToList();
                    var searchParts = new List<string> { culture.Name, culture.TwoLetterISOLanguageName, culture.ThreeLetterISOLanguageName, culture.NativeName, culture.EnglishName };
                    foreach (var related in relatedCultures)
                    {
                        searchParts.Add(related.Name);
                        searchParts.Add(related.NativeName);
                        searchParts.Add(related.EnglishName);
                        try
                        {
                            var region = new RegionInfo(related.Name);
                            searchParts.Add(region.TwoLetterISORegionName);
                            searchParts.Add(region.ThreeLetterISORegionName);
                            searchParts.Add(region.NativeName);
                            searchParts.Add(region.EnglishName);
                        }
                        catch (ArgumentException)
                        {
                        }
                    }

                    return new AppLanguage(
                        culture.Name,
                        string.Equals(culture.NativeName, culture.EnglishName, StringComparison.OrdinalIgnoreCase)
                            ? culture.NativeName
                            : $"{culture.NativeName} ({culture.EnglishName})",
                        string.Join(' ', searchParts));
                })
                .OrderBy(language => language.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            languages.Insert(0, new AppLanguage(SystemLanguageTag, "Use Windows language"));
            return languages;
        }

        private static bool IsChildCultureOf(CultureInfo candidate, CultureInfo parent)
        {
            for (var current = candidate.Parent; !string.IsNullOrWhiteSpace(current.Name); current = current.Parent)
                if (string.Equals(current.Name, parent.Name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool LanguageMatchesQuery(AppLanguage language, string query)
        {
            var normalizedQuery = NormalizeSearchText(query);
            if (normalizedQuery.Length == 0) return true;
            return NormalizeSearchText(language.DisplayName + " " + language.Tag + " " + language.SearchTerms)
                .Contains(normalizedQuery, StringComparison.Ordinal);
        }

        private static string NormalizeSearchText(string value)
        {
            var decomposed = value.Normalize(System.Text.NormalizationForm.FormD);
            return new string(decomposed
                .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        }

        public static string Preference =>
            ApplicationData.Current.LocalSettings.Values[SettingKey] as string ?? SystemLanguageTag;

        public static string CurrentLanguageTag
        {
            get
            {
                var requested = Preference == SystemLanguageTag
                    ? (GlobalizationPreferences.Languages.FirstOrDefault() ?? CultureInfo.CurrentUICulture.Name)
                    : Preference;
                var exact = Languages.FirstOrDefault(x => string.Equals(x.Tag, requested, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact.Tag;

                try
                {
                    var culture = CultureInfo.GetCultureInfo(requested);
                    while (!string.IsNullOrWhiteSpace(culture.Name))
                    {
                        var match = Languages.FirstOrDefault(x => string.Equals(x.Tag, culture.Name, StringComparison.OrdinalIgnoreCase));
                        if (match != null) return match.Tag;
                        culture = culture.Parent;
                    }
                }
                catch (CultureNotFoundException)
                {
                }

                var neutral = requested.Split('-')[0];
                return Languages.FirstOrDefault(x => string.Equals(x.Tag, neutral, StringComparison.OrdinalIgnoreCase))?.Tag ?? "en";
            }
        }

        public static void InitializeForStartup()
        {
            var language = CurrentLanguageTag;

            try
            {
                ApplicationLanguages.PrimaryLanguageOverride = Preference == SystemLanguageTag ? "" : language;
            }
            catch
            {
            }

            try
            {
                var culture = CultureInfo.GetCultureInfo(language);
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                CultureInfo.CurrentUICulture = culture;
            }
            catch (CultureNotFoundException)
            {
            }
        }

        public static bool IsRightToLeft
        {
            get
            {
                try { return CultureInfo.GetCultureInfo(CurrentLanguageTag).TextInfo.IsRightToLeft; }
                catch (CultureNotFoundException) { return false; }
            }
        }

        public static void SetLanguage(string tag)
        {
            if (!Languages.Any(x => x.Tag == tag)) tag = SystemLanguageTag;
            ApplicationData.Current.LocalSettings.Values[SettingKey] = tag;
            ApplicationLanguages.PrimaryLanguageOverride = tag == SystemLanguageTag ? "" : tag;
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void Apply(DependencyObject root)
        {
            if (root is FrameworkElement element)
                element.FlowDirection = IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

            _ = TranslateElementAsync(root);
            WatchElement(root);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                Apply(VisualTreeHelper.GetChild(root, i));

            // Navigation submenus are logical children and may not exist in the visual tree until opened.
            if (root is NavigationView navigation)
            {
                ApplyNavigationItems(navigation.MenuItems);
                ApplyNavigationItems(navigation.FooterMenuItems);
            }
            else if (root is NavigationViewItem navigationItem)
            {
                ApplyNavigationItems(navigationItem.MenuItems);
            }
        }

        public static void ApplyOpenPopups(XamlRoot? xamlRoot)
        {
            if (xamlRoot == null) return;
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
                if (popup.Child != null) Apply(popup.Child);
        }

        public static void RegisterWindow(Window window)
        {
            if (window.Content is not FrameworkElement root) return;

            void ApplyWindow()
            {
                Apply(root);
                ApplyOpenPopups(root.XamlRoot);
                _ = TranslateWindowTitleAsync(window);
            }

            root.Loaded += (_, _) => ApplyWindow();
            EventHandler languageHandler = (_, _) => window.DispatcherQueue.TryEnqueue(ApplyWindow);
            LanguageChanged += languageHandler;

            var popupTimer = window.DispatcherQueue.CreateTimer();
            popupTimer.Interval = TimeSpan.FromMilliseconds(350);
            popupTimer.Tick += (_, _) => ApplyOpenPopups(root.XamlRoot);
            popupTimer.Start();

            window.Closed += (_, _) =>
            {
                popupTimer.Stop();
                LanguageChanged -= languageHandler;
            };
        }

        private static async Task TranslateWindowTitleAsync(Window window)
        {
            var current = window.Title;
            var translated = await TranslateAsync(current);
            if (!string.Equals(current, translated, StringComparison.Ordinal)) window.Title = translated;
        }

        private static void ApplyNavigationItems(IEnumerable<object> items)
        {
            foreach (var item in items)
                if (item is DependencyObject dependencyObject) Apply(dependencyObject);
        }

        public static async Task<string> TranslateAsync(string english)
        {
            if (string.IsNullOrWhiteSpace(english)) return english;
            var source = NormalizeToEnglish(english);
            var language = CurrentLanguageTag;
            if (language == "en") return source;

            var builtIn = Translate(source);
            if (!string.Equals(builtIn, source, StringComparison.Ordinal)) return builtIn;

            var key = language + "\n" + source;
            try
            {
                return await TranslationCache.GetOrAdd(key,
                    _ => new Lazy<Task<string>>(() => DownloadTranslationAsync(source, language))).Value;
            }
            catch
            {
                TranslationCache.TryRemove(key, out _);
                return source;
            }
        }

        public static string Translate(string english)
        {
            if (string.IsNullOrWhiteSpace(english)) return english;
            var source = NormalizeToEnglish(english);
            if (CurrentLanguageTag == "en") return source;
            return GetTranslations(CurrentLanguageTag).TryGetValue(source, out var translated) ? translated : source;
        }

        private static string NormalizeToEnglish(string value)
        {
            if (DownloadedTranslationSources.TryGetValue(value, out var downloadedSource)) return downloadedSource;
            foreach (var language in Languages)
            {
                if (language.Tag is SystemLanguageTag or "en") continue;
                foreach (var pair in GetTranslations(language.Tag))
                    if (string.Equals(pair.Value, value, StringComparison.Ordinal)) return pair.Key;
            }
            return value;
        }

        private static async Task TranslateElementAsync(DependencyObject value)
        {
            var state = ElementStates.GetOrCreateValue(value);
            if (state.IsUpdating) return;
            state.IsUpdating = true;
            var language = CurrentLanguageTag;
            try
            {
                if (value is TextBlock text)
                    SetIfChanged(text.Text, await TranslateAsync(text.Text), translated => text.Text = translated);
                if (value is ContentControl content && content.Content is string contentText)
                    SetIfChanged(contentText, await TranslateAsync(contentText), translated => content.Content = translated);
                if (value is NavigationViewItem nav && nav.Content is string navText)
                    SetIfChanged(navText, await TranslateAsync(navText), translated => nav.Content = translated);
                if (value is NavigationViewItemHeader header && header.Content is string headerText)
                    SetIfChanged(headerText, await TranslateAsync(headerText), translated => header.Content = translated);
                if (value is ToggleSwitch toggle)
                {
                    if (toggle.Header is string h)
                        SetIfChanged(h, await TranslateAsync(h), translated => toggle.Header = translated);
                    var on = toggle.OnContent?.ToString() ?? "On";
                    SetIfChanged(on, await TranslateAsync(on), translated => toggle.OnContent = translated);
                    var off = toggle.OffContent?.ToString() ?? "Off";
                    SetIfChanged(off, await TranslateAsync(off), translated => toggle.OffContent = translated);
                }
                if (value is TextBox box && !string.IsNullOrEmpty(box.PlaceholderText))
                    SetIfChanged(box.PlaceholderText, await TranslateAsync(box.PlaceholderText), translated => box.PlaceholderText = translated);
                if (value is ComboBox combo && combo.Header is string comboHeader)
                    SetIfChanged(comboHeader, await TranslateAsync(comboHeader), translated => combo.Header = translated);
                if (value is ContentDialog dialog)
                {
                    if (dialog.Title is string title)
                        SetIfChanged(title, await TranslateAsync(title), translated => dialog.Title = translated);
                    await SetDialogTextAsync(dialog.PrimaryButtonText, translated => dialog.PrimaryButtonText = translated);
                    await SetDialogTextAsync(dialog.SecondaryButtonText, translated => dialog.SecondaryButtonText = translated);
                    await SetDialogTextAsync(dialog.CloseButtonText, translated => dialog.CloseButtonText = translated);
                }
                if (value is AppBarButton appBarButton && !string.IsNullOrWhiteSpace(appBarButton.Label))
                    SetIfChanged(appBarButton.Label, await TranslateAsync(appBarButton.Label), translated => appBarButton.Label = translated);
                if (value is MenuFlyoutItem menuItem && !string.IsNullOrWhiteSpace(menuItem.Text))
                    SetIfChanged(menuItem.Text, await TranslateAsync(menuItem.Text), translated => menuItem.Text = translated);
                if (value is PasswordBox passwordBox && !string.IsNullOrWhiteSpace(passwordBox.PlaceholderText))
                    SetIfChanged(passwordBox.PlaceholderText, await TranslateAsync(passwordBox.PlaceholderText), translated => passwordBox.PlaceholderText = translated);
                if (value is AutoSuggestBox suggestBox && !string.IsNullOrWhiteSpace(suggestBox.PlaceholderText))
                    SetIfChanged(suggestBox.PlaceholderText, await TranslateAsync(suggestBox.PlaceholderText), translated => suggestBox.PlaceholderText = translated);
            }
            finally
            {
                state.IsUpdating = false;
            }

            // Re-run only after releasing the guard if the selection changed mid-request.
            if (language != CurrentLanguageTag) _ = TranslateElementAsync(value);
        }

        private static async Task SetDialogTextAsync(string current, Action<string> setter)
        {
            if (!string.IsNullOrWhiteSpace(current))
                SetIfChanged(current, await TranslateAsync(current), setter);
        }

        private static void SetIfChanged(string current, string translated, Action<string> setter)
        {
            if (!string.Equals(current, translated, StringComparison.Ordinal)) setter(translated);
        }

        private static void WatchElement(DependencyObject value)
        {
            var state = ElementStates.GetOrCreateValue(value);
            if (state.IsWatched) return;
            state.IsWatched = true;

            if (value is TextBlock text)
                text.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => TranslateAfterExternalChange(text));
            if (value is ContentControl content)
                content.RegisterPropertyChangedCallback(ContentControl.ContentProperty, (_, _) => TranslateAfterExternalChange(content));
            if (value is TextBox box)
                box.RegisterPropertyChangedCallback(TextBox.PlaceholderTextProperty, (_, _) => TranslateAfterExternalChange(box));
        }

        private static void TranslateAfterExternalChange(DependencyObject value)
        {
            var state = ElementStates.GetOrCreateValue(value);
            if (!state.IsUpdating) _ = TranslateElementAsync(value);
        }

        private static async Task<string> DownloadTranslationAsync(string source, string language)
        {
            var target = language switch
            {
                "zh-Hans" => "zh-CN",
                "zh-Hant" => "zh-TW",
                _ => language
            };
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=" +
                Uri.EscapeDataString(target) + "&dt=t&q=" + Uri.EscapeDataString(source);
            using var response = await TranslationClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            var segments = json.RootElement[0];
            var translated = string.Concat(segments.EnumerateArray().Select(segment => segment[0].GetString()));
            if (string.IsNullOrWhiteSpace(translated)) return source;
            DownloadedTranslationSources[translated] = source;
            return translated;
        }

        private static Dictionary<string, string> GetTranslations(string language) => language switch
        {
            "es" => Make("Configuración", "Idioma", "Usar el idioma de Windows", "Fondo", "Activado", "Desactivado", "Buscar actualizaciones", "Restablecer valores predeterminados", "Inicio", "Música", "Vídeo", "Juegos", "Social", "Notificaciones", "Privacidad", "Acerca de"),
            "fr" => Make("Paramètres", "Langue", "Utiliser la langue de Windows", "Arrière-plan", "Activé", "Désactivé", "Rechercher des mises à jour", "Rétablir les paramètres par défaut", "Accueil", "Musique", "Vidéo", "Jeux", "Réseaux sociaux", "Notifications", "Confidentialité", "À propos"),
            "de" => Make("Einstellungen", "Sprache", "Windows-Sprache verwenden", "Hintergrund", "Ein", "Aus", "Nach Updates suchen", "Standardeinstellungen wiederherstellen", "Startseite", "Musik", "Video", "Spiele", "Soziales", "Benachrichtigungen", "Datenschutz", "Info"),
            "it" => Make("Impostazioni", "Lingua", "Usa la lingua di Windows", "Sfondo", "Attivo", "Disattivo", "Controlla aggiornamenti", "Ripristina impostazioni predefinite", "Home", "Musica", "Video", "Giochi", "Social", "Notifiche", "Privacy", "Informazioni"),
            "pt" => Make("Definições", "Idioma", "Usar o idioma do Windows", "Segundo plano", "Ligado", "Desligado", "Procurar atualizações", "Repor predefinições", "Início", "Música", "Vídeo", "Jogos", "Social", "Notificações", "Privacidade", "Acerca de"),
            "nl" => Make("Instellingen", "Taal", "Windows-taal gebruiken", "Achtergrond", "Aan", "Uit", "Controleren op updates", "Standaardinstellingen herstellen", "Start", "Muziek", "Video", "Games", "Sociaal", "Meldingen", "Privacy", "Over"),
            "pl" => Make("Ustawienia", "Język", "Użyj języka systemu Windows", "Tło", "Wł.", "Wył.", "Sprawdź aktualizacje", "Przywróć ustawienia domyślne", "Strona główna", "Muzyka", "Wideo", "Gry", "Społeczność", "Powiadomienia", "Prywatność", "Informacje"),
            "tr" => Make("Ayarlar", "Dil", "Windows dilini kullan", "Arka plan", "Açık", "Kapalı", "Güncellemeleri denetle", "Varsayılanları geri yükle", "Ana Sayfa", "Müzik", "Video", "Oyunlar", "Sosyal", "Bildirimler", "Gizlilik", "Hakkında"),
            "ar" => Make("الإعدادات", "اللغة", "استخدام لغة Windows", "الخلفية", "تشغيل", "إيقاف", "التحقق من وجود تحديثات", "استعادة الإعدادات الافتراضية", "الرئيسية", "الموسيقى", "الفيديو", "الألعاب", "اجتماعي", "الإشعارات", "الخصوصية", "حول"),
            "hi" => Make("सेटिंग्स", "भाषा", "Windows की भाषा इस्तेमाल करें", "पृष्ठभूमि", "चालू", "बंद", "अपडेट जाँचें", "डिफ़ॉल्ट पुनर्स्थापित करें", "होम", "संगीत", "वीडियो", "गेम", "सोशल", "सूचनाएँ", "गोपनीयता", "परिचय"),
            "zh-Hans" => Make("设置", "语言", "使用 Windows 语言", "后台", "开", "关", "检查更新", "恢复默认设置", "主页", "音乐", "视频", "游戏", "社交", "通知", "隐私", "关于"),
            "ja" => Make("設定", "言語", "Windows の言語を使用", "バックグラウンド", "オン", "オフ", "更新プログラムを確認", "既定値に戻す", "ホーム", "音楽", "ビデオ", "ゲーム", "ソーシャル", "通知", "プライバシー", "バージョン情報"),
            "ko" => Make("설정", "언어", "Windows 언어 사용", "백그라운드", "켜기", "끄기", "업데이트 확인", "기본값 복원", "홈", "음악", "비디오", "게임", "소셜", "알림", "개인 정보", "정보"),
            _ => new(StringComparer.Ordinal)
        };

        private static Dictionary<string, string> Make(params string[] t)
        {
            string[] e = { "Settings", "Language", "Use Windows language", "Background", "On", "Off", "Check for updates", "Reset defaults", "Home", "Music", "Video", "Gaming", "Social", "Notifications", "Privacy", "About" };
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < e.Length; i++) result[e[i]] = t[i];
            return result;
        }
    }
}
