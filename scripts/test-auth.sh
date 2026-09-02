#!/usr/bin/env bash
set -euo pipefail
./scripts/test-unit.sh
dotnet test tests/DataPitcher.Auth.IntegrationTests/DataPitcher.Auth.IntegrationTests.csproj "$@"
rm -rf artifacts/auth-production-publish
dotnet publish src/DataPitcher.Auth.Hosting/DataPitcher.Auth.Hosting.csproj --configuration Release --output artifacts/auth-production-publish
test ! -e artifacts/auth-production-publish/DataPitcher.Auth.Development.dll
