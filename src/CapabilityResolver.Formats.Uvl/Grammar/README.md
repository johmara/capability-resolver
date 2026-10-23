# UVL grammar

- `UVLLexer.g4`, `UVLParser.g4`: the official UVL grammar, unmodified, from
  https://github.com/Universal-Variability-Language/uvl-parser (`uvl/`).
  Licensed under the GNU LGPL v3, see `LICENSE-uvl-parser.txt`.
- `UVLCSharpLexer.g4`, `UVLCSharpParser.g4`: the C# variant, following the official Java and Python variants.
  The lexer adds the indentation handling that the base grammar leaves to each language.
- `../Generated/`: the C# generated from these by `generate.sh`. Committed, so building needs no Java.

TODO: have Centiro legal confirm use of the LGPL grammar and generated code before this is used beyond the demo.
