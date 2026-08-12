#!/usr/bin/env bash
set -e

(
  cd ../verdant
  dotnet build
  dotnet test
)

dotnet build
dotnet test
