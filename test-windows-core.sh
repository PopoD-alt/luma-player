#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
LUMA_ROOT="$PWD"
if [[ -x "$LUMA_ROOT/.cache/dotnet/dotnet" ]]; then
  LUMA_DOTNET="$LUMA_ROOT/.cache/dotnet/dotnet"
  export DOTNET_ROOT="$LUMA_ROOT/.cache/dotnet"
else
  LUMA_DOTNET="$(command -v dotnet || true)"
fi
if [[ -z "$LUMA_DOTNET" ]]; then
  echo "Install the .NET SDK pinned in Windows/global.json first." >&2
  exit 1
fi
export DOTNET_CLI_HOME="$LUMA_ROOT/.cache/dotnet-home"
export NUGET_PACKAGES="$LUMA_ROOT/.cache/nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cd Windows
"$LUMA_DOTNET" run --project Luma.Core.Tests -c Release -- "$LUMA_ROOT/Tests/Fixtures"
