#!/usr/bin/env bash
set -euo pipefail
dotnet build tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj
dotnet test tests/DataPitcher.Providers.PostgreSql.IntegrationTests/DataPitcher.Providers.PostgreSql.IntegrationTests.csproj --no-build "$@"
