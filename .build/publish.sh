#!/bin/bash
set -e

# Configuration
PACKAGE_DIR="EntityFrameworkCore.Sqlite.Concurrency/bin/Release"
SOURCE="https://api.nuget.org/v3/index.json"

# Check if NUGET_API_KEY is set
if [ -z "$NUGET_API_KEY" ]; then
    echo "Error: NUGET_API_KEY environment variable is not set."
    exit 1
fi

# Find the latest .nupkg file
PACKAGE_FILE=$(ls $PACKAGE_DIR/*.nupkg | grep -v ".symbols.nupkg" | grep -v ".snupkg" | head -n 1)

if [ -z "$PACKAGE_FILE" ]; then
    echo "Error: No .nupkg file found in $PACKAGE_DIR"
    exit 1
fi

echo "Publishing $PACKAGE_FILE to NuGet..."
dotnet nuget push "$PACKAGE_FILE" --api-key "$NUGET_API_KEY" --source "$SOURCE" --skip-duplicate

echo "Publishing completed."
