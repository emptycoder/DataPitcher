#!/usr/bin/env bash
set -euo pipefail
dotnet build tests/DataPitcher.PostgreSql.IntegrationTests/DataPitcher.PostgreSql.IntegrationTests.csproj
dotnet test tests/DataPitcher.PostgreSql.IntegrationTests/DataPitcher.PostgreSql.IntegrationTests.csproj --no-build "$@"
