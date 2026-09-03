#!/usr/bin/env bash
set -euo pipefail
dotnet tool restore
dotnet csharpier format .
