---
name: autocad-2019-native-ui
description: >-
  AutoCAD 2019 (R23.0) native WPF UI style, color theme (COLORTHEME 0=Dark / 1=Light),
  classic 26px CAcToolBar (7x2 etched dot gripper, dual-line separator, UxTheme TP_BUTTON
  translucent blue hover/press film), RibbonToolTip dual-card template, AcLayer.dll /
  AdUiPalettes.dll DataGrid, TreeView, ComboBox, TextBox, SquareButton, CategoryHeader,
  ContextMenu, StatusBar, PaletteSet hosting architecture, and 16x16/20x20 vector icon
  specifications decompiled from acad.exe, AcLayer.dll, AdUiPalettes.dll, AdWindows.dll,
  AcWindows.dll, adui23.dll, and UxTheme.dll. Use this skill whenever designing,
  implementing, or styling WPF windows, PaletteSet panels, toolbars, buttons, tooltips,
  DataGrids, TreeViews, ComboBoxes, or icons for AutoCAD 2019 .NET plugins.
---

# AutoCAD 2019 Native UI Style & Architecture Skill

This skill provides the **exact, binary-decompiled UI specifications, color tokens (all 113 `PaletteTheme` Dark/Light colors), and drop-in XAML templates** extracted directly from AutoCAD 2019 (`acad.exe`, `AcLayer.dll`, `AdUiPalettes.dll`, `AdWindows.dll`, `AcWindows.dll`, `adui23.dll`, and Windows 10 `UxTheme.dll`).

Do **not** guess colors or improvise approximate styles, and do **not** re-decompile AutoCAD binaries. Use the exact values and templates documented below and in `./references/` and `./resources/`.

> [!CAUTION]
> **Decompilation / Reflection Pitfall (`AcDxUi.dll`)**:
> Never call `Assembly.LoadFrom` + `GetTypes()` on `C:\Program Files\Autodesk\AutoCAD 2019\AcDxUi.dll` in an external PowerShell/reflection process. `AcDxUi.dll` is a C++/CLI mixed-mode assembly whose `<Module>..cctor` initializes unmanaged AutoCAD globals and deadlocks outside `acad.exe`. All managed theme/control definitions reside in pure managed assemblies: `AdUiPalettes.dll`, `AcLayer.dll`, `AdWindows.dll`, `AcWindows.dll`, and `AdUIMgd.dll`.

---

## 1. Architectural Rule: Why WPF Inside `PaletteSet` Must Use These Templates

- AutoCAD's dockable panel container `Autodesk.AutoCAD.Windows.PaletteSet` is a C++/MFC `CAdUiPaletteSet` host.
- Calling `PaletteSet.AddVisual(string name, Visual control)` embeds a WPF visual tree via an internal `ElementHost` / `HwndSource`.
- AutoCAD's native `CAcToolBar` (`ToolbarWindow32`), `CAdUiPalette`, and `AcLayer.dll` (`LayerManagerControl`) are Win32/MFC/WinForms controls, **not** reusable WPF controls. Embedding child Win32 HWNDs inside a WPF visual tree triggers **WPF/Win32 Airspace conflicts** (z-order clipping, DPI mismatch, focus/shortcut swallowing).
- Conversely, AutoCAD's `Autodesk.Windows.RibbonControl` (`AdWindows.dll`) is only for the top application-level Ribbon bar.
- **Conclusion**: Any custom dockable panel (`PaletteSet`) in an AutoCAD .NET plugin must be built in pure WPF and styled with the exact decompiled templates below to achieve 100% pixel-identical native appearance.

---

## 2. Native 26px Toolbar (`acad.exe` `CAcToolBar` + `UxTheme.dll` `TP_BUTTON`)

### 2.1 Toolbar Container & Background (`CAcColorTheme` `0xA2`)
- **Total Height**: `26px` (`Height="26" MinHeight="26" MaxHeight="26"`)
- **CornerRadius**: `0` (strictly square corners)
- **Outer Border**: `1px` solid (`#FF222933` in Dark theme / `#FF656565` in Light theme)
- **Toolbar Background (`0xA2`)**:
  - **Dark (`COLORTHEME = 0`)**: `#FF494949`
  - **Light (`COLORTHEME = 1`)**: `#FFD9D9D9`
- **Padding**: `2,0,4,0`

### 2.2 Left Gripper Strip (`acad.exe` `0x1402b5c1d..0x1402b5da4`)
Decompiled from `acad.exe` (`0x1402b5c1d..0x1402b5da4`):
1. **Gripper Strip Background (`0xA8` `ToolbarGripBackgroundColor`)**:
   - `<Canvas Width="7" Height="19" Margin="1,0,4,0" VerticalAlignment="Center" Background="{DynamicResource Brush.ToolBar.GripBackground}">`
   - **Dark**: `#FF525252` | **Light**: `#FFA1A3AB`
2. **7 Rows × 2 Columns of 3D Etched Dots**:
   - Loop `ebx = 0..6` (**7 rows**) at `Y ∈ {3, 5, 7, 9, 11, 13, 15}` (each row is `1px` high, separated by `1px` of `#FF525252`/`#FFA1A3AB` background strip).
   - Within each row `Y`, 4 horizontally adjacent pixels (`X = 1, 2, 3, 4`) are drawn:
     - `X = 1`: **DarkDot (`0xA6`)**
     - `X = 2`: **LightDot (`0xA7`)** *(immediately to the RIGHT of DarkDot, NEVER below it!)*
     - `X = 3`: **DarkDot (`0xA6`)**
     - `X = 4`: **LightDot (`0xA7`)**
   - **Dot Colors**:
     - `0xA6` (`ToolbarGripDarkDotColor`): `#FF000000` (Dark) / `#FF1A1B1D` (Light)
     - `0xA7` (`ToolbarGripLightDotColor`): `#FF909090` (Dark) / `#FFEEEEF2` (Light)

### 2.3 Dual-Line Etched Separator (`adui23.dll`)
- `<Border Width="2" Height="18" Margin="4,0,5,0" Background="{DynamicResource Brush.ToolBar.SeparatorDark}" BorderBrush="{DynamicResource Brush.ToolBar.SeparatorLight}" BorderThickness="0,0,1,0"/>`
- **Dark**: Left 1px `#FF222933` + Right 1px `#FF6D6D6D`
- **Light**: Left 1px `#FFA1A3AB` + Right 1px `#FFFFFFFF`

### 2.4 Toolbar Icon Button (`22×22` Button, `20×20` Icon) & `UxTheme.dll` Hover/Press Film
`CAcToolBar` delegates button hot-tracking to Windows `UxTheme.dll` (`OpenThemeData(hwnd, L"Toolbar")`, `TP_BUTTON = 1`, `TS_HOT = 2`, `TS_PRESSED = 3`). Raw 32-bit ARGB pixel dump from `DrawThemeBackground`:

| State | Layer | Exact ARGB Value | Composited on Dark (`#FF494949`) | Composited on Light (`#FFD9D9D9`) |
| :--- | :--- | :--- | :--- | :--- |
| **Normal** | Fill & Border | `Transparent` | `#FF494949` | `#FFD9D9D9` |
| **Hover (`TS_HOT = 2`)** | Inner Film (`BtnFill`) | **`#1A008CFF`** (`10.2%` `#008CFF`) | `#FF42505C` | `#FFC3D1DD` |
| **Hover (`TS_HOT = 2`)** | 1px Border (`BtnBorder`) | **`#33008CFF`** (`20.0%` `#008CFF`) | `#FF3A566D` | `#FFAECAE1` |
| **Pressed (`TS_PRESSED = 3`)** | Inner Film (`BtnFill`) | **`#33008CFF`** (`20.0%` `#008CFF`) | `#FF3A566D` | `#FFAECAE1` |
| **Pressed (`TS_PRESSED = 3`)** | 1px Border (`BtnBorder`) | **`#66008CFF`** (`40.0%` `#008CFF`) | `#FF2C6492` | `#FF82BAE8` |

> **CRITICAL WPF RENDERING RULE (No Alpha Double-Blending)**:
> Separate the fill and border into two sibling `<Border>` elements inside a `<Grid>`:
> - `BtnFill`: `Margin="1"` (fills only the inner `20×20` region with `#1A008CFF` / `#33008CFF`)
> - `BtnBorder`: `Background="Transparent" BorderThickness="1"` (draws only the outer `1px` ring with `#33008CFF` / `#66008CFF`)

---

## 3. Native Command ToolTip (`AdWindows.dll` `/AdWindows;component/themes/tooltip.xaml`)

Decompiled from `AcWindows.dll!ToolTipUtil.CreateToolTip` and `AdWindows.dll!Autodesk.Internal.Windows.ToolTip` (`RibbonToolTipStyle`):

AutoCAD 2019 command tooltips use a **two-zone nested card layout** (identical in both Dark and Light themes):
1. **Outer Container (`mBackgroundRectangle`)**:
   - **Background**: Vertical `LinearGradientBrush` (`#FFEFEFEF` -> `#FFECECEC`), **BorderBrush**: `#FF818181`, `BorderThickness="1"`, `CornerRadius="2"`, `Padding="1"`.
2. **First-Line Title (`mTitle` / `ToolTipTitleTextBlockStyle`)**:
   - `Margin="9,9,9,6"`, `FontSize="12"`, `FontWeight="Bold"`, **Foreground**: **`#FF4C4C4C`** *(Medium-dark charcoal gray — NEVER use pure black `#FF000000`!)*.
3. **Second-Line Description Card (`mContentBorder` + `mContent` / `ToolTipTextBlockStyle`)**:
   - Inner card `<Border Background="#FFFBFBFB" BorderBrush="#FFDFDFDF" BorderThickness="1" CornerRadius="3" Padding="8" Margin="4,2,4,4">` with `<TextBlock FontSize="12" FontWeight="Normal" Foreground="#FF4C4C4C" LineHeight="18" TextWrapping="Wrap"/>`.

---

## 4. Native `DataGrid`, `TreeView`, `CategoryHeader`, `ComboBox`, `TextBox`, & `SquareButton` (`AcLayer.dll` + `AdUiPalettes.dll`)

AutoCAD 2019's flagship dockable table/tree window is **Layer Properties Manager (`AcLayer.dll`)**, which combines `MultiTreeView` (`FilterTree`) on the left, a `SplitContainer` in the middle, `LayerGrid` (`BaseGrid` + `ThemedDataGridViewCellStyle`) on the right, a `26px` `ToolBar` on top, and a `24px` `StatusBar` at the bottom, styled via `AdUiPalettes.dll` (`PaletteThemeDefaults.DarkColors` & `LightColors`).

### 4.1 Core Palette & Table Hierarchy (`AcLayer.dll` + `AdUiPalettes.dll`)

| UI Element | `PaletteTheme` Token | Dark (`COLORTHEME = 0`) | Light (`COLORTHEME = 1`) | Key Structural / Layout Rules |
| :--- | :--- | :--- | :--- | :--- |
| **Outer Container / Splitter** | `PaletteContainerBackgroundColor` (`OverallColor`) | **`#FF525252`** | **`#FFE6E6E6`** | `SplitterWidth = 4`, also used for column header vertical divider |
| **1px Control Outer Border** | `PaletteTabBorderColor` (`ControlBorderColor`) | **`#FF333333`** | **`#FF9E9E9E`** | Frames `TreeView`, `DataGrid`, `TitlePane`, `ComboBox`, `StatusBar` |
| **Collapsible Section Header / StatusBar** | `PropertyInspectorCategoryBackgroundColor` | **`#FF4A4A4A`** | **`#FFDEDEDE`** | **`Height = 24px`** (aligns horizontally with `DataGridColumnHeader`!) |
| **DataGrid & TreeView Surface / Rows** | `PaletteTabBackgroundColor` / `PaletteTreeOrListBackgroundColor` | **`#FF5C5C5C`** (AltRow: `#FF565656`) | **`#FFF7F7F7`** (AltRow: `#FFF0F0F0`) | Both `TreeView` and `DataGrid` share the same slate-gray surface in Dark theme |
| **DataGrid Cell GridLines** | `PropertyInspectorItemBorderColor` | **`#FF4D4D4D`** | **`#FFE0E0E0`** | Crisp 1px horizontal & vertical cell gridlines |
| **DataGrid Column Header** | `PaletteColumnHeaderBackgroundColor` | **`#FF666666`** | **`#FFFAFAFA`** | **`Height = 24px`**, `FontSize = 12`, `FontWeight = Normal` (NOT 30px/13px Bold!) |
| **Column Header Border & Divider** | `ListBoxBorder` + `ListBoxColumnHeaderBorder` | Bottom: **`#FF333333`**, Right: **`#FF525252`** | Bottom: **`#FF9E9E9E`**, Right: **`#FFD4D4D4`** | 1px dark bottom border + 1px subtle right vertical divider (`DrawColumnHeaderDivider`) |
| **Column Header Hover / Sorted** | `SquareButtonHoverBackground` / `ListBoxColumnHeaderSortCellBackground` | Hover: **`#FF787878 -> #FF5C5C5C`**, Sorted: **`#FF5C5C5C -> #FF525252`** | Hover: **`#FFEBEBEB -> #FFD4D4D4`**, Sorted: **`#FFF0F0F0 -> #FFE6E6E6`** | Sort arrow (`7×4` triangle): `#FFEBEBEB` (Dark) / `#FF616161` (Light) |
| **Row / TreeNode Height** | `BaseGrid.SetRowHeight` | **`22px`** (`MinHeight="22"`) | **`22px`** (`MinHeight="22"`) | Compact industrial row density (`RowHeaderWidth = 32`) |
| **Row / TreeNode Hover** | `ListBoxHoverBackground` + `ListBoxHoverBorderColor` | Fill: **`#FF666666`**, Border: **`#FF3B3B3B`** | Fill: **`#FFE6E6E6`**, Border: **`#FFB8B8B8`** | Subtle highlight on mouseover |
| **Row / TreeNode Selected** | `ListBoxSelectBackground` + `ListBoxSelectBorderColor` | Fill: **`#FF1E5E96`**, Border: **`#FF203D63`**, Text: **`#FFFFFFFF`** | Fill: **`#FFCCE8FF`**, Border: **`#FF70B4F0`**, Text: **`#FF000000`** | Preserves high contrast for both text and colored status dots |
| **TreeView Expander Arrow** | `CategoryExpanderColor` | **`#FFEBEBEB`** | **`#FF616161`** | `14×14` hit box, `5×7` right triangle / `7×5` down triangle |
| **ComboBox & SquareButton** | `ComboBoxBackground*` / `SquareButton*` | **`#FF787878 -> #FF5C5C5C`**, Border: **`#FF333333`**, Arrow: **`#CCFFFFFF`** | **`#FFFFFFFF -> #FFF0F0F0`**, Border: **`#FF9E9E9E`**, Arrow: **`#FF616161`** | Active/Pressed: `#FF4D4D4D -> #FF333333` (Dark) / `#FFE6E6E6` (Light) |
| **Search / Text Input Box** | `TextInputActiveBackground*` / `PaletteSearchActive*` | **`#FF363636 -> #FF2B2B2B`**, Border: **`#FF222933`**, Focus: **`#FF009BF5`** | **`#FFFFFFFF`**, Border: **`#FF9E9E9E`**, Focus: **`#FF0099F7`** | Used for toolbar search box and dialog text inputs |

---

## 5. Vector Icon & Theme Palette Guidelines (`COLORTHEME` `0=Dark` / `1=Light`)

1. **Icon Geometry & Sizing**:
   - Toolbar button size: `22×22`, icon size inside button: **`20×20`**.
   - Outline stroke width: **`1.0px ~ 1.2px`** (fine 1px CAD linework).
2. **Dual-Tone Semantic Icon Brushes**:
   - **Dark (`COLORTHEME = 0`)**: Primary `#FFE4E8EC`, Accent `#FF38ABFF`, Success `#FF3CC978`, Warning `#FFFFB900`, Danger `#FFF14C4C`, Muted `#FF9AA2AC`, Surface `#2238ABFF`.
   - **Light (`COLORTHEME = 1`)**: Primary `#FF2B3540`, Accent `#FF0078D7`, Success `#FF107C41`, Warning `#FFC87000`, Danger `#FFD12F2F`, Muted `#FF6E7781`, Surface `#1A0078D7`.

---

## 6. Resources & Reference Files in This Skill

- **Complete Decompiled Color (All 113 `PaletteTheme` Tokens) & Disassembly Reference**:
  [decompiled-color-and-layout-spec.md](./references/decompiled-color-and-layout-spec.md)
- **Drop-In XAML Styles (`ToolBarStyle`, `CadToolbarIconButtonStyle`, `CadToolbarSeparatorStyle`, `CadToolTipStyle`)**:
  [NativeToolbarAndToolTip.xaml](./resources/NativeToolbarAndToolTip.xaml)
- **Drop-In XAML Styles (`DataGridStyle`, `TreeViewStyle`, `CadComboBoxStyle`, `CadTextBoxStyle`, `CadSquareButtonStyle`, `CadContextMenuStyle`, `StatusBarStyle`)**:
  [NativeDataGridAndTreeView.xaml](./resources/NativeDataGridAndTreeView.xaml)
- **Drop-In Dark & Light Theme Brush Dictionaries**:
  [NativeThemeBrushes.DarkAndLight.xaml](./resources/NativeThemeBrushes.DarkAndLight.xaml)
