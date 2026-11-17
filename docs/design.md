# Design of the tool windows

The copy dialog, Find files and the Multi-rename tool follow HTML templates the user gave (17 November 2026). Their
shared look is code here, so the next windows can take it too.

## Colors (`src/NextCommander.App/Styles/DesignTokens.axaml`)

`Nc*Brush`, light and dark (theme dictionaries, switched with the color theme's variant): `NcSurface`, `NcSurface2`,
`NcSurface3`, `NcField`, `NcLine`, `NcLineStrong`, `NcText`, `NcMuted`, `NcFaint`, `NcAccent`, `NcAccentHover`,
`NcAccentSoft`, `NcOnAccent`, `NcOk` / `NcOkSoft`, `NcWarn` / `NcWarnSoft`, `NcBad` / `NcBadSoft`; the
focus rings `NcFocusRing` / `NcBadRing`; `NcMonoFont`. Use them with `DynamicResource` (in code:
`this[!Prop] = new DynamicResourceExtension("NcTextBrush")`) so light / dark switch live; no hard-coded colors.

## Classes (`src/NextCommander.App/Styles/Design.axaml`)

| Class | What |
|---|---|
| `Window.nc-window` | window ground and text |
| `Button.nc` + `.primary` `.ghost` `.stop` `.side` `.sm` `.big` | buttons (a default button is drawn as primary) |
| `Button.nc-tail` (`.flat`) | a small button inside a field (Browse, the drop-down arrow) |
| `Button.nc-icon` / `ToggleButton.nc-icon` | square icon buttons (pin, save as default) |
| `ToggleButton.nc-chip`, `ToggleButton.nc-tog` | option chips; the small `.*` / `Aa` / `ab` toggles inside a field |
| `Border.nc-combo` (`.big`, `.err`) + `TextBox.nc-bare` | a field: icon, bare text box, buttons; framed in the accent color while focused |
| `ComboBox.nc-select`, `NumericUpDown.nc-num`, `TextBox.nc-mono` | other fields |
| `Border.nc-card`, `Border.nc-options` + `Border.nc-opt`, `CheckBox.nc-switch` | cards, option rows with a switch |
| `Border.nc-seg` + `RadioButton.nc-seg` | segmented control |
| `Border.nc-disclosure` + `Border.nc-summary` | a section that opens and closes |
| `Border.nc-kbd`, `Border.nc-tag`, `Border.nc-count` (`.ok` `.warn` `.bad`) | key caps, small tags, counts |
| `Border.nc-footer`, `Border.nc-statusbar`, `Border.nc-badge-lg` | window bars, the big badge |
| `TextBlock.nc-title` `.nc-meta` `.nc-label` `.nc-hint` `.nc-section` `.nc-mono` | text |

## Code (`src/NextCommander.App/Services/Design/Nc.cs`)

`Nc.Icon(Nc.Icons.Folder)` (the templates' outline icons), `Nc.Button(text, "primary", key: "Enter")`,
`Nc.Combo(textBox, icon, tail…)`, `Nc.Field(label, field, hint, below…)`, `Nc.Line(main, side…)`, `Nc.Card(title, …)`,
`Nc.Option(checkBox, title, description, tag)`, `Nc.Options(rows…)`, `Nc.Chip`, `Nc.Tog`, `Nc.Segmented`, `Nc.Count`,
`Nc.Kbd`, `Nc.Footer(left, right…)`; `NcDisclosure` (title, summary while closed, tool buttons, body).

## Pictures

`FM_DESIGN_SHOTS=<folder> dotnet test tests/NextCommander.App.Tests --filter TemplateScreensTests` writes each of
these windows light and dark as PNG.
