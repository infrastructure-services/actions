#!/usr/bin/env bash
set -euo pipefail
exec bash "$GITHUB_ACTION_PATH/../schema-capture/run-schema-capture.sh" --new-ef-bootstrap-readonly
