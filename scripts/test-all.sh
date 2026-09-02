#!/usr/bin/env bash
set -euo pipefail
rm -rf artifacts/test-results
dotnet build DataPitcher.sln
dotnet test DataPitcher.sln --no-build
dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --no-build --collect:"XPlat Code Coverage" --results-directory artifacts/test-results -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
report="$(find artifacts/test-results -name coverage.opencover.xml -print -quit)"
test -n "$report"
sequence="$(xmllint --xpath "string(/CoverageSession/Summary/@sequenceCoverage)" "$report")"
branch="$(xmllint --xpath "string(/CoverageSession/Summary/@branchCoverage)" "$report")"
visited_methods="$(xmllint --xpath "string(/CoverageSession/Summary/@visitedMethods)" "$report")"
total_methods="$(xmllint --xpath "string(/CoverageSession/Summary/@numMethods)" "$report")"
awk -v s="$sequence" -v b="$branch" -v vm="$visited_methods" -v nm="$total_methods" '
BEGIN {
  method = (nm == 0) ? 100 : (vm / nm * 100)
  ok = 1
  if (s + 0 != 100) { printf "SequenceCoverage is %s%%, expected 100%%\n", s; ok = 0 }
  if (b + 0 != 100) { printf "BranchCoverage is %s%%, expected 100%%\n", b; ok = 0 }
  if (method != 100) { printf "MethodCoverage is %.2f%% (%s/%s methods), expected 100%%\n", method, vm, nm; ok = 0 }
  exit (ok ? 0 : 1)
}'
