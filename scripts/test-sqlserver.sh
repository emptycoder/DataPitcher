#!/usr/bin/env bash
set -euo pipefail
dotnet build tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj
dotnet test tests/DataPitcher.Providers.SqlServer.IntegrationTests/DataPitcher.Providers.SqlServer.IntegrationTests.csproj --no-build "$@"
