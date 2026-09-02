#!/usr/bin/env bash
set -euo pipefail
rm -rf artifacts/test-results artifacts/coverage-report
dotnet tool restore
dotnet build DataPitcher.sln
# Exclude only AliasPattern's compiler-generated RegexGenerator.g.cs output; its regex-engine fallback paths are not constructible input behavior.
# Exclude only ASP.NET OpenAPI's generated XML-comment namespace and compiler-services interceptor; they are framework source-generator output, not handwritten API code.
dotnet test DataPitcher.sln --no-build --collect:"XPlat Code Coverage" --results-directory artifacts/test-results -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover 'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile=**/RegexGenerator.g.cs,**/OpenApiXmlCommentSupport.generated.cs'
dotnet tool run reportgenerator -reports:"artifacts/test-results/**/coverage.opencover.xml" -targetdir:"artifacts/coverage-report" -reporttypes:"JsonSummary"
summary="artifacts/coverage-report/Summary.json"
line=$(jq '.summary.linecoverage' "$summary")
branch=$(jq '.summary.branchcoverage' "$summary")
method=$(jq '.summary.methodcoverage' "$summary")
if [ "$method" = "null" ]; then
  covered=$(jq '.summary.coveredmethods' "$summary")
  total=$(jq '.summary.totalmethods' "$summary")
  method=$(awk -v c="$covered" -v t="$total" 'BEGIN { printf "%.2f", (t==0) ? 100 : (c/t*100) }')
fi
printf "Merged coverage: line=%s%% branch=%s%% method=%s%%\n" "$line" "$branch" "$method"
awk -v l="$line" -v b="$branch" -v m="$method" 'BEGIN { if (l!=100||b!=100||m!=100) exit 1 }'
