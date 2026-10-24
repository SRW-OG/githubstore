param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [int]$TimeoutSeconds = 20
)

# This smoke check operates only the already-open test instance. It neither saves
# rules nor installs/downloads packages. Start that instance with isolated data.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-Element([string]$AutomationId, [bool]$RequireEnabled = $true) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $processCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $processCondition)
        if ($null -ne $window) {
            $condition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
            $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $element -and (-not $RequireEnabled -or $element.Current.IsEnabled)) { return $element }
        }
        Start-Sleep -Milliseconds 150
    } while ((Get-Date) -lt $deadline)
    throw "Enabled automation element not found: $AutomationId"
}

function Invoke-Element($Element) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
        return
    }
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        $pattern.Select()
        return
    }
    throw "Element cannot be invoked or selected: $($Element.Current.AutomationId)"
}

Invoke-Element (Wait-Element 'SettingsNavigationItem')
Invoke-Element (Wait-Element 'InspectLibraryButton')
Invoke-Element (Wait-Element 'LoadLibraryEditorButton')
$expander = Wait-Element 'LibraryEditorExpander'
$expand = $null
if ($expander.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) {
    $expand.Expand()
} else { throw 'Editor expander does not expose ExpandCollapsePattern.' }
$repository = Wait-Element 'LibraryEditorRepository'
if ([string]::IsNullOrWhiteSpace($repository.Current.Name)) { throw 'Repository selector has no accessible name.' }
$beforePreview = Wait-Element -AutomationId 'LibraryEditorSave' -RequireEnabled $false
if ($beforePreview.Current.IsEnabled) { throw 'Save must be disabled before preview.' }
Invoke-Element (Wait-Element 'LibraryEditorPreview')
$save = Wait-Element 'LibraryEditorSave'
if ($save.Current.Name -match '^LibraryEditor') { throw 'Editor renders an untranslated localization key.' }
[pscustomobject]@{
    ProcessId = $ProcessId
    Passed = $true
    Checked = @('Settings navigation', 'Offline inspection', 'Load existing entries', 'Local policy preview', 'Save enabled only after preview')
    NotChecked = @('Network policy save', 'File picker/export UI', 'Installation', 'Upgrade', 'Rollback')
} | ConvertTo-Json -Depth 3
