# GitHistory.UI

A native WPF control library for reusable desktop workspaces. It targets
`net10.0-windows` and has **no package dependencies** or references to application
or Git models. Controls retain normal WPF commands, bindings, keyboard navigation,
validation, and UI Automation support.

## Use in another application

Add a project reference to `GitHistory.UI.csproj`, then merge the palette before
the control dictionary in `App.xaml`:

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceDictionary Source="/GitHistory.UI;component/Themes/Dark.xaml"/>
      <ResourceDictionary Source="/GitHistory.UI;component/Themes/Controls.xaml"/>
      <ResourceDictionary Source="/GitHistory.UI;component/Themes/Workspace.xaml"/>
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

Styles use dynamic brush resources, including the rendered chart. App-local
resources can override any token or style without changing the library.

Set the host window's `Background="{DynamicResource CanvasBrush}"`,
`Foreground="{DynamicResource TextBrush}"`, `FontFamily="Segoe UI Variable Text, Segoe UI"`,
and `FontSize="13"`. Text inherits the containing control's color and typography,
so primary buttons, selected dates, disabled controls, and custom content use the
correct foreground in each theme.

## Theme choices and runtime switching

| Name | Appearance | Palette dictionary |
| --- | --- | --- |
| `Dark` | Deep blue surfaces and a blue accent | `Themes/Dark.xaml` |
| `Light` | Pale blue surfaces and a blue accent | `Themes/Light.xaml` |
| `Classic` | Original charcoal surfaces and a teal accent | `Themes/Classic.xaml` |
| `Dusk` | Muted aubergine surfaces and a lavender accent | `Themes/Dusk.xaml` |

Use any palette path in the initial resource merge. Switch themes at runtime
through the reusable catalog on the WPF dispatcher:

```csharp
using GitHistory.UI.Theming;

IReadOnlyList<ThemeDescriptor> choices = ThemeCatalog.Themes;
ThemeDescriptor selected = ThemeCatalog.Apply(Application.Current, "Classic");
// selected.Name is the canonical name; selected.IsLight can drive native window chrome.
```

The catalog exposes `Name`, `Source` (`Uri`), and `IsLight` for each choice, in
`Dark`, `Light`, `Classic`, `Dusk` order. `ThemeCatalog.Get(name)` resolves a name
without applying it. Names are case-insensitive; unknown or missing names resolve
to `Dark`. `Apply` replaces only the library's palette dictionaries and preserves
control dictionaries and app-specific overrides. The host owns its settings
store and may save the returned canonical name.

All palettes include the same neutral, interaction, and semantic tokens. Classic
retains the earlier charcoal/teal and status colors, with a brighter tertiary text
color for readability. The GitHistory host's `Ctrl+D` and command palette cycle
through the four choices in catalog order.

## Controls

```xml
xmlns:ui="clr-namespace:GitHistory.UI.Controls;assembly=GitHistory.UI"
xmlns:uib="clr-namespace:GitHistory.UI.Behaviors;assembly=GitHistory.UI"
```

| Control | Purpose | Properties |
| --- | --- | --- |
| `SearchBox` | TextBox with a non-interactive placeholder | `Placeholder`, `ShowSearchIcon` (default `true`), standard `Text` binding |
| `Pane` | Bordered content section | `Header`, `HeaderDescription`, `HeaderActions`, `Content`, `Padding` |
| `ActivityChart` | Compact daily bar chart with filtering interactions | `ItemsSource`, `DateMemberPath`, `ValueMemberPath`, `SelectedItem`, `RangeStart`, `RangeEnd` |
| `BusyIndicator` | Indeterminate progress with a status label | Standard `Content` and `Visibility` |
| `Sidebar` | Expandable navigation with a resize grip | `Header`, `Content`, `IsExpanded`, `ExpandedWidth`, `CollapsedWidth` |
| `WorkspaceLayout` | Three resizable arrangements of existing panes | `Layout`, `ColumnRatio`, `RowRatio`, attached `Region` |

`SearchBox` has its own implicit style. Omit an explicit `FilterEditor` style on
it; that named style is for ordinary `TextBox` controls. Set `ShowSearchIcon="False"`
for placeholder fields that are not searches. Supply `AutomationProperties.Name`
as a persistent accessible label; a placeholder is only a visual hint.

```xml
<ui:Pane Header="Activity" HeaderDescription="Select a day or drag a date range">
  <ui:ActivityChart Height="62" ItemsSource="{Binding DailyActivity}"
                    DateMemberPath="Date" ValueMemberPath="Count"
                    SelectedItem="{Binding SelectedDay, Mode=TwoWay}"
                    RangeStart="{Binding From, Mode=TwoWay}"
                    RangeEnd="{Binding Until, Mode=TwoWay}"
                    AutomationProperties.Name="Daily activity"/>
</ui:Pane>
```

The chart accepts any item type with readable date and numeric properties,
including dotted member paths. Dates may be `DateTime`, `DateTimeOffset`, or
`DateOnly`; values must convert to finite numbers. Negative values are displayed
as zero. `INotifyCollectionChanged` updates are observed through WPF's weak event
manager. Replace the collection when changing an existing item's values.

`RangeEnd` is **exclusive**: September 1 through September 3 is represented as
`RangeStart = September 1`, `RangeEnd = September 4`. Click or Left/Right selects
a day and its original item. Drag selects an interval. Home/End jumps to the
first/last day; Shift+arrow extends an interval. Keep date editors available
alongside the compact chart when precise date entry is needed.

## Sidebar and workspace layouts

Put `Sidebar` in an `Auto`-width column. Its header button collapses the content
to a narrow rail and restores the previous width when expanded. Drag the right
edge to resize between 220 and 420 DIPs, or focus that edge and use Left/Right
(Shift for larger steps; Home restores 252). `Sidebar.ToggleCommand` can be wired
to a host keyboard shortcut using the sidebar as `CommandTarget`.

`WorkspaceLayout` keeps the same pane instances and their selections as the
arrangement changes. Add three children and assign their regions:

```xml
<ui:WorkspaceLayout x:Name="Workspace" Layout="Balanced">
  <ui:Pane ui:WorkspaceLayout.Region="Primary" Header="Records">
    <DataGrid ItemsSource="{Binding Records}"/>
  </ui:Pane>
  <ui:Pane ui:WorkspaceLayout.Region="History" Header="History">
    <ListBox ItemsSource="{Binding History}"/>
  </ui:Pane>
  <ui:Pane ui:WorkspaceLayout.Region="Inspector" Header="Inspector" Content="{Binding SelectedRecord}"/>
</ui:WorkspaceLayout>
```

Supply a `DataTemplate` for the selected record to define its inspector content.

| Layout | Arrangement |
| --- | --- |
| `Balanced` | Records above history and inspector |
| `SideBySide` | Records on the left, history above inspector on the right |
| `FocusDiff` | Records and history above a full-width inspector |

Bind a layout picker to `WorkspaceLayout.Presets` (`Name` for display, `Value`
for selection) and the control's two-way `Layout` property. Each arrangement
remembers its own proportions. Dividers support dragging and arrow keys.
`CaptureLayouts()` returns serializable `WorkspaceLayoutState` records;
`RestoreLayouts(states, selected)` restores them. `ResetLayouts()` restores all
defaults. The host owns storage and can also persist the sidebar's `IsExpanded`
and `ExpandedWidth`. No application services are required by either control.

## Native control styles

The dictionary supplies implicit styles for Button, TextBox, PasswordBox,
ComboBox, DatePicker, CheckBox, DataGrid, TreeView, ListBox, TabControl, ContextMenu,
ScrollBar, ProgressBar, and ToolTip, with hover, focus, disabled, and selection
states. Named styles include `ActionButton`, `PrimaryButton`, `QuietButton`,
`FilterEditor`, `ViewTab`, `OverlayCard`, `Eyebrow`, and `Mono`.

`DataGrid` defaults suit a read-only file or record browser. Set editing,
selection, row height, and column options locally for other uses. Full-row
selection has a row focus cue without an overlapping cell outline; cell-selection
modes retain their cell focus cue. Column resizing,
sorting, reordering, row virtualization, and grouped virtualization remain native
WPF behavior. Opt into the header menu only where useful:

```xml
<DataGrid uib:DataGridOptions.IsEnabled="True" ItemsSource="{Binding Records}">
  <DataGrid.GroupStyle>
    <GroupStyle HeaderTemplate="{StaticResource GridGroupHeader}"/>
  </DataGrid.GroupStyle>
</DataGrid>
```

The menu offers column visibility, grouping, and clearing grouping. The library
does not save layout preferences; the hosting application owns persistence.

For grids that refresh their items while a resizable pane changes width, opt into
`uib:StarColumnSizing.IsEnabled="True"`. It recalculates proportional (`*`)
columns after WPF's queued sizing callbacks finish, preventing a native timing
issue from leaving the columns too narrow. It preserves column bindings and
virtualization, coalesces source/size events, and detaches listeners on unload.
GitHistory enables this on its file-history grid.

## Palette tokens

Neutral tokens: `CanvasBrush`, `SidebarBrush`, `SurfaceBrush`, `ElevatedBrush`,
`HoverBrush`, `LineBrush`, `TextBrush`, `MutedBrush`, `QuietBrush`.

Interaction tokens: `AccentBrush`, `AccentHoverBrush`, `AccentTextBrush`,
`AccentSoftBrush`, `FocusBrush`, `OverlayBrush`, `ChartTrackBrush`.

Semantic tokens: `AddedBrush`, `DeletedBrush`, `ModifiedBrush`, `RenamedBrush`,
their `...BackgroundBrush` companions, `DiffHeaderBrush`, and
`DiffHeaderBackgroundBrush`. They are colors only; application badges and
domain-specific presentation remain in the consuming project.
