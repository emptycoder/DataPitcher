#!/usr/bin/env bash
set -euo pipefail

npm --prefix web ci
npm --prefix web run typecheck
npm --prefix web run test:coverage
