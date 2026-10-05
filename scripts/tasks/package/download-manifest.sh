#!/usr/bin/env bash
set -euo pipefail
TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$TASK_DIR/../../lib/context.sh"
source "$TASK_DIR/../../lib/guards.sh"
require_command python3
require_file "$RENDERER_PACKAGE_ZIP"
python3 - "$RENDERER_PACKAGE_ZIP" "$RENDERER_PROJECT_ROOT/Info.json" "$RENDERER_RELEASE_ROOT/TUFReplay-Renderer.download.json" <<'PY'
import hashlib, json, pathlib, sys
package, info, destination = map(pathlib.Path, sys.argv[1:])
metadata = json.loads(info.read_text())
manifest = {
    'schemaVersion': 1,
    'modId': metadata['Id'],
    'version': metadata['Version'],
    'packageAsset': package.name,
    'bytes': package.stat().st_size,
    'sha256': hashlib.sha256(package.read_bytes()).hexdigest(),
}
destination.write_text(json.dumps(manifest, indent=2) + '\n')
print('Download manifest:', destination)
PY
