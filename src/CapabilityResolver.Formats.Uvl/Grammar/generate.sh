#!/bin/sh
# Regenerates ../Generated from the grammar files. Needs Java 17+ and the ANTLR 4.13.1 tool jar
# (the version of the Antlr4.Runtime.Standard package this project references):
#   ANTLR_JAR=/path/to/antlr4-4.13.1-complete.jar ./generate.sh
set -e
: "${ANTLR_JAR:?set ANTLR_JAR to antlr4-4.13.1-complete.jar}"
cd "$(dirname "$0")"
rm -f ../Generated/*.cs ../Generated/*.interp ../Generated/*.tokens
java -jar "$ANTLR_JAR" -Dlanguage=CSharp -visitor -no-listener \
  -package CapabilityResolver.Formats.Uvl.Grammar -lib . -o ../Generated \
  UVLCSharpLexer.g4 UVLCSharpParser.g4
