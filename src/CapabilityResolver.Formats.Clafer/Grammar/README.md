# Clafer grammar

The parser is generated mechanically from the **official Clafer grammar**, the LBNF file
[`official/clafer.cf`](https://github.com/gsdlab/clafer/blob/master/src/clafer.cf) (gsdlab/clafer, last changed in
commit `23c7b41`, MIT licensed, see `LICENSE-clafer.txt`; the vendored copy is identical to `master`). No rule is
translated by hand.

```
official/clafer.cf --BNFC--> bnfc/*.g4 --transform.py--> ClaferLexer.g4, ClaferParser.g4 --ANTLR--> ../Generated/*.cs
```

1. **BNFC 2.9.6.3** (`bnfc --java-antlr --force`) turns `clafer.cf` into ANTLR 4 grammars for its Java backend. They
   are kept as received in `bnfc/`. `--force` is needed because BNFC's Java backend rejects `clafer.cf` over Java
   class-name clashes; only the `.g4` files are used, not the Java classes. BNFC is GPL-licensed but is only a
   build-time tool: it is **not vendored**, and neither building nor running this project needs it.
2. **`transform.py`** (documented at its top) makes those grammars target-neutral:
   it strips the Java `returns [...]` clauses and actions, keeps each LBNF constructor name as the ANTLR alternative
   label (`# EAnd`), gives pass-through alternatives a `Coerce<Rule>` label, renames the generated
   `Surrogate_id_SYMB_<n>` tokens after their literals (`K_abstract`, `LBRACK`, ...), skips comment tokens in the
   lexer, and moves the empty alternative of each optional part to the end of its rule so ANTLR resolves ambiguities
   by consuming tokens, as upstream's Happy parser does. Labels that ANTLR rejects because they equal a rule's
   context name (`Module`, `Clafer`, `Constraint`, `Assertion`, `Transition`, `NCard`, `TransGuard`, `Decl`,
   `VarBinding`) get the suffix `Alt` (`ClaferAlt`, ...).
3. **ANTLR 4.13.1** generates the C# in `../Generated/` from `ClaferLexer.g4`, `ClaferParser.g4` and the two wrapper
   grammars below. The generated code is committed, so building needs neither Java nor BNFC.

The only hand-written grammar files are the wrappers:

- `ClaferCSharpLexer.g4`: imports the generated lexer and ports the token-level layout resolver of upstream
  `LayoutResolver.hs`, which turns indentation into `{ }` tokens before parsing (the LBNF grammar itself only knows
  explicit braces).
- `ClaferCSharpParser.g4`: imports the generated parser over the wrapper lexer's tokens.

Regenerate with `generate.sh` (set `ANTLR_JAR`, optionally `JAVA`, and `BNFC_BIN` to also re-run BNFC; without
`BNFC_BIN` the committed `bnfc/` output is used). The output was checked to be byte-identical to the committed
files.

Known differences from the official front end: comments are skipped in the lexer, and `[alloy| ... |]` and
`[choco| ... |]` blocks are rejected (their tokens are not part of any parser rule).

TODO: have Centiro legal confirm use of the MIT-licensed grammar and of the generated code before this is used
beyond the demo.
