$ErrorActionPreference = 'Stop'
Remove-Item artifacts/test-results -Recurse -Force -ErrorAction SilentlyContinue
dotnet build DataPitcher.sln
dotnet test DataPitcher.sln --no-build
dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --no-build --collect:'XPlat Code Coverage' --results-directory artifacts/test-results -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
$report = (Get-ChildItem artifacts/test-results -Filter coverage.opencover.xml -Recurse | Select-Object -First 1).FullName
if (-not $report) { throw 'Coverlet did not write coverage.opencover.xml.' }
[xml]$coverage = Get-Content $report
$summary = $coverage.CoverageSession.Summary
$sequence = [double]$summary.sequenceCoverage
$branch = [double]$summary.branchCoverage
$visitedMethods = [double]$summary.visitedMethods
$totalMethods = [double]$summary.numMethods
$method = if ($totalMethods -eq 0) { 100 } else { $visitedMethods / $totalMethods * 100 }
$failures = @()
if ($sequence -ne 100) { $failures += "SequenceCoverage is $sequence%, expected 100%." }
if ($branch -ne 100) { $failures += "BranchCoverage is $branch%, expected 100%." }
if ($method -ne 100) { $failures += "MethodCoverage is $method% ($visitedMethods/$totalMethods methods), expected 100%." }
if ($failures.Count -gt 0) { throw ($failures -join "`n") }
