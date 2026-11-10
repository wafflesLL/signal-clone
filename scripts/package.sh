#!/usr/bin/env bash
# Build the submission zips: artifacts.zip (code + tests + docs) and transcripts.zip.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p dist
rm -f dist/artifacts.zip dist/transcripts.zip
git archive --format=zip -o dist/artifacts.zip HEAD -- src tests docs scripts README.md pyproject.toml
(cd transcripts && zip -qr ../dist/transcripts.zip . -x '.gitkeep')
echo "wrote dist/artifacts.zip and dist/transcripts.zip"
