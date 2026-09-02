#!/usr/bin/env bash
set -euo pipefail
rm -rf artifacts/test-results
dotnet build DataPitcher.sln
dotnet test DataPitcher.sln --no-build --collect:"XPlat Code Coverage" --results-directory artifacts/test-results -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
seq_num=0; seq_vis=0; br_num=0; br_vis=0; m_num=0; m_vis=0
while IFS= read -r report; do
  read -r sn sv bn bv mn mv < <(xmllint --xpath 'concat(/CoverageSession/Summary/@numSequencePoints," ",/CoverageSession/Summary/@visitedSequencePoints," ",/CoverageSession/Summary/@numBranchPoints," ",/CoverageSession/Summary/@visitedBranchPoints," ",/CoverageSession/Summary/@numMethods," ",/CoverageSession/Summary/@visitedMethods)' "$report")
  seq_num=$((seq_num + sn)); seq_vis=$((seq_vis + sv))
  br_num=$((br_num + bn)); br_vis=$((br_vis + bv))
  m_num=$((m_num + mn)); m_vis=$((m_vis + mv))
done < <(find artifacts/test-results -name coverage.opencover.xml)
awk -v sn="$seq_num" -v sv="$seq_vis" -v bn="$br_num" -v bv="$br_vis" -v mn="$m_num" -v mv="$m_vis" 'BEGIN {
  line = (sn==0) ? 100 : (sv/sn*100)
  branch = (bn==0) ? 100 : (bv/bn*100)
  method = (mn==0) ? 100 : (mv/mn*100)
  printf "Aggregate coverage: line=%.2f%% branch=%.2f%% method=%.2f%%\n", line, branch, method
  if (line!=100||branch!=100||method!=100) exit 1
}'
