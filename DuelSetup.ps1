Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Opponent = $env:TCPTUNNEL_DUEL_PEER
if ([string]::IsNullOrEmpty($Opponent) -or $Opponent.Length -lt 3 -or $Opponent.Length -gt 20) { return }
$ru = $env:TCPTUNNEL_DUEL_LANGUAGE -eq 'ru'
function Tr($Russian, $English) { if ($ru) { $Russian } else { $English } }
function Get-DuelTargetRadius([string]$Size) {
    return @{ large = 30; medium = 21; small = 12; any = 30 }[$Size]
}
function Get-DuelFieldLimit([int]$Width, [int]$Height, [int]$Radius) {
    $pitch = 2 * $Radius + 12
    $columns = 1 + [Math]::Floor(($Width - 2 * ($Radius + 8)) / $pitch)
    $rows = 1 + [Math]::Floor(($Height - 123 - 2 * $Radius) / $pitch)
    return [Math]::Max(0, $columns * $rows - 1)
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if (-not ('TcpTunnelLab.DialogChrome' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace TcpTunnelLab {
    public static class DialogChrome {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SetWindowTheme(IntPtr window, string appName, string idList);

        public static void Caption(IntPtr window, int background, int foreground) {
            int dark = 1;
            if (DwmSetWindowAttribute(window, 20, ref dark, 4) != 0)
                DwmSetWindowAttribute(window, 19, ref dark, 4);
            DwmSetWindowAttribute(window, 35, ref background, 4);
            DwmSetWindowAttribute(window, 36, ref foreground, 4);
        }

        public static void Combo(IntPtr window) {
            SetWindowTheme(window, "DarkMode_CFD", null);
        }
    }
}
'@
}

$palette = @{
    Background = [Drawing.Color]::FromArgb(18,22,26)
    Input = [Drawing.Color]::FromArgb(27,34,40)
    Border = [Drawing.Color]::FromArgb(59,75,85)
    Text = [Drawing.Color]::FromArgb(221,230,235)
    Muted = [Drawing.Color]::FromArgb(155,173,184)
    Accent = [Drawing.Color]::FromArgb(119,222,208)
    Selected = [Drawing.Color]::FromArgb(40,68,73)
}

function New-LabDialog([string]$Title, [int]$Width, [int]$Height) {
    $form = [Windows.Forms.Form]::new()
    $form.Text = $Title
    $form.ClientSize = [Drawing.Size]::new($Width,$Height)
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'
    $form.MinimizeBox = $false
    $form.MaximizeBox = $false
    $form.BackColor = $palette.Background
    $form.ForeColor = $palette.Text
    $form.Font = [Drawing.Font]::new('Consolas', 10)
    $form.Add_Disposed({ param($sender, $eventArgs) $sender.Font.Dispose() })
    $form.Add_HandleCreated({
        param($sender, $eventArgs)
        if (-not [Windows.Forms.SystemInformation]::HighContrast) {
            [TcpTunnelLab.DialogChrome]::Caption($sender.Handle, [Drawing.ColorTranslator]::ToWin32($palette.Background), [Drawing.ColorTranslator]::ToWin32($palette.Text))
        }
    })
    $form
}

function Place($Form, $Control, [int]$X, [int]$Y, [int]$Width, [int]$Height) {
    if ($Control -is [Windows.Forms.ComboBox]) {
        $Control.FlatStyle = 'Flat'
        $Control.DrawMode = 'OwnerDrawFixed'
        $Control.ItemHeight = 21
        $Control.BackColor = $palette.Input
        $Control.ForeColor = $palette.Text
        $Control.Add_HandleCreated({
            param($sender, $eventArgs)
            if (-not [Windows.Forms.SystemInformation]::HighContrast) { [TcpTunnelLab.DialogChrome]::Combo($sender.Handle) }
        })
        $Control.Add_DrawItem({
            param($sender, $item)
            if ($item.Index -lt 0) { return }
            $selected = ($item.State -band [Windows.Forms.DrawItemState]::Selected) -ne 0
            $background = if ($selected) { $palette.Selected } else { $palette.Input }
            $foreground = if (-not $sender.Enabled) { $palette.Muted } elseif ($selected) { $palette.Accent } else { $palette.Text }
            $brush = [Drawing.SolidBrush]::new($background)
            try { $item.Graphics.FillRectangle($brush, $item.Bounds) }
            finally { $brush.Dispose() }
            $bounds = $item.Bounds
            $bounds.X += 5; $bounds.Width = [Math]::Max(1,$bounds.Width - 5)
            $flags = [Windows.Forms.TextFormatFlags]'VerticalCenter,EndEllipsis,SingleLine,NoPrefix'
            [Windows.Forms.TextRenderer]::DrawText($item.Graphics, [string]$sender.Items[$item.Index], $sender.Font, $bounds, $foreground, $flags)
            $item.DrawFocusRectangle()
        })
        if (-not [Windows.Forms.SystemInformation]::HighContrast) {
            $Control.SetBounds(0,0,$Width,$Height)
            $frame = [Windows.Forms.Panel]::new()
            $frame.BackColor = $palette.Border
            $frame.TabStop = $false
            $frame.SetBounds($X,$Y,$Control.Width,$Control.Height)
            $Control.Region = [Drawing.Region]::new([Drawing.Rectangle]::new(1,1,$Control.Width - 2,$Control.Height - 2))
            $frame.Controls.Add($Control)
            $Form.Controls.Add($frame)
            return $Control
        }
    }
    elseif ($Control -is [Windows.Forms.TextBox] -or $Control -is [Windows.Forms.NumericUpDown]) {
        $Control.BackColor = $palette.Input
        $Control.ForeColor = $palette.Text
        $Control.BorderStyle = 'FixedSingle'
    }
    elseif ($Control -is [Windows.Forms.Button]) {
        $Control.FlatStyle = 'Flat'
        $Control.UseVisualStyleBackColor = $false
        $Control.BackColor = $palette.Input
        $Control.ForeColor = $palette.Text
        $Control.FlatAppearance.BorderColor = $palette.Border
        $Control.FlatAppearance.MouseOverBackColor = $palette.Selected
        $Control.FlatAppearance.MouseDownBackColor = $palette.Selected
    }
    elseif ($Control -is [Windows.Forms.CheckBox]) {
        $Control.FlatStyle = 'Flat'
        $Control.ForeColor = $palette.Text
        $Control.FlatAppearance.CheckedBackColor = $palette.Selected
        $Control.FlatAppearance.MouseOverBackColor = $palette.Input
    }
    $Control.SetBounds($X,$Y,$Width,$Height)
    $Form.Controls.Add($Control)
    $Control
}

function Label($Form, [string]$Text, [int]$X, [int]$Y, [int]$Width) {
    $item = [Windows.Forms.Label]::new()
    $item.Text = $Text
    $item.ForeColor = $palette.Muted
    Place $Form $item $X $Y $Width 26
}

$setup = New-LabDialog (Tr "TCPTunnel / Дуэль с @$Opponent" "TCPTunnel / Duel with @$Opponent") 478 327
$arena = [Windows.Forms.Screen]::FromPoint([Windows.Forms.Cursor]::Position).WorkingArea
$width = 900
$height = 540
$modes = @('static','moving','minefield')
$sizes = @('large','medium','small','any')
$levels = @('easy','normal','hard')
$modeLabel = Label $setup (Tr 'Режим дуэли' 'Mode') 14 21 174
$mode = Place $setup ([Windows.Forms.ComboBox]::new()) 190 18 272 25
$mode.DropDownStyle = 'DropDownList'
$mode.Items.AddRange(@((Tr 'Статичные мишени' 'Static targets'),(Tr 'Движущиеся мишени' 'Moving targets'),(Tr 'Минное поле' 'Minefield')))
$mode.SelectedIndex = 0
$sizeLabel = Label $setup (Tr 'Размер мишеней' 'Target size') 14 62 174
$size = Place $setup ([Windows.Forms.ComboBox]::new()) 190 59 272 25
$size.DropDownStyle = 'DropDownList'
$size.Items.AddRange(@((Tr 'Большие' 'Large'),(Tr 'Средние' 'Medium'),(Tr 'Маленькие' 'Small'),(Tr 'Без разницы' 'Random')))
$size.SelectedIndex = 1
$difficultyLabel = Label $setup (Tr 'Сложность' 'Difficulty') 14 103 174
$difficulty = Place $setup ([Windows.Forms.ComboBox]::new()) 190 100 272 25
$difficulty.DropDownStyle = 'DropDownList'
$difficulty.Items.AddRange(@((Tr 'Лёгкая' 'Easy'),(Tr 'Средняя' 'Normal'),(Tr 'Высокая' 'Hard')))
$difficulty.SelectedIndex = 1
$minutesLabel = Label $setup (Tr 'Время (минуты)' 'Minutes') 14 138 174
$minutes = Place $setup ([Windows.Forms.NumericUpDown]::new()) 190 135 84 25
$minutes.Minimum = 1; $minutes.Maximum = 15; $minutes.Value = 2
$blitz = Place $setup ([Windows.Forms.CheckBox]::new()) 190 167 270 24
$blitz.Text = (Tr 'Блиц' 'Blitz')
$mineLabel = Label $setup (Tr 'Мишеней на экране' 'Targets on screen') 14 103 174
$targets = Place $setup ([Windows.Forms.NumericUpDown]::new()) 190 100 84 25
$targets.Minimum = 1
$limitLabel = Label $setup '' 284 102 176
$description = Label $setup (Tr 'Наведи курсор на параметр, чтобы увидеть описание.' 'Hover a setting or use Tab to read about it.') 14 230 448
$description.Height = 44
$cancel = Place $setup ([Windows.Forms.Button]::new()) 246 283 100 28
$cancel.Text = (Tr 'Отмена' 'Cancel'); $cancel.DialogResult = 'Cancel'
$start = Place $setup ([Windows.Forms.Button]::new()) 355 283 107 28
$start.Text = (Tr 'Вызвать' 'Challenge'); $start.DialogResult = 'OK'
$start.ForeColor = $start.FlatAppearance.BorderColor = $palette.Accent
$setup.AcceptButton = $start; $setup.CancelButton = $cancel

function Set-DuelHint([string]$Text, [Windows.Forms.Control[]]$Controls) {
    $hintLabel = $description
    $showHint = { param($sender, $eventArgs) $hintLabel.Text = $Text }.GetNewClosure()
    foreach ($control in $Controls) {
        $control.Add_MouseEnter($showHint)
        $control.Add_Enter($showHint)
        if ($control -is [Windows.Forms.ComboBox] -and $control.Parent -is [Windows.Forms.Panel]) {
            $control.Parent.Add_MouseEnter($showHint)
        }
        if ($control -is [Windows.Forms.NumericUpDown]) {
            foreach ($child in $control.Controls) { $child.Add_MouseEnter($showHint) }
        }
    }
}

Set-DuelHint (Tr 'Статичные стоят на месте. Движущиеся меняют траекторию. В минном поле новые шары появляются сразу после попадания.' 'Static targets stay still. Moving targets follow varied paths. Minefield replaces each hit immediately.') @($modeLabel,$mode)
Set-DuelHint (Tr 'Размер задаёт диаметр шара. «Без разницы» выбирает размер случайно при каждом появлении.' 'Choose a target diameter or use a random size for each new target.') @($sizeLabel,$size)
Set-DuelHint (Tr 'Чем выше сложность, тем быстрее движутся шары, чаще появляются и меньше живут.' 'Higher difficulty increases movement speed and spawn frequency and shortens target lifetime.') @($difficultyLabel,$difficulty)
Set-DuelHint (Tr 'Длительность раунда: от 1 до 15 целых минут.' 'Round length: 1 to 15 whole minutes.') @($minutesLabel,$minutes)
Set-DuelHint (Tr 'В Blitz мишени общие, победителя определяет число попаданий. ' 'Blitz uses shared targets; the player with more confirmed hits wins.') @($blitz)
Set-DuelHint (Tr 'Число одновременно видимых шаров. Лимит оставляет место для замены без пересечений.' 'Concurrent targets. The limit leaves room to replace targets without overlap.') @($mineLabel,$targets,$limitLabel)
Set-DuelHint (Tr 'Отправить сопернику вызов с этими параметрами.' 'Send the duel invitation with these settings.') @($start)
Set-DuelHint (Tr 'Закрыть окно без запуска.' 'Close without sending an invitation.') @($cancel)

$choices = @{ Minefield = $false; SavedSize = 1; Updating = $false }
$refresh = {
    if ($choices.Updating) { return }
    $show = $mode.SelectedIndex -eq 2
    if ($show -ne $choices.Minefield) {
        $choices.Updating = $true
        try {
            if ($show) { $choices.SavedSize = $size.SelectedIndex; $size.SelectedIndex = 3 }
            else { $size.SelectedIndex = $choices.SavedSize }
            $choices.Minefield = $show
        }
        finally { $choices.Updating = $false }
    }
    $size.Enabled = -not $show
    $difficultyLabel.Visible = $difficulty.Visible = -not $show
    if ($difficulty.Parent -is [Windows.Forms.Panel]) { $difficulty.Parent.Visible = -not $show }
    $limit = Get-DuelFieldLimit $width $height (Get-DuelTargetRadius $sizes[$size.SelectedIndex])
    $targets.Maximum = [Math]::Max(1,$limit)
    $limitLabel.Text = (Tr "Лимит: $limit шт." "Limit: $limit")
    $mineLabel.Visible = $targets.Visible = $limitLabel.Visible = $show
    $start.Enabled = -not $show -or $limit -gt 0
}
$mode.Add_SelectedIndexChanged($refresh)
$size.Add_SelectedIndexChanged($refresh)
& $refresh
$targets.Value = [Math]::Min(12, $targets.Maximum)
try {
    if ($setup.ShowDialog() -ne 'OK') { return }
    $options = @{
        Mode = $modes[$mode.SelectedIndex]; Size = $sizes[$size.SelectedIndex]; Difficulty = $levels[$difficulty.SelectedIndex]
        Minutes = [int]$minutes.Value; Targets = [int]$targets.Value; Blitz = $blitz.Checked
    }
}
finally { $setup.Dispose() }
[Console]::WriteLine(($options | ConvertTo-Json -Compress))

