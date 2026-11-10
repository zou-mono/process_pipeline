# AutoCAD 2019 (R23.0) Binary-Decompiled UI Color & Layout Specification

This reference contains the complete color tables, BAML templates, and disassembly-verified coordinates extracted from AutoCAD 2019 (`acad.exe`, `AdUiPalettes.dll`, `adui23.dll`, `AdWindows.dll`, `AcWindows.dll`, and Windows 10 `UxTheme.dll`).

---

## 1. `AdUiPalettes.dll` (`Autodesk.Windows.Palettes.PaletteTheme`) & `acad.exe` (`CAcColorTheme`)

Initialized by `AdUiImpMgdPaletteTheme.InitializeTheme` in `adui23.dll` (`0x180071dd0`) from `CAcColorTheme::GetColor(index)` (`acad.exe` `0x14030a9e8`):

| `CAcColorTheme` Index | `PaletteTheme` Property | Dark Theme (`COLORTHEME=0`) | Light Theme (`COLORTHEME=1`) | Usage in AutoCAD 2019 |
| :--- | :--- | :--- | :--- | :--- |
| `0x05` (`5`) | `PaletteBackgroundColor` | `#FF3B4453` | `#FFCFD4DF` | Palette client background |
| `0x06` (`6`) | `PaletteBorderColor` | `#FF222933` | `#FFA1A3AB` | Palette & toolbar outer dark border |
| `0x07..0x0C` | `PaletteTab*` | `#FF2D3642` / `#FFFFFFFF` | `#FFB8BDC8` / `#FF1A1B1D` | Palette vertical/horizontal tabs |
| `0x0D..0x18` | `PaletteCaption*` | `#FF222933` / `#FFD5D7DC` | `#FFA1A3AB` / `#FF1A1B1D` | Floating palette title bar |
| `0x5F` (`95`) | `PaletteTreeViewBackground` | `#FF313946` | `#FFEEEEF2` | Tree/list background inside palette |
| `0x60` (`96`) | `PaletteContainerBackground` | `#FF222933` | `#FFA1A3AB` | Outer container / recessed background |
| `0x98` (`152`) | `SquareButtonHoverTop1` | `#FFF9FCFD` | `#FFF9FCFD` | Split-band GDI+ button top half start |
| `0x99` (`153`) | `SquareButtonHoverTop2` | `#FFDCEFF2` | `#FFDCEFF2` | Split-band GDI+ button top half end |
| `0x9A` (`154`) | `SquareButtonHoverBottom1` | `#FFC2E2EC` | `#FFC2E2EC` | Split-band GDI+ button bottom half start |
| `0x9B` (`155`) | `SquareButtonHoverBottom2` | `#FFD7ECF2` | `#FFD7ECF2` | Split-band GDI+ button bottom half end |
| `0x9C` (`156`) | `SquareButtonRolloverTop1` | `#FFF5FAFC` | `#FFF5FAFC` | Split-band GDI+ rollover top half start |
| `0x9D` (`157`) | `SquareButtonRolloverTop2` | `#FFA5D4E0` | `#FFA5D4E0` | Split-band GDI+ rollover top half end |
| `0x9E` (`158`) | `SquareButtonRolloverBottom1` | `#FF89C5D6` | `#FF89C5D6` | Split-band GDI+ rollover bottom half start |
| `0x9F` (`159`) | `SquareButtonRolloverBottom2` | `#FFB3D8E5` | `#FFB3D8E5` | Split-band GDI+ rollover bottom half end |
| `0xA0` (`160`) | `SquareButtonOuterBorder` | `#FF9B9B9B` | `#FF9B9B9B` | Square button outer border |
| `0xA1` (`161`) | `SquareButtonInnerBorder` | `#FFFFFFFF` | `#FFFFFFFF` | Square button inner highlight border |
| `0xA2` (`162`) | `ToolbarBackground` | **`#FF494949`** | **`#FFD9D9D9`** | **`CAcToolBar` background (`0x1402b5b9e`)** |
| `0xA6` (`166`) | `ToolbarGripDarkDotColor` | **`#FF000000`** | **`#FF1A1B1D`** | **`CAcToolBar` gripper dark etched dot (`0x1402b5c1d`)** |
| `0xA7` (`167`) | `ToolbarGripLightDotColor` | **`#FF909090`** | **`#FFEEEEF2`** | **`CAcToolBar` gripper light etched dot (`0x1402b5c28`)** |
| `0xA8` (`168`) | `ToolbarGripBackgroundColor` | **`#FF525252`** | **`#FFA1A3AB`** | **`CAcToolBar` gripper strip background (`0x1402b5c39`)** |

---

## 2. `acad.exe` `CAcToolBar` Gripper Drawing Disassembly (`0x1402b5c1d..0x1402b5da4`)

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

## 3. `UxTheme.dll` (`Toolbar` Class, `TP_BUTTON = 1`) Raw Pixel Dump

`CAcToolBar` (`ToolbarWindow32`, `TBSTYLE_FLAT`) uses Windows `UxTheme.dll` (`OpenThemeData(hwnd, L"Toolbar")`, `TP_BUTTON = 1`) for button hover (`TS_HOT = 2`), pressed (`TS_PRESSED = 3`), and checked (`TS_CHECKED = 5`, `TS_HOTCHECKED = 6`) states:

- **Hover (`TS_HOT = 2`)**:
  - Premultiplied ARGB on `#00000000`:
    - Outer 1px border (`row 0`, `row 20`, `col 0`, `col 21`): `#33001C33` -> **Un-premultiplied `#33008CFF`** (`Alpha = 0x33 = 20.0%`, `RGB = #008CFF`)
    - Inner `20×20` fill (`rows 1..19`, `cols 1..20`): `#1A000E1A` -> **Un-premultiplied `#1A008CFF`** (`Alpha = 0x1A = 10.2%`, `RGB = #008CFF`)
  - Composited on Dark Toolbar (`#FF494949`):
    - Border = **`#FF3A566D`**, Fill = **`#FF42505C`**
  - Composited on Light Toolbar (`#FFD9D9D9`):
    - Border = **`#FFAECAE1`**, Fill = **`#FFC3D1DD`**
- **Pressed / Checked (`TS_PRESSED = 3`, `TS_CHECKED = 5`)**:
  - Premultiplied ARGB on `#00000000`:
    - Outer 1px border: `#66003866` -> **Un-premultiplied `#66008CFF`** (`Alpha = 0x66 = 40.0%`, `RGB = #008CFF`)
    - Inner `20×20` fill: `#33001C33` -> **Un-premultiplied `#33008CFF`** (`Alpha = 0x33 = 20.0%`, `RGB = #008CFF`)
  - Composited on Dark Toolbar (`#FF494949`):
    - Border = **`#FF2C6492`**, Fill = **`#FF3A566D`**
  - Composited on Light Toolbar (`#FFD9D9D9`):
    - Border = **`#FF82BAE8`**, Fill = **`#FFAECAE1`**

---

## 4. `AdWindows.dll` (`/AdWindows;component/themes/tooltip.xaml`) Decompiled BAML

Extracted from `AdWindows.g.resources` -> `themes/tooltip.baml` and `Autodesk.Internal.Windows.ToolTip`:

### 4.1 `Autodesk.Internal.Windows.ToolTip` DependencyProperty Defaults
- `BackgroundStartColorProperty`: **`#FFEFEFEF`**
- `BackgroundEndColorProperty`: **`#FFECECEC`**
- `BackgroundBorderColorProperty`: **`#FF818181`**
- `HighlightBorderColorProperty`: **`#FF38ABDF`**

### 4.2 `ToolTipTitleTextBlockStyle` & `ToolTipTextBlockStyle`
```xml
<Style x:Key="ToolTipTextBlockStyle" TargetType="{x:Type TextBlock}">
  <Setter Property="FontFamily" Value="Microsoft YaHei UI" />
  <Setter Property="FontSize" Value="12" />
  <Setter Property="FontWeight" Value="Normal" />
  <Setter Property="TextWrapping" Value="Wrap" />
</Style>

<Style x:Key="ToolTipTitleTextBlockStyle" TargetType="{x:Type TextBlock}">
  <Setter Property="FontFamily" Value="Microsoft YaHei UI" />
  <Setter Property="FontSize" Value="12" />
  <Setter Property="FontWeight" Value="Bold" />
  <Setter Property="TextWrapping" Value="Wrap" />
</Style>
```

### 4.3 `RibbonToolTipBase` & `RibbonToolTipStyle` (`themes/tooltip.xaml`)
```xml
<Style x:Key="{x:Type aw:RibbonToolTipBase}" TargetType="{x:Type aw:RibbonToolTipBase}">
  <Setter Property="TextElement.Foreground" Value="#FF4C4C4C" />
</Style>

<!-- Inside RibbonToolTipStyle ControlTemplate: -->
<Border Name="mRibbonToolTipBorder" Margin="1,1,1,1">
  <StackPanel>
    <!-- Header Zone (on #FFEFEFEF outer background) -->
    <TextBlock Name="mTitle"
               Margin="9,10,9,6"
               Style="{DynamicResource ToolTipTitleTextBlockStyle}" />
    <!-- Inner Off-White Content Card -->
    <Border Name="mContentBorder"
            Background="#FFFBFBFB"
            BorderBrush="#FFDFDFDF"
            BorderThickness="1,1,1,1"
            CornerRadius="3,3,3,3"
            Padding="8,8,8,8"
            Margin="4,3,4,4">
      <ContentControl Name="mContent"
                      Style="{DynamicResource ToolTipContentControlStyle}" />
    </Border>
  </StackPanel>
</Border>
```
