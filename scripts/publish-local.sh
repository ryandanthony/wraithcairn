#!/usr/bin/env bash
# Publish a self-contained linux-x64 build of spike-royalterminal to
# ~/temp/spike-royalterminal.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

dotnet publish -c Release -r linux-x64 --self-contained true -o ~/temp/spike-royalterminal

echo "Published to ~/temp/spike-royalterminal"
