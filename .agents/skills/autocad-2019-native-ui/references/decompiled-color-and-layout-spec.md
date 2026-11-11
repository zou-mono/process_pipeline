# AutoCAD 2019 (R23.0) Binary-Decompiled UI Color & Layout Specification

This reference contains the complete color tables (including all 113 `PaletteThemeDefaults` Dark & Light RGB colors), BAML templates, and disassembly-verified coordinates extracted from AutoCAD 2019 (`acad.exe`, `AcLayer.dll`, `AdUiPalettes.dll`, `adui23.dll`, `AdWindows.dll`, `AcWindows.dll`, and Windows 10 `UxTheme.dll`).

---

## 1. `AdUiPalettes.dll` (`Autodesk.Windows.Palettes.PaletteTheme`) & `acad.exe` (`CAcColorTheme`)

Initialized by `AdUiImpMgdPaletteTheme.InitializeTheme` in `adui23.dll` (`0x180071dd0`) from `CAcColorTheme::GetColor(index)` (`acad.exe` `0x14030a9e8`):

| `CAcColorTheme` Index | `PaletteTheme` Property | Dark Theme (`COLORTHEME=0`) | Light Theme (`COLORTHEME=1`) | Usage in AutoCAD 2019 |
| :--- | :--- | :--- | :--- | :--- |
| `0x05` (`5`) | `PaletteBackgroundColor` | `#FF3B4453` | `#FFCFD4DF` | Legacy MFC Palette client background |
| `0x06` (`6`) | `PaletteBorderColor` | `#FF222933` | `#FFA1A3AB` | Palette & toolbar outer dark border |
| `0x07..0x0C` | `PaletteTab*` | `#FF2D3642` / `#FFFFFFFF` | `#FFB8BDC8` / `#FF1A1B1D` | Palette vertical/horizontal tabs |
| `0x0D..0x18` | `PaletteCaption*` | `#FF222933` / `#FFD5D7DC` | `#FFA1A3AB` / `#FF1A1B1D` | Floating palette title bar |
| `0x5F` (`95`) | `PaletteTreeViewBackground` | `#FF313946` | `#FFEEEEF2` | Legacy tree/list background |
| `0x60` (`96`) | `PaletteContainerBackground` | `#FF222933` | `#FFA1A3AB` | Outer container / recessed background |
| `0xA2` (`162`) | `ToolbarBackground` | **`#FF494949`** | **`#FFD9D9D9`** | **`CAcToolBar` background (`0x1402b5b9e`)** |
| `0xA6` (`166`) | `ToolbarGripDarkDotColor` | **`#FF000000`** | **`#FF1A1B1D`** | **`CAcToolBar` gripper dark etched dot (`0x1402b5c1d`)** |
| `0xA7` (`167`) | `ToolbarGripLightDotColor` | **`#FF909090`** | **`#FFEEEEF2`** | **`CAcToolBar` gripper light etched dot (`0x1402b5c28`)** |
| `0xA8` (`168`) | `ToolbarGripBackgroundColor` | **`#FF525252`** | **`#FFA1A3AB`** | **`CAcToolBar` gripper strip background (`0x1402b5c39`)** |

---

## 2. All 113 `PaletteThemeDefaults` Dark & Light Colors (`AdUiPalettes.dll`)

Generated directly via `Autodesk.Windows.Themes.ThemeBase.GenerateColors` on `PaletteThemeDefaults.DarkColors` (`DarkOverallColor`, `DarkTextColor`) and `PaletteThemeDefaults.LightColors` (`LightOverallColor`, `LightTextColor`):

| `PaletteTheme` Key (`AdUiPalettes.dll`) | Dark (`COLORTHEME = 0`) | Light (`COLORTHEME = 1`) |
| :--- | :--- | :--- |
| `CategoryExpanderColor` | `#FFEBEBEB` | `#FF616161` |
| `CategoryExpanderFillColor` | `#FFFFFFFF` | `#FFE6E6E6` |
| `CategoryExpanderShadowColor` | `#A6000000` | `#6EFFFFFF` |
| `ComboBoxActiveBackgroundBottomColor` | `#FF333333` | `#FFFCFCFC` |
| `ComboBoxActiveBackgroundTopColor` | `#FF4D4D4D` | `#FFF0F0F0` |
| `ComboBoxArrowColor` | `#CCFFFFFF` | `#FF8F8F8F` |
| `ComboBoxArrowShadowColor` | `#59424242` | `#268F8F8F` |
| `ComboBoxBackgroundBottomColor` | `#FF5C5C5C` | `#FFF0F0F0` |
| `ComboBoxBackgroundTopColor` | `#FF787878` | `#FFFFFFFF` |
| `ComboBoxHoverBackgroundBottomColor` | `#FF949494` | `#FFD4D4D4` |
| `ComboBoxHoverBackgroundTopColor` | `#FFADADAD` | `#FFEBEBEB` |
| `ComboBoxHoverSelectHighlightColor` | `#21FFFFFF` | `#21FFFFFF` |
| `ComboBoxHoverTextColor` | `#FF000000` | `#FF000000` |
| `ComboBoxSelectBackgroundBottomColor` | `#FF0056F5` | `#FF0056F5` |
| `ComboBoxSelectBackgroundTopColor` | `#FF009BF5` | `#FF009BF5` |
| `ComboBoxSelectTextColor` | `#FFFFFFFF` | `#FFFFFFFF` |
| `CommandLineCommandOptionsTextHighlightedColor` | `#FF0095EB` | `#FF0052EB` |
| `CommandLineHistoryBackgroundColor` | `#B3858585` | `#B3E6E6E6` |
| `CommandLineSuggestionListExpandedBackgroundColor` | `#CC333333` | `#CCE6E6E6` |
| `ControlActiveOuterBorderColor` | `#FF0056F5` | `#FF0099F7` |
| `ControlActiveTextColor` | `#FFFFFFFF` | `#FF000000` |
| `ControlBorderColor` | `#FF333333` | `#FF9E9E9E` |
| `ControlCaretColor` | `#FF009BF5` | `#FF0099F7` |
| `ControlErrorOuterBorderColor` | `#FFFFCC00` | `#FFFFCC00` |
| `ControlHighlightColor` | `#1AFFFFFF` | `#FFFFFFFF` |
| `ControlTextColor` | `#FFFFFFFF` | `#FF000000` |
| `FileTabsActiveBackgroundTopColor` | `#FF8F8F8F` | `#FFFFFFFF` |
| `FileTabsHighlightBottomColor` | `#FF636363` | `#FFE6E6E6` |
| `FileTabsHighlightTopColor` | `#FF7D7D7D` | `#FFF7F7F7` |
| `FileTabsHorizListBoxBackgroundColor` | `#CC5C5C5C` | `#CCF0F0F0` |
| `FileTabsInactiveBackgroundBottomColor` | `#FF525252` | `#FFD4D4D4` |
| `FileTabsInactiveBackgroundTopColor` | `#FF666666` | `#FFE6E6E6` |
| `FileTabsInnerHighlightColor` | `#0DFFFFFF` | `#1BFFFFFF` |
| `FileTabsRowBackgroundColor` | `#FF383838` | `#FFB3B3B3` |
| `GroupGripColor` | `#FF383838` | `#FF9E9E9E` |
| `GroupGripShadowColor` | `#0DFFFFFF` | `#4DFFFFFF` |
| `IPETextFocusBackgroundBottomColor` | `#FF2B2B2B` | `#FFBFBFBF` |
| `IPETextFocusBackgroundTopColor` | `#FF363636` | `#FFC9C9C9` |
| `ListBoxClickBackgroundBottomColor` | `#FF3D3D3D` | `#FF6B6B6B` |
| `ListBoxClickBackgroundTopColor` | `#FF5C5C5C` | `#FF8C8C8C` |
| `ListBoxClickBorderColor` | `#FF0A0A0A` | `#FF303030` |
| `ListBoxHoverBackgroundBottomColor` | `#FF6B6B6B` | `#FFB8B8B8` |
| `ListBoxHoverBackgroundTopColor` | `#FF8C8C8C` | `#FFE6E6E6` |
| `ListBoxHoverBorderColor` | `#FF3B3B3B` | `#FF9E9E9E` |
| `ListBoxInnerShadowColor` | `#14FFFFFF` | `#2CFFFFFF` |
| `ListBoxSelectBackgroundBottomColor` | `#FF3174E0` | `#FF32A5E3` |
| `ListBoxSelectBackgroundTopColor` | `#FF40A1CF` | `#FF41CAD1` |
| `ListBoxSelectBorderColor` | `#FF203D63` | `#FF215166` |
| `ListBoxSelectHoverBackgroundBottomColor` | `#FF3682F5` | `#FF36B7F7` |
| `ListBoxSelectHoverBackgroundTopColor` | `#FF07AAF5` | `#FF07EBF7` |
| `ListBoxSelectHoverBorderColor` | `#FF0A0A0A` | `#FF757575` |
| `ListBoxSelectTextColor` | `#FFFFFFFF` | `#FFFFFFFF` |
| `OverallColor` | `#FF525252` | `#FFE6E6E6` |
| `OverallTextColor` | `#FFFFFFFF` | `#FF000000` |
| `PaletteActivityIndicatorOutlineColor` | `#595C5C5C` | `#59F0F0F0` |
| `PaletteColumnHeaderBackgroundColor` | `#FF666666` | `#FFFAFAFA` |
| `PaletteContainerBackgroundColor` | `#FF525252` | `#FFE6E6E6` |
| `PaletteContainerBorderColor` | `#FF5C5C5C` | `#FFF0F0F0` |
| `PaletteContainerCloseColor` | `#42FFFFFF` | `#33E6E6E6` |
| `PaletteContainerHighlightColor` | `#42FFFFFF` | `#42FFFFFF` |
| `PaletteHyperlinkTextColor` | `#FF96C9FF` | `#FF0000FF` |
| `PaletteSearchActiveBottomColor` | `#FF2B2B2B` | `#FFBFBFBF` |
| `PaletteSearchActiveTopColor` | `#FF363636` | `#FFC9C9C9` |
| `PaletteSearchInactiveBottomColor` | `#FF525252` | `#FFE6E6E6` |
| `PaletteSearchInactiveTopColor` | `#FF5C5C5C` | `#FFF0F0F0` |
| `PaletteTabActiveBottomColor` | `#FF5C5C5C` | `#FFF0F0F0` |
| `PaletteTabActiveTopColor` | `#FF8F8F8F` | `#FFFFFFFF` |
| `PaletteTabBackgroundColor` | `#FF5C5C5C` | `#FFF0F0F0` |
| `PaletteTabBorderColor` | `#FF333333` | `#FF9E9E9E` |
| `PaletteTabHighlightColor` | `#33FFFFFF` | `#6EFFFFFF` |
| `PaletteTabInactiveColor` | `#FF525252` | `#FFE6E6E6` |
| `PaletteTabScrollColor` | `#99333333` | `#999E9E9E` |
| `PaletteTextActiveColor` | `#FFFFFFFF` | `#FF000000` |
| `PaletteTextInactiveColor` | `#8CFFFFFF` | `#99000000` |
| `PaletteTextSearchAutocompleteColor` | `#80FFFFFF` | `#99000000` |
| `PaletteTextSearchPlaceholderColor` | `#40FFFFFF` | `#CC000000` |
| `PaletteTextSearchUserstringColor` | `#FFFFFFFF` | `#FF000000` |
| `PaletteTreeOrListBackgroundColor` | `#FF5C5C5C` | `#FFF7F7F7` |
| `PropertyInspectorCategoryBackgroundColor` | `#FF4A4A4A` | `#FFDEDEDE` |
| `PropertyInspectorItemBorderColor` | `#FF525252` | `#FFE6E6E6` |
| `PushButtonActiveBackgroundBottomColor` | `#FF5C5C5C` | `#FFFFFFFF` |
| `PushButtonActiveBackgroundTopColor` | `#FF787878` | `#FFF7F7F7` |
| `PushButtonActiveShadowColor` | `#80000000` | `#4D000000` |
| `PushButtonActiveTextColor` | `#8CFFFFFF` | `#B3000000` |
| `PushButtonBackgroundBottomColor` | `#FF5C5C5C` | `#FFF0F0F0` |
| `PushButtonBackgroundTopColor` | `#FF787878` | `#FFFFFFFF` |
| `SpinControlBackgroundBottomColor` | `#FF5C5C5C` | `#FFF0F0F0` |
| `SpinControlBackgroundTopColor` | `#FF787878` | `#FFFFFFFF` |
| `SpinControlBorderColor` | `#1A424242` | `#36FFFFFF` |
| `SpinControlClickBackgroundBottomColor` | `#FF787878` | `#FFFFFFFF` |
| `SpinControlClickBackgroundTopColor` | `#FF5C5C5C` | `#FFE8E8E8` |
| `SpinControlHighlightColor` | `#33FFFFFF` | `#36FFFFFF` |
| `SquareButtonClickBackgroundBottomColor` | `#FF4D4D4D` | `#FFE6E6E6` |
| `SquareButtonClickBackgroundTopColor` | `#FF4D4D4D` | `#FFE6E6E6` |
| `SquareButtonClickBorderColor` | `#FF333333` | `#FF9E9E9E` |
| `SquareButtonClickHighlightColor` | `#33000000` | `#37000000` |
| `SquareButtonHoverBackgroundBottomColor` | `#FF5C5C5C` | `#FFF0F0F0` |
| `SquareButtonHoverBackgroundTopColor` | `#FF787878` | `#FFFFFFFF` |
| `SquareButtonHoverBorderColor` | `#FF333333` | `#FF9E9E9E` |
| `SquareButtonHoverHighlightColor` | `#1AFFFFFF` | `#87FFFFFF` |
| `StatusBarTooltipBackgroundColor` | `#FF000000` | `#F2E6E6E6` |
| `StatusBarTooltipStateTextColor` | `#FF0099F7` | `#D90099F7` |
| `TextInputActiveBackgroundBottomColor` | `#FF2B2B2B` | `#FFC4C4C4` |
| `TextInputActiveBackgroundTopColor` | `#FF363636` | `#FFCFCFCF` |
| `TextInputActiveHighlightColor` | `#0DFFFFFF` | `#54FFFFFF` |
| `TextInputActiveTextColor` | `#FFFFFFFF` | `#FF000000` |
| `TextInputInnerShadowColor` | `#33424242` | `#33F0F0F0` |
| `ToolbarBackgroundColor` | `#FF5F5F5F` | `#FFEBEBEB` |
| `ToolbarBorderColor` | `#FF323232` | `#FF656565` |
| `ToolbarCloseButtonColor` | `#FFB0B0B0` | `#FFEBEBEB` |
| `ToolbarGripBackgroundColor` | `#FF525252` | `#FFA1A3AB` |
| `ToolbarGripDarkDotColor` | `#FF000000` | `#FF1A1B1D` |
| `ToolbarGripLightDotColor` | `#FF909090` | `#FFEEEEF2` |
| `ToolbarRolloverBorderColor` | `#FF7CBDCF` | `#FF7CBDCF` |
| `TreeControlLineColor` | `#FF333333` | `#FF9E9E9E` |

---

## 3. `AcLayer.dll` (`LayerGrid`, `BaseGrid`, `ThemedDataGridViewCellStyle`, `MultiTreeView`, `LayerManagerControl`) Disassembly

Decompiled from `C:\Program Files\Autodesk\AutoCAD 2019\AcLayer.dll`:

### 3.1 `ThemedDataGridViewCellStyle.Initialize`
```csharp
PaletteTheme currentTheme = ThemeManager.PaletteSettings.CurrentTheme;
this.BackColor = FromMediaColor(currentTheme.PaletteTabBackgroundColor); // #FF5C5C5C (Dark) / #FFF0F0F0 (Light)
this.SelectionBackgroundTopColor = FromMediaColor(currentTheme.ComboBoxSelectBackgroundTopColor);
this.SelectionBackgroundBottomColor = FromMediaColor(currentTheme.ComboBoxSelectBackgroundBottomColor);
this.SelectionBackColor = this.SelectionBackgroundTopColor;
this.HoverBackgroundTopColor = FromMediaColor(currentTheme.ComboBoxHoverBackgroundTopColor);
this.HoverBackgroundBottomColor = FromMediaColor(currentTheme.ComboBoxHoverBackgroundBottomColor);
this.ForeColor = FromMediaColor(currentTheme.PaletteTextActiveColor); // #FFFFFFFF (Dark) / #FF000000 (Light)
this.SelectionForeColor = FromMediaColor(currentTheme.ComboBoxSelectTextColor); // #FFFFFFFF
this.DisabledTextColor = FromMediaColor(currentTheme.PaletteTextInactiveColor); // #8CFFFFFF (Dark) / #99000000 (Light)
this.ColumnDividerBrush = new SolidBrush(FromMediaColor(currentTheme.PaletteContainerBackgroundColor)); // #FF525252 (Dark) / #FFE6E6E6 (Light)
```

### 3.2 `LayerGrid.UpdateCurrentTheme` & `BaseGrid.DrawColumnHeaderDivider`
```csharp
PaletteTheme currentTheme = ThemeManager.PaletteSettings.CurrentTheme;
base.BackgroundColor = FromMediaColor(currentTheme.PaletteTabBackgroundColor); // #FF5C5C5C (Dark) / #FFF0F0F0 (Light)
base.GridColor = FromMediaColor(currentTheme.PaletteContainerBackgroundColor); // #FF525252 (Dark) / #FFE6E6E6 (Light)
base.ColumnHeadersDefaultCellStyle.BackColor = FromMediaColor(currentTheme.PaletteColumnHeaderBackgroundColor); // #FF666666 (Dark) / #FFFAFAFA (Light)
base.ColumnHeadersDefaultCellStyle.ForeColor = base.ForeColor; // #FFFFFFFF (Dark) / #FF000000 (Light)
base.EnableHeadersVisualStyles = false;
// BaseGrid.DrawColumnHeaderDivider draws a 1px vertical line at HeaderCell.Right using ColumnDividerBrush
```

### 3.3 `MultiTreeView.UpdateCurrentTheme` & `LayerManagerControl.BorderPaint`
```csharp
PaletteTheme currentTheme = ThemeManager.PaletteSettings.CurrentTheme;
this.BackColor = FromMediaColor(currentTheme.PaletteTabBackgroundColor); // #FF5C5C5C (Dark) / #FFF0F0F0 (Light)
this.ForeColor = FromMediaColor(currentTheme.PaletteTextActiveColor);    // #FFFFFFFF (Dark) / #FF000000 (Light)
this.LineColor = FromMediaColor(currentTheme.TreeControlLineColor);      // #FF333333 (Dark) / #FF9E9E9E (Light)
// LayerManagerControl.BorderPaint draws a 1px rectangle around TreeView and LayerGrid using PaletteTabBorderColor (#FF333333 Dark / #FF9E9E9E Light)
```

---

## 4. `acad.exe` `CAcToolBar` Gripper Drawing Disassembly (`0x1402b5c1d..0x1402b5da4`)

In `acad.exe` (`0x1402b5c1d..0x1402b5da4`):
1. Queries `CAcColorTheme::GetColor(0xA6)` (`r15d` = DarkDot), `0xA7` (`r12d` = LightDot), and `0xA8` (`eax` = GripBackground).
2. Calls `CDC::FillSolidRect(rect, GripBackground)` to fill the vertical gripper strip (`#FF525252` in Dark, `#FFA1A3AB` in Light).
3. Sets `edi = rect.left + 2 * dpiUnit`, `esi = rect.top + 3 * dpiUnit`.
4. Loops `for (ebx = 0; ebx < 7; ebx++)` (`cmp ebx, 0x7`), where `y = esi + ebx * 2 * dpiUnit` (`Y = 3, 5, 7, 9, 11, 13, 15` at 100% DPI):
   - `0x1402b5d12`: `FillSolidRect(x = edi + 0, y, 1, 1, DarkDot)`
   - `0x1402b5d38`: `FillSolidRect(x = edi + 1, y, 1, 1, LightDot)`
   - `0x1402b5d60`: `FillSolidRect(x = edi + 2, y, 1, 1, DarkDot)`
   - `0x1402b5d7b`: `FillSolidRect(x = edi + 3, y, 1, 1, LightDot)`

---

## 5. `UxTheme.dll` (`Toolbar` Class, `TP_BUTTON = 1`) Raw Pixel Dump

`CAcToolBar` (`ToolbarWindow32`, `TBSTYLE_FLAT`) uses Windows `UxTheme.dll` (`OpenThemeData(hwnd, L"Toolbar")`, `TP_BUTTON = 1`) for button hover (`TS_HOT = 2`), pressed (`TS_PRESSED = 3`), and checked (`TS_CHECKED = 5`, `TS_HOTCHECKED = 6`) states:

- **Hover (`TS_HOT = 2`)**:
  - Outer 1px border: **`#33008CFF`** (`Alpha = 0x33 = 20.0%`, `RGB = #008CFF`)
  - Inner `20×20` fill: **`#1A008CFF`** (`Alpha = 0x1A = 10.2%`, `RGB = #008CFF`)
- **Pressed / Checked (`TS_PRESSED = 3`, `TS_CHECKED = 5`)**:
  - Outer 1px border: **`#66008CFF`** (`Alpha = 0x66 = 40.0%`, `RGB = #008CFF`)
  - Inner `20×20` fill: **`#33008CFF`** (`Alpha = 0x33 = 20.0%`, `RGB = #008CFF`)

---

## 6. `AdWindows.dll` (`/AdWindows;component/themes/tooltip.xaml`) Decompiled BAML

- `BackgroundStartColorProperty`: **`#FFEFEFEF`**
- `BackgroundEndColorProperty`: **`#FFECECEC`**
- `BackgroundBorderColorProperty`: **`#FF818181`**
- `RibbonToolTipBase.Foreground`: **`#FF4C4C4C`**
- `mContentBorder`: `Background="#FFFBFBFB"`, `BorderBrush="#FFDFDFDF"`, `BorderThickness="1"`, `CornerRadius="3"`, `Padding="8"`, `Margin="4,3,4,4"`

---

## 7. Decompiled Native Icon Libraries (`acadbtn.xmx`, `acadbtn_light.xmx`, & `AcLayer.dll`) and `ContextMenu` (`AcWindows.dll` `contextmenuhost.baml`)

### 7.1 `acadbtn.xmx` & `acadbtn_light.xmx` Binary Structure
- Located in `C:\Program Files\Autodesk\AutoCAD 2019\`:
  - `acadbtn.xmx` (`14,030,336` bytes): Dark theme (`COLORTHEME = 0`) command icon library (`3,222` `RT_RCDATA` (`#10`) entries).
  - `acadbtn_light.xmx` (`15,232,512` bytes): Light theme (`COLORTHEME = 1`) command icon library (`3,222` `RT_RCDATA` (`#10`) entries).
- Naming convention: `RCDATA_16_<COMMAND_ID>` (`16×16` pixels) and `RCDATA_32_<COMMAND_ID>` (`32×32` pixels).
- Stream format: `49 49 2A 00` (Little-Endian 32-bit `Bgra32` TIFF with alpha channel) or `00 00 01 00` (32-bit ICO).
- Every `RCDATA_16_*` icon has distinct Dark (`acadbtn.xmx`, light strokes on dark background) and Light (`acadbtn_light.xmx`, dark strokes on light background) artwork.

### 7.2 `AcWindows.dll` `features/statusbar/contextmenuhost.baml` & `statusbarresource.baml`
- Native WPF `ContextMenu` surface uses `SolidColorBrush_34 = #FF3B3B3B` (`CurrentTheme.ContainerBackgroundBrush` Dark) / `#FFF0F0F0` (Light), outer border `#FF222933` (Dark) / `#FF9E9E9E` (Light), left icon gutter `#FF333333` (Dark) / `#FFE4E4E4` (Light), and `SquareButtonHoverBackgroundBrush` / `SquareButtonHoverBorderBrush` (`#1A008CFF` fill + `#33008CFF` border) on `Trigger IsHighlighted="True"`.
- Because WPF `ContextMenu` opens inside its own top-level `PopupRoot` (disconnected from `UserControl`'s visual and logical tree) and `Application.Current` inside AutoCAD is `AcWindows.dll`, `ContextMenu.Resources.MergedDictionaries` must explicitly merge the plugin's base `App.xaml` and active `Dark.xaml`/`Light.xaml` dictionary (`CadThemes.SyncContextMenuTheme`).
