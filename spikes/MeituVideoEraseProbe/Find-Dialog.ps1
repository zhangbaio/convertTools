$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::NameProperty, "打开视频")
$dlg = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($null -eq $dlg) {
  Write-Host "NOT_IN_CHILDREN"
  $classCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ClassNameProperty, "#32770")
  $all = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $classCond)
  Write-Host "dialog-class-count=$($all.Count)"
  foreach ($d in $all) { Write-Host ("classwin name='{0}' id='{1}'" -f $d.Current.Name, $d.Current.AutomationId) }
} else {
  Write-Host ("FOUND name='{0}' class='{1}' hwnd={2}" -f $dlg.Current.Name, $dlg.Current.ClassName, $dlg.Current.NativeWindowHandle)
  $editCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Edit)
  $edits = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
  foreach ($ed in $edits) {
    $val = ""
    try { $val = $ed.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
    Write-Host ("edit id='{0}' name='{1}' value='{2}'" -f $ed.Current.AutomationId, $ed.Current.Name, $val)
  }
  $btnCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
  foreach ($b in $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
    Write-Host ("button name='{0}' id='{1}'" -f $b.Current.Name, $b.Current.AutomationId)
  }
}
