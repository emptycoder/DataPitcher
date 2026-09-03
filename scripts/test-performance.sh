#!/usr/bin/env bash
# Runs the opt-in performance tests (schema scan, plan sealing, transfer) against real SQL Server and PostgreSQL
# containers and appends the timings to artifacts/performance/results.jsonl.
#   DATAPITCHER_PERF_ROWS=100000            root rows to transfer (default 20000)
#   DATAPITCHER_PERF_BUDGET_SECONDS=60       fail a phase slower than this (default 120)
#   DATAPITCHER_PERF_RESULTS=path.jsonl      where to append results
set -euo pipefail
export DATAPITCHER_PERF=1
dotnet build tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj -nologo -v q
dotnet build tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj -nologo -v q
dotnet test tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj --no-build --filter "Category=Performance" --logger "console;verbosity=normal" "$@"
dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --no-build --filter "Category=Performance" --logger "console;verbosity=normal" "$@"
echo "Results: artifacts/performance/results.jsonl"
tail -n 4 artifacts/performance/results.jsonl 2>/dev/null || true
