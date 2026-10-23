#!/bin/sh
# Regenerates the Clafer parser from the official grammar. Pipeline:
#
#   official/clafer.cf --bnfc--> bnfc/*.g4 --transform.py--> ClaferLexer.g4, ClaferParser.g4 --antlr--> ../Generated/*.cs
#
# Environment:
#   ANTLR_JAR  required: ANTLR 4.13.1 tool jar (the version of the Antlr4.Runtime.Standard package referenced)
#   JAVA       optional: java executable (default: java on PATH; Java 17+)
#   BNFC_BIN   optional: BNFC 2.9.6.3 executable. If set, bnfc/ is regenerated from official/clafer.cf first;
#              if not, the committed bnfc/ output is used. BNFC is a build-time tool only and is not vendored.
#   python3 is needed for transform.py.
set -e
: "${ANTLR_JAR:?set ANTLR_JAR to antlr4-4.13.1-complete.jar}"
JAVA="${JAVA:-java}"
cd "$(dirname "$0")"

if [ -n "$BNFC_BIN" ]; then
  work="$(mktemp -d)"
  # --force: BNFC's Java backend rejects clafer.cf over Java class-name clashes (Module, Clafer, ...). We only use
  # the .g4 files, not the Java AST classes, so the clashes do not matter.
  "$BNFC_BIN" --java-antlr --force -o "$work" official/clafer.cf > "$work/bnfc.log" 2>&1
  mkdir -p bnfc
  cp "$work/clafer/ClaferLexer.g4" "$work/clafer/ClaferParser.g4" bnfc/
  echo "BNFC output (including the Java classes we do not use) and its log are in $work"
fi

python3 transform.py bnfc .

mkdir -p ../Generated
"$JAVA" -jar "$ANTLR_JAR" -Dlanguage=CSharp -visitor -no-listener \
  -package CapabilityResolver.Formats.Clafer.Grammar -lib . -o ../Generated \
  ClaferCSharpLexer.g4 ClaferCSharpParser.g4
