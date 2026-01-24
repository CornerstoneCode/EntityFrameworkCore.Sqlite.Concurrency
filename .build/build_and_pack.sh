#!/bin/bash
set -e

# Configuration
PROJECT_PATH="EntityFrameworkCore.Sqlite.Concurrency/EFCore.Sqlite.Concurrency.csproj"
CONFIGURATION="Release"
OUTPUT_DIR="EntityFrameworkCore.Sqlite.Concurrency/bin/Release"

echo "Building project in $CONFIGURATION mode..."
dotnet build "$PROJECT_PATH" -c "$CONFIGURATION"

echo "Packing project..."
dotnet pack "$PROJECT_PATH" -c "$CONFIGURATION" --no-build

echo "Build and pack completed successfully."
echo "Packages are located in $OUTPUT_DIR"
