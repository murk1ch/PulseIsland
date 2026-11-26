---
name: wpf-animation-custom-chrome
description: >-
  Use this skill when building frameless transparent WPF windows, Dynamic Island
  pill animations, DWM acrylic blur (DwmBackdropService), FlowDocument Markdown/LaTeX
  rendering, onboarding transitions, in-box placeholders, and bilingual EN/RU layouts.
---

# WPF Animation, Custom Chrome, DWM Blur & Bilingual UI Skill

## 1. Frameless Dynamic Island Window (`UI/IslandWindow.xaml(.cs)`)
- Attributes: `WindowStyle="None"`, `AllowsTransparency="True"`, `Background="Transparent"`, `Topmost="True"`, `ShowInTaskbar="False"`, `ResizeMode="NoResize"`.
- **Full-Pill Click-to-Expand:** Keep a hit-testable fill (`#01000000` or theme brush) on `IslandChromeBorder` / `CompactContent` so clicking anywhere on the compact pill expands it.
- **Dynamic Expanded Height:** Re-measure `ExpandedContent.Measure(new Size(ExpandedWidth, double.PositiveInfinity))` inside `CalculateExpandedHeight()` whenever collapsible sections (Translation History, File Drop Shelf, Notifications) toggle so cards never overlap.
- **Settings Button Rule:** Keep `SettingsButton` visible **only** when `IslandState == Expanded`.

## 2. DWM Glass Blur & Safe Tree Walking (`Interop/DwmBackdropService.cs`)
- Uses `SetWindowCompositionAttribute` (`WCA_ACCENT_POLICY` + `ACCENT_ENABLE_BLURBEHIND`) clipped to exact rounded bounds via `CreateRoundRectRgn` + `SetWindowRgn` (since `DwmEnableBlurBehindWindow` is deprecated on Windows 10/11).
- `ApplyToCard` lowers card brush alpha (`blurAlpha`, e.g., `0xAC` / `0x9C`) for both `SolidColorBrush` and `LinearGradientBrush` themes when `EnableDwmBackdropBlur` is enabled.
- **`DwmBackdropService.GetParentSafe(DependencyObject?)`:** Always use `GetParentSafe` instead of raw `VisualTreeHelper.GetParent` during drag-move or hit-testing — it safely handles both `Visual`/`Visual3D` and `ContentElement`/`FrameworkContentElement` (`FlowDocument`, `Run`, `Span`, `Paragraph`, `TableCell`) without throwing `InvalidOperationException` when clicking inside a `RichTextBox`.

## 3. Rich AI Formatting & Fluid Onboarding
- **Markdown + LaTeX `FlowDocument` (`Services/AiResponseFormatter.cs`):** Converts LLM Markdown (`**bold**`, `*italic*`, headings, lists, code fences, tables) and LaTeX (`\(...\)`, `\[...\]`, `$$...$$`, `$...$`) into clean Unicode and rich WPF `FlowDocument` blocks for `AiAssistantPopupWindow`'s `AnswerRichTextBox`.
- **3-Step Onboarding (`UI/WelcomeOnboardingWindow.xaml(.cs)`):** Uses fluid `CubicEase` opacity, `ScaleTransform`, and `BlurEffect` entrance/exit animations plus smooth `UpdateStepVisuals(animate: true)` transitions across `Step1Panel` -> `Step2Panel` -> `Step3Panel` with live EN/RU language, 7-theme, and DWM blur previews.
- **Draggable Popups & Capture Overlay:** `TranslationPopupWindow` and `AiAssistantPopupWindow` support `DragMove()` outside interactive controls; `LassoOverlayWindow` uses `Background="#0402040A"`, `Cursor="None"`, a dual-contrast `Pulse Orb` reticle (`CursorReticle`), 60 FPS `CompositionTarget.Rendering` fading trail, and a shrinkable rectangle selection (`SelectionPreviewOuter`/`SelectionPreviewInner` + `DimBackdropPath`) supporting LMB translate and RMB/Shift+LMB Vision capture.

## 4. In-Box Placeholders, Responsive Bilingual Layouts & Live Localization
- **In-Box Placeholder `Grid` Overlays:** Place a `TextBlock` (`IsHitTestVisible="False"`) inside the same `Grid` cell as the `TextBox` (e.g., `TranslationSearchBox`, `PromptPlaceholderText`) and hide it via `DataTrigger` when `Text` is non-empty or `IsKeyboardFocused` is `True`.
- **Responsive EN/RU Layouts:** Prefer `Grid` (`*` / `Auto`) and `WrapPanel` over fixed pixel widths so Russian labels (often 20–35% longer than English) wrap cleanly without clipping or overlapping buttons.
- **Live `{DynamicResource Loc_*}` Localization (`Services/LocalizationService.cs`):** Bind XAML strings with `{DynamicResource Loc_*}` (and use `LocalizationService.Pick(en, ru)` in C#) so switching language updates every open window immediately with 100% EN/RU key parity.
