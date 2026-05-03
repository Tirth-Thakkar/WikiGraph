#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="$ROOT_DIR/uml"
PNG_DPI="${UML_PNG_DPI:-250}"
ENABLE_PNG=0

usage() {
  echo "Usage: $0 [output_dir] [--png]"
  echo
  echo "Arguments:"
  echo "  output_dir   Optional output directory for generated UML files."
  echo "  --png        Also render PNG files (disabled by default)."
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --png)
      ENABLE_PNG=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    -*)
      echo "Unknown option: $1"
      usage
      exit 1
      ;;
    *)
      if [[ "$OUT_DIR" != "$ROOT_DIR/uml" ]]; then
        echo "Only one output directory may be provided."
        usage
        exit 1
      fi
      OUT_DIR="$1"
      shift
      ;;
  esac
done

cd "$ROOT_DIR"

# Ensure local dotnet tool dependencies are present.
dotnet tool restore >/dev/null

if ! command -v plantuml >/dev/null 2>&1; then
  echo "Missing dependency: plantuml"
  echo "Install it with: sudo apt install -y plantuml"
  exit 1
fi

if ! command -v dot >/dev/null 2>&1; then
  echo "Missing dependency: graphviz (dot)"
  echo "Install it with: sudo apt install -y graphviz"
  exit 1
fi

# PlantUmlClassDiagramGenerator targets .NET 8; this flag allows execution on .NET 10.
dotnet tool run --allow-roll-forward puml-gen \
  "$ROOT_DIR" \
  "$OUT_DIR" \
  -dir \
  -allInOne \
  -addPackageTags \
  -createAssociation \
  -excludePaths .git,.vscode,**/bin,**/obj,**/openapi,**/wwwroot,node_modules

INCLUDE_PUML="$OUT_DIR/include.puml"
if [[ -f "$INCLUDE_PUML" ]]; then
  tmp_file="$(mktemp)"
  {
    head -n 1 "$INCLUDE_PUML"
    cat <<'LAYOUT'
'
' Use a built-in PlantUML theme preset. 
!theme mars
left to right direction
skinparam linetype ortho
skinparam ranksep 140
skinparam nodesep 140
skinparam padding 10
skinparam classAttributeIconSize 0
LAYOUT
    tail -n +2 "$INCLUDE_PUML"
  } > "$tmp_file"
  mv "$tmp_file" "$INCLUDE_PUML"
fi

mapfile -d '' puml_files < <(find "$OUT_DIR" -type f -name "*.puml" -print0 | sort -z)

if [[ ${#puml_files[@]} -eq 0 ]]; then
  echo "No .puml files found in: $OUT_DIR"
  exit 1
fi

plantuml -tsvg "${puml_files[@]}"
if [[ "$ENABLE_PNG" -eq 1 ]]; then
  plantuml -tpng -Sdpi="$PNG_DPI" "${puml_files[@]}"
fi

echo "UML generated at: $OUT_DIR"
echo "Main entry file: $OUT_DIR/include.puml"
echo "SVG renders generated for all diagrams (*.svg)"
if [[ "$ENABLE_PNG" -eq 1 ]]; then
  echo "PNG renders generated for all diagrams (*.png) at ${PNG_DPI} DPI"
else
  echo "PNG renders disabled by default. Pass --png to enable PNG output."
fi