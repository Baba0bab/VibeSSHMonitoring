# SPDX-License-Identifier: GPL-3.0-or-later
# Pilote l'application par UI Automation (sans souris ni clavier).
#   ./tools/ui-test/Invoke-UiElement.ps1 -Name "Paramètres"                  # clic / sélection par nom
#   ./tools/ui-test/Invoke-UiElement.ps1 -Name "Open SSH" -Index 1           # 2e élément de ce nom
#   ./tools/ui-test/Invoke-UiElement.ps1 -Name ([string][char]0xE712)        # bouton à glyphe (ici « ⋯ »)
#   ./tools/ui-test/Invoke-UiElement.ps1 -Window "Ajouter" -List             # liste les éléments nommés
#   ./tools/ui-test/Invoke-UiElement.ps1 -AutomationId HostBox -Value "1.2.3.4"   # remplit un champ (x:Name)
param(
    [string] $Name,
    [string] $Window = "Vibe SSH Monitoring",
    [int] $Index = 0,
    [switch] $List,
    [string] $AutomationId,
    [string] $Value,
    [string] $ProcessName = "VibeSSHMonitoring"
)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]::Descendants
$proc = Get-Process $ProcessName -ErrorAction Stop | Select-Object -First 1
$pidCondition = New-Object System.Windows.Automation.PropertyCondition ($A::ProcessIdProperty), $proc.Id

# Remplissage d'un champ par son x:Name (TextBox, PasswordBox), toutes fenêtres confondues
if ($AutomationId) {
    $element = $A::RootElement.FindAll($scope, $pidCondition) | Where-Object { $_.Current.AutomationId -eq $AutomationId } | Select-Object -First 1
    if (-not $element) { Write-Error "Champ introuvable : $AutomationId"; exit 1 }
    $pattern = $null
    if (-not $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        Write-Error "$AutomationId n'accepte pas de valeur"; exit 1
    }
    $pattern.SetValue($Value)
    "set $AutomationId"
    exit 0
}

# Les fenêtres possédées (dialogues) sont des descendants : recherche dans tout l'arbre
$windowCondition = New-Object System.Windows.Automation.AndCondition $pidCondition,
    (New-Object System.Windows.Automation.PropertyCondition ($A::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Window))
$windowElement = $A::RootElement.FindAll($scope, $windowCondition) | Where-Object { $_.Current.Name -like "*$Window*" } | Select-Object -Last 1
if (-not $windowElement) { Write-Error "Fenêtre introuvable : $Window"; exit 1 }

if ($List) {
    $windowElement.FindAll($scope, [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.Name } |
        ForEach-Object { "{0} [{1}]" -f $_.Current.Name, $_.Current.ControlType.ProgrammaticName }
    exit 0
}

$hits = $windowElement.FindAll($scope, (New-Object System.Windows.Automation.PropertyCondition ($A::NameProperty), $Name))
if ($hits.Count -le $Index) { Write-Error "Élément introuvable : $Name"; exit 1 }

# Un texte dans un bouton : on remonte jusqu'à l'élément cliquable
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$target = $hits[$Index]
for ($i = 0; $i -lt 4 -and $target; $i++) {
    $patterns = $target.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName }
    if ($patterns -match "InvokePattern|SelectionItemPattern|TogglePattern") { break }
    $target = $walker.GetParent($target)
}

$pattern = $null
if ($target.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke(); "invoke $Name" }
elseif ($target.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); "select $Name" }
elseif ($target.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle(); "toggle $Name" }
else { Write-Error "Aucune action possible sur : $Name"; exit 1 }
