#!/usr/bin/env bash
set -euo pipefail
rm -rf artifacts/unit-test-results
dotnet build tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj
dotnet build tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj
dotnet test tests/DataPitcher.UnitTests/DataPitcher.UnitTests.csproj --no-build "$@" --collect:"XPlat Code Coverage" --results-directory artifacts/unit-test-results -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
dotnet test tests/DataPitcher.ArchitectureTests/DataPitcher.ArchitectureTests.csproj --no-build
report="$(find artifacts/unit-test-results -name coverage.opencover.xml -print -quit)"
if [ -n "$report" ]; then
  read -r sequence branch visited total < <(xmllint --xpath 'concat(/CoverageSession/Summary/@sequenceCoverage," ",/CoverageSession/Summary/@branchCoverage," ",/CoverageSession/Summary/@visitedMethods," ",/CoverageSession/Summary/@numMethods)' "$report")
  awk -v s="$sequence" -v b="$branch" -v v="$visited" -v t="$total" 'BEGIN { m=t==0?100:v/t*100; printf "Unit lane coverage (informational, not gated): line=%s%% branch=%s%% method=%.2f%%\n",s,b,m }'
fi
