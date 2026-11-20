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

        var nGroup = new IslandNotification("1", "Telegram", "DevChat → Alice", "Привет", NotificationKind.Info, DateTime.Now, SenderHint: "DevChat → Alice");
        Assert.Equal("Alice", NotificationActivator.ExtractTelegramChatTarget(nGroup));

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

        var ocrService = new WindowsOcrService();
        var region = new CapturedScreenRegion(width, height, bgraPixels);
        var result = await ocrService.RecognizeAsync(region, autoDetectLanguage: false, preferredLanguageTag: "en-US");

        Assert.True(result.Success, $"OCR failed: {result.ErrorMessage}");
        Assert.Contains("HELLO", result.Text.ToUpperInvariant());
        Assert.Contains("WORLD", result.Text.ToUpperInvariant());
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
    public void LocalizationService_DefaultsToEnglishAndSwitchesToRussianCleanly()
    {
        var previous = LocalizationService.CurrentLanguage;
        try
        {
            var defaults = new AppSettings();
            Assert.Equal(AppLanguage.English, defaults.UiLanguage);

            LocalizationService.ApplyLanguage(AppLanguage.English);
            Assert.False(LocalizationService.IsRussian);
            Assert.Equal("Settings", LocalizationService.Get("Loc_Island_Settings"));
            Assert.Equal("Save settings", LocalizationService.Get("Loc_Settings_SaveBtn"));
            Assert.Equal("English", LanguageCatalog.FindOrDefault("en").DisplayName);
            Assert.Contains("GB", DiskSpaceSummaryDto.FormatBytes(2L * 1024 * 1024 * 1024));

            var vm = new IslandViewModel();
            Assert.Equal("Waiting for music playback", vm.ExpandedTrackTitle);
            Assert.Contains("Mic", vm.MicLabel);

            LocalizationService.ApplyLanguage(AppLanguage.Russian);
            Assert.True(LocalizationService.IsRussian);
            Assert.Equal("Настройки", LocalizationService.Get("Loc_Island_Settings"));
            Assert.Equal("Сохранить настройки", LocalizationService.Get("Loc_Settings_SaveBtn"));
            Assert.Equal("Английский", LanguageCatalog.FindOrDefault("en").DisplayName);
            Assert.Contains("ГБ", DiskSpaceSummaryDto.FormatBytes(2L * 1024 * 1024 * 1024));

            vm.RefreshLocalizedProperties();
            Assert.Equal("Ожидание воспроизведения музыки", vm.ExpandedTrackTitle);
            Assert.Contains("Мик.", vm.MicLabel);
        }
        finally
        {
            LocalizationService.ApplyLanguage(previous);
        }
    }
}
