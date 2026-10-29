#!/usr/bin/env sh
set -eu
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
dotnet build "$script_dir/Source/DoWorkRightNow.csproj" -c Release "$@"
