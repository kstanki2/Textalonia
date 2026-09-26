param(
    [Parameter(Mandatory)][string]$Results,
    [switch]$Enforce
)
$ErrorActionPreference = 'Stop'
$status = Get-Content -LiteralPath (Join-Path $Results 'status.json') -Raw | ConvertFrom-Json
if ($status.status -ne 'complete') { throw 'Only a complete benchmark run can be assessed.' }
$rows = Get-Content -LiteralPath (Join-Path $Results 'results.json') -Raw | ConvertFrom-Json
if ($rows.Count -ne $status.cases) { throw 'Result count does not match the completed run.' }
$checks = @(
    foreach ($row in $rows) {
        if ($null -ne $row.BudgetMs) {
            [ordered]@{ workload = $row.Workload; operation = $row.Operation; metric = 'p95-ms'; actual = $row.P95Ms; budget = $row.BudgetMs; passed = $row.P95Ms -le $row.BudgetMs }
        }
        $allocationBudget = $null
        if ($row.Operation -eq 'first-viewport') { $allocationBudget = 64MB }
        elseif ($row.Operation -match '^(type-|delete-|caret-)' -or $row.Operation -eq 'scroll') { $allocationBudget = 4MB + 4L * $row.Utf16Length }
        if ($null -ne $allocationBudget) {
            $bytes = @($row.Samples.AllocatedBytes | Sort-Object)
            $p95 = $bytes[[int][Math]::Ceiling(.95 * $bytes.Count) - 1]
            [ordered]@{ workload = $row.Workload; operation = $row.Operation; metric = 'p95-managed-allocated-bytes'; actual = $p95; budget = $allocationBudget; passed = $p95 -le $allocationBudget }
        }
        if ($null -ne $row.MedianRetainedUndoBytes) {
            [ordered]@{ workload = $row.Workload; operation = $row.Operation; metric = 'median-retained-undo-bytes'; actual = $row.MedianRetainedUndoBytes; budget = 64MB; passed = $row.MedianRetainedUndoBytes -le 64MB }
        }
    }
)
$misses = @($checks | Where-Object { -not $_.passed })
[ordered]@{ policy = 'docs/PERFORMANCE.md Phase 2 budgets'; checks = $checks; failedChecks = $misses.Count } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Results 'budgets.json') -Encoding utf8
Write-Output ("Budget checks: {0} passed, {1} exceeded. See budgets.json." -f ($checks.Count - $misses.Count), $misses.Count)
if ($Enforce -and $misses.Count -gt 0) { throw 'Phase 2 budgets exceeded.' }
