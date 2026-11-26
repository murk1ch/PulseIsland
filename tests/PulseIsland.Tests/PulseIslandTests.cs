using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulseIsland.Audio;
using PulseIsland.Core;
using PulseIsland.Interop;
using PulseIsland.Services;
using PulseIsland.Translation;
using PulseIsland.ViewModels;
using Xunit;

namespace PulseIsland.Tests;

public sealed class LassoGeometryTests
{
    [Fact]
    public void ComputeBoundingBox_ReturnsCorrectBoxWithPadding()
    {
        var points = new List<Point>
        {
            new(100, 200),
            new(250, 210),
            new(260, 280),
            new(110, 275)
        };
        var screenBounds = new Rect(0, 0, 1920, 1080);

        var box = LassoGeometryHelper.ComputeBoundingBox(points, screenBounds, padding: 5.0);

        Assert.NotNull(box);
        Assert.Equal(95, box!.Value.X);
        Assert.Equal(195, box.Value.Y);
        Assert.Equal(170, box.Value.Width); // (260+5) - (100-5)
        Assert.Equal(90, box.Value.Height); // (280+5) - (200-5)
    }

    [Fact]
    public void ComputeBoundingBox_ClampsToScreenBoundaries()
    {
        var points = new List<Point>
        {
            new(2, 3),
            new(100, 60)
        };
        var screenBounds = new Rect(0, 0, 500, 400);

        var box = LassoGeometryHelper.ComputeBoundingBox(points, screenBounds, padding: 10.0);

        Assert.NotNull(box);
        Assert.Equal(0, box!.Value.X);
        Assert.Equal(0, box.Value.Y);
        Assert.Equal(110, box.Value.Width);
        Assert.Equal(70, box.Value.Height);
    }

    [Fact]
    public void ComputeBoundingBox_RejectsTinyAccidentalClick()
    {
        var points = new List<Point>
        {
            new(400, 400),
            new(403, 402)
        };
        var screenBounds = new Rect(0, 0, 1920, 1080);

        var box = LassoGeometryHelper.ComputeBoundingBox(points, screenBounds, padding: 6.0);

        Assert.Null(box);
    }
}

public sealed class SecurityHelperTests
{
    [Fact]
    public void ProtectAndUnprotectString_RoundTripsPlaintextAndHidesSecret()
    {
        const string secretApiKey = "deepl-secret-key-987654321:fx";

        string encrypted = SecurityHelper.ProtectString(secretApiKey);

        Assert.False(string.IsNullOrWhiteSpace(encrypted));
        Assert.NotEqual(secretApiKey, encrypted);
        Assert.DoesNotContain("deepl-secret", encrypted, StringComparison.OrdinalIgnoreCase);

        string decrypted = SecurityHelper.UnprotectString(encrypted);
        Assert.Equal(secretApiKey, decrypted);
    }

    [Fact]
    public void UnprotectString_GracefullyHandlesCorruptedOrEmptyInput()
    {
        Assert.Equal(string.Empty, SecurityHelper.UnprotectString(string.Empty));
        Assert.Equal(string.Empty, SecurityHelper.UnprotectString("not-valid-base64!!!"));
    }
}

public sealed class PortablePathsAndSettingsTests
{
    [Fact]
    public void PortablePaths_AllResolveInsidePortableRoot_AndNeverSystemAppData()
    {
        PortablePaths.EnsureDirectoriesExist();

        string normalizedRoot = Path.GetFullPath(PortablePaths.RootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] allPaths =
        {
            PortablePaths.RootDirectory,
            PortablePaths.ConfigDirectory,
            PortablePaths.SettingsFilePath,
            PortablePaths.ModelsDirectory,
            PortablePaths.CacheDirectory,
            PortablePaths.DownloadsCacheDirectory,
            PortablePaths.LogsDirectory
        };

        foreach (var path in allPaths)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool insideRoot = string.Equals(full, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                              full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
            Assert.True(insideRoot, $"Path '{path}' must stay inside PortablePaths.RootDirectory ('{normalizedRoot}')!");

            if (!string.IsNullOrEmpty(appData) && !normalizedRoot.StartsWith(appData, StringComparison.OrdinalIgnoreCase))
                Assert.False(full.StartsWith(appData, StringComparison.OrdinalIgnoreCase), $"Path '{path}' must not point to Roaming AppData!");
            if (!string.IsNullOrEmpty(localAppData) && !normalizedRoot.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
                Assert.False(full.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase), $"Path '{path}' must not point to LocalAppData!");
        }
    }

    [Fact]
    public void SettingsService_SavesAndReloadsCustomValuesCleanly()
    {
        var service = new SettingsService();
        var original = service.Load();

        try
        {
            var modified = new AppSettings
            {
                TargetLanguage = "de",
                AutoDetectSourceLanguage = false,
                ManualSourceLanguage = "fr",
                HotkeyGesture = "Ctrl+Shift+T",
                WaveBarCount = 24,
                WaveStyle = WaveVisualStyle.SmoothCurve,
                WaveColorMode = WaveColorMode.ThemeAdaptive,
                ThemeStyle = AppThemeStyle.CrimsonBlack,
                EnableDwmBackdropBlur = true,
                ShowTrackProgressBar = false,
                EnableAlbumArtGlow = false,
                UseCustomIslandAppearance = true,
                IslandCornerRadius = 18.0,
                IslandBackgroundOpacity = 0.78,
                EnableSmartClipboard = false,
                EnableClipboardTranslateHotkey = false,
                ClipboardTranslateHotkeyGesture = "Ctrl+Alt+Z",
                EnableVolumeAndMicIndicator = false,
                EnableFileDropZone = false,
                EncryptedDeepLApiKey = SecurityHelper.ProtectString("test-key-123")
            };

            service.Save(modified);

            var secondService = new SettingsService();
            var reloaded = secondService.Load();

            Assert.Equal("de", reloaded.TargetLanguage);
            Assert.False(reloaded.AutoDetectSourceLanguage);
            Assert.Equal("fr", reloaded.ManualSourceLanguage);
            Assert.Equal("Ctrl+Shift+T", reloaded.HotkeyGesture);
            Assert.Equal(24, reloaded.WaveBarCount);
            Assert.Equal(WaveVisualStyle.SmoothCurve, reloaded.WaveStyle);
            Assert.Equal(WaveColorMode.ThemeAdaptive, reloaded.WaveColorMode);
            Assert.Equal(AppThemeStyle.CrimsonBlack, reloaded.ThemeStyle);
            Assert.True(reloaded.EnableDwmBackdropBlur);
            Assert.False(reloaded.ShowTrackProgressBar);
            Assert.False(reloaded.EnableAlbumArtGlow);
            Assert.True(reloaded.UseCustomIslandAppearance);
            Assert.Equal(18.0, reloaded.IslandCornerRadius);
            Assert.Equal(0.78, reloaded.IslandBackgroundOpacity);
            Assert.False(reloaded.EnableSmartClipboard);
            Assert.False(reloaded.EnableClipboardTranslateHotkey);
            Assert.Equal("Ctrl+Alt+Z", reloaded.ClipboardTranslateHotkeyGesture);
            Assert.False(reloaded.EnableVolumeAndMicIndicator);
            Assert.False(reloaded.EnableFileDropZone);
            Assert.Equal("test-key-123", SecurityHelper.UnprotectString(reloaded.EncryptedDeepLApiKey));
        }
        finally
        {
            // Restore original settings
            service.Save(original);
        }
    }

    [Fact]
    public void SmartClipboardService_AddsSearchesPinsAndCleansUp()
    {
        bool hadFile = System.IO.File.Exists(PortablePaths.ClipboardHistoryFilePath);
        string? backup = hadFile ? System.IO.File.ReadAllText(PortablePaths.ClipboardHistoryFilePath) : null;

        try
        {
            using var clip = new SmartClipboardService();
            clip.AddText("UniqueKeyword_Alpha_Token");
            clip.AddText("Other_Snippet_Beta");

            var found = clip.Search("Alpha_Token");
            Assert.Equal(1, found.Count);
            Assert.Equal("UniqueKeyword_Alpha_Token", found[0].Text);

            clip.TogglePin(found[0].Id);
            var pinned = clip.Search(null, pinnedOnly: true);
            Assert.True(pinned.Any(x => x.Id == found[0].Id && x.IsPinned));

            clip.Remove(found[0].Id);
            Assert.Equal(0, clip.Search("Alpha_Token").Count);
        }
        finally
        {
            if (hadFile && backup is not null)
                System.IO.File.WriteAllText(PortablePaths.ClipboardHistoryFilePath, backup);
            else if (!hadFile && System.IO.File.Exists(PortablePaths.ClipboardHistoryFilePath))
                System.IO.File.Delete(PortablePaths.ClipboardHistoryFilePath);
        }
    }

    [Fact]
    public void ThemeService_ContainsAllSevenRequestedThemes()
    {
        var allStyles = Enum.GetValues<AppThemeStyle>();
        Assert.Equal(7, allStyles.Length);
        foreach (var style in allStyles)
        {
            Assert.True(ThemeService.Palettes.ContainsKey(style), $"Missing palette for {style}");
            Assert.False(string.IsNullOrWhiteSpace(ThemeService.Palettes[style].DisplayName));
        }
        Assert.True(ThemeService.Palettes[AppThemeStyle.LightWhite].IsLightTheme);
        Assert.False(ThemeService.Palettes[AppThemeStyle.Classic].IsLightTheme);
    }

    [Fact]
    public void IslandViewModel_SettingsButtonIsVisibleOnlyWhenExpanded()
    {
        var vm = new IslandViewModel();

        vm.State = IslandState.Hidden;
        Assert.False(vm.IsSettingsButtonVisible);

        vm.State = IslandState.Compact;
        Assert.False(vm.IsSettingsButtonVisible);

        vm.State = IslandState.Expanded;
        Assert.True(vm.IsSettingsButtonVisible);
    }

    [Fact]
    public void IslandViewModel_NotificationPushAndDismiss_UpdatesHistoryAndActiveNotification()
    {
        var vm = new IslandViewModel();
        Assert.False(vm.HasActiveNotification);
        Assert.False(vm.IsCompactNotificationActive);
        Assert.False(vm.HasNotificationHistory);

        var n1 = new IslandNotification("id-1", "Telegram", "Дуров", "Привет! Островок работает отлично",
            NotificationKind.Default, DateTime.Now);
        vm.PushNotification(n1);

        Assert.True(vm.HasActiveNotification);
        Assert.True(vm.IsCompactNotificationActive);
        Assert.True(vm.HasNotificationHistory);
        Assert.Equal("id-1", vm.ActiveNotification?.Id);
        Assert.Equal("•", vm.NotificationKindLabel);
        Assert.Equal(1, vm.NotificationHistory.Count);

        var n2 = new IslandNotification("id-2", "Windows Defender", "Безопасность", "Угрозы не обнаружены",
            NotificationKind.Success, DateTime.Now);
        vm.PushNotification(n2);

        Assert.Equal("id-2", vm.ActiveNotification?.Id);
        Assert.Equal("✓", vm.NotificationKindLabel);
        Assert.Equal("#22C55E", vm.NotificationKindColor);
        Assert.Equal(2, vm.NotificationHistory.Count);
        Assert.Equal("id-2", vm.NotificationHistory[0].Id);

        // Dismiss active notification hides compact and active banner but preserves history
        vm.DismissNotification();
        Assert.False(vm.HasActiveNotification);
        Assert.False(vm.IsCompactNotificationActive);
        Assert.True(vm.HasNotificationHistory);
        Assert.Equal(2, vm.NotificationHistory.Count);

        // Clear history
        vm.ClearNotificationHistory();
        Assert.False(vm.HasNotificationHistory);
        Assert.Equal(0, vm.NotificationHistory.Count);
        Assert.Equal("0", vm.NotificationCountText);

        // Seed history stays silent (no ActiveNotification or compact toast)
        vm.SeedNotificationHistory(new[] { n1, n2 });
        Assert.False(vm.HasActiveNotification);
        Assert.False(vm.IsCompactNotificationActive);
        Assert.Equal("2", vm.NotificationCountText);
    }

    [Fact]
    public void IslandViewModel_FileDropZoneVisible_OnlyWhenDraggingOrHasFiles()
    {
        var vm = new IslandViewModel { IsFileDropZoneEnabled = true };
        Assert.False(vm.IsFileDropZoneVisible);

        vm.IsDragOverFileDropZone = true;
        Assert.True(vm.IsFileDropZoneVisible);

        vm.IsDragOverFileDropZone = false;
        Assert.False(vm.IsFileDropZoneVisible);

        vm.AddDroppedFiles(new[] { new DropShelfItem(@"C:\temp\note.txt", "note.txt", "1 KB", false) });
        Assert.True(vm.IsFileDropZoneVisible);

        vm.ClearDroppedFiles();
        Assert.False(vm.IsFileDropZoneVisible);
    }

    [Fact]
    public void NotificationFormattingAndSounds_DetectMediaTypesChatTargetsAndGenerateWav()
    {
        var voice = WindowsNotificationListener.FormatNotificationContent("Telegram", "Алексей", "Голосовое сообщение");
        Assert.Equal("Алексей", voice.Title);
        Assert.Equal("Voice Message", voice.ContentTag);
        Assert.Equal("Voice Message", voice.Body);

        var circle = WindowsNotificationListener.FormatNotificationContent("Telegram", "Мария", "Видеосообщение");
        Assert.Equal("Video Message", circle.ContentTag);

        var photo = WindowsNotificationListener.FormatNotificationContent("Telegram", "Иван → Чат команды", "Дмитрий: Фотография");
        Assert.Equal("Photo", photo.ContentTag);
        Assert.Equal("Дмитрий: Photo", photo.Body);

        var video = WindowsNotificationListener.FormatNotificationContent("Telegram", "Олег", "Video");
        Assert.Equal("Video", video.ContentTag);

        var text = WindowsNotificationListener.FormatNotificationContent("Telegram", "Сергей", "Привет, загляни в проект через пять минут");
        Assert.Equal("", text.ContentTag);
        Assert.True(text.Body.StartsWith("Привет, загляни", StringComparison.Ordinal));

        var nGroup = new IslandNotification("1", "Telegram", "Alice → Dev Chat", "Hello", NotificationKind.Info, DateTime.Now, SenderHint: "Alice → Dev Chat");
        Assert.Equal("Dev Chat", NotificationActivator.ExtractTelegramChatTarget(nGroup));

        var nDirect = new IslandNotification("2", "Telegram", "Алексей", "Ок", NotificationKind.Info, DateTime.Now, SenderHint: "Алексей");
        Assert.Equal("Алексей", NotificationActivator.ExtractTelegramChatTarget(nDirect));

        var nDiscord = new IslandNotification("3", "Discord", "Ivan (#dev-chat, Team)", "Check PR", NotificationKind.Info, DateTime.Now, SenderHint: "Ivan (#dev-chat, Team)");
        Assert.Equal("#dev-chat", NotificationActivator.ExtractDiscordChatTarget(nDiscord));

        byte[] wav = NotificationSoundService.GetOrBuildWav(NotificationSoundPreset.PulseChime);
        Assert.True(wav.Length > 44);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
    }

    [Fact]
    public void WeatherAndLocalLlmCatalog_ParseWeatherAndBuildPromptsCleanly()
    {
        const string sampleWttrJson = """
        {
          "current_condition": [
            {
              "temp_C": "14",
              "FeelsLikeC": "12",
              "windspeedKmph": "11",
              "humidity": "62",
              "lang_ru": [{ "value": "Переменная облачность" }]
            }
          ],
          "nearest_area": [
            {
              "areaName": [{ "value": "Москва" }]
            }
          ]
        }
        """;

        var weather = WeatherService.ParseWttrJson(sampleWttrJson);
        Assert.NotNull(weather);
        Assert.Equal("Москва", weather!.City);
        Assert.Equal(14, weather.TempC);
        Assert.Equal(12, weather.FeelsLikeC);
        Assert.Contains("+14°C", WeatherService.FormatWeatherBrief(weather));

        var (wSys, wUsr) = LocalLlmService.BuildWeatherPrompts(weather);
        Assert.Contains("Москва", wUsr);
        Assert.True(wSys.Length > 10);

        var (sSys, sUsr) = LocalLlmService.BuildSummaryPrompts("Длинный тестовый текст про разработку.");
        Assert.Contains("тестовый текст", sUsr);

        var aiCatalog = new LocalLlmCatalogService();
        var aiStatus = aiCatalog.GetStatus();
        Assert.Equal(LocalLlmCatalogService.DefaultModelId, aiStatus.ModelId);
        Assert.False(string.IsNullOrWhiteSpace(aiStatus.HardwareSummary));
    }

    [Fact]
    public void OfflineModelCatalogService_ChecksCompletenessAndDiskSummary()
    {
        var catalogService = new OfflineModelCatalogService();
        var summary = catalogService.GetDiskSpaceSummary();

        Assert.True(summary.TotalSizeBytes > 0);
        Assert.True(summary.AvailableFreeBytes > 0);

        var statuses = catalogService.GetModelStatuses("opus-mt-en-ru");
        Assert.True(statuses.Count >= 4);
        Assert.True(statuses.Any(s => s.ModelId == "opus-mt-en-ru" && s.IsSelected));
        Assert.False(statuses.Any(s => s.ModelId == "_runtime"));
    }

    [Fact]
    public void SentencePieceAndOrtNativeRuntime_WorkZeroDependency()
    {
        string normalized = SentencePieceMarianTokenizer.PreprocessInputText("IS THE BIBLE LINK DOWN FOR YOU?!?!?***");
        Assert.Equal("Is the bible link down for you?", normalized);

        string curly = SentencePieceMarianTokenizer.PreprocessInputText("It\u2019s \u201Cordering\u201D time");
        Assert.Equal("It's \"ordering\" time", curly);

        string enRuDir = System.IO.Path.Combine(PortablePaths.ModelsDirectory, "opus-mt-en-ru");
        if (System.IO.File.Exists(System.IO.Path.Combine(enRuDir, "encoder_model.onnx")) &&
            System.IO.File.Exists(System.IO.Path.Combine(enRuDir, "decoder_model.onnx")) &&
            OrtNativeRuntime.IsRuntimeAvailable())
        {
            using var holder = new OrtNativeRuntime.OrtSessionHolder(
                System.IO.Path.Combine(enRuDir, "encoder_model.onnx"),
                System.IO.Path.Combine(enRuDir, "decoder_model.onnx"));
            long[] sampleIds = [42, 0];
            long[] sampleMask = [1, 1];
            long[] generated = holder.RunGreedySeq2Seq(sampleIds, sampleMask, bosTokenId: 62517, eosTokenId: 0, maxLength: 6, CancellationToken.None);
            Assert.True(generated.Length >= 2, $"Expected ONNX decoder to generate at least 1 token, got len={generated.Length}");
        }
    }
}

public sealed class FftAndHotkeyTests
{
    [Fact]
    public void FftSpectrumProcessor_ProducesNonZeroBandsFor1kHzSineWave_AndDecays()
    {
        const int sampleRate = 48000;
        const int fftSize = 1024;
        var samples = new float[fftSize];

        for (int i = 0; i < fftSize; i++)
        {
            samples[i] = (float)(0.8 * Math.Sin(2.0 * Math.PI * 1000.0 * i / sampleRate));
        }

        var processor = new FftSpectrumProcessor(fftSize, initialBandCount: 16);
        var bands = new double[16];

        processor.ComputeBands(samples, sampleRate, bands, sensitivity: 1.5, smoothing: 0.5);

        double maxBand = bands.Max();
        Assert.True(maxBand > 0.15, $"Expected significant FFT response for 1kHz sine wave, got max={maxBand}");

        // Verify decay to zero
        for (int step = 0; step < 60; step++)
        {
            processor.DecayToZero(bands, decayFactor: 0.7);
        }
        Assert.True(bands.Max() < 0.01, "Expected bands to decay to zero on silence.");
    }

    [Fact]
    public void HotkeyService_ParsesValidAndInvalidGestures()
    {
        Assert.True(HotkeyService.TryParseGesture("Alt+Q", out uint mods1, out uint vk1));
        Assert.True((mods1 & Win32Api.MOD_ALT) != 0);
        Assert.True(vk1 != 0);

        Assert.True(HotkeyService.TryParseGesture("Ctrl+Shift+T", out uint mods2, out uint vk2));
        Assert.True((mods2 & Win32Api.MOD_CONTROL) != 0);
        Assert.True((mods2 & Win32Api.MOD_SHIFT) != 0);
        Assert.True(vk2 != 0);

        Assert.False(HotkeyService.TryParseGesture("", out _, out _));
        Assert.False(HotkeyService.TryParseGesture("Ctrl+Alt", out _, out _));
    }

    [Fact]
    public void LanguageCatalog_NormalizesRegionSubtagsAndFallbacks()
    {
        var en = LanguageCatalog.FindOrDefault("en-US");
        Assert.Equal("en", en.Code);

        var ru = LanguageCatalog.FindOrDefault("ru-RU");
        Assert.Equal("ru", ru.Code);

        var fallback = LanguageCatalog.FindOrDefault("unknown-xyz", "ru");
        Assert.Equal("ru", fallback.Code);
    }

    [Fact]
    public void LocalLlmCatalogAndMode_SupportsMultipleModelsWithoutAutoDownload()
    {
        Assert.True(LocalLlmCatalogService.BuiltInModels.Count >= 4);
        Assert.Equal("qwen2.5-3b-instruct", LocalLlmCatalogService.FindModel("unknown-id").ModelId);
        Assert.Equal("qwen3-4b-instruct", LocalLlmCatalogService.FindModel("qwen3-4b-instruct").ModelId);
        Assert.Equal("qwen2.5-7b-instruct", LocalLlmCatalogService.FindModel("qwen2.5-7b-instruct").ModelId);
        Assert.Equal("gemma-2-9b-it", LocalLlmCatalogService.FindModel("gemma-2-9b-it").ModelId);

        var catalog = new LocalLlmCatalogService();
        var missingStatus = catalog.GetStatus("qwen2.5-7b-instruct");
        Assert.Equal("qwen2.5-7b-instruct", missingStatus.ModelId);
        Assert.False(string.IsNullOrWhiteSpace(missingStatus.StatusText));
        Assert.False(string.IsNullOrWhiteSpace(missingStatus.Description));
    }

    [Fact]
    public void WasapiAudioCaptureService_StartsReadsAndStopsCleanly()
    {
        using var capture = new WasapiAudioCaptureService();
        capture.Start(null);
        Thread.Sleep(150);

        var buffer = new float[1024];
        capture.CopyLatestSamples(buffer);
        capture.Stop();

        Assert.True(capture.SampleRate >= 8000);
    }

    [Fact]
    public async Task WindowsOcrService_RecognizesRenderedEnglishText()
    {
        const int width = 420;
        const int height = 100;

        byte[] bgraPixels = new byte[width * height * 4];
        var thread = new Thread(() =>
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                var ft = new FormattedText(
                    "HELLO WORLD",
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    40,
                    Brushes.Black,
                    1.0);
                dc.DrawText(ft, new Point(30, 22));
            }

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.CopyPixels(bgraPixels, width * 4, 0);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        using var ocrService = new WindowsOcrService();
        var region = new CapturedScreenRegion(width, height, bgraPixels);
        var result = await ocrService.RecognizeAsync(region, autoDetectLanguage: false, preferredLanguageTag: "en-US");

        // Fresh Windows Server CI runners may ship without any Windows.Media.Ocr language packs installed.
        if (!result.Success && IsMissingWindowsOcrPackError(result.ErrorMessage))
            return;

        Assert.True(result.Success, $"OCR failed: {result.ErrorMessage}");
        Assert.Contains("HELLO", result.Text.ToUpperInvariant());
        Assert.Contains("WORLD", result.Text.ToUpperInvariant());
    }

    private static bool IsMissingWindowsOcrPackError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;
        return errorMessage.Contains("no installed", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("no language", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("нет установленных", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("not installed", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("NO_OCR_LANG_PACKS", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsOcrService_ScorerPrefersRealCyrillicOverLatinHallucination()
    {
        // Classic Windows OCR tie bug: en-US misreads Russian "Привет мир" as "npuBeT Mup" (same letter count!)
        var candidates = new List<OcrCandidate>
        {
            new(0, "en-US", "npuBeT Mup"),
            new(0, "ru", "Привет мир"),
            new(1, "ru", "Привет мир")
        };

        var best = WindowsOcrService.SelectBestCandidate(candidates);
        Assert.Equal("Привет мир", best.CleanText);
        Assert.True(best.ResolvedLanguageTag.StartsWith("ru", StringComparison.OrdinalIgnoreCase));

        // Reverse check: clean English vs Cyrillic-garbled English
        var enCandidates = new List<OcrCandidate>
        {
            new(0, "ru", "Не11о Wоr1d"),
            new(0, "en-US", "Hello World")
        };
        var bestEn = WindowsOcrService.SelectBestCandidate(enCandidates);
        Assert.Equal("Hello World", bestEn.CleanText);
        Assert.True(bestEn.ResolvedLanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));

        // Border-noise line cleanup
        Assert.Equal("Hello World", WindowsOcrService.CleanOcrText("|\n| Hello World |\n_"));
    }

    [Fact]
    public async Task WindowsOcrService_RecognizesDiverseFontsSizesThemesAndColors()
    {
        static CapturedScreenRegion RenderSample(
            string text,
            string fontFamily,
            double fontSizePx,
            FontStyle style,
            FontWeight weight,
            Func<Brush> foregroundFactory,
            Func<Brush> backgroundFactory,
            int padX = 10,
            int padY = 6)
        {
            int width = 100;
            int height = 40;
            byte[] pixels = Array.Empty<byte>();

            var thread = new Thread(() =>
            {
                var foreground = foregroundFactory();
                var background = backgroundFactory();
                var ft = new FormattedText(
                    text,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily(fontFamily), style, weight, FontStretches.Normal),
                    fontSizePx,
                    foreground,
                    1.0);

                width = Math.Max(32, (int)Math.Ceiling(ft.Width) + padX * 2);
                height = Math.Max(16, (int)Math.Ceiling(ft.Height) + padY * 2);

                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
                    dc.DrawText(ft, new Point(padX, padY));
                }

                var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                pixels = new byte[width * height * 4];
                rtb.CopyPixels(pixels, width * 4, 0);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            return new CapturedScreenRegion(width, height, pixels);
        }

        using var ocrService = new WindowsOcrService();

        // 1. Tiny 11px UI font on dark VS Code / Discord theme with tight 2px crop
        var tinyDark = RenderSample(
            "Download update", "Segoe UI", 11, FontStyles.Normal, FontWeights.Normal,
            () => new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            () => new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            padX: 2, padY: 2);
        var r1 = await ocrService.RecognizeAsync(tinyDark, autoDetectLanguage: true);
        if (!r1.Success && IsMissingWindowsOcrPackError(r1.ErrorMessage))
            return;
        Assert.True(r1.Success, $"Tiny dark UI failed: {r1.ErrorMessage}");
        Assert.Contains("DOWNLOAD", r1.Text.ToUpperInvariant());

        // 2. Small monospace code font on dark navy
        var monoCode = RenderSample(
            "Settings saved", "Consolas", 13, FontStyles.Normal, FontWeights.Normal,
            () => new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)),
            () => new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)),
            padX: 4, padY: 3);
        var r2 = await ocrService.RecognizeAsync(monoCode, autoDetectLanguage: true);
        Assert.True(r2.Success, $"Small Consolas failed: {r2.ErrorMessage}");
        Assert.Contains("SETTINGS", r2.Text.ToUpperInvariant());

        // 3. Giant 140px bold display header (banner/poster/meme size)
        var giantHeader = RenderSample(
            "WARNING", "Arial Black", 140, FontStyles.Normal, FontWeights.Bold,
            () => Brushes.White,
            () => new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)),
            padX: 18, padY: 14);
        var r3 = await ocrService.RecognizeAsync(giantHeader, autoDetectLanguage: true);
        Assert.True(r3.Success, $"Giant 140px header failed: {r3.ErrorMessage}");
        Assert.Contains("WARNING", r3.Text.ToUpperInvariant());

        // 4. Colored text on colored background (bright red on dark teal)
        var coloredOnColored = RenderSample(
            "Play Music", "Segoe UI", 26, FontStyles.Normal, FontWeights.Bold,
            () => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
            () => new SolidColorBrush(Color.FromRgb(0x11, 0x5E, 0x59)),
            padX: 10, padY: 8);
        var r4 = await ocrService.RecognizeAsync(coloredOnColored, autoDetectLanguage: true);
        Assert.True(r4.Success, $"Colored-on-colored failed: {r4.ErrorMessage}");
        Assert.Contains("MUSIC", r4.Text.ToUpperInvariant());

        // 5. Russian Cyrillic italic serif text with auto-detection (only asserted when the OS has a Russian OCR language pack)
        var russianItalic = RenderSample(
            "Привет мир", "Georgia", 24, FontStyles.Italic, FontWeights.Normal,
            () => new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
            () => new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
            padX: 10, padY: 8);
        var r5 = await ocrService.RecognizeAsync(russianItalic, autoDetectLanguage: true);
        if (r5.Success && r5.DetectedLanguageTag.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Contains("привет", r5.Text.ToLowerInvariant());
        }

        // 6. Serif italic light text on a diagonal gradient background
        var gradientSerif = RenderSample(
            "Hello Friend", "Times New Roman", 26, FontStyles.Italic, FontWeights.Normal,
            () => new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
            () => new LinearGradientBrush(
                Color.FromRgb(0x1E, 0x29, 0x3B),
                Color.FromRgb(0x4C, 0x1D, 0x95),
                new Point(0, 0),
                new Point(1, 1)),
            padX: 12, padY: 10);
        var r6 = await ocrService.RecognizeAsync(gradientSerif, autoDetectLanguage: true);
        Assert.True(r6.Success, $"Gradient serif failed: {r6.ErrorMessage}");
        Assert.Contains("HELLO", r6.Text.ToUpperInvariant());

        // 7. Low-contrast muted gray text on dark gray
        var lowContrast = RenderSample(
            "System Ready", "Calibri", 18, FontStyles.Normal, FontWeights.Normal,
            () => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            () => new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)),
            padX: 8, padY: 6);
        var r7 = await ocrService.RecognizeAsync(lowContrast, autoDetectLanguage: true);
        Assert.True(r7.Success, $"Low-contrast gray failed: {r7.ErrorMessage}");
        Assert.Contains("SYSTEM", r7.Text.ToUpperInvariant());

        // 8. Ultra-tiny 8pt (10.5px) Windows tooltip font with 1px tight crop
        var ultraTiny = RenderSample(
            "Confirm action", "Tahoma", 10.5, FontStyles.Normal, FontWeights.Normal,
            () => Brushes.Black,
            () => new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
            padX: 1, padY: 1);
        var r8 = await ocrService.RecognizeAsync(ultraTiny, autoDetectLanguage: true);
        Assert.True(r8.Success, $"Ultra-tiny 8pt font failed: {r8.ErrorMessage}");
        Assert.Contains("CONFIRM", r8.Text.ToUpperInvariant());

        // 9. Stylized font (Comic Sans Bold yellow on dark purple)
        var stylizedComic = RenderSample(
            "Quick Start", "Comic Sans MS", 22, FontStyles.Normal, FontWeights.Bold,
            () => new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)),
            () => new SolidColorBrush(Color.FromRgb(0x3B, 0x07, 0x64)),
            padX: 10, padY: 8);
        var r9 = await ocrService.RecognizeAsync(stylizedComic, autoDetectLanguage: true);
        Assert.True(r9.Success, $"Stylized Comic Sans yellow-on-purple failed: {r9.ErrorMessage}");
        Assert.Contains("QUICK", r9.Text.ToUpperInvariant());

        // 10. Game/video subtitle style: crisp white bold text on noisy/striped dark-to-mid background
        var subtitleGrad = RenderSample(
            "Press Space", "Trebuchet MS", 24, FontStyles.Normal, FontWeights.Bold,
            () => Brushes.White,
            () => new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromRgb(0x11, 0x18, 0x27), 0.0),
                    new(Color.FromRgb(0x47, 0x55, 0x69), 0.35),
                    new(Color.FromRgb(0x1E, 0x29, 0x3B), 0.65),
                    new(Color.FromRgb(0x33, 0x41, 0x55), 1.0)
                },
                new Point(0, 0),
                new Point(1, 0.4)),
            padX: 10, padY: 8);
        var r10 = await ocrService.RecognizeAsync(subtitleGrad, autoDetectLanguage: true);
        Assert.True(r10.Success, $"Subtitle-style textured bg failed: {r10.ErrorMessage}");
        Assert.Contains("SPACE", r10.Text.ToUpperInvariant());

        // 11. Exact real-world meme case: pale yellow text ("me in 15 yrs still doing\nmy fake scenarios")
        // where the left letter "m" sits over a bright beige wall (#B7A9A6) while the rest sits over dark shadows (#2B2B2E)
        var splitBgMeme = RenderSample(
            "me in 15 yrs still doing\nmy fake scenarios", "Arial", 36, FontStyles.Normal, FontWeights.SemiBold,
            () => new SolidColorBrush(Color.FromRgb(0xFE, 0xFB, 0xB4)),
            () => new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromRgb(0xB7, 0xA9, 0xA6), 0.0),
                    new(Color.FromRgb(0xB7, 0xA9, 0xA6), 0.14),
                    new(Color.FromRgb(0x2B, 0x2B, 0x2E), 0.22),
                    new(Color.FromRgb(0x48, 0x48, 0x4C), 0.70),
                    new(Color.FromRgb(0x1F, 0x1F, 0x22), 1.0)
                },
                new Point(0, 0),
                new Point(1, 0)),
            padX: 14, padY: 10);
        var r11 = await ocrService.RecognizeAsync(splitBgMeme, autoDetectLanguage: true);
        Assert.True(r11.Success, $"Split bright/dark photo meme failed: {r11.ErrorMessage}");
        string reflowed11 = TranslationCoordinator.ReflowWrappedOcrLines(r11.Text);
        Assert.Contains("me in 15 yrs", reflowed11.ToLowerInvariant());
        Assert.Contains("fake scenarios", reflowed11.ToLowerInvariant());

        // Verify superstring completeness promotion: clipped "e in 15 yrs" never beats full "me in 15 yrs"
        var clippedVsFull = WindowsOcrService.SelectBestCandidate(
        [
            new(0, "en-US", "e in 15 yrs still doing\nmy fake scenarios"),
            new(1, "en-US", "e in 15 yrs still doing\nmy fake scenarios"),
            new(2, "en-US", "me in 15 yrs still doing\nmy fake scenarios")
        ]);
        Assert.True(clippedVsFull.CleanText.StartsWith("me in 15", StringComparison.OrdinalIgnoreCase));

        // Verify Offline ONNX translates the reflowed meme sentence coherently without dropping "still doing"
        using var offline = new OnnxOfflineTranslationService();
        if (offline.IsModelInstalled("opus-mt-en-ru"))
        {
            var onnxRes = await offline.TranslateAsync(reflowed11, "opus-mt-en-ru", "en", "ru");
            Assert.True(onnxRes.Success, $"ONNX failed: {onnxRes.ErrorMessage}");
            Console.WriteLine($"    [MEME OCR -> ONNX] '{reflowed11}' => '{onnxRes.TranslatedText}'");
            Assert.True(onnxRes.TranslatedText.Length >= 25, $"Expected complete translation, got: '{onnxRes.TranslatedText}'");
        }
    }

    [Fact]
    public void IslandViewModel_MouseBattery_UpdatesCompactVisibilityAndSamplesSafely()
    {
        var vm = new IslandViewModel();
        var s = new AppSettings
        {
            EnableMouseBatteryIndicator = true,
            ShowMouseBatteryInCompact = true,
            MouseLowBatteryThresholdPercent = 15,
            MouseConnectionMode = MouseConnectionMode.Auto
        };
        vm.ConfigureSystemIndicators(s);

        // No device yet -> Compact badge stays hidden, Expanded text falls back to "--%"
        Assert.False(vm.HasMouseBattery);
        Assert.False(vm.ShowCompactMouseBattery);
        Assert.Equal("--%", vm.MouseBatteryText);

        // Mouse detected (84%, charging) and music is NOT active -> shown in Compact
        vm.UpdateMouseBattery(new MouseBatterySnapshot(true, 84, true, "Logitech G Pro", "2.4 ГГц"));
        Assert.True(vm.HasMouseBattery);
        Assert.True(vm.IsMouseCharging);
        Assert.False(vm.IsMouseBatteryLow);
        Assert.Equal("84%", vm.MouseBatteryText);
        Assert.True(vm.ShowCompactMouseBattery);

        // Music starts playing in Compact -> Compact mouse badge hides automatically
        vm.TrackInfo = new MediaTrackInfo("Artist", "Track", true, true, null, Color.FromRgb(0x4C, 0xC9, 0xF0));
        vm.ShowMusicInCompact = true;
        Assert.False(vm.ShowCompactMouseBattery);

        // Low battery & not charging -> IsMouseBatteryLow becomes true
        vm.ShowMusicInCompact = false;
        vm.UpdateMouseBattery(new MouseBatterySnapshot(true, 12, false, "BT Mouse", "Bluetooth"));
        Assert.True(vm.IsMouseBatteryLow);
        Assert.True(vm.ShowCompactMouseBattery);

        // Native P/Invoke enumeration executes cleanly in all modes
        var autoSample = MouseBatteryService.Sample(MouseConnectionMode.Auto);
        Assert.True(autoSample.Percent is >= 0 and <= 100);

        // Attack Shark X3 (event 0x4010 -> byte[1] == 0x10) encodes 1..10 step level (10%..100%)
        Assert.True(MouseBatteryService.TryParseBekenBatteryPacket(
            new byte[] { 0x03, 0x10, 0x40, 0x01, 0x09 }, 5, out int pctX3Ninety, out bool chgX3Ninety));
        Assert.Equal(90, pctX3Ninety);
        Assert.False(chgX3Ninety);

        Assert.True(MouseBatteryService.TryParseBekenBatteryPacket(
            new byte[] { 0x03, 0x10, 0x40, 0x01, 0x0A }, 5, out int pctX3Full, out bool chgX3Full));
        Assert.Equal(100, pctX3Full);
        Assert.False(chgX3Full);

        Assert.True(MouseBatteryService.TryParseBekenBatteryPacket(
            new byte[] { 0x03, 0x10, 0x40, 0x03, 0x04 }, 5, out int pctX3Charging, out bool chgX3Charging));
        Assert.Equal(40, pctX3Charging);
        Assert.True(chgX3Charging);

        // Other Beken models (e.g. X6/X11/R1) encode direct 0..100%
        Assert.True(MouseBatteryService.TryParseBekenBatteryPacket(
            new byte[] { 0x03, 0x8E, 0x40, 0x03, 0x64 }, 5, out int pctCharging, out bool chgCharging));
        Assert.Equal(100, pctCharging);
        Assert.True(chgCharging);

        Assert.True(MouseBatteryService.TryParseBekenBatteryPacket(
            new byte[] { 0x03, 0x55, 0x40, 0x01, 0x2D }, 5, out int pctDirect, out bool chgDirect));
        Assert.Equal(45, pctDirect);
        Assert.False(chgDirect);

        Assert.False(MouseBatteryService.TryParseBekenBatteryPacket(
            new byte[] { 0x03, 0x10, 0x10, 0x01, 0x05 }, 5, out _, out _));

        // Bluetooth ContainerId grouping: ignores BT headphones and resolves mouse even when battery sits on sibling GATT service node
        var audioCid = Guid.NewGuid();
        var mouseCid = Guid.NewGuid();
        var onlyAudio = MouseBatteryService.SelectBestBluetoothMouseBattery(
        [
            new MouseBatteryService.PnpBatteryNodeInfo(audioCid, @"BTH\DEV_AA", "AudioEndpoint", "BTH", "WH-1000XM5", 80, false)
        ]);
        Assert.False(onlyAudio.HasDevice);

        var btMouse = MouseBatteryService.SelectBestBluetoothMouseBattery(
        [
            new MouseBatteryService.PnpBatteryNodeInfo(audioCid, @"BTH\DEV_AA", "AudioEndpoint", "BTH", "WH-1000XM5", 80, false),
            new MouseBatteryService.PnpBatteryNodeInfo(mouseCid, @"BTHLE\DEV_BB", "Bluetooth", "BTHLE", "Attack Shark X3", null, false),
            new MouseBatteryService.PnpBatteryNodeInfo(mouseCid, @"BTHLEDEVICE\{0000180F-0000-1000-8000-00805F9B34FB}_DEV_BB", "HIDClass", "BTHLEDevice", "Служба батареи Bluetooth LE", 72, false),
            new MouseBatteryService.PnpBatteryNodeInfo(mouseCid, @"HID\{00001812-0000-1000-8000-00805F9B34FB}_DEV_BB", "Mouse", "HID", "HID-совместимая мышь", null, false)
        ]);
        Assert.True(btMouse.HasDevice);
        Assert.Equal(72, btMouse.Percent);
        Assert.False(btMouse.IsCharging);
        Assert.Equal("Attack Shark X3", btMouse.DeviceName);
        Assert.Equal("Bluetooth", btMouse.ConnectionLabel);
    }

    [Fact]
    public void LassoGeometry_ComputeRectangleBox_ShrinksWhenCursorMovesBack()
    {
        var bounds = new Rect(0, 0, 1920, 1080);
        var start = new Point(200, 150);

        // User drags wide first
        var wide = LassoGeometryHelper.ComputeRectangleBox(start, new Point(600, 450), bounds, padding: 0);
        Assert.NotNull(wide);
        Assert.Equal(400, wide!.Value.Width);
        Assert.Equal(300, wide.Value.Height);

        // User pulls cursor back -> selection shrinks immediately!
        var shrunk = LassoGeometryHelper.ComputeRectangleBox(start, new Point(340, 210), bounds, padding: 0);
        Assert.NotNull(shrunk);
        Assert.Equal(140, shrunk!.Value.Width);
        Assert.Equal(60, shrunk.Value.Height);
        Assert.True(shrunk.Value.Width < wide.Value.Width);
        Assert.True(shrunk.Value.Height < wide.Value.Height);

        // Dragging right-to-left / bottom-to-top also works cleanly
        var reverse = LassoGeometryHelper.ComputeRectangleBox(new Point(500, 400), new Point(300, 250), bounds, padding: 0);
        Assert.NotNull(reverse);
        Assert.Equal(300, reverse!.Value.X);
        Assert.Equal(250, reverse.Value.Y);
        Assert.Equal(200, reverse.Value.Width);
        Assert.Equal(150, reverse.Value.Height);

        // Thin horizontal underline still expands vertically for single-line text
        var underline = LassoGeometryHelper.ComputeRectangleBox(new Point(100, 200), new Point(320, 202), bounds, padding: 0);
        Assert.NotNull(underline);
        Assert.True(underline!.Value.Height >= 30);
    }

    [Fact]
    public void AiResponseFormatter_ConvertsLatexAndMarkdownToReadableUnicodeAndFlowDocument()
    {
        string raw = "<think>internal reasoning</think>\n" +
                     "### Формулы двойного угла\n" +
                     "Основное тождество: \\(\\sin(2\\alpha) = 2\\sin(\\alpha)\\cos(\\alpha)\\) и " +
                     "\\(\\cos(2\\alpha) = \\cos^2(\\alpha) - \\sin^2(\\alpha) = 2\\cos^2(\\alpha) - 1 = 1 - 2\\sin^2(\\alpha)\\).\n" +
                     "- Дробь и корень: $$\\frac{a+b}{2} \\ge \\sqrt{a \\cdot b}$$\n" +
                     "- **Важно**: цена $100 не должна ломаться, а `code` сохраняется.";

        string plain = AiResponseFormatter.FormatToReadablePlainText(raw);
        Assert.DoesNotContain("<think>", plain);
        Assert.DoesNotContain(@"\alpha", plain);
        Assert.DoesNotContain(@"\sin", plain);
        Assert.DoesNotContain(@"\(", plain);
        Assert.Contains("sin(2α)", plain);
        Assert.Contains("cos²(α)", plain);
        Assert.Contains("sin²(α)", plain);
        Assert.Contains("(a+b)/2 ≥ √(a · b)", plain);
        Assert.Contains("• Важно:", plain);
        Assert.Contains("$100", plain);

        // Verify WPF FlowDocument builds cleanly without exceptions on an STA thread
        int blockCount = 0;
        Exception? uiEx = null;
        var sta = new Thread(() =>
        {
            try
            {
                var doc = AiResponseFormatter.BuildFlowDocument(
                    raw,
                    Brushes.White,
                    Brushes.Gold,
                    Brushes.Black,
                    Brushes.Gray);
                blockCount = doc.Blocks.Count;
            }
            catch (Exception ex)
            {
                uiEx = ex;
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();

        Assert.Null(uiEx);
        Assert.True(blockCount >= 3);
    }

    [Fact]
    public void LocalLlmCatalogAndVision_SupportsVlmModelsMmprojAndInMemoryJpegEncoding()
    {
        var vl3 = LocalLlmCatalogService.FindModel("qwen2.5-vl-3b-instruct");
        var vl7 = LocalLlmCatalogService.FindModel("qwen2.5-vl-7b-instruct");
        var text7 = LocalLlmCatalogService.FindModel("qwen2.5-7b-instruct");

        Assert.True(vl3.SupportsVision);
        Assert.True(vl7.SupportsVision);
        Assert.False(text7.SupportsVision);
        Assert.False(string.IsNullOrWhiteSpace(vl3.MmprojFileName));
        Assert.False(string.IsNullOrWhiteSpace(vl3.MmprojDownloadUrl));
        Assert.False(string.IsNullOrWhiteSpace(vl7.MmprojFileName));
        Assert.False(string.IsNullOrWhiteSpace(vl7.MmprojDownloadUrl));
        Assert.True(vl3.TotalApproximateBytes > vl3.ApproximateBytes);

        string? mmprojPath = LocalLlmCatalogService.GetMmprojFilePath(vl3.ModelId);
        Assert.NotNull(mmprojPath);
        Assert.Contains("mmproj", mmprojPath!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(LocalLlmCatalogService.GetMmprojFilePath(text7.ModelId));

        var prevLang = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.ApplyLanguage(AppLanguage.English);
            var (sysEn, usrEn) = LocalLlmService.BuildImageQuestionPrompts("What is in this photo?", "Hello");
            Assert.Contains("vision", sysEn, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("What is in this photo?", usrEn);
            Assert.Contains("Hello", usrEn);

            LocalizationService.ApplyLanguage(AppLanguage.Russian);
            var (sysRu, usrRu) = LocalLlmService.BuildImageQuestionPrompts("Что на картинке?", "Hello");
            Assert.Contains("зрением", sysRu, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Что на картинке?", usrRu);
            Assert.Contains("Hello", usrRu);
        }
        finally
        {
            LocalizationService.ApplyLanguage(prevLang);
        }

        // Encode a 1600x900 synthetic BGRA32 buffer to JPEG in memory and verify JPEG SOI header (FF D8)
        int w = 1600, h = 900;
        byte[] bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 200;
            bgra[i + 1] = 120;
            bgra[i + 2] = 60;
            bgra[i + 3] = 255;
        }
        var region = new CapturedScreenRegion(w, h, bgra);
        byte[] jpeg = ScreenCaptureService.EncodeToJpegBytes(region, maxSide: 800, quality: 82);
        Assert.True(jpeg.Length > 200);
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
    }

    [Fact]
    public void WindowsOcrService_NormalizesDegreeSymbolsAndClipboardBitmapZeroAlpha()
    {
        Assert.Equal("25°C", WindowsOcrService.NormalizeDegreeSymbols("25 oC"));
        Assert.Equal("+24°C", WindowsOcrService.NormalizeDegreeSymbols("+240C"));
        Assert.Equal("-15°C", WindowsOcrService.NormalizeDegreeSymbols("-150C"));
        Assert.Equal("0°C", WindowsOcrService.NormalizeDegreeSymbols("00C"));
        Assert.Equal("25°C", WindowsOcrService.NormalizeDegreeSymbols("25ºС"));
        Assert.Equal("Температура 22°C, ветер 3 м/с", WindowsOcrService.NormalizeDegreeSymbols("Температура 220C, ветер 3 м/с"));
        // Must not corrupt hex constants or unrelated codes
        Assert.Equal("0x200C", WindowsOcrService.NormalizeDegreeSymbols("0x200C"));
        Assert.Equal("Model 200C", WindowsOcrService.NormalizeDegreeSymbols("Model 200C"));

        // Verify ScreenCaptureService.FromBitmapSource fixes zero-alpha CF_DIB clipboard screenshots
        byte[] zeroAlphaBgra = new byte[4 * 4 * 4];
        for (int i = 0; i < zeroAlphaBgra.Length; i += 4)
        {
            zeroAlphaBgra[i] = 90;
            zeroAlphaBgra[i + 1] = 140;
            zeroAlphaBgra[i + 2] = 210;
            zeroAlphaBgra[i + 3] = 0; // typical Windows clipboard DIB alpha=0
        }
        var bmp = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, zeroAlphaBgra, 16);
        var converted = ScreenCaptureService.FromBitmapSource(bmp);
        Assert.NotNull(converted);
        Assert.Equal(255, converted!.Bgra32Pixels[3]);
    }

    [Fact]
    public async Task PaddleOcrOnnxService_RecognizesCursiveHandwritingDegreesMathAndAllFonts()
    {
        if (!PaddleOcrOnnxService.IsInstalled())
            return;

        static CapturedScreenRegion RenderSample(
            string text,
            string fontFamily,
            double fontSizePx,
            FontStyle style,
            FontWeight weight,
            Func<Brush> fgFactory,
            Func<Brush> bgFactory,
            int padX = 12,
            int padY = 10)
        {
            int width = 0, height = 0;
            byte[] pixels = Array.Empty<byte>();
            var thread = new Thread(() =>
            {
                var fg = fgFactory();
                var bg = bgFactory();
                var ft = new FormattedText(
                    text,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily(fontFamily), style, weight, FontStretches.Normal),
                    fontSizePx,
                    fg,
                    1.0);
                width = Math.Max(32, (int)Math.Ceiling(ft.Width) + padX * 2);
                height = Math.Max(18, (int)Math.Ceiling(ft.Height) + padY * 2);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
                    dc.DrawText(ft, new Point(padX, padY));
                }
                var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                pixels = new byte[width * height * 4];
                rtb.CopyPixels(pixels, width * 4, 0);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return new CapturedScreenRegion(width, height, pixels);
        }

        using var paddle = new PaddleOcrOnnxService();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Russian cursive / handwritten script font (Segoe Script)
        var ruCursive = RenderSample(
            "Привет мир", "Segoe Script", 28, FontStyles.Normal, FontWeights.Normal,
            () => new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)),
            () => Brushes.White);
        var (rRuCursive, c1) = await paddle.RecognizeAsync(ruCursive);
        Console.WriteLine($"    [PP-OCRv5 RU Cursive] '{rRuCursive.Text}' (conf={c1:0.00})");
        Assert.True(rRuCursive.Success, $"RU cursive failed: {rRuCursive.ErrorMessage}");
        Assert.Contains("привет", rRuCursive.Text.ToLowerInvariant());
        Assert.Contains("мир", rRuCursive.Text.ToLowerInvariant());

        // 2. English handwritten script font (Lucida Handwriting)
        var enHand = RenderSample(
            "Beautiful script", "Lucida Handwriting", 26, FontStyles.Normal, FontWeights.Normal,
            () => new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),
            () => new SolidColorBrush(Color.FromRgb(0xFD, 0xFA, 0xF0)));
        var (rEnHand, c2) = await paddle.RecognizeAsync(enHand);
        Console.WriteLine($"    [PP-OCRv5 EN Handwriting] '{rEnHand.Text}' (conf={c2:0.00})");
        Assert.True(rEnHand.Success);
        Assert.Contains("beautiful", rEnHand.Text.ToLowerInvariant());
        Assert.Contains("script", rEnHand.Text.ToLowerInvariant());

        // 3. Calligraphic font (Monotype Corsiva)
        var corsiva = RenderSample(
            "Quick brown fox", "Monotype Corsiva", 30, FontStyles.Italic, FontWeights.Normal,
            () => new SolidColorBrush(Color.FromRgb(0x31, 0x2E, 0x81)),
            () => Brushes.White);
        var (rCorsiva, c3) = await paddle.RecognizeAsync(corsiva);
        Console.WriteLine($"    [PP-OCRv5 Corsiva] '{rCorsiva.Text}' (conf={c3:0.00})");
        Assert.True(rCorsiva.Success);
        Assert.Contains("quick", rCorsiva.Text.ToLowerInvariant());
        Assert.Contains("fox", rCorsiva.Text.ToLowerInvariant());

        // 4. Exact Celsius degrees (°C must NOT become 0C!)
        var degrees = RenderSample(
            "Температура +25°C и -15°C, 0°C", "Segoe UI", 20, FontStyles.Normal, FontWeights.SemiBold,
            () => Brushes.White,
            () => new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)));
        var (rDeg, c4) = await paddle.RecognizeAsync(degrees);
        Console.WriteLine($"    [PP-OCRv5 Degrees] '{rDeg.Text}' (conf={c4:0.00})");
        Assert.True(rDeg.Success);
        Assert.Contains("25°C", rDeg.Text);
        Assert.Contains("-15°C", rDeg.Text);
        Assert.Contains("0°C", rDeg.Text);

        // 5. Math formula with Greek alpha and superscript on gradient background
        var mathGrad = RenderSample(
            "sin α = 3/5, cos²α + sin²α = 1", "Cambria", 26, FontStyles.Italic, FontWeights.Normal,
            () => new SolidColorBrush(Color.FromRgb(0x7F, 0x1D, 0x1D)),
            () => new LinearGradientBrush(Colors.White, Color.FromRgb(0xBE, 0xF2, 0x64), new Point(0, 0), new Point(1, 0)));
        var (rMath, c5) = await paddle.RecognizeAsync(mathGrad);
        Console.WriteLine($"    [PP-OCRv5 Math Gradient] '{rMath.Text}' (conf={c5:0.00})");
        Assert.True(rMath.Success);
        Assert.Contains("sin", rMath.Text.ToLowerInvariant());
        Assert.Contains("3/5", rMath.Text);

        // 6. Tiny 10.5px UI font on dark theme
        var tinyUi = RenderSample(
            "Download update", "Segoe UI", 10.5, FontStyles.Normal, FontWeights.Normal,
            () => new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            () => new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            padX: 3, padY: 3);
        var (rTiny, c6) = await paddle.RecognizeAsync(tinyUi);
        Console.WriteLine($"    [PP-OCRv5 Tiny 10.5px] '{rTiny.Text}' (conf={c6:0.00})");
        Assert.True(rTiny.Success);
        Assert.Contains("download", rTiny.Text.ToLowerInvariant());
        Assert.Contains("update", rTiny.Text.ToLowerInvariant());

        // 7. Split bright/dark meme photo
        var splitMeme = RenderSample(
            "me in 15 yrs still doing\nmy fake scenarios", "Arial", 36, FontStyles.Normal, FontWeights.SemiBold,
            () => new SolidColorBrush(Color.FromRgb(0xFE, 0xFB, 0xB4)),
            () => new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromRgb(0xB7, 0xA9, 0xA6), 0.0),
                    new(Color.FromRgb(0xB7, 0xA9, 0xA6), 0.14),
                    new(Color.FromRgb(0x2B, 0x2B, 0x2E), 0.22),
                    new(Color.FromRgb(0x48, 0x48, 0x4C), 0.70),
                    new(Color.FromRgb(0x1F, 0x1F, 0x22), 1.0)
                },
                new Point(0, 0),
                new Point(1, 0)),
            padX: 14, padY: 10);
        var (rMeme, c7) = await paddle.RecognizeAsync(splitMeme);
        Console.WriteLine($"    [PP-OCRv5 Split Meme] '{rMeme.Text.Replace("\r\n", " / ").Replace("\n", " / ")}' (conf={c7:0.00})");
        Assert.True(rMeme.Success);
        Assert.Contains("me in 15", rMeme.Text.ToLowerInvariant());
        Assert.Contains("fake scenarios", rMeme.Text.ToLowerInvariant());

        // 8. Additional Russian cursive/print & stylized fonts (Segoe Print + Gabriola + Russian Italic)
        var ruPrint = RenderSample(
            "Ёжик бежит домой", "Segoe Print", 24, FontStyles.Normal, FontWeights.Normal,
            () => new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)),
            () => new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)));
        var (rRuPrint, cRuPrint) = await paddle.RecognizeAsync(ruPrint);
        Console.WriteLine($"    [PP-OCRv5 RU Segoe Print] '{rRuPrint.Text}' (conf={cRuPrint:0.00})");
        Assert.True(rRuPrint.Success);
        Assert.Contains("бежит", rRuPrint.Text.ToLowerInvariant());
        Assert.Contains("домой", rRuPrint.Text.ToLowerInvariant());

        var ruGabriola = RenderSample(
            "Красивый шрифт", "Gabriola", 34, FontStyles.Normal, FontWeights.Normal,
            () => new SolidColorBrush(Color.FromRgb(0x3B, 0x07, 0x64)),
            () => Brushes.White);
        var (rGab, cGab) = await paddle.RecognizeAsync(ruGabriola);
        Console.WriteLine($"    [PP-OCRv5 RU Gabriola] '{rGab.Text}' (conf={cGab:0.00})");
        Assert.True(rGab.Success);
        Assert.Contains("красивый", rGab.Text.ToLowerInvariant());
        Assert.Contains("шриф", rGab.Text.ToLowerInvariant());

        Console.WriteLine($"    [PP-OCRv5 Benchmark Total Time] {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void MouseBatteryService_CompxAtkVxeLamzuPulsarProtocols_BuildAndParseSafely()
    {
        // 1) Compx / VXE / ATK request checksum: 0x55 - (0x08 + 0x04) = 0x49
        byte[] reqWithRid = MouseBatteryService.BuildCompxBatteryRequest(17, reportId: 0x08, includeReportIdInChecksum: true);
        Assert.Equal(17, reqWithRid.Length);
        Assert.Equal(0x08, reqWithRid[0]);
        Assert.Equal(0x04, reqWithRid[1]);
        Assert.Equal(0x49, reqWithRid[16]);

        byte[] reqPayloadOnly = MouseBatteryService.BuildCompxBatteryRequest(17, reportId: 0x08, includeReportIdInChecksum: false);
        Assert.Equal(0x51, reqPayloadOnly[16]);

        // 2) Layout A: VXE R1 / Mad R / VGN F1 / ATK F1 (VID 0x3554)
        byte[] vxePacket = new byte[17];
        vxePacket[0] = 0x08;
        vxePacket[1] = 0x04;
        vxePacket[2] = 0x00; // status OK
        vxePacket[4] = 0x02; // len
        vxePacket[6] = 87;   // 87%
        vxePacket[7] = 0x01; // charging
        Assert.True(MouseBatteryService.TryParseCompxBatteryResponse(vxePacket, 17, out int vxePct, out bool vxeChg));
        Assert.Equal(87, vxePct);
        Assert.True(vxeChg);

        // Sleeping mouse status (0xA1) must be rejected
        vxePacket[2] = 0xA1;
        Assert.False(MouseBatteryService.TryParseCompxBatteryResponse(vxePacket, 17, out _, out _));

        // 3) Layout B: Compx CX52850 / Darmoshark / Zaopin (VID 0x25A7)
        byte[] compxPacket = new byte[17];
        compxPacket[0] = 0x08;
        compxPacket[1] = 0x04;
        compxPacket[2] = 0x00;
        compxPacket[3] = 0x02;
        compxPacket[5] = 63;
        compxPacket[6] = 0x00;
        Assert.True(MouseBatteryService.TryParseCompxBatteryResponse(compxPacket, 17, out int cxPct, out bool cxChg));
        Assert.Equal(63, cxPct);
        Assert.False(cxChg);

        // 3b) Compact Compx packet with charging=0x01 must NOT be misread as 1% not charging
        byte[] compactCharging = new byte[17];
        compactCharging[0] = 0x08;
        compactCharging[1] = 0x04;
        compactCharging[2] = 0x00;
        compactCharging[3] = 85;
        compactCharging[4] = 0x01;
        Assert.True(MouseBatteryService.TryParseCompxBatteryResponse(compactCharging, 17, out int ccPct, out bool ccChg));
        Assert.Equal(85, ccPct);
        Assert.True(ccChg);

        // 4) Pulsar / Lamzu Aurora Nordic 4K/8K packet
        byte[] pulsarPacket = new byte[65];
        pulsarPacket[0] = 0x04;
        pulsarPacket[1] = 0x04;
        pulsarPacket[2] = 95;
        pulsarPacket[3] = 0x00;
        Assert.True(MouseBatteryService.TryParseLamzuPulsarBatteryResponse(pulsarPacket, 65, out int pulPct, out bool pulChg));
        Assert.Equal(95, pulPct);
        Assert.False(pulChg);

        // 5) Enthusiast mouse name matching for Bluetooth & PnP fallback
        Assert.True(MouseBatteryService.IsLikelyMouseName("ATK Blazing Sky F1 Ultimate"));
        Assert.True(MouseBatteryService.IsLikelyMouseName("VXE MAD R Major"));
        Assert.True(MouseBatteryService.IsLikelyMouseName("Pulsar Xlite V3 Mini"));
        Assert.True(MouseBatteryService.IsLikelyMouseName("Lamzu Maya X"));
        Assert.True(MouseBatteryService.IsLikelyMouseName("Scyrox V8"));
        Assert.True(MouseBatteryService.IsLikelyMouseName("Ninjutso Sora V2"));
    }

    [Fact]
    public void AiAssistant_MarkdownTableWhisperAndMultiTurn_WorkCleanly()
    {
        // 1) Markdown table -> WPF FlowDocument Table block
        string mdWithTable =
            "Вот таблица расходов:\n\n" +
            "| Категория | Сумма | Статус |\n" +
            "| --- | --- | --- |\n" +
            "| Сервер | $45 | Оплачено |\n" +
            "| Домен | $12 | Активен |\n";

        bool hasTableBlock = false;
        var sta = new Thread(() =>
        {
            var doc = AiResponseFormatter.BuildFlowDocument(
                mdWithTable,
                Brushes.White,
                Brushes.SkyBlue,
                Brushes.Black,
                Brushes.Gray);
            hasTableBlock = doc.Blocks.OfType<System.Windows.Documents.Table>().Any();
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        Assert.True(hasTableBlock);

        var (tSys, tUsr) = LocalLlmService.BuildTableExtractPrompts("Итог 57");
        Assert.Contains("Markdown", tSys, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Итог 57", tUsr);

        // Verify transparent pixel alpha-blends onto white (never solid black in JPEG)
        byte[] transparentBgra = [0, 0, 0, 0, 10, 20, 30, 255];
        byte[] composited = ScreenCaptureService.CompositeBgraOverWhiteIfNeeded(transparentBgra);
        Assert.Equal(255, composited[0]);
        Assert.Equal(255, composited[1]);
        Assert.Equal(255, composited[2]);
        Assert.Equal(255, composited[3]);
        Assert.Equal(10, composited[4]);

        // Verify currency $10 followed by $x + y$ does not swallow Russian prose into LaTeX math
        string currencyAndMath = AiResponseFormatter.FormatToReadablePlainText("Цена $10 за штуку, формула $x + y$");
        Assert.Contains("$10 за штуку", currencyAndMath);
        Assert.Contains("x + y", currencyAndMath);

        // 2) Whisper transcript sanitizer & portable WAV header writer
        Assert.Equal("Привет, расскажи погоду на завтра.",
            WhisperVoiceService.CleanWhisperTranscript("[BLANK_AUDIO]\nПривет, расскажи погоду на завтра.\n(музыка)"));
        Assert.Equal(string.Empty, WhisperVoiceService.CleanWhisperTranscript("[BLANK_AUDIO]\n[Music]"));
        Assert.True(WhisperVoiceService.WhisperDirectory.StartsWith(PortablePaths.ModelsDirectory, StringComparison.OrdinalIgnoreCase));

        PortablePaths.EnsureDirectoriesExist();
        string tempDir = System.IO.Path.Combine(PortablePaths.CacheDirectory, "temp");
        System.IO.Directory.CreateDirectory(tempDir);
        string tempWav = System.IO.Path.Combine(tempDir, $"test_whisper_{Guid.NewGuid():N}.wav");
        try
        {
            byte[] pcm = new byte[320];
            WhisperVoiceService.WriteWav16Mono(tempWav, pcm, 16000);
            byte[] wavBytes = System.IO.File.ReadAllBytes(tempWav);
            Assert.Equal(44 + pcm.Length, wavBytes.Length);
            Assert.Equal((byte)'R', wavBytes[0]);
            Assert.Equal((byte)'I', wavBytes[1]);
            Assert.Equal((byte)'F', wavBytes[2]);
            Assert.Equal((byte)'F', wavBytes[3]);
        }
        finally
        {
            if (System.IO.File.Exists(tempWav))
                System.IO.File.Delete(tempWav);
        }
    }

    [Fact]
    public void SmartClipboardAndDropShelf_ImageHistoryDeduplicationAndQuickActions()
    {
        using var clip = new SmartClipboardService();
        clip.ClearUnpinned();

        byte[] imgA = new byte[8 * 8 * 4];
        for (int i = 0; i < imgA.Length; i += 4)
        {
            imgA[i] = 40;
            imgA[i + 1] = 120;
            imgA[i + 2] = 220;
            imgA[i + 3] = 255;
        }

        clip.AddImagePixels(imgA, 8, 8);
        clip.AddImagePixels((byte[])imgA.Clone(), 8, 8); // duplicate must deduplicate!
        var snap1 = clip.GetImageSnapshot();
        Assert.Equal(1, snap1.Count);
        Assert.True(clip.TryGetImageRegion(snap1[0].Id, out var reg) && reg is not null);
        Assert.Equal(8, reg!.Width);

        // Push 10 distinct images -> bounded to MaxImageEntries (8)
        for (int k = 1; k <= 10; k++)
        {
            byte[] buf = new byte[8 * 8 * 4];
            Array.Fill(buf, (byte)(k * 17));
            clip.AddImagePixels(buf, 8, 8);
        }
        Assert.Equal(SmartClipboardService.MaxImageEntries, clip.GetImageSnapshot().Count);
        clip.ClearUnpinned();

        // DropShelfItem image action flags
        var jpgItem = new DropShelfItem(Path.Combine(PortablePaths.RootDirectory, "photo.jpg"), "photo.jpg", "120 KB", false);
        Assert.True(jpgItem.IsImageFile);
        Assert.True(jpgItem.CanConvertToPng);
        Assert.True(jpgItem.CanCompressToJpg);

        var pngItem = new DropShelfItem(Path.Combine(PortablePaths.RootDirectory, "icon.png"), "icon.png", "40 KB", false);
        Assert.True(pngItem.IsImageFile);
        Assert.False(pngItem.CanConvertToPng);
        Assert.True(pngItem.CanCompressToJpg);

        var txtItem = new DropShelfItem(Path.Combine(PortablePaths.RootDirectory, "notes.txt"), "notes.txt", "4 KB", false);
        Assert.False(txtItem.IsImageFile);
    }

    [Fact]
    public void IslandViewModel_LiveActivitiesAndTranslationSearch_UpdateCleanly()
    {
        var vm = new IslandViewModel
        {
            IsVolumeAndMicEnabled = true
        };

        // Active mic in Discord + 2 render devices -> compact mic pill & output switcher enabled
        vm.AudioState = new PulseIsland.Audio.AudioEndpointState(
            MasterVolumePercent: 68,
            IsSpeakerMuted: false,
            IsMicMuted: false,
            HasSpeakerDevice: true,
            HasMicDevice: true,
            IsMicActivelyCapturing: true,
            ActiveMicAppName: "Discord",
            CurrentRenderShortName: "Наушники",
            CurrentRenderFullName: "Наушники (HyperX)",
            RenderDeviceCount: 2);

        Assert.True(vm.ShowCompactActiveMicPill);
        Assert.True(vm.CanSwitchAudioOutput);
        Assert.Equal("Наушники", vm.AudioOutputShortLabel);
        Assert.Contains("Discord", vm.MicLabel);
        Assert.Equal("Discord", PulseIsland.Audio.AudioEndpointMonitorService.FormatFriendlyProcessLabel("Discord.exe"));

        // Translation history live search filter
        vm.ClearTranslations();
        vm.AddTranslation(new TranslationRecord("Hello world", "Привет мир", "en", "ru", DateTime.Now, false));
        vm.AddTranslation(new TranslationRecord("Battery level", "Уровень заряда", "en", "ru", DateTime.Now, true));
        Assert.Equal(2, vm.FilteredTranslationHistory.Count);

        vm.TranslationSearchQuery = "заряд";
        Assert.Equal(1, vm.FilteredTranslationHistory.Count);
        Assert.Equal("Уровень заряда", vm.FilteredTranslationHistory[0].TranslatedText);
        Assert.False(vm.ShowEmptyTranslationSearchHint);

        vm.TranslationSearchQuery = "несуществующий_запрос_xyz";
        Assert.Equal(0, vm.FilteredTranslationHistory.Count);
        Assert.True(vm.ShowEmptyTranslationSearchHint);

        vm.TranslationSearchQuery = string.Empty;
        Assert.Equal(2, vm.FilteredTranslationHistory.Count);
        vm.ClearTranslations();

        // AppSettings custom prompt chips & onboarding flag round-trip JSON serialization
        var s = new AppSettings
        {
            HasCompletedOnboarding = true,
            CustomAiPrompts = [new CustomAiPromptChip("Код-ревью", "Проверь код:")]
        };
        string json = System.Text.Json.JsonSerializer.Serialize(s);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.True(loaded.HasCompletedOnboarding);
        Assert.Equal(1, loaded.CustomAiPrompts.Count);
        Assert.Equal("Код-ревью", loaded.CustomAiPrompts[0].Title);
    }

    [Fact]
    public void LocalizationService_EnglishAndRussianDictionaries_HaveFullKeyParityAndLiveSwitch()
    {
        var en = LocalizationService.EnglishDictionary;
        var ru = LocalizationService.RussianDictionary;

        Assert.True(en.Count > 150, $"Expected >150 localization keys, got {en.Count}");
        Assert.Equal(en.Count, ru.Count);

        foreach (var kv in en)
        {
            Assert.True(ru.ContainsKey(kv.Key), $"Missing Russian key: {kv.Key}");
            Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"Empty English value for: {kv.Key}");
            Assert.False(string.IsNullOrWhiteSpace(ru[kv.Key]), $"Empty Russian value for: {kv.Key}");
        }

        var prevLang = LocalizationService.CurrentLanguage;
        AppLanguage? notified = null;
        void Handler(AppLanguage lang) => notified = lang;
        LocalizationService.LanguageChanged += Handler;
        try
        {
            LocalizationService.ApplyLanguage(AppLanguage.English);
            Assert.Equal(AppLanguage.English, notified);
            Assert.False(LocalizationService.IsRussian);
            Assert.Equal("Copy", LocalizationService.Get("Loc_Copy"));
            Assert.Equal("English", LocalizationService.Pick("English", "Русский"));
            Assert.Contains("MB", DiskSpaceSummaryDto.FormatBytes(5 * 1024 * 1024));
            Assert.Equal("English", LanguageCatalog.FindOrDefault("en").DisplayName);

            LocalizationService.ApplyLanguage(AppLanguage.Russian);
            Assert.Equal(AppLanguage.Russian, notified);
            Assert.True(LocalizationService.IsRussian);
            Assert.Equal("Копировать", LocalizationService.Get("Loc_Copy"));
            Assert.Equal("Русский", LocalizationService.Pick("English", "Русский"));
            Assert.Contains("МБ", DiskSpaceSummaryDto.FormatBytes(5 * 1024 * 1024));
            Assert.Equal("Английский", LanguageCatalog.FindOrDefault("en").DisplayName);
        }
        finally
        {
            LocalizationService.LanguageChanged -= Handler;
            LocalizationService.ApplyLanguage(prevLang);
        }
    }

    [Fact]
    public void AppSettings_UiLanguageAndDefaultPrompts_RoundTripAndDetectBuiltIns()
    {
        var defaults = new AppSettings();
        Assert.Equal(AppLanguage.English, defaults.UiLanguage);
        Assert.Equal("en", defaults.TargetLanguage);
        Assert.True(AppSettings.IsBuiltInDefaultPromptSet(defaults.CustomAiPrompts));
        Assert.True(AppSettings.IsBuiltInDefaultPromptSet(AppSettings.GetDefaultCustomAiPrompts(AppLanguage.Russian)));
        Assert.False(AppSettings.IsBuiltInDefaultPromptSet([new CustomAiPromptChip("My Custom", "Do custom thing")]));

        defaults.UiLanguage = AppLanguage.Russian;
        string json = System.Text.Json.JsonSerializer.Serialize(defaults);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(AppLanguage.Russian, loaded.UiLanguage);
    }
}
