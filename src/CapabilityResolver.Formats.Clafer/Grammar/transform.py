#!/usr/bin/env python3
"""Turns the ANTLR grammars that BNFC emits for the official clafer.cf (`bnfc --java-antlr`) into
target-neutral grammars that ANTLR can generate C# from.

BNFC's Java output differs from a plain ANTLR grammar in these ways; the transform undoes exactly them:

  parser (ClaferParser.g4)
    * every rule has `returns [ clafer.Absyn.X result ]` and every alternative a Java action
      `{ $result = new clafer.Absyn.<Label>(...); }`           -> both removed
    * the LBNF constructor `<Label>` of each alternative       -> kept as the ANTLR alternative label `# <Label>`
    * labels that ANTLR rejects because they equal a rule's context name (e.g. label `Clafer` vs rule `clafer`)
                                                               -> suffixed with `Alt` (ClaferAlt)
    * pass-through alternatives (no constructor, `$result = $p.result;`, the precedence ladder and `( exp )`)
                                                               -> labelled `Coerce<Rule>` (ANTLR wants all or none)
    * list rules (`listX`, helper lists built with addLast/addFirst) and start_* rules are left unlabelled
    * the empty alternative of an optional part (`card`, `reference`, `elements`, ...) is moved to the end of its
      rule. BNFC lists it first, and ANTLR resolves an ambiguity in favour of the first alternative (skip the optional
      part); the upstream LALR parser (Happy) resolves shift/reduce conflicts by shifting, i.e. consuming the token.
      Last-place empty alternatives give ANTLR the same greedy behaviour (e.g. in `Zero2 0..1` followed by `One 1` the
      `0..1` belongs to Zero2, not to One as its group cardinality).
    * element labels (`p_1_4=PosIdent`)                        -> removed
    * `Surrogate_id_SYMB_<n>` token names                      -> readable names (see token_name)

  lexer (ClaferLexer.g4)
    * token renames as above
    * `PosLineComment` and `PosBlockComment` are position tokens in clafer.cf (the upstream front end
      handles comments outside the grammar)                    -> `-> skip`
    * everything else (PosIdent, PosString, ..., PosAlloy, PosChoco, ErrorToken) is kept as BNFC wrote it

usage: transform.py <bnfc output dir containing ClaferLexer.g4 and ClaferParser.g4> <output dir>
"""
import re
import sys
from pathlib import Path

# Names for punctuation tokens, by literal. Keywords are named K_<word>. An unknown literal is an error,
# so a new upstream symbol cannot silently get a bad name.
SYMBOL_NAMES = {
    "=": "EQUALS", "[": "LBRACK", "]": "RBRACK", "<<": "LGOAL", ">>": "RGOAL", "{": "LBRACE", "}": "RBRACE",
    "`": "BACKTICK", ":": "COLON", "->": "ARROW", "->>": "ARROW_BAG", ":=": "COLON_EQUALS", "?": "QUESTION",
    "+": "PLUS", "*": "STAR", "..": "DOTDOT", "|": "BAR", "<=>": "IFF", "=>": "IMPLIES", "||": "OR_OP",
    "&&": "AND_OP", "!": "BANG", "<": "LT", ">": "GT", "<=": "LE", ">=": "GE", "!=": "NE", "-": "MINUS",
    "/": "SLASH", "%": "PERCENT", "#": "HASH", "<:": "DOMAIN", ":>": "RANGE", "++": "UNION", ",": "COMMA",
    "--": "DIFFERENCE", "**": "INTERSECTION", "&": "AMP", ".": "DOT", "-->>": "SYNC_ARROW", "-[": "LGUARD",
    "]->>": "RGUARD_SYNC", "-->": "NEXT_ARROW", "]->": "RGUARD_NEXT", ";": "SEMI", "\\\\": "BACKSLASH",
    "(": "LPAREN", ")": "RPAREN",
}

SUFFIX_FOR_CLASHING_LABELS = "Alt"


def token_name(literal):
    if re.fullmatch(r"[A-Za-z]+", literal):
        return "K_" + literal
    if literal in SYMBOL_NAMES:
        return SYMBOL_NAMES[literal]
    raise SystemExit(f"no token name for literal '{literal}': add it to SYMBOL_NAMES in transform.py")


def read_token_names(lexer_text):
    names = {}
    for m in re.finditer(r"^(Surrogate_id_SYMB_\d+)\s*:\s*'((?:[^'\\]|\\.)*)'\s*;", lexer_text, re.M):
        names[m.group(1)] = token_name(m.group(2))
    if len(set(names.values())) != len(names):
        raise SystemExit("token names are not unique")
    return names


def rename_tokens(text, names):
    return re.sub(r"\bSurrogate_id_SYMB_\d+\b", lambda m: names[m.group(0)], text)


def transform_lexer(text, names):
    text = rename_tokens(text, names)
    for token in ("PosLineComment", "PosBlockComment"):
        text, count = re.subn(rf"^({token}\s*:.*?);\s*$", r"\1 -> skip;", text, flags=re.M)
        if count != 1:
            raise SystemExit(f"expected exactly one {token} rule in the BNFC lexer")
    return text.replace("// -*- Java -*-", "// -*- ANTLR -*-", 1)


def capitalize(name):
    return name[:1].upper() + name[1:]


def transform_parser(text, names):
    text = rename_tokens(text, names)
    lines = text.split("\n")

    rule_names = {m.group(1) for line in lines if (m := re.match(r"^(\w+)( returns \[.*\])?\s*$", line)) and not line.startswith(("//", " "))}
    clashing = {capitalize(r) for r in rule_names}

    out, i, rule, is_plain, alts = [], 0, None, False, []

    def flush_alternatives():
        # Empty alternatives last (see the module docstring), except in the recursive list rules.
        ordered = alts if is_plain else [a for a in alts if a[0]] + [a for a in alts if not a[0]]
        for n, (symbols, label) in enumerate(ordered):
            op = ":" if n == 0 else "|"
            out.append(f"  {op} {symbols}{label}".rstrip() if symbols else f"  {op}{label}")
        alts.clear()

    while i < len(lines):
        line = lines[i]

        header = re.match(r"^(\w+) returns \[ ([\w.]+) result \]\s*$", line)
        if header:
            rule = header.group(1)
            # list rules (built with addLast/addFirst) and start rules keep their shape and get no labels
            is_plain = rule.startswith("list") or rule.startswith("start_")
            out.append(rule)
            i += 1
            continue

        alt = re.match(r"^  ([:|])\s*(.*?)\s*$", line)
        if alt and rule is not None:
            action = lines[i + 1].strip()
            if not (action.startswith("{") and action.endswith("}")):
                raise SystemExit(f"line {i + 2}: expected a BNFC action after the alternative, found: {action}")

            symbols = re.sub(r"/\*\s*empty\s*\*/", "", alt.group(2))
            symbols = re.sub(r"\b\w+=(?=\w)", "", symbols).strip()  # drop element labels p_1_2=...

            label = ""
            if not is_plain:
                constructor = re.search(r"new clafer\.Absyn\.(\w+)\(", action)
                if constructor:
                    label = constructor.group(1)
                    if label in clashing:
                        label += SUFFIX_FOR_CLASHING_LABELS
                else:
                    label = "Coerce" + capitalize(rule.rstrip("_"))
                label = f" # {label}"

            alts.append((symbols, label))
            i += 2
            continue

        if line.strip() == ";":
            flush_alternatives()
        out.append(line)
        i += 1

    return "\n".join(out).replace("// -*- Java -*-", "// -*- ANTLR -*-", 1)


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    source, target = Path(sys.argv[1]), Path(sys.argv[2])
    lexer = (source / "ClaferLexer.g4").read_text()
    parser = (source / "ClaferParser.g4").read_text()

    names = read_token_names(lexer)
    target.mkdir(parents=True, exist_ok=True)
    (target / "ClaferLexer.g4").write_text(transform_lexer(lexer, names))
    (target / "ClaferParser.g4").write_text(transform_parser(parser, names))


if __name__ == "__main__":
    main()
