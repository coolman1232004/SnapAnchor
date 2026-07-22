using SnapAnchor.Services;
using SnapAnchor.Controls;
using SnapAnchor.Models;
using SnapAnchor.Windows;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ScreenRecorderLib;
using SkiaSharp;
using Drawing = System.Drawing;
using DrawingImaging = System.Drawing.Imaging;

namespace SnapAnchor.RecognitionSmoke;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main()
    {
        if ((Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty).Contains("AnnotationQa", StringComparison.OrdinalIgnoreCase))
            return RunAnnotationQa();

        LocalizationSmoke.Run();
        CompatibilitySmoke.Run(CreatePatternImage);
        UpdateUiSmoke.Run();

        var physicalUnion = DisplayTopologyService.UnionBounds(
        [
            new Drawing.Rectangle(-1920, 0, 1920, 1080),
            new Drawing.Rectangle(0, -200, 2560, 1440)
        ]);
        if (physicalUnion != new Drawing.Rectangle(-1920, -200, 4480, 1440)) return 84;
        var physicalDesktop = DisplayTopologyService.VirtualBoundsPixels();
        if (physicalDesktop.Width <= 0 || physicalDesktop.Height <= 0) return 85;

        var nonStandardDpi = BitmapSource.Create(7, 5, 144, 120, PixelFormats.Bgra32, null, new byte[7 * 5 * 4], 7 * 4);
        var normalizedDpi = CaptureService.NormalizeDpi96(nonStandardDpi);
        if (normalizedDpi.PixelWidth != 7 || normalizedDpi.PixelHeight != 5 ||
            Math.Abs(normalizedDpi.DpiX - 96) > 0.01 || Math.Abs(normalizedDpi.DpiY - 96) > 0.01) return 86;
        Console.WriteLine($"PHYSICAL DISPLAY PIPELINE: {physicalDesktop} with lossless 96-DPI bitmap normalization verified");

        var portableCleanupRoot = Path.Combine(Path.GetTempPath(), $"snapanchor-portable-cleanup-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(portableCleanupRoot);
            var legacyExecutable = string.Concat("Snap", "Pin", ".exe");
            File.WriteAllText(Path.Combine(portableCleanupRoot, legacyExecutable), "obsolete apphost");
            var obsoleteFiles = PortableUpdateService.DetermineObsoleteFiles(
                ["obsolete-runtime.dll"], new HashSet<string>(["SnapAnchor.exe"], StringComparer.OrdinalIgnoreCase), portableCleanupRoot);
            if (!obsoleteFiles.Contains(legacyExecutable, StringComparer.OrdinalIgnoreCase) ||
                !obsoleteFiles.Contains("obsolete-runtime.dll", StringComparer.OrdinalIgnoreCase)) return 15;
            Console.WriteLine("PORTABLE CLEANUP: unmanaged previous-name executable is removed during in-place updates");
        }
        finally
        {
            if (Directory.Exists(portableCleanupRoot)) Directory.Delete(portableCleanupRoot, recursive: true);
        }

        var textImage = CreateTextImage("SNAPANCHOR OCR TEST 2026");
        var textResult = await RecognitionService.RecognizeAsync(textImage, "eng");
        Console.WriteLine($"OCR: {textResult.Text.Replace(Environment.NewLine, " | ")} ERROR: {textResult.ErrorMessage}");
        if (!textResult.Text.Contains("SNAPANCHOR", StringComparison.OrdinalIgnoreCase)) return 2;
        if (textResult.RecognizedWords.Count == 0 ||
            textResult.RecognizedWords.Any(word => word.Width <= 0 || word.Height <= 0 || word.X < 0 || word.Y < 0)) return 50;
        var selectionText = PinnedImageWindow.ComposeRecognizedText(
            [
                new RecognizedWord("Award", 10, 10, 40, 14, 0, 95),
                new RecognizedWord("2026", 55, 10, 36, 14, 0, 96),
                new RecognizedWord("SGD", 10, 32, 30, 14, 1, 94),
                new RecognizedWord("98.00", 45, 32, 42, 14, 1, 97)
            ], 3, 0);
        if (selectionText != $"Award 2026{Environment.NewLine}SGD 98.00") return 51;
        var spreadsheetSelection = PinnedImageWindow.ComposeRecognizedText(
            [
                new RecognizedWord("Project", 10, 10, 42, 14, 0, 95),
                new RecognizedWord("SGD", 180, 10, 30, 14, 0, 96),
                new RecognizedWord("98.00", 320, 10, 42, 14, 0, 97)
            ], 0, 2);
        if (spreadsheetSelection != "Project\tSGD\t98.00") return 52;
        var initialTextToolbar = PinnedImageWindow.StableTextToolbarPosition(null,
            new Point(120, 240), new Point(120, 190), new Size(156, 44), new Size(640, 480), true);
        var scrolledTextToolbar = PinnedImageWindow.StableTextToolbarPosition(initialTextToolbar,
            new Point(120, 80), new Point(120, 30), new Size(156, 44), new Size(640, 480), false);
        if (initialTextToolbar != scrolledTextToolbar) return 57;
        Console.WriteLine($"PIN TEXT SELECTION: {textResult.RecognizedWords.Count} positioned OCR words and multiline copy order verified");

        var japaneseModel = Path.Combine(AppContext.BaseDirectory, "tessdata", "jpn.traineddata");
        if (!File.Exists(japaneseModel) || new FileInfo(japaneseModel).Length < 1_000_000) return 31;
        var japaneseProbe = await RecognitionService.RecognizeAsync(CreatePatternImage(160, 80), "jpn");
        Console.WriteLine($"JAPANESE OCR MODEL: {new FileInfo(japaneseModel).Length:N0} bytes, error={japaneseProbe.ErrorMessage}");
        if (japaneseProbe.ErrorMessage.Contains("OCR:", StringComparison.OrdinalIgnoreCase)) return 32;

        var clippedOcrArea = CaptureOverlayWindow.OcrAreaWithin(
            new Rect(20, 30, 100, 80),
            new Point(45, 50),
            new Point(180, 140));
        if (clippedOcrArea != new Rect(45, 50, 75, 60)) return 33;
        Console.WriteLine($"OCR SUB-REGION: clipped to {clippedOcrArea}");

        if (CaptureService.ExtensionForFormat("WEBP") != ".webp" ||
            CaptureService.ExtensionForFormat("JPEG") != ".jpg" ||
            CaptureService.FilterIndexForFormat("WEBP") != 3 ||
            CaptureService.FilterIndexForFormat("PNG") != 1) return 34;
        Console.WriteLine("EXPORT DEFAULTS: PNG, JPEG and WebP mappings verified");

        const string payload = "https://example.com/snapanchor-test";
        var qrImage = CreateQrImage(payload);
        var qrResult = await RecognitionService.RecognizeAsync(qrImage, "eng");
        Console.WriteLine($"BARCODE: {qrResult.BarcodeFormat} {qrResult.BarcodeText}");
        if (!qrResult.BarcodeText.Contains(payload, StringComparison.Ordinal)) return 3;

        var external = ElementDetectionService.TopExternalWindow();
        if (external is not null)
        {
            var center = new Point(external.Bounds.X + external.Bounds.Width / 2, external.Bounds.Y + external.Bounds.Height / 2);
            var hierarchy = ElementDetectionService.DetectHierarchy(center, IntPtr.Zero);
            Console.WriteLine($"DETECTION LEVELS: {hierarchy.Count}");
            if (hierarchy.Count == 0) return 4;
        }

        var desktopProbe = RunSta(() =>
        {
            var window = new Window
            {
                Width = 1,
                Height = 1,
                Left = -10000,
                Top = -10000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Opacity = 0
            };
            window.Show();
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var id = VirtualDesktopService.GetDesktopId(handle);
            var onCurrent = VirtualDesktopService.IsOnCurrentDesktop(handle);
            var moveSucceeded = id is null || VirtualDesktopService.MoveWindowToDesktop(handle, id.Value);
            window.Close();
            return (id, onCurrent, moveSucceeded);
        });
        Console.WriteLine($"VIRTUAL DESKTOP: {VirtualDesktopService.ShortLabel(desktopProbe.id)}, current={desktopProbe.onCurrent}, move={desktopProbe.moveSucceeded}");
        if (VirtualDesktopService.IsSupported && desktopProbe.id is null) return 11;
        if (desktopProbe.id is not null && (!desktopProbe.onCurrent || !desktopProbe.moveSucceeded)) return 12;

        var edgePlacement = OverlayLayoutService.PlaceBelowAndKeepVisible(
            new Rect(950, 300, 190, 330),
            new Size(520, 82),
            new Size(1145, 650));
        if (edgePlacement.X < 8 || edgePlacement.Y < 8 || edgePlacement.X + 520 > 1137 || edgePlacement.Y + 82 > 642) return 16;
        Console.WriteLine($"OVERLAY LAYOUT: edge toolbar at {edgePlacement.X:N0}, {edgePlacement.Y:N0}");

        foreach (var dpiScale in new[] { 1.0, 1.25, 1.5 })
        {
            var logical = DpiLayoutService.AvailableLogicalSize(new Size(1920, 1080), dpiScale, dpiScale);
            var expectedWidth = 1920 / dpiScale - 24;
            var expectedHeight = 1080 / dpiScale - 24;
            if (Math.Abs(logical.Width - expectedWidth) > 0.01 || Math.Abs(logical.Height - expectedHeight) > 0.01) return 36;
        }
        var mixedDpiRegion = CaptureCoordinateService.ToBitmapRegion(
            new Point(-1200, 200),
            new Point(640, 900),
            new System.Drawing.Rectangle(-1920, 0, 4480, 1440),
            4480,
            1440);
        if (mixedDpiRegion != new Int32Rect(720, 200, 1840, 700)) return 37;
        Console.WriteLine("DPI LAYOUT: 100%, 125% and 150% logical bounds verified");

        var pinPlacement = RunSta(() =>
        {
            var workingArea = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var target = new Drawing.Rectangle(workingArea.Left + 40, workingArea.Top + 40, 320, 180);
            var pin = new PinnedImageWindow(CreatePatternImage(320, 180), initialScreenPixelBounds: target) { ShowActivated = false, Opacity = 0 };
            pin.Show();
            pin.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            NativeMethods.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(pin).Handle, out var actual);
            var moved = pin.MoveFromAnnotationOverlay(new Vector(12, 8));
            var moveModeWorks = Math.Abs(moved.X - 12) < 0.1 && Math.Abs(moved.Y - 8) < 0.1;
            pin.CompleteAnnotationOverlayMove();
            var selectableTextLayer = pin.FindName("SelectableTextLayer") as Canvas;
            var longSelectableTextLayer = pin.FindName("LongSelectableTextLayer") as Canvas;
            var selectableLayers = selectableTextLayer is not null &&
                longSelectableTextLayer is not null &&
                pin.FindName("TextSelectionToolbar") is Border &&
                pin.FindName("CancelTextSelectionButton") is Button;
            var selectableCursorDefaults = selectableTextLayer?.Cursor == System.Windows.Input.Cursors.Arrow &&
                longSelectableTextLayer?.Cursor == System.Windows.Input.Cursors.Arrow;
            var pixelPerfectPresentation = pin.FindName("Frame") is Border { BorderThickness: var frameThickness } &&
                frameThickness == new Thickness(0) &&
                ((Border)pin.FindName("Frame")).Effect is null &&
                pin.FindName("PinOutline") is Border &&
                pin.FindName("PinnedImage") is Image pinnedImage &&
                pinnedImage.Stretch == Stretch.Fill &&
                RenderOptions.GetBitmapScalingMode(pinnedImage) == BitmapScalingMode.NearestNeighbor;
            pin.Close();
            return (Target: target, Actual: actual, SelectableLayers: selectableLayers,
                SelectableCursorDefaults: selectableCursorDefaults,
                SelectableTextDefaultOff: !new AppSettings().PinTextSelectableByDefault,
                MoveModeWorks: moveModeWorks, PixelPerfectPresentation: pixelPerfectPresentation);
        });
        var selectableCursorPolicy =
            PinnedImageWindow.SelectableTextCursor([new Rect(20, 15, 80, 24)], new Point(40, 25)) == System.Windows.Input.Cursors.IBeam &&
            PinnedImageWindow.SelectableTextCursor([new Rect(20, 15, 80, 24)], new Point(140, 70)) == System.Windows.Input.Cursors.Arrow &&
            PinnedImageWindow.SelectableTextCursor([], new Point(140, 70), selecting: true) == System.Windows.Input.Cursors.IBeam;
        if (Math.Abs(pinPlacement.Actual.Left - pinPlacement.Target.Left) > 1 ||
            Math.Abs(pinPlacement.Actual.Top - pinPlacement.Target.Top) > 1 ||
            Math.Abs(pinPlacement.Actual.Width - pinPlacement.Target.Width) > 1 ||
            Math.Abs(pinPlacement.Actual.Height - pinPlacement.Target.Height) > 1 ||
            !pinPlacement.SelectableLayers || !pinPlacement.SelectableCursorDefaults ||
            !pinPlacement.SelectableTextDefaultOff || !pinPlacement.MoveModeWorks ||
            !pinPlacement.PixelPerfectPresentation || !selectableCursorPolicy) return 39;
        Console.WriteLine("PIN PLACEMENT: stable physical bounds, pixel-perfect rendering and context-aware selectable OCR cursors verified");

        var sharpnessPolicy = RunSta(() =>
        {
            var source = CreatePatternImage(160, 100);
            var editor = new AnnotationEditorControl();
            editor.LoadImage(source);
            editor.SetExternalBackgroundMode(true);
            editor.ConfigureCaptureOverlay(new Rect(20, 15, 160, 100), new Size(600, 300),
                showActions: true, showCancelAction: true, startWithNoTool: true, allowToolToggleOff: true);
            editor.Measure(new Size(600, 300));
            editor.Arrange(new Rect(0, 0, 600, 300));
            editor.UpdateLayout();
            editor.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            editor.UpdateLayout();
            var surfaceHost = (Border)editor.FindName("SurfaceHost");
            var annotationCanvas = (Canvas)editor.FindName("AnnotationCanvas");
            var background = (Image)editor.FindName("BackgroundImage");
            var cancel = (Button)editor.FindName("CancelActionButton");
            var toolbar = (Grid)editor.FindName("ToolbarHost");
            var toolbarPosition = toolbar.TranslatePoint(new Point(), editor);
            var expectedToolbarPosition = OverlayLayoutService.PlaceBelowAndKeepVisible(
                new Rect(20, 15, 160, 100),
                new Size(toolbar.ActualWidth, toolbar.ActualHeight),
                new Size(600, 300), 5);
            var startsInMoveMode = editor.ActiveTool == "None" &&
                ((Border)editor.FindName("PropertiesToolbarFrame")).Visibility == Visibility.Collapsed;
            editor.ToggleConfiguredTool("Rectangle");
            var activatesTool = editor.ActiveTool == "Rectangle";
            editor.ToggleConfiguredTool("Rectangle");
            var togglesBackToMoveMode = editor.ActiveTool == "None";
            var flattened = editor.Flatten();
            return (Border: surfaceHost.BorderThickness,
                Scaling: RenderOptions.GetBitmapScalingMode(background),
                ExternalBackgroundHidden: background.Visibility == Visibility.Collapsed,
                ExternalSurfaceReceivesInput: surfaceHost.Background is SolidColorBrush { Color.A: > 0 } &&
                    annotationCanvas.Background is SolidColorBrush { Color.A: > 0 },
                CancelVisible: cancel.Visibility == Visibility.Visible,
                FlattenedSizeMatches: flattened.PixelWidth == source.PixelWidth && flattened.PixelHeight == source.PixelHeight,
                ExactToolbarPlacement: Math.Abs(toolbarPosition.X - expectedToolbarPosition.X) < 1 &&
                    Math.Abs(toolbarPosition.Y - expectedToolbarPosition.Y) < 1,
                StartsInMoveMode: startsInMoveMode,
                ActivatesTool: activatesTool,
                TogglesBackToMoveMode: togglesBackToMoveMode);
        });
        if (sharpnessPolicy.Border != new Thickness(0) ||
            sharpnessPolicy.Scaling != BitmapScalingMode.NearestNeighbor ||
            !sharpnessPolicy.ExternalBackgroundHidden || !sharpnessPolicy.CancelVisible ||
            !sharpnessPolicy.FlattenedSizeMatches || !sharpnessPolicy.ExactToolbarPlacement ||
            !sharpnessPolicy.ExternalSurfaceReceivesInput ||
            !sharpnessPolicy.StartsInMoveMode || !sharpnessPolicy.ActivatesTool ||
            !sharpnessPolicy.TogglesBackToMoveMode) return 35;

        var livePinOverlay = RunSta(() =>
        {
            var targetScreen = DisplayTopologyService.EnumerateMonitorBoundsPixels().First();
            var targetBounds = new Drawing.Rectangle(
                targetScreen.Left + 48,
                targetScreen.Top + 48,
                Math.Min(320, Math.Max(160, targetScreen.Width - 96)),
                Math.Min(180, Math.Max(100, targetScreen.Height - 96)));
            var pin = new PinnedImageWindow(CreatePatternImage(320, 180), applyTextSelectableDefault: false)
            {
                Width = 320,
                Height = 180,
                ShowActivated = false
            };
            PinnedAnnotationOverlayWindow? overlay = null;
            try
            {
                pin.Show();
                pin.SetScreenPixelBounds(targetBounds);
                pin.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                pin.BeginEditMode();
                var placementFrame = new System.Windows.Threading.DispatcherFrame();
                var placementWait = new System.Windows.Threading.DispatcherTimer(
                    TimeSpan.FromMilliseconds(180),
                    System.Windows.Threading.DispatcherPriority.Background,
                    (_, _) => placementFrame.Continue = false,
                    pin.Dispatcher);
                placementWait.Start();
                System.Windows.Threading.Dispatcher.PushFrame(placementFrame);
                placementWait.Stop();
                overlay = (PinnedAnnotationOverlayWindow?)typeof(PinnedImageWindow)
                    .GetField("_annotationOverlay", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(pin);
                if (overlay is null) return (ReceivesPointer: false, Aligned: false, MatchesOwnerMonitor: false, Visible: false);

                overlay.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                var editor = (AnnotationEditorControl)typeof(PinnedAnnotationOverlayWindow)
                    .GetField("_editor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(overlay)!;
                var surface = (Border)editor.FindName("SurfaceHost");
                var pinTopLeft = pin.PointToScreen(new Point());
                var surfaceTopLeft = surface.PointToScreen(new Point());
                var pinCenter = pin.PointToScreen(new Point(pin.ActualWidth / 2, pin.ActualHeight / 2));
                var topWindow = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint
                {
                    X = (int)Math.Round(pinCenter.X),
                    Y = (int)Math.Round(pinCenter.Y)
                });
                var overlayHandle = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
                NativeMethods.GetWindowRect(overlayHandle, out var overlayPixels);
                return (
                    ReceivesPointer: topWindow == overlayHandle,
                    Aligned: Math.Abs(pinTopLeft.X - surfaceTopLeft.X) < 2 &&
                        Math.Abs(pinTopLeft.Y - surfaceTopLeft.Y) < 2,
                    MatchesOwnerMonitor: Math.Abs(overlayPixels.Left - targetScreen.Left) <= 1 &&
                        Math.Abs(overlayPixels.Top - targetScreen.Top) <= 1 &&
                        Math.Abs(overlayPixels.Width - targetScreen.Width) <= 1 &&
                        Math.Abs(overlayPixels.Height - targetScreen.Height) <= 1,
                    Visible: editor.Opacity > 0.99);
            }
            finally
            {
                overlay?.Close();
                pin.Close();
            }
        });
        Console.WriteLine($"PIN TOOLBAR LIVE: pointer={livePinOverlay.ReceivesPointer}, aligned={livePinOverlay.Aligned}, monitor={livePinOverlay.MatchesOwnerMonitor}, visible={livePinOverlay.Visible}");
        if (!livePinOverlay.ReceivesPointer || !livePinOverlay.Aligned || !livePinOverlay.MatchesOwnerMonitor || !livePinOverlay.Visible) return 83;

        var mixedDpiOverlayBounds = PinnedAnnotationOverlayWindow.PhysicalBoundsToOverlay(
            new NativeMethods.NativeRect { Left = 1920, Top = 120, Right = 2560, Bottom = 480 },
            new NativeMethods.NativeRect { Left = -1920, Top = 0, Right = 3840, Bottom = 1440 },
            1.5,
            1.5);
        if (Math.Abs(mixedDpiOverlayBounds.X - 2560) > 0.01 ||
            Math.Abs(mixedDpiOverlayBounds.Y - 80) > 0.01 ||
            Math.Abs(mixedDpiOverlayBounds.Width - 426.6667) > 0.01 ||
            Math.Abs(mixedDpiOverlayBounds.Height - 240) > 0.01) return 83;
        Console.WriteLine("PIN TOOLBAR: owner-monitor-scoped mixed-DPI placement, move-first tool state, toggle-off tools and lossless editing verified");

        var dashboardPaletteMatches = RunSta(() =>
        {
            static bool IsColor(Brush? brush, byte red, byte green, byte blue) =>
                brush is SolidColorBrush solid && solid.Color == Color.FromRgb(red, green, blue);

            var window = new MainWindow();
            var capture = (Button)window.FindName("CaptureButton");
            var pin = (Button)window.FindName("PinClipboardButton");
            var card = (Border)window.FindName("CaptureOverviewCard");
            var shortcutPanel = (Border)window.FindName("ShortcutPanel");
            var tray = (System.Windows.Forms.NotifyIcon?)typeof(MainWindow)
                .GetField("_trayIcon", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(window);
            var taskbarIcon = window.Icon as BitmapSource;
            var hasGitHubUpdateCommand = tray?.ContextMenuStrip?.Items
                .OfType<System.Windows.Forms.ToolStripItem>()
                .Any(item => item.Text == "Check for updates…") == true;
            var matches = IsColor(window.Background, 0xFA, 0xF9, 0xF7) &&
                IsColor(capture.Background, 0x29, 0x25, 0x24) &&
                IsColor(pin.Background, 0xE7, 0xE5, 0xE4) &&
                IsColor(card.Background, 0xFF, 0xFF, 0xFF) &&
                IsColor(shortcutPanel.BorderBrush, 0xE7, 0xE5, 0xE4) &&
                taskbarIcon is { PixelWidth: >= 32, PixelHeight: >= 32 } &&
                hasGitHubUpdateCommand;
            window.DisposeLayoutPreview();
            return matches;
        });
        if (!dashboardPaletteMatches) return 50;
        Console.WriteLine("DASHBOARD PALETTE: warm neutral background, charcoal actions and white cards verified");

        var aboutEdition = RunSta(() =>
        {
            var preferences = new PreferencesWindow(new AppSettings());
            var edition = ((TextBlock)preferences.FindName("EditionText")).Text;
            var copyButton = ((Button)preferences.FindName("CopyVersionButton")).Content as string == "Copy version information";
            var icon = preferences.Icon as BitmapSource;
            return (Edition: edition, CopyButton: copyButton, Icon: icon is { PixelWidth: >= 32, PixelHeight: >= 32 });
        });
        if (!aboutEdition.Edition.Contains("portable copy", StringComparison.OrdinalIgnoreCase) ||
            !aboutEdition.CopyButton || !aboutEdition.Icon ||
            !DiagnosticsService.ProductSummary(true).Contains("portable copy", StringComparison.OrdinalIgnoreCase)) return 74;
        Console.WriteLine("ABOUT EDITION: portable/installed identity and copyable version summary verified");

        if (!ElevationService.NeedsElevation(true, false) ||
            ElevationService.NeedsElevation(false, false) ||
            ElevationService.NeedsElevation(true, true)) return 51;
        Console.WriteLine("ADMINISTRATOR MODE: opt-in elevation decision verified without forcing the manifest");

        var stableToolbarAnchor = RunSta(() =>
        {
            const double expectedLeft = 238;
            const double expectedTop = 385;
            var editor = new AnnotationEditorControl();
            editor.LoadImage(CreatePatternImage(700, 300));
            editor.ConfigureCaptureOverlay(new Rect(70, 60, 700, 300), new Size(920, 620),
                new Rect(70, 60, 700, 300), showActions: true,
                toolbarLeft: expectedLeft, toolbarTop: expectedTop);
            editor.Measure(new Size(920, 620));
            editor.Arrange(new Rect(0, 0, 920, 620));
            editor.UpdateLayout();
            editor.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            editor.UpdateLayout();
            var toolbar = (Grid)editor.FindName("ToolbarHost");
            var before = toolbar.TranslatePoint(new Point(), editor);
            editor.ActivateConfiguredTool("Text");
            editor.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            editor.UpdateLayout();
            var after = toolbar.TranslatePoint(new Point(), editor);
            editor.SetCapturePropertiesToolbarVisible(false);
            var propertiesHideDuringResize = ((Border)editor.FindName("PropertiesToolbarFrame")).Visibility == Visibility.Collapsed;
            editor.SetCapturePropertiesToolbarVisible(true);
            var propertiesReturnAfterResize = ((Border)editor.FindName("PropertiesToolbarFrame")).Visibility == Visibility.Visible;
            return Math.Abs(before.X - expectedLeft) < 1 && Math.Abs(before.Y - expectedTop) < 1 &&
                Math.Abs(after.X - before.X) < 1 && Math.Abs(after.Y - before.Y) < 1 &&
                propertiesHideDuringResize && propertiesReturnAfterResize;
        });
        if (!stableToolbarAnchor) return 48;
        Console.WriteLine("ANNOTATION POSITION: primary toolbar anchor remains fixed when the property row changes");

        if (Environment.GetEnvironmentVariable("SNAPANCHOR_RENDER_LAYOUT") == "1")
        {
            var dashboardLayoutPath = Path.Combine(AppContext.BaseDirectory, "dashboard-light-preview.png");
            var dashboardPreview = RunSta(() =>
            {
                var window = new MainWindow();
                if (((Image)window.FindName("AppLogoImage")).Source is not BitmapSource { PixelWidth: > 0, PixelHeight: > 0 })
                    throw new InvalidDataException("The dashboard logo resource did not load.");
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(900, 660));
                content.Arrange(new Rect(0, 0, 900, 660));
                content.UpdateLayout();
                var rendered = new RenderTargetBitmap(900, 660, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(content);
                rendered.Freeze();
                window.DisposeLayoutPreview();
                return rendered;
            });
            CaptureService.SavePng(dashboardPreview, dashboardLayoutPath);
            Console.WriteLine($"DASHBOARD LIGHT PREVIEW: {dashboardLayoutPath}");

            var captureToolbarPath = Path.Combine(AppContext.BaseDirectory, "capture-toolbar-light-preview.png");
            var captureToolbarPreview = RunSta(() =>
            {
                var overlay = new CaptureOverlayWindow(CreatePatternImage(900, 560));
                var toolbar = (Border)overlay.FindName("ActionBar");
                toolbar.Visibility = Visibility.Visible;
                if (toolbar.Parent is Panel parent) parent.Children.Remove(toolbar);
                var host = new Border
                {
                    Width = 850,
                    Height = 92,
                    Padding = new Thickness(16),
                    Background = new SolidColorBrush(Color.FromRgb(102, 110, 120)),
                    Child = toolbar
                };
                host.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/SnapAnchor;component/Resources/ToolbarResources.xaml", UriKind.RelativeOrAbsolute)
                });
                ToolbarThemeService.ApplyTo(host.Resources, "Compact");
                host.Measure(new Size(850, 92));
                host.Arrange(new Rect(0, 0, 850, 92));
                host.UpdateLayout();
                var rendered = new RenderTargetBitmap(850, 92, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(host);
                rendered.Freeze();
                overlay.Close();
                return rendered;
            });
            CaptureService.SavePng(captureToolbarPreview, captureToolbarPath);
            Console.WriteLine($"CAPTURE TOOLBAR PREVIEW: {captureToolbarPath}");

            var recordingControlsPath = Path.Combine(AppContext.BaseDirectory, "recording-controls-preview.png");
            var recordingControlsPreview = RunSta(() =>
            {
                var overlay = new CaptureOverlayWindow(CreatePatternImage(900, 560));
                var recordingBar = (Border)overlay.FindName("RecordingBar");
                recordingBar.Visibility = Visibility.Visible;
                if (recordingBar.Parent is Panel parent) parent.Children.Remove(recordingBar);
                var host = new Border { Width = 470, Height = 64, Padding = new Thickness(8), Background = Brushes.White, Child = recordingBar };
                host.Measure(new Size(470, 64));
                host.Arrange(new Rect(0, 0, 470, 64));
                host.UpdateLayout();
                var rendered = new RenderTargetBitmap(470, 64, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(host);
                rendered.Freeze();
                overlay.Close();
                return rendered;
            });
            CaptureService.SavePng(recordingControlsPreview, recordingControlsPath);
            Console.WriteLine($"RECORDING CONTROLS PREVIEW: {recordingControlsPath}");

            var recordingReviewPath = Path.Combine(AppContext.BaseDirectory, "recording-review-preview.png");
            var recordingReviewPreview = RunSta(() =>
            {
                var overlay = new CaptureOverlayWindow(CreatePatternImage(900, 560));
                var review = (Border)overlay.FindName("RecordingReviewPanel");
                ((TextBlock)overlay.FindName("RecordingSavePathText")).Text = @"Will save to: C:\Users\Example\Videos\SnapAnchor\recording.mp4";
                review.Visibility = Visibility.Visible;
                if (review.Parent is Panel parent) parent.Children.Remove(review);
                review.Measure(new Size(620, 470));
                review.Arrange(new Rect(0, 0, 620, 470));
                review.UpdateLayout();
                var rendered = new RenderTargetBitmap(620, 470, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(review);
                rendered.Freeze();
                overlay.Close();
                return rendered;
            });
            CaptureService.SavePng(recordingReviewPreview, recordingReviewPath);
            Console.WriteLine($"RECORDING REVIEW PREVIEW: {recordingReviewPath}");

            var layoutPath = Path.Combine(AppContext.BaseDirectory, "annotation-layout-preview.png");
            var layoutPreview = RunSta(() =>
            {
                var source = CreatePatternImage(950, 333);
                var editor = new AnnotationEditorControl();
                editor.LoadImage(source);
                editor.ConfigureCaptureOverlay(new Rect(95, 32, 950, 333), new Size(1145, 650), new Rect(650, 372, 395, 34), showActions: true);
                editor.Measure(new Size(1145, 650));
                editor.Arrange(new Rect(0, 0, 1145, 650));
                editor.UpdateLayout();
                editor.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                editor.UpdateLayout();
                var rendered = new RenderTargetBitmap(1145, 650, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(editor);
                rendered.Freeze();
                return rendered;
            });
            CaptureService.SavePng(layoutPreview, layoutPath);
            Console.WriteLine($"ANNOTATION LAYOUT PREVIEW: {layoutPath}");

            var selectionPath = Path.Combine(AppContext.BaseDirectory, "annotation-selection-preview.png");
            var selectionPreview = RunSta(() =>
            {
                var item = new AnnotationItem { Kind = AnnotationKind.Rectangle, X = 140, Y = 70, Width = 260, Height = 120, Color = "#FFEF4444", Thickness = 4 };
                var editor = new AnnotationEditorControl();
                editor.LoadImage(CreatePatternImage(600, 280), [item]);
                editor.ConfigureCaptureOverlay(new Rect(80, 40, 600, 280), new Size(760, 440), new Rect(280, 325, 400, 34), showActions: true);
                var type = typeof(AnnotationEditorControl);
                type.GetField("_selectedId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(editor, (Guid?)item.Id);
                type.GetMethod("RenderAnnotations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, null);
                editor.Measure(new Size(760, 440));
                editor.Arrange(new Rect(0, 0, 760, 440));
                editor.UpdateLayout();
                var rendered = new RenderTargetBitmap(760, 440, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(editor);
                rendered.Freeze();
                return rendered;
            });
            CaptureService.SavePng(selectionPreview, selectionPath);
            Console.WriteLine($"ANNOTATION SELECTION PREVIEW: {selectionPath}");

            foreach (var (suffix, buttonName) in new[]
            {
                ("arrow", "ArrowToolButton"), ("pencil", "PencilToolButton"), ("marker", "MarkerToolButton"),
                ("blur", "BlurToolButton"), ("text", "TextToolButton"), ("eraser", "EraserToolButton")
            })
            {
                var contextualPath = Path.Combine(AppContext.BaseDirectory, $"annotation-{suffix}-preview.png");
                var contextualPreview = RunSta(() =>
                {
                    var editor = new AnnotationEditorControl();
                    editor.LoadImage(CreatePatternImage(950, 333));
                    editor.ConfigureCaptureOverlay(new Rect(95, 32, 950, 333), new Size(1145, 650), new Rect(650, 372, 395, 34), showActions: true);
                    ((Button)editor.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (suffix == "text")
                    {
                        var editorType = typeof(AnnotationEditorControl);
                        editorType.GetMethod("BeginTextEditor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                            .Invoke(editor, [new Point(265, 105), null]);
                        var inlineText = (TextBox?)editorType.GetField("_textEditor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor);
                        if (inlineText is not null) inlineText.Text = "Type here";
                    }
                    if (suffix == "eraser")
                        typeof(AnnotationEditorControl).GetMethod("PositionCustomToolCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, [new Point(480, 165), true]);
                    editor.Measure(new Size(1145, 650));
                    editor.Arrange(new Rect(0, 0, 1145, 650));
                    editor.UpdateLayout();
                    editor.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    editor.UpdateLayout();
                    var rendered = new RenderTargetBitmap(1145, 650, 96, 96, PixelFormats.Pbgra32);
                    rendered.Render(editor);
                    rendered.Freeze();
                    return rendered;
                });
                CaptureService.SavePng(contextualPreview, contextualPath);
                Console.WriteLine($"ANNOTATION {suffix.ToUpperInvariant()} PREVIEW: {contextualPath}");
            }

            var historyLayoutPath = Path.Combine(AppContext.BaseDirectory, "history-layout-preview.png");
            var historyPreview = RunSta(() =>
            {
                var window = new HistoryWindow();
                if (window.Width != 1120 || window.Height != 760 ||
                    window.Background is not SolidColorBrush { Color: var background } || background != Color.FromRgb(250, 249, 247) ||
                    window.FindName("FilterPanel") is not Border { CornerRadius: var radius } || radius.TopLeft != 12 ||
                    window.FindName("BackToDashboardButton") is not Button)
                    throw new InvalidOperationException("History window does not use the redesigned light layout.");
                var itemType = typeof(HistoryWindow).GetNestedType("HistoryViewItem", System.Reflection.BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("History preview item type is missing.");
                var previewItems = Array.CreateInstance(itemType, 3);
                for (var index = 0; index < previewItems.Length; index++)
                {
                    var item = Activator.CreateInstance(itemType, nonPublic: true)!;
                    void Set(string property, object value) => itemType.GetProperty(property)!.SetValue(item, value);
                    Set("Id", $"preview-{index}");
                    Set("Thumbnail", CreatePatternImage(560 + index * 10, 290));
                    Set("SizeLabel", $"{1915 + index * 2} x {915 - index * 2}");
                    Set("TimeLabel", $"Jul 21  10:05:{35 - index * 10:00}");
                    Set("SourceLabel", "Copied");
                    Set("RecognitionLabel", index == 1 ? "OCR text" : string.Empty);
                    Set("EditLabel", "Edit");
                    Set("ContextLabel", "Show context");
                    Set("ContextVisibility", Visibility.Visible);
                    Set("Title", "Copied");
                    Set("FavoriteGlyph", index == 2 ? "★" : "☆");
                    Set("ActiveVisibility", Visibility.Visible);
                    Set("DeletedVisibility", Visibility.Collapsed);
                    previewItems.SetValue(item, index);
                }
                ((ListBox)window.FindName("HistoryList")).ItemsSource = previewItems;
                ((TextBlock)window.FindName("SummaryText")).Text = "43 saved items";
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                var host = new Border
                {
                    Width = 1120,
                    Height = 760,
                    Background = new SolidColorBrush(Color.FromRgb(250, 249, 247)),
                    Child = content
                };
                host.Measure(new Size(1120, 760));
                host.Arrange(new Rect(0, 0, 1120, 760));
                host.UpdateLayout();
                var rendered = new RenderTargetBitmap(1120, 760, 96, 96, PixelFormats.Pbgra32);
                rendered.Render(host);
                rendered.Freeze();
                return rendered;
            });
            CaptureService.SavePng(historyPreview, historyLayoutPath);
            Console.WriteLine($"HISTORY LAYOUT PREVIEW: {historyLayoutPath}");

            var preferenceTabs = new[] { "general", "capture", "toolbar", "recording", "pins", "history", "output", "hotkeys", "ocr", "about" };
            for (var tabIndex = 0; tabIndex < preferenceTabs.Length; tabIndex++)
            {
                var selectedTab = tabIndex;
                var preferencesLayoutPath = Path.Combine(AppContext.BaseDirectory, $"preferences-{preferenceTabs[tabIndex]}-preview.png");
                var preferencesPreview = RunSta(() =>
                {
                    var window = new PreferencesWindow(new AppSettings
                    {
                        OutputFormat = "WEBP",
                        ImageQuality = 86,
                        OutputFileName = "SnapAnchor_$yyyy-MM-dd_HH-mm-ss.webp"
                    })
                    {
                        Left = -10000,
                        Top = -10000,
                        ShowActivated = false,
                        Opacity = 0
                    };
                    window.Show();
                    var tabs = (TabControl)window.FindName("Tabs");
                    tabs.SelectedIndex = selectedTab;
                    var content = (FrameworkElement)window.Content;
                    content.InvalidateMeasure();
                    content.Measure(new Size(620, 470));
                    content.Arrange(new Rect(0, 0, 620, 470));
                    content.UpdateLayout();
                    content.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    content.Measure(new Size(620, 470));
                    content.Arrange(new Rect(0, 0, 620, 470));
                    content.UpdateLayout();
                    var rendered = new RenderTargetBitmap(620, 470, 96, 96, PixelFormats.Pbgra32);
                    rendered.Render(content);
                    rendered.Freeze();
                    window.Close();
                    return rendered;
                });
                CaptureService.SavePng(preferencesPreview, preferencesLayoutPath);
                Console.WriteLine($"PREFERENCES {preferenceTabs[tabIndex].ToUpperInvariant()} PREVIEW: {preferencesLayoutPath}");
            }
        }

        var instanceId = $"SnapAnchor.Smoke.{Guid.NewGuid():N}";
        using (var primaryInstance = new SingleInstanceService(instanceId))
        using (var secondaryInstance = new SingleInstanceService(instanceId))
        {
            if (!primaryInstance.IsPrimaryInstance || secondaryInstance.IsPrimaryInstance) return 13;
            var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            primaryInstance.ActivationRequested += (_, _) => activated.TrySetResult();
            primaryInstance.StartListening();
            secondaryInstance.SignalPrimaryInstance();
            await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var forwarded = new TaskCompletionSource<AppCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
            primaryInstance.CommandReceived += (_, args) =>
            {
                if (args.Command.Kind == AppCommandKind.CaptureCopy) forwarded.TrySetResult(args.Command);
            };
            if (!AppCommand.TryParse(["capture-copy", "--delay", "2", "--size", "640x360", "--ratio", "16:9", "--cursor"], out var parsedCommand, out _) ||
                parsedCommand.DelaySeconds != 2 || parsedCommand.FixedWidth != 640 || parsedCommand.FixedHeight != 360 ||
                Math.Abs(parsedCommand.AspectRatio - 16d / 9d) > 0.001 || !parsedCommand.IncludeCursor) return 41;
            secondaryInstance.SignalPrimaryInstance(parsedCommand);
            var received = await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (received.FixedWidth != 640 || !received.IncludeCursor) return 41;
            Console.WriteLine("SINGLE INSTANCE: activation and CLI command forwarding received");
        }

        var whiteboardPolicy = RunSta(() =>
        {
            var transparent = new WhiteboardWindow(true) { ShowActivated = false, Opacity = 0 };
            transparent.Show();
            transparent.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var transparentEditor = (AnnotationEditorControl)transparent.FindName("Editor");
            var transparentImage = (BitmapSource)((Image)transparentEditor.FindName("BackgroundImage")).Source;
            var transparentPixel = new byte[4];
            transparentImage.CopyPixels(new Int32Rect(0, 0, 1, 1), transparentPixel, 4, 0);
            var actionsVisible = ((Button)transparentEditor.FindName("ApplyActionButton")).Visibility == Visibility.Visible;
            transparent.Close();

            var solid = new WhiteboardWindow(false) { ShowActivated = false, Opacity = 0 };
            var solidEditor = (AnnotationEditorControl)solid.FindName("Editor");
            var solidImage = (BitmapSource)((Image)solidEditor.FindName("BackgroundImage")).Source;
            var solidPixel = new byte[4];
            solidImage.CopyPixels(new Int32Rect(0, 0, 1, 1), solidPixel, 4, 0);
            solid.Close();
            return (TransparentAlpha: transparentPixel[3], SolidAlpha: solidPixel[3], ActionsVisible: actionsVisible);
        });
        if (whiteboardPolicy.TransparentAlpha != 0 || whiteboardPolicy.SolidAlpha != 255 || !whiteboardPolicy.ActionsVisible) return 42;
        Console.WriteLine("WHITEBOARD: solid and transparent inline annotation modes verified");

        var recoveryRoot = Path.Combine(Path.GetTempPath(), $"snapanchor-recovery-{Guid.NewGuid():N}");
        try
        {
            var atomicPath = Path.Combine(recoveryRoot, "atomic.json");
            AtomicFileService.WriteJson(atomicPath, new RecoveryProbe("first"));
            AtomicFileService.WriteJson(atomicPath, new RecoveryProbe("second"));
            File.WriteAllText(atomicPath, "{corrupt");
            if (!AtomicFileService.TryReadJson<RecoveryProbe>(atomicPath, out var recovered) || recovered?.Value != "first") return 14;
            Console.WriteLine("ATOMIC STORAGE: backup recovered after primary corruption");

            var sessionRoot = Path.Combine(recoveryRoot, "session");
            var image = CreatePatternImage(48, 32);
            PinSessionService.SaveCore(sessionRoot, [new PinSessionSnapshot(image, new PinSessionItem
            {
                Group = "First", HistoryRecordId = "refreshable-record", MonitorDeviceName = @"\\.\DISPLAY2",
                PhysicalLeft = -1280, PhysicalTop = 120, PhysicalWidth = 640, PhysicalHeight = 480,
                LongScrollOffset = 245, ClickThrough = true, IsVisible = false
            })]);
            PinSessionService.SaveCore(sessionRoot, [new PinSessionSnapshot(image, new PinSessionItem { Group = "Second" })]);
            using (var pointer = JsonDocument.Parse(File.ReadAllText(Path.Combine(sessionRoot, "current.json"))))
            {
                var current = pointer.RootElement.GetProperty("Current").GetString()!;
                var currentIndex = Path.Combine(sessionRoot, current, "session.json");
                File.WriteAllText(currentIndex, "broken");
                File.WriteAllText(AtomicFileService.BackupPath(currentIndex), "broken");
            }
            var recoveredSession = PinSessionService.LoadCore(sessionRoot);
            if (recoveredSession.Count != 1 || recoveredSession[0].Item.Group != "First" || recoveredSession[0].Item.HistoryRecordId != "refreshable-record" ||
                recoveredSession[0].Item.PhysicalLeft != -1280 || recoveredSession[0].Item.LongScrollOffset != 245 ||
                !recoveredSession[0].Item.ClickThrough || recoveredSession[0].Item.IsVisible) return 15;
            if (LocalizationService.Translate("Capture", "简体中文") != "截图" ||
                LocalizationService.Translate("General", "繁體中文") != "一般" ||
                LocalizationService.Translate("Restart required", "繁體中文") != "需要重新啟動" ||
                LocalizationService.Translate("Run SnapAnchor on system startup", "简体中文") != "系统启动时运行 SnapAnchor" ||
                LocalizationService.Normalize("invalid") != "English") return 56;
            var localizedControls = RunSta(() =>
            {
                var text = new TextBlock { Text = "Configuration storage" };
                var check = new CheckBox { Content = "Run SnapAnchor on system startup" };
                var panel = new StackPanel();
                panel.Children.Add(text);
                panel.Children.Add(check);
                LocalizationService.Apply(panel, "简体中文");
                return (text.Text, check.Content as string);
            });
            Console.WriteLine($"LOCALIZATION: text='{localizedControls.Item1}', checkbox='{localizedControls.Item2}'");
            if (localizedControls.Item1 != "配置存储" || localizedControls.Item2 != "系统启动时运行 SnapAnchor") return 63;
            Console.WriteLine("PIN SESSION: verified generation, physical monitor state and application-wide localization recovered");
        }
        finally
        {
            if (Directory.Exists(recoveryRoot)) Directory.Delete(recoveryRoot, recursive: true);
        }

        var tallSource = CreatePatternImage(500, 900);
        var scrollFrames = new List<BitmapSource>
        {
            CaptureService.Crop(tallSource, new Int32Rect(0, 0, 500, 360)),
            CaptureService.Crop(tallSource, new Int32Rect(0, 180, 500, 360)),
            CaptureService.Crop(tallSource, new Int32Rect(0, 360, 500, 360)),
            CaptureService.Crop(tallSource, new Int32Rect(0, 540, 500, 360))
        };
        var stitched = ScrollingCaptureService.StitchFrames(scrollFrames);
        Console.WriteLine($"STITCHED: {stitched.PixelWidth} x {stitched.PixelHeight}");
        if (stitched.PixelWidth != 500 || stitched.PixelHeight != 900 || !PixelsEqual(tallSource, stitched)) return 5;

        var annotationItems = new List<AnnotationItem>
        {
            new() { Kind = AnnotationKind.Number, X = 25, Y = 25, Width = 48, Height = 48, Text = "1", Color = "#FF1677FF", FillOpacity = 1, Rotation = 8 },
            new() { Kind = AnnotationKind.Callout, X = 90, Y = 35, Width = 250, Height = 100, Text = "Editable callout", Color = "#FFEF4444", FillOpacity = 0.2, Dashed = true, Opacity = 0.9 },
            new() { Kind = AnnotationKind.Arrow, Points = [new Point(40, 180), new Point(330, 165)], Color = "#FF22C55E", Thickness = 6, ArrowHeads = "Both" },
            new() { Kind = AnnotationKind.Mosaic, X = 15, Y = 125, Width = 75, Height = 65, Thickness = 5, EffectShape = "Ellipse" },
            new() { Kind = AnnotationKind.Blur, X = 105, Y = 125, Width = 75, Height = 65, Thickness = 5 },
            new() { Kind = AnnotationKind.Magnify, X = 200, Y = 115, Width = 120, Height = 90, Thickness = 4, EffectShape = "Ellipse" }
        };
        var documentJson = JsonSerializer.Serialize(new AnnotationDocument { Items = annotationItems });
        var restoredDocument = JsonSerializer.Deserialize<AnnotationDocument>(documentJson);
        if (restoredDocument?.Items.Count != 6 || restoredDocument.Items[1].Kind != AnnotationKind.Callout ||
            restoredDocument.Items[2].ArrowHeads != "Both" || restoredDocument.Items[2].Points.Count != 2 ||
            restoredDocument.Items[3].EffectShape != "Ellipse" || restoredDocument.Items[5].Kind != AnnotationKind.Magnify) return 6;
        var annotationBase = CreatePatternImage(400, 240);
        var annotationFlattened = RunSta(() =>
        {
            var editor = new AnnotationEditorControl();
            editor.LoadImage(annotationBase, restoredDocument.Items);
            return editor.Flatten();
        });
        Console.WriteLine($"ANNOTATIONS: {restoredDocument.Items.Count} editable objects, rendered {annotationFlattened.PixelWidth} x {annotationFlattened.PixelHeight}");
        if (annotationFlattened.PixelWidth != annotationBase.PixelWidth || annotationFlattened.PixelHeight != annotationBase.PixelHeight) return 7;

        var annotationToolbarMatches = RunSta(() =>
        {
            var editor = new AnnotationEditorControl();
            editor.LoadImage(annotationBase);
            var defaults = new AppSettings();
            editor.ApplyToolbarConfiguration(defaults.AnnotationToolbarOrder, defaults.AnnotationToolbarEnabled);
            var panel = (WrapPanel)editor.FindName("ToolPanel");
            var tags = panel.Children.OfType<Button>().Select(button => button.Tag as string).ToArray();
            var visibleTags = panel.Children.OfType<Button>()
                .Where(button => button.Visibility == Visibility.Visible)
                .Select(button => button.Tag as string)
                .ToArray();
            var palette = (StackPanel)editor.FindName("ColorPalettePanel");
            var swatches = palette.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>().Single().Children.OfType<Button>().Count();
            var shapeProperties = (StackPanel)editor.FindName("ShapeOptionsPanel");
            var arrowProperties = (StackPanel)editor.FindName("ArrowOptionsPanel");
            var textToolButton = (Button)editor.FindName("TextToolButton");
            var primaryToolbar = (Border)editor.FindName("PrimaryToolbarFrame");
            primaryToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            textToolButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var defaultsMatch = defaults.AnnotationToolbarOrder.SequenceEqual(
                    new[] { "Rectangle", "Arrow", "Pencil", "Marker", "Blur", "Text", "Eraser", "Ellipse", "Line", "Magnify" }) &&
                defaults.AnnotationToolbarEnabled.SequenceEqual(
                    new[] { "Rectangle", "Arrow", "Pencil", "Marker", "Blur", "Text", "Eraser" }) &&
                defaults.CaptureToolbarOrder.SequenceEqual(
                    new[] { "Cancel", "Pin", "Save", "Copy", "PinThumbnail", "QuickSave", "LongCapture", "Record", "OCR", "Recapture" }) &&
                defaults.CaptureToolbarEnabled.SequenceEqual(new[] { "Cancel", "Pin", "Save", "Copy" }) &&
                tags.SequenceEqual(defaults.AnnotationToolbarOrder) &&
                visibleTags.SequenceEqual(defaults.AnnotationToolbarEnabled) &&
                new[] { "Ellipse", "Line", "Magnify" }.All(tag => tags.Contains(tag)) &&
                new[] { "Select", "Number", "Callout", "Mosaic" }.All(tag => !tags.Contains(tag));
            editor.ApplyToolbarConfiguration(["Magnify", "Text"], ["Magnify", "Text"]);
            var customVisibleTags = panel.Children.OfType<Button>()
                .Where(button => button.Visibility == Visibility.Visible)
                .Select(button => button.Tag as string)
                .ToArray();
            return defaultsMatch && customVisibleTags.SequenceEqual(new[] { "Magnify", "Text" }) &&
                swatches == 20 && ((StackPanel)editor.FindName("TextOptionsPanel")).Visibility == Visibility.Visible &&
                ((StackPanel)editor.FindName("ShapeOptionsPanel")).Visibility == Visibility.Collapsed &&
                textToolButton.Width == 28 && textToolButton.Height == 28 && textToolButton.Padding == new Thickness(4) &&
                textToolButton.Margin == new Thickness(1, 0, 1, 0) &&
                primaryToolbar.DesiredSize.Height <= 34.1 &&
                textToolButton.Content is System.Windows.Shapes.Path &&
                shapeProperties.Children.OfType<Button>().All(button => button.Tag is null) &&
                arrowProperties.Children.OfType<Button>().All(button => button.Tag is null);
        });
        if (!annotationToolbarMatches) return 43;
        Console.WriteLine("ANNOTATION TOOLS: Snipaste-style defaults are first-row configurable; removed tools stay absent");

        var toolbarPreferencesAreSwapped = RunSta(() =>
        {
            var preferences = new PreferencesWindow(new AppSettings());
            var annotationPanel = (StackPanel)preferences.FindName("AnnotationToolbarSettingsPanel");
            var capturePanel = (StackPanel)preferences.FindName("CaptureToolbarSettingsPanel");
            var tabs = (TabControl)preferences.FindName("Tabs");
            var dailyUpdates = (CheckBox)preferences.FindName("CheckUpdatesDailyBox");
            var everyTabScrolls = tabs.Items.OfType<TabItem>().All(tab =>
                tab.Content is ScrollViewer scrollViewer &&
                scrollViewer.VerticalScrollBarVisibility == ScrollBarVisibility.Auto);
            var result = Grid.GetColumn(annotationPanel) == 0 && Grid.GetColumn(capturePanel) == 2 &&
                everyTabScrolls && dailyUpdates.IsChecked == false &&
                preferences.Width == 620 && preferences.Height == 470;
            preferences.Close();
            return result;
        });
        if (!toolbarPreferencesAreSwapped) return 75;
        Console.WriteLine("PREFERENCES LAYOUT: compact 620 x 470 dialog; every tab scrolls; toolbar columns remain ordered");

        var captureToolbarMatches = RunSta(() =>
        {
            var overlay = new CaptureOverlayWindow(CreatePatternImage(320, 180));
            overlay.ApplyCaptureToolbarConfiguration(["OCR", "Recapture", "Copy"], ["OCR", "Recapture"]);
            var panel = (StackPanel)overlay.FindName("CaptureActionPanel");
            var allTags = panel.Children.OfType<Button>().Select(button => button.Tag as string).ToArray();
            var visibleTags = panel.Children.OfType<Button>()
                .Where(button => button.Visibility == Visibility.Visible)
                .Select(button => button.Tag as string)
                .ToArray();
            var compactSpacing = panel.Children.OfType<Button>().All(button =>
                button.Width == 28 && button.Height == 28 && button.Padding == new Thickness(4) && button.Margin == new Thickness(1, 0, 1, 0));
            var actionBar = (Border)overlay.FindName("ActionBar");
            actionBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var compactHeight = actionBar.DesiredSize.Height <= 34.1;
            var historySlotsStartMuted = ((StackPanel)overlay.FindName("AnnotationHistoryPanel")).Visibility == Visibility.Visible &&
                !((Button)overlay.FindName("CaptureUndoButton")).IsEnabled &&
                !((Button)overlay.FindName("CaptureRedoButton")).IsEnabled;
            overlay.Close();
            return allTags.Contains("OCR") && allTags.Contains("Recapture") &&
                visibleTags.SequenceEqual(new[] { "OCR", "Recapture" }) && compactSpacing && compactHeight && historySlotsStartMuted;
        });
        if (!captureToolbarMatches) return 46;
        Console.WriteLine("CAPTURE ACTIONS: OCR, recapture and every output action are configurable");

        var captureAnnotationToolbarMatches = RunSta(() =>
        {
            var overlay = new CaptureOverlayWindow(CreatePatternImage(320, 180));
            overlay.ApplyCaptureAnnotationToolbarConfiguration(
                ["Ellipse", "Line", "Magnify", "Rectangle"],
                ["Ellipse", "Line", "Magnify"]);
            var panel = (WrapPanel)overlay.FindName("CaptureAnnotationToolPanel");
            var allTags = panel.Children.OfType<Button>().Select(button => button.Tag as string).ToArray();
            var visibleTags = panel.Children.OfType<Button>()
                .Where(button => button.Visibility == Visibility.Visible)
                .Select(button => button.Tag as string)
                .ToArray();
            var reviewHeader = (DockPanel)overlay.FindName("RecordingReviewHeader");
            var savePath = (TextBlock)overlay.FindName("RecordingSavePathText");
            var hasRecordingIcons = overlay.FindName("RecordingPauseIcon") is Grid &&
                overlay.FindName("RecordingResumeIcon") is System.Windows.Shapes.Path &&
                overlay.FindName("RecordingStopButton") is Button &&
                overlay.FindName("ReviewPlayIcon") is System.Windows.Shapes.Path &&
                overlay.FindName("ReviewPauseIcon") is Grid &&
                overlay.FindName("RecordingPauseLabel") is TextBlock { Text: "Pause" } &&
                overlay.FindName("ReviewPlayLabel") is TextBlock { Text: "Play" } &&
                overlay.FindName("DiscardRecordingButton") is Button { Content: "Discard" };
            var overlayType = typeof(CaptureOverlayWindow);
            var detectionRect = (System.Windows.Shapes.Rectangle)overlay.FindName("DetectionRect");
            detectionRect.Visibility = Visibility.Visible;
            overlayType.GetField("_selection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(overlay, new Rect(20, 20, 180, 100));
            overlayType.GetMethod("RenderSelection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(overlay, null);
            var finalizedSelectionHidesDetection = detectionRect.Visibility == Visibility.Collapsed;
            overlay.Measure(new Size(320, 180));
            overlay.Arrange(new Rect(0, 0, 320, 180));
            overlay.UpdateLayout();
            overlayType.GetMethod("BeginInlineAnnotation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(overlay, new object?[] { "Ellipse" });
            var inlineEditor = (AnnotationEditorControl)overlay.FindName("CaptureInlineEditor");
            var ellipseButton = panel.Children.OfType<Button>()
                .First(button => string.Equals(button.Tag as string, "Ellipse", StringComparison.OrdinalIgnoreCase));
            var annotationStartsActive = inlineEditor.ActiveTool == "Ellipse";
            overlayType.GetMethod("QuickAnnotation_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(overlay, new object[] { ellipseButton, new RoutedEventArgs() });
            var captureToolTogglesOff = inlineEditor.ActiveTool == "None" &&
                ellipseButton.Background == Brushes.Transparent;
            overlay.Close();
            return visibleTags.SequenceEqual(new[] { "Ellipse", "Line", "Magnify" }) &&
                !allTags.Contains("Select") && !allTags.Contains("Number") && !allTags.Contains("Callout") &&
                reviewHeader.Cursor == System.Windows.Input.Cursors.SizeAll && savePath is not null && hasRecordingIcons &&
                finalizedSelectionHidesDetection && annotationStartsActive && captureToolTogglesOff;
        });
        if (!captureAnnotationToolbarMatches) return 47;
        Console.WriteLine("CAPTURE ANNOTATION: direct first-row tools, movable review header, save path and recording icons verified");

        var resizeToolbarLifecycleMatches = RunSta(() =>
        {
            var overlay = new CaptureOverlayWindow(CreatePatternImage(900, 600))
            {
                Width = 900,
                Height = 600,
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                Opacity = 0
            };
            overlay.Show();
            overlay.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var overlayType = typeof(CaptureOverlayWindow);
            var selection = new Rect(120, 90, 420, 260);
            overlayType.GetField("_selection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(overlay, selection);
            overlayType.GetMethod("RenderSelection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(overlay, null);
            var actionBar = (Border)overlay.FindName("ActionBar");
            actionBar.Visibility = Visibility.Visible;
            overlayType.GetMethod("PositionActionBar", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(overlay, null);
            var handle = (FrameworkElement)overlay.FindName("HandleSE");
            var down = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                Environment.TickCount, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                Source = handle
            };
            overlayType.GetMethod("Handle_MouseLeftButtonDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(overlay, [handle, down]);
            var hiddenWhileDragging = actionBar.Visibility == Visibility.Collapsed;
            // Showing a transparent overlay can deliver a real cursor move on
            // multi-monitor test machines. Restore the controlled rectangle so
            // this probe measures toolbar lifecycle rather than cursor location.
            overlayType.GetField("_selection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(overlay, selection);
            var up = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                Environment.TickCount, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                Source = overlay
            };
            overlayType.GetMethod("Window_MouseLeftButtonUp", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(overlay, [overlay, up]);
            var toolbarTop = Canvas.GetTop(actionBar);
            var adjustedSelection = overlay.CurrentSelection;
            var returnedBelowSelection = actionBar.Visibility == Visibility.Visible &&
                toolbarTop >= adjustedSelection.Bottom + 6 && toolbarTop <= adjustedSelection.Bottom + 10;
            Console.WriteLine($"CAPTURE RESIZE TOOLBAR PROBE: hidden={hiddenWhileDragging}, visible={actionBar.Visibility}, top={toolbarTop:N1}, expected={adjustedSelection.Bottom + 8:N1}");
            overlay.Close();
            return hiddenWhileDragging && returnedBelowSelection;
        });
        if (!resizeToolbarLifecycleMatches) return 59;
        Console.WriteLine("CAPTURE RESIZE TOOLBAR: hidden during handle drag and restored below the adjusted region");

        var recropShiftMatches =
            CaptureOverlayWindow.ShiftAnnotationsForRecrop(
                [new AnnotationItem
                {
                    Kind = AnnotationKind.Arrow,
                    X = 40,
                    Y = 30,
                    Points = [new Point(40, 30), new Point(120, 80)]
                }],
                new Int32Rect(100, 80, 300, 200),
                new Int32Rect(70, 60, 360, 240))[0] is { X: 70, Y: 50 } shiftedRecrop &&
            shiftedRecrop.Points[0] == new Point(70, 50) && shiftedRecrop.Points[1] == new Point(150, 100);
        if (!recropShiftMatches) return 49;
        Console.WriteLine("ANNOTATION RECROP: editable objects retain screen position when the eight capture handles resize the region");

        var annotationInteractionMatches = RunSta(() =>
        {
            var item = new AnnotationItem { Kind = AnnotationKind.Rectangle, X = 40, Y = 35, Width = 180, Height = 95 };
            var editor = new AnnotationEditorControl();
            editor.LoadImage(annotationBase, [item]);
            var type = typeof(AnnotationEditorControl);
            type.GetField("_dragging", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(editor, true);
            type.GetField("_activeId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(editor, (Guid?)item.Id);
            type.GetMethod("Surface_MouseLeftButtonUp", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, [null, null]);

            var canvas = (Canvas)editor.FindName("AnnotationCanvas");
            var handleCount = canvas.Children.OfType<FrameworkElement>().Count(child => child.Tag is AnnotationHandleTag);
            var hasDecorativeSelectionOutline = canvas.Children.OfType<System.Windows.Shapes.Rectangle>().Any(rectangle =>
                rectangle.Tag is null && rectangle.StrokeDashArray is { Count: > 0 });
            var surface = (Grid)editor.FindName("Surface");

            ((Button)editor.FindName("TextToolButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var textCursor = surface.Cursor == System.Windows.Input.Cursors.IBeam;
            type.GetMethod("StartTextEditorDrag", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(editor, [new Point(24, 24)]);
            type.GetMethod("UpdateTextEditorDrag", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(editor, [new Point(284, 154)]);
            type.GetMethod("FinishTextEditorDrag", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(editor, null);
            var textEditor = (TextBox?)type.GetField("_textEditor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor);
            var textFrame = (Grid?)type.GetField("_textEditorFrame", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor);
            var paintStyleTextEditor = textEditor is not null && textFrame is not null &&
                textEditor.Background is SolidColorBrush { Color.A: 0 } && textEditor.BorderThickness == new Thickness(0) &&
                textEditor.AcceptsReturn && textEditor.TextWrapping == TextWrapping.Wrap && textEditor.CaretBrush is not null &&
                Math.Abs(textFrame.Width - 260) < 1 && Math.Abs(textFrame.Height - 130) < 1 &&
                textFrame.Children.OfType<System.Windows.Shapes.Rectangle>().Any(frame => frame.StrokeDashArray is { Count: > 0 }) &&
                textFrame.Children.OfType<System.Windows.Controls.Primitives.Thumb>().Count() == 8;
            type.GetMethod("CancelTextEditor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, null);
            ((Button)editor.FindName("EraserToolButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var eraserCursor = surface.Cursor == System.Windows.Input.Cursors.None &&
                ((Canvas)editor.FindName("ToolCursorLayer")).Children.Count == 1;
            ((Button)editor.FindName("PencilToolButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var pencilCursor = surface.Cursor == System.Windows.Input.Cursors.Pen;
            ((Button)editor.FindName("RectangleToolButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var shapeCursor = surface.Cursor == System.Windows.Input.Cursors.Cross;
            var toolbarHasNoBlankFocusTarget = double.IsNaN(((Border)editor.FindName("PrimaryToolbarFrame")).Width) &&
                !((Button)editor.FindName("BlurToolButton")).Focusable &&
                !((Button)editor.FindName("EraserToolButton")).Focusable;

            ((Button)editor.FindName("BlurToolButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var directBrush = (AnnotationItem)type.GetMethod("AddBrushStroke", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(editor, [new Point(120, 80)])!;
            var blurUsesStrokePath = directBrush.Kind == AnnotationKind.Blur && directBrush.Points.Count == 1 &&
                directBrush.Width == 0 && directBrush.Height == 0 &&
                type.GetField("_selectedId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor) is null &&
                ((StackPanel)editor.FindName("BlurOptionsPanel")).Visibility == Visibility.Visible;

            return handleCount == 8 && !hasDecorativeSelectionOutline && textCursor && paintStyleTextEditor && eraserCursor && pencilCursor && shapeCursor &&
                toolbarHasNoBlankFocusTarget && blurUsesStrokePath;
        });
        if (!annotationInteractionMatches) return 45;
        Console.WriteLine("ANNOTATION INTERACTION: Paint-style multiline text frame, eight resize grips and direct blur brush activation verified");

        var brushEffectsMatch = RunSta(() =>
        {
            var line = new AnnotationItem
            {
                Kind = AnnotationKind.Pencil,
                Points = [new Point(40, 210), new Point(360, 210)],
                Color = "#FFEF4444",
                Thickness = 14
            };
            var eraser = new AnnotationItem
            {
                Kind = AnnotationKind.Eraser,
                Points = [new Point(200, 185), new Point(200, 235)],
                Thickness = 34
            };
            var eraserEditor = new AnnotationEditorControl();
            eraserEditor.LoadImage(annotationBase, [line, eraser]);
            var eraserCanvas = (Canvas)eraserEditor.FindName("AnnotationCanvas");
            var eraserPath = eraserCanvas.Children.OfType<System.Windows.Shapes.Path>()
                .SingleOrDefault(path => Equals(path.Tag, eraser.Id));
            var eraserPaintsOriginalPixels = eraserPath is
            {
                Stroke: ImageBrush { ImageSource: BitmapSource eraserSource },
                StrokeStartLineCap: PenLineCap.Round,
                StrokeEndLineCap: PenLineCap.Round
            } && ReferenceEquals(eraserSource, annotationBase) &&
                Math.Abs(eraserPath.StrokeThickness - eraser.Thickness) < 0.01 &&
                eraserPath.Data.Bounds.Contains(new Point(200, 210));

            var blur = new AnnotationItem
            {
                Kind = AnnotationKind.Blur,
                Points = [new Point(105, 70), new Point(105, 180)],
                Thickness = 36
            };
            var blurEditor = new AnnotationEditorControl();
            blurEditor.LoadImage(annotationBase, [blur]);
            var blurCanvas = (Canvas)blurEditor.FindName("AnnotationCanvas");
            var blurPath = blurCanvas.Children.OfType<System.Windows.Shapes.Path>()
                .SingleOrDefault(path => Equals(path.Tag, blur.Id));
            var blurPaintsOnlyItsStroke = blurPath is
            {
                Stroke: ImageBrush { ImageSource: BitmapSource blurSource },
                StrokeStartLineCap: PenLineCap.Round,
                StrokeEndLineCap: PenLineCap.Round
            } && !ReferenceEquals(blurSource, annotationBase) &&
                Math.Abs(blurPath.StrokeThickness - blur.Thickness) < 0.01 &&
                blurPath.Data.Bounds.Contains(new Point(105, 120)) &&
                !blurPath.Data.Bounds.Contains(new Point(320, 120));

            return eraserPaintsOriginalPixels && blurPaintsOnlyItsStroke;
        });
        if (!brushEffectsMatch) return 46;
        Console.WriteLine("PAINT BRUSH EFFECTS: eraser removes partial strokes and blur affects only the painted path");

        var legacyRecord = JsonSerializer.Deserialize<CaptureRecord>("{\"Id\":\"legacy\",\"FileName\":\"legacy.png\",\"Width\":10,\"Height\":8}");
        if (legacyRecord is null || legacyRecord.HasContext || !string.IsNullOrEmpty(legacyRecord.SourceKind)) return 17;
        var migratedRecord = new CaptureRecord
        {
            Id = "current",
            FileName = "current.png",
            ContextFileName = "current_context.jpg",
            ContextWidth = 1920,
            ContextHeight = 1080,
            SourceKind = "Pinned",
            SourceRegion = new CaptureRegion { X = 4, Y = 5, Width = 100, Height = 80 }
        };
        var restoredRecord = JsonSerializer.Deserialize<CaptureRecord>(JsonSerializer.Serialize(migratedRecord));
        if (restoredRecord is null || !restoredRecord.HasContext || restoredRecord.ContextWidth != 1920 || restoredRecord.SourceRegion?.Width != 100 || restoredRecord.SourceKind != "Pinned") return 18;
        Console.WriteLine("HISTORY MODEL: legacy migration and full-context metadata preserved");

        var outputRoot = Path.Combine(Path.GetTempPath(), $"snapanchor-output-{Guid.NewGuid():N}");
        var webpOutput = SettingsService.CreateOutputPath(outputRoot, "SnapAnchor_$yyyy.webp");
        var jpegOutput = SettingsService.CreateOutputPath(outputRoot, "SnapAnchor_$yyyy.jpeg");
        if (Path.GetExtension(webpOutput) != ".webp" || Path.GetExtension(jpegOutput) != ".jpeg" ||
            HotkeyOptions.Get("CtrlShiftD").VirtualKey != 0x44 || HotkeyOptions.Get("CtrlAlt9").VirtualKey != 0x39 || HotkeyOptions.All.Length < 200) return 23;
        var normalizedHotkeyExclusions = HotkeyExclusionService.Normalize([" chrome ", "CHROME", "PowerPnt", ""]);
        if (normalizedHotkeyExclusions.Count != 2 ||
            !HotkeyExclusionService.IsExcluded("chrome", normalizedHotkeyExclusions) ||
            HotkeyExclusionService.IsExcluded("notepad", normalizedHotkeyExclusions)) return 40;
        Directory.Delete(outputRoot, recursive: true);
        Console.WriteLine("DRAW COMMAND: configurable Ctrl+Shift+D hotkey and export extensions verified");
        Console.WriteLine("HOTKEY EXCLUSIONS: normalized application matching verified");

        if (new AppSettings().UpdateFeedUrl != AppSettings.DefaultUpdateFeedUrl ||
            !Uri.TryCreate(AppSettings.DefaultUpdateFeedUrl, UriKind.Absolute, out var updateFeed) ||
            updateFeed.Scheme != Uri.UriSchemeHttps ||
            !updateFeed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return 58;
        var dailyCheckNow = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        if (new AppSettings().CheckUpdatesDaily ||
            !UpdateCheckScheduleService.IsDailyCheckDue(null, dailyCheckNow, TimeZoneInfo.Utc) ||
            UpdateCheckScheduleService.IsDailyCheckDue(dailyCheckNow.AddHours(-6), dailyCheckNow, TimeZoneInfo.Utc) ||
            !UpdateCheckScheduleService.IsDailyCheckDue(dailyCheckNow.AddDays(-1), dailyCheckNow, TimeZoneInfo.Utc) ||
            UpdateCheckScheduleService.IsDailyCheckDue(dailyCheckNow.AddHours(1), dailyCheckNow, TimeZoneInfo.Utc)) return 76;
        var installedExecutable = Path.Combine(Path.GetTempPath(), "SnapAnchorInstalled", "SnapAnchor.exe");
        var portableExecutable = Path.Combine(Path.GetTempPath(), "SnapAnchorPortable", "SnapAnchor.exe");
        var portableUrl = UpdateService.ResolvePackageUrl(updateFeed, null, "SnapAnchor-Portable-win-x64.zip");
        if (UpdateService.IsPortableLocation(installedExecutable, Path.GetDirectoryName(installedExecutable)) ||
            !UpdateService.IsPortableLocation(portableExecutable, Path.GetDirectoryName(installedExecutable)) ||
            !portableUrl.Equals("https://github.com/coolman1232004/SnapAnchor/releases/latest/download/SnapAnchor-Portable-win-x64.zip", StringComparison.OrdinalIgnoreCase)) return 62;
        Console.WriteLine("UPDATE CHANNEL: startup and once-per-day schedules plus installed/portable package routing verified");

        var effectedOutput = CaptureService.ApplyOutputEffects(CreatePatternImage(160, 96), new AppSettings
        {
            OutputBorderWidth = 2,
            OutputBorderColor = "#FFEF4444",
            OutputIncludeShadow = true,
            OutputShadowSize = 4,
            OutputShadowColor = "#66000000"
        });
        if (effectedOutput.PixelWidth != 180 || effectedOutput.PixelHeight != 116) return 44;
        Console.WriteLine("OUTPUT EFFECTS: configurable border and shadow dimensions verified");

        var drawingMode = RunSta(() =>
        {
            var overlay = new CaptureOverlayWindow(CreatePatternImage(320, 180), CaptureCompletionMode.FullScreenDraw)
            {
                Width = 320,
                Height = 180,
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                Opacity = 0
            };
            var captureActions = (StackPanel)overlay.FindName("CaptureActionPanel");
            var actionsBeforeAnnotation = captureActions.Children.OfType<Button>()
                .Where(button => button.Visibility == Visibility.Visible)
                .Select(button => button.Tag as string)
                .ToArray();
            overlay.Show();
            overlay.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var editor = (AnnotationEditorControl)overlay.FindName("CaptureInlineEditor");
            var actionsDuringAnnotation = captureActions.Children.OfType<Button>()
                .Where(button => button.Visibility == Visibility.Visible)
                .Select(button => button.Tag as string)
                .ToArray();
            var captureUndo = (Button)overlay.FindName("CaptureUndoButton");
            var captureRedo = (Button)overlay.FindName("CaptureRedoButton");
            var annotationHistoryEnabled = captureUndo.IsEnabled && captureRedo.IsEnabled;
            var result = (
                overlay.IsDrawingInline,
                overlay.CurrentSelection,
                HandlesVisible: ((Canvas)overlay.FindName("SelectionHandles")).Visibility == Visibility.Visible &&
                    Panel.GetZIndex((Canvas)overlay.FindName("SelectionHandles")) > Panel.GetZIndex(editor),
                SharedToolbar: ((Border)overlay.FindName("ActionBar")).Visibility == Visibility.Visible &&
                    editor.Visibility == Visibility.Visible &&
                    actionsBeforeAnnotation.SequenceEqual(actionsDuringAnnotation) &&
                    ((StackPanel)overlay.FindName("AnnotationHistoryPanel")).Visibility == Visibility.Visible &&
                    overlay.FindName("AnnotationFinishButton") is null &&
                    annotationHistoryEnabled &&
                    ((Border)editor.FindName("PrimaryToolbarFrame")).Visibility == Visibility.Collapsed &&
                    ((Border)editor.FindName("PropertiesToolbarFrame")).Visibility == Visibility.Visible);
            overlay.Close();
            return result;
        });
        if (!drawingMode.IsDrawingInline || !drawingMode.HandlesVisible || !drawingMode.SharedToolbar || drawingMode.CurrentSelection.Width < 300 || drawingMode.CurrentSelection.Height < 160) return 24;
        Console.WriteLine($"FULL-SCREEN DRAW: capture actions remain visible while properties open over {drawingMode.CurrentSelection.Width:N0} x {drawingMode.CurrentSelection.Height:N0}");

        var formatRoot = Path.Combine(Path.GetTempPath(), $"snapanchor-formats-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(formatRoot);
            var formatImage = CreatePatternImage(160, 96);
            var jpegPath = Path.Combine(formatRoot, "test.jpg");
            var webpPath = Path.Combine(formatRoot, "test.webp");
            CaptureService.SaveImage(formatImage, jpegPath, 88);
            CaptureService.SaveImage(formatImage, webpPath, 88);
            var jpegBytes = File.ReadAllBytes(jpegPath);
            using var webp = SKBitmap.Decode(webpPath);
            if (jpegBytes.Length < 100 || jpegBytes[0] != 0xFF || jpegBytes[1] != 0xD8 || webp is null || webp.Width != 160 || webp.Height != 96) return 19;
            Console.WriteLine($"IMAGE EXPORT: JPEG {jpegBytes.Length} bytes, WebP {new FileInfo(webpPath).Length} bytes");
        }
        finally
        {
            if (Directory.Exists(formatRoot)) Directory.Delete(formatRoot, recursive: true);
        }

        var historyRoot = Path.Combine(Path.GetTempPath(), $"snapanchor-history-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("SNAPANCHOR_HISTORY_ROOT", historyRoot);
        try
        {
            var context = CreatePatternImage(320, 180);
            var region = new Int32Rect(40, 30, 120, 80);
            var result = CaptureService.Crop(context, region);
            var contextRecord = HistoryService.Add(result, region, context, "Pinned");
            var refreshablePin = RunSta(() =>
            {
                var pin = new PinnedImageWindow(result, historyRecordId: contextRecord.Id) { ShowActivated = false, Opacity = 0 };
                pin.Show();
                var canRefresh = pin.CanRefreshScreenshot;
                pin.Close();
                return canRefresh;
            });
            if (!refreshablePin) return 43;
            var restoredContext = HistoryService.LoadContextImage(contextRecord);
            var contextPreview = HistoryService.LoadContextPreview(contextRecord, 160);
            var rawPreview = HistoryService.LoadContextImage(contextRecord, 160);
            var listed = HistoryService.List().SingleOrDefault(record => record.Id == contextRecord.Id);
            if (restoredContext?.PixelWidth != 320 || restoredContext.PixelHeight != 180 || listed?.SourceRegion?.X != 40 ||
                listed.SourceKind != "Pinned" || listed.ContextWidth != 320 || contextPreview is null || rawPreview is null || PixelsEqual(contextPreview, rawPreview)) return 20;
            File.WriteAllText(Path.Combine(historyRoot, "history.json"), "{ damaged");
            if (HistoryService.List().All(record => record.Id != contextRecord.Id)) return 38;
            var fullRecord = HistoryService.Add(context, new Int32Rect(0, 0, 320, 180), context, "Edited");
            if (fullRecord.HasContext) return 21;
            if (Environment.GetEnvironmentVariable("SNAPANCHOR_RENDER_LAYOUT") == "1")
                CaptureService.SavePng(contextPreview, Path.Combine(AppContext.BaseDirectory, "context-highlight-preview.png"));
            HistoryService.UpdateMetadata(contextRecord.Id, title: "Quarterly award", favorite: true,
                tags: ["finance", "reference"]);
            var organized = HistoryService.Find(contextRecord.Id);
            if (organized?.Title != "Quarterly award" || !organized.IsFavorite || !organized.Tags.Contains("finance")) return 53;
            HistoryService.Delete(contextRecord.Id);
            HistoryService.Delete(fullRecord.Id);
            if (HistoryService.List().Any(record => record.Id == contextRecord.Id) ||
                HistoryService.List(includeDeleted: true).All(record => record.Id != contextRecord.Id || !record.IsDeleted)) return 54;
            HistoryService.Restore(contextRecord.Id);
            if (HistoryService.Find(contextRecord.Id)?.IsDeleted != false) return 55;
            HistoryService.Delete(contextRecord.Id);
            HistoryService.EmptyRecycleBin();
            if (Directory.EnumerateFiles(historyRoot).Any(path =>
                    !Path.GetFileName(path).Equals("history.json", StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileName(path).Equals("history.json.bak", StringComparison.OrdinalIgnoreCase))) return 22;
            Console.WriteLine("HISTORY CONTEXT: favorites, tags, recycle restore, highlighted context and cleanup verified");
        }
        finally
        {
            if (Directory.Exists(historyRoot)) Directory.Delete(historyRoot, recursive: true);
        }

        var gifPath = Path.Combine(Path.GetTempPath(), $"snapanchor-recording-{Guid.NewGuid():N}.gif");
        try
        {
            var gifFrame = CreatePatternImage(180, 110);
            var gifFrames = Enumerable.Repeat(gifFrame, 32).ToArray();
            ScreenRecordingService.SaveGif(gifFrames, gifPath, TimeSpan.FromMilliseconds(125));
            using var gifStream = File.OpenRead(gifPath);
            var decoder = new GifBitmapDecoder(gifStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Console.WriteLine($"RECORDING GIF: {decoder.Frames.Count} frames, {new FileInfo(gifPath).Length} bytes");
            if (decoder.Frames.Count != gifFrames.Length || new FileInfo(gifPath).Length < 100) return 8;
        }
        finally
        {
            if (File.Exists(gifPath)) File.Delete(gifPath);
        }
        if (Environment.GetEnvironmentVariable("SNAPANCHOR_TEST_MP4") == "1")
        {
            var stillPath = Path.Combine(Path.GetTempPath(), $"snapanchor-mp4-source-{Guid.NewGuid():N}.png");
            var mp4Path = Path.ChangeExtension(stillPath, ".mp4");
            CaptureService.SavePng(CreatePatternImage(320, 180), stillPath);
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var recorder = Recorder.CreateRecorder(new RecorderOptions
            {
                SourceOptions = new SourceOptions { RecordingSources = [new ImageRecordingSource { SourcePath = stillPath, OutputSize = new ScreenSize(320, 180) }] },
                OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Video, OutputFrameSize = new ScreenSize(320, 180) },
                VideoEncoderOptions = new VideoEncoderOptions { Encoder = new H264VideoEncoder { EncoderProfile = H264Profile.Main }, Framerate = 24, Bitrate = 2_000_000, IsFixedFramerate = true },
                AudioOptions = new AudioOptions { IsAudioEnabled = false }
            });
            recorder.OnRecordingComplete += (_, e) => completion.TrySetResult(e.FilePath);
            recorder.OnRecordingFailed += (_, e) => completion.TrySetException(new InvalidOperationException(e.Error));
            recorder.Record(mp4Path);
            await Task.Delay(1200);
            recorder.Stop();
            var completedPath = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var mp4Length = new FileInfo(completedPath).Length;
            Console.WriteLine($"RECORDING MP4: {recorder.CurrentFrameNumber} frames, {mp4Length} bytes");
            if (mp4Length < 1000 || recorder.CurrentFrameNumber < 1) return 9;
            var trimmedPath = await MediaTrimService.TrimMp4Async(completedPath, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(150), CancellationToken.None);
            var trimmedLength = new FileInfo(trimmedPath).Length;
            Console.WriteLine($"TRIMMED MP4: {trimmedLength} bytes");
            if (trimmedLength < 1000) return 10;
            File.Delete(stillPath);
            File.Delete(trimmedPath);
        }
        return 0;
    }

    private static int RunAnnotationQa()
    {
        var exitCode = 0;
        var thread = new Thread(() =>
        {
            var source = CreatePatternImage(760, 430);
            var editor = new AnnotationEditorControl();
            editor.LoadImage(source,
            [
                new AnnotationItem
                {
                    Kind = AnnotationKind.Pencil,
                    Points = [new Point(90, 210), new Point(670, 210)],
                    Color = "#FFEF4444",
                    Thickness = 18
                },
                new AnnotationItem
                {
                    Kind = AnnotationKind.Rectangle,
                    X = 190,
                    Y = 95,
                    Width = 380,
                    Height = 225,
                    Color = "#FF3B82F6",
                    Thickness = 5
                }
            ]);
            var window = new Window
            {
                Title = "SnapAnchor Annotation QA",
                Width = 900,
                Height = 610,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = Brushes.White,
                Content = editor
            };
            var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            application.MainWindow = window;
            exitCode = application.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static BitmapSource CreateTextImage(string text)
    {
        using var bitmap = new Drawing.Bitmap(900, 220, DrawingImaging.PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(Drawing.Color.White);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Drawing.Font("Arial", 58, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            graphics.DrawString(text, font, Drawing.Brushes.Black, new Drawing.PointF(30, 65));
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, DrawingImaging.ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }

    private static BitmapSource CreateQrImage(string text)
    {
        var matrix = new MultiFormatWriter().encode(text, BarcodeFormat.QR_CODE, 360, 360);
        var stride = matrix.Width * 4;
        var pixels = new byte[stride * matrix.Height];
        for (var y = 0; y < matrix.Height; y++)
        for (var x = 0; x < matrix.Width; x++)
        {
            var value = matrix[x, y] ? (byte)0 : (byte)255;
            var offset = y * stride + x * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        var bitmap = BitmapSource.Create(matrix.Width, matrix.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreatePatternImage(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = y * stride + x * 4;
            var hash = unchecked((uint)(x * 73856093) ^ (uint)(y * 19349663));
            pixels[offset] = (byte)(hash & 0xFF);
            pixels[offset + 1] = (byte)((hash >> 8) & 0xFF);
            pixels[offset + 2] = (byte)((hash >> 16) & 0xFF);
            pixels[offset + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool PixelsEqual(BitmapSource first, BitmapSource second)
    {
        if (first.PixelWidth != second.PixelWidth || first.PixelHeight != second.PixelHeight) return false;
        var stride = first.PixelWidth * 4;
        var a = new byte[stride * first.PixelHeight];
        var b = new byte[a.Length];
        new FormatConvertedBitmap(first, PixelFormats.Bgra32, null, 0).CopyPixels(a, stride, 0);
        new FormatConvertedBitmap(second, PixelFormats.Bgra32, null, 0).CopyPixels(b, stride, 0);
        return a.AsSpan().SequenceEqual(b);
    }

    private static bool PixelEqualsAt(BitmapSource first, BitmapSource second, int x, int y)
    {
        if (first.PixelWidth != second.PixelWidth || first.PixelHeight != second.PixelHeight) return false;
        x = Math.Clamp(x, 0, first.PixelWidth - 1);
        y = Math.Clamp(y, 0, first.PixelHeight - 1);
        var a = new byte[4];
        var b = new byte[4];
        new FormatConvertedBitmap(first, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), a, 4, 0);
        new FormatConvertedBitmap(second, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), b, 4, 0);
        return a.AsSpan().SequenceEqual(b);
    }

    internal static T RunSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("STA test failed.", failure);
        return result;
    }

    private sealed record RecoveryProbe(string Value);
}
