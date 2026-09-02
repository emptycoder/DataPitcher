$ErrorActionPreference = 'Stop'
Remove-Item artifacts/test-results, artifacts/coverage-report -Recurse -Force -ErrorAction SilentlyContinue
dotnet tool restore
dotnet build DataPitcher.sln
dotnet test DataPitcher.sln --no-build --collect:'XPlat Code Coverage' --results-directory artifacts/test-results -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
dotnet tool run reportgenerator -reports:'artifacts/test-results/**/coverage.opencover.xml' -targetdir:'artifacts/coverage-report' -reporttypes:'JsonSummary'
$summary = (Get-Content artifacts/coverage-report/Summary.json | ConvertFrom-Json).summary
$line = $summary.linecoverage
$branch = $summary.branchcoverage
$method = $summary.methodcoverage
if ($null -eq $method) {
  $covered = $summary.coveredmethods
  $total = $summary.totalmethods
  $method = if ($total -eq 0) { 100 } else { $covered / $total * 100 }
}
Write-Output "Merged coverage: line=$line% branch=$branch% method=$method%"
$failures = @()
if ($line -ne 100) { $failures += "LineCoverage is $line%, expected 100%." }
if ($branch -ne 100) { $failures += "BranchCoverage is $branch%, expected 100%." }
if ($method -ne 100) { $failures += "MethodCoverage is $method%, expected 100%." }
if ($failures.Count -gt 0) { throw ($failures -join "`n") }
