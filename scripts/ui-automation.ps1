param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [ValidateSet('list', 'invoke', 'setvalue', 'expand', 'selectitem', 'selectfirst')][string]$Action = 'list',
    [string]$Name,
    [int]$Index = 0,
    [string]$Value,
    # 按名称片段匹配列表项（列表项自动化名称是整条记录，无法精确匹配）
    [string]$StartsWith
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$windowCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $windowCondition)
if ($null -eq $window) { throw "No top-level window found for process $ProcessId." }

if ($Action -eq 'list') {
    $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $rows = foreach ($element in $all) {
        $elementName = $element.Current.Name
        if (-not [string]::IsNullOrWhiteSpace($elementName)) {
            [pscustomobject]@{
                Type = $element.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
                Name = $elementName
            }
        }
    }
    $rows | Select-Object -First 200 | Format-Table -AutoSize | Out-String -Width 220
    exit 0
}

if ($Action -eq 'selectitem') {
    if ([string]::IsNullOrWhiteSpace($Value)) { throw 'Value is required for the selectitem action.' }
    # 下拉项位于独立弹出层，需从桌面根节点按名称查找后选择。
    $itemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Value)
    $popupCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $popups = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $popupCondition)
    $selected = $false
    foreach ($popup in $popups) {
        $itemMatches = $popup.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCondition)
        foreach ($item in $itemMatches) {
            $selectionPattern = $null
            if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectionPattern)) {
                $selectionPattern.Select()
                $selected = $true
                Write-Output "Selected dropdown item '$Value'."
            }
        }
    }
    if (-not $selected) { throw "No dropdown item named '$Value' was found." }
    exit 0
}

if ($Action -eq 'selectfirst') {
    # 列表项的自动化名称是整条记录文本，用片段匹配更实际。
    if ([string]::IsNullOrWhiteSpace($StartsWith)) { throw 'StartsWith is required for selectfirst.' }
    $all = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $all) {
        if ($element.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { continue }
        if ($element.Current.Name -notlike "$StartsWith*") { continue }
        $selectionPattern = $null
        if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectionPattern)) {
            $selectionPattern.Select()
            Write-Output "Selected list item starting with '$StartsWith'."
            exit 0
        }
    }
    throw "No selectable list item starting with '$StartsWith' was found."
}

if ([string]::IsNullOrWhiteSpace($Name)) { throw 'Name is required for this action.' }

$nameCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
$matches = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)
if ($matches.Count -eq 0) { throw "No element named '$Name' was found." }
if ($Index -ge $matches.Count) { throw "Index $Index is out of range; found $($matches.Count) matches." }
$target = $matches.Item($Index)

if ($Action -eq 'setvalue') {
    if ($null -eq $Value) { throw 'Value is required for the setvalue action.' }
    $valuePattern = $null
    if (-not $target.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
        throw "Element '$Name' does not support ValuePattern."
    }
    $valuePattern.SetValue($Value)
    Write-Output "Set '$Name' to '$Value'."
    exit 0
}

if ($Action -eq 'expand') {
    $expandPattern = $null
    if (-not $target.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expandPattern)) {
        throw "Element '$Name' does not support ExpandCollapsePattern."
    }
    $expandPattern.Expand()
    Start-Sleep -Milliseconds 900
    # 下拉弹出层是独立顶层窗口，必须从桌面根节点搜索。
    $popupCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $popups = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $popupCondition)
    foreach ($popup in $popups) {
        $items = $popup.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($item in $items) {
            if (-not [string]::IsNullOrWhiteSpace($item.Current.Name)) {
                Write-Output "$($item.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '')|$($item.Current.Name)"
            }
        }
    }
    exit 0
}

$invoked = $false
$invokePattern = $null
if ($target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invokePattern)) {
    $invokePattern.Invoke()
    $invoked = $true
}
else {
    $selectionPattern = $null
    if ($target.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectionPattern)) {
        $selectionPattern.Select()
        $invoked = $true
    }
}

if (-not $invoked) { throw "Element '$Name' does not support Invoke or SelectionItem." }
Write-Output "Invoked '$Name' (index $Index, type $($target.Current.ControlType.ProgrammaticName))."
