---
name: wpf-animation-custom-chrome
description: >-
  Use this skill when building frameless transparent WPF windows, Dynamic Island pill
  morphing Storyboard animations, DWM acrylic glass blur (DwmBackdropService.cs),
  and Canvas-based fading cursor trail overlays.
---

# WPF Animation, Custom Chrome & DWM Backdrop Skill

## 1. Frameless Dynamic Island Window (`UI/IslandWindow.xaml(.cs)`)
- Window attributes: `WindowStyle="None"`, `AllowsTransparency="True"`, `Background="Transparent"`, `Topmost="True"`, `ShowInTaskbar="False"`, `ResizeMode="NoResize"`.
- **Full-Pill Click-to-Expand:** Ensure the outer pill container (`IslandChromeBorder` / `CompactContent`) has a hit-testable transparent fill (`#01000000` or theme background brush) so clicking **anywhere** on the compact pill expands it, not just over child text/wave controls.
- **Dynamic Expanded Height:** When collapsible sections inside `ExpandedContent` (such as Translation History items or File Drop Shelf) expand/collapse, call `ExpandedContent.Measure(new Size(ExpandedWidth, double.PositiveInfinity))` inside `CalculateExpandedHeight()` so lower cards never overlap.
- **Settings Button Rule:** Keep `SettingsButton` visible **only** when `IslandState == Expanded`.

## 2. Real DWM Glass Blur (`Interop/DwmBackdropService.cs`)
- `DwmEnableBlurBehindWindow` is deprecated on Windows 10/11 and fails with `AllowsTransparency="True"`.
- Instead, `DwmBackdropService` uses undocumented/standard `SetWindowCompositionAttribute` (`ACCENT_ENABLE_BLURBEHIND` / `WCA_ACCENT_POLICY`) combined with `CreateRoundRectRgn` + `SetWindowRgn` matching the pill/card `CornerRadius`, and lowers background card opacity (`0.56` on `IslandBackgroundLayer`, `0x9C` alpha in `ApplyToCard`) when `EnableDwmBackdropBlur` is true.

## 3. Draggable Translation Popup & Fading Cursor Trail
- `TranslationPopupWindow`: supports dragging anywhere on the card via `OnPopupMouseLeftButtonDown` → `DragMove()` (excluding interactive buttons).
- `LassoOverlayWindow`: full-screen `Background="#01000000"`, `Cursor="None"`, pooled 60 FPS `WaveCanvasRenderer`/`CompositionTarget.Rendering` white dot + fading trail + LMB lasso `Polyline`.
