#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="$ROOT_DIR/uml"
PNG_DPI="${UML_PNG_DPI:-500}"
ENABLE_PNG=0
export PLANTUML_LIMIT_SIZE="${PLANTUML_LIMIT_SIZE:-8192}"

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
  generated_include="$(mktemp)"
  cp "$INCLUDE_PUML" "$generated_include"

  tmp_file="$(mktemp)"
  cat <<'OVERVIEW' > "$tmp_file"
@startuml
'
' Simplified overview.
' The detailed all-in-one diagram is emitted from this same file as include-detailed.
!theme mars
left to right direction
skinparam linetype ortho
skinparam shadowing false
skinparam roundCorner 6
skinparam ranksep 45
skinparam nodesep 45
skinparam padding 5
skinparam WrapWidth 220
skinparam DefaultFontSize 12
skinparam RectangleFontSize 12
skinparam ArrowFontSize 10

title WikiGraph UML Overview

rectangle "<b>WikiGraph.Client</b>\nProgram\nApiClient" as Client

rectangle "<b>API Controllers</b>\nSessionController\nHealthController" as Controllers

rectangle "<b>Application Services</b>\nWikiSessionService\nGeminiService\nGeminiReply" as Services

rectangle "<b>Wikipedia Infrastructure</b>\nWikipediaService\nWikiApiSection\nWikiSearchCandidate\nWikiSearchResults\nWikiSearchResolution" as WikiInfra

rectangle "<b>Persistence</b>\nSessionMemoryDb\nSqliteSessionRepository\nSqliteVectorStore\nSqliteConnectionFactory\nISqliteConnectionFactory" as Persistence

rectangle "<b>Application Models</b>\nWikiArticle\nWikiSection\nWikiMatch\nWikiTopicReference\nWikiLookupPlan\nTextTools" as Models

rectangle "<b>Contracts / DTOs</b>\nSessionSummary\nSessionDetailDto\nMessageDto\nCitationDto\nGraphDto\nGraphNodeDto\nGraphEdgeDto\nCreateSessionRequest\nAddWikiArticleRequest" as Contracts

rectangle "<b>Configuration</b>\nGeminiOptions\nServiceCollectionExtensions" as Config

rectangle "<b>Tests</b>\nApiEndpointTests\nGeminiServiceTests\nWikipediaCitationTests\nFakeChatCompletionService\nQueuedWikipediaHandler\nTempSqliteConnectionFactory" as Tests

Client --> Controllers
Client ..> Contracts

Controllers --> Services
Controllers ..> Contracts

Services --> WikiInfra
Services --> Persistence
Services --> Config
Services ..> Models
Services ..> Contracts

WikiInfra ..> Models
Persistence ..> Models
Persistence ..> Contracts
Persistence --> Config

Tests ..> Controllers
Tests ..> Services
Tests ..> Persistence

@enduml

@startuml include-detailed
'
' Detailed all-in-one class graph. This keeps generated class members and
' associations for inspection while using the same theme as every UML render.
!theme mars
scale max 1920*1920
left to right direction
skinparam linetype ortho
skinparam shadowing false
skinparam roundCorner 6
skinparam ranksep 100
skinparam nodesep 100
skinparam padding 6
skinparam WrapWidth 520
skinparam DefaultFontSize 12
skinparam ClassFontSize 13
skinparam ClassAttributeFontSize 11
skinparam ClassStereotypeFontSize 10
skinparam ArrowFontSize 10
skinparam classAttributeIconSize 0
OVERVIEW
  sed '1d;$d' "$generated_include" >> "$tmp_file"
  cat <<'OVERVIEW' >> "$tmp_file"
@enduml
OVERVIEW
  mv "$tmp_file" "$INCLUDE_PUML"
  rm -f "$generated_include"
fi

while IFS= read -r -d '' puml_file; do
  if [[ "$puml_file" == "$INCLUDE_PUML" ]]; then
    continue
  fi

  tmp_file="$(mktemp)"
  {
    head -n 1 "$puml_file"
    cat <<'LAYOUT'
'
' Detailed per-file layout for horizontal subdiagram renders.
!theme mars
scale max 1024*768
left to right direction
skinparam linetype ortho
skinparam shadowing false
skinparam roundCorner 6
skinparam ranksep 80
skinparam nodesep 50
skinparam padding 6
skinparam WrapWidth 520
skinparam DefaultFontSize 12
skinparam ClassFontSize 13
skinparam ClassAttributeFontSize 11
skinparam ClassStereotypeFontSize 10
skinparam ArrowFontSize 10
skinparam classAttributeIconSize 0
hide empty members
LAYOUT
    tail -n +2 "$puml_file"
  } > "$tmp_file"
  mv "$tmp_file" "$puml_file"
done < <(find "$OUT_DIR" -type f -name "*.puml" -print0)

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
