#!/usr/bin/env bash
# Publish a self-contained linux-x64 build of wraithcairn to
# ~/temp/wraithcairn.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

dotnet publish -c Release -r linux-x64 --self-contained true -o ~/temp/wraithcairn

echo "Published to ~/temp/wraithcairn"
