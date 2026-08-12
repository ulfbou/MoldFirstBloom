#!/usr/bin/env bash

set -e

pushd ../verdant
dotnet build
dotnet test
popd

dotnet build
dotnet test
