// C# lexer for Clafer. Imports the base lexer (generated from clafer.cf by BNFC and transform.py) and adds the
// layout rule. This file and ClaferCSharpParser.g4 are the only hand-written grammar files.
//
// The official Clafer front end does not put indentation into the grammar: LayoutResolver.hs
// (gsdlab/clafer, src/Language/Clafer/Front/LayoutResolver.hs) rewrites the token stream, inserting '{' when
// a line starts deeper than the current level and '}' when it starts shallower, and ignoring line starts
// inside [ ] and { }. This is a port of its token-level `resolve`/`addNewLines` to a lexer that emits
// LBRACE/RBRACE tokens. Levels are 1-based columns; a tab advances to the next multiple of 8, like the
// official Alex lexer.
lexer grammar ClaferCSharpLexer;

import ClaferLexer;

@header {
using System.Collections.Generic;
}

@members {
  private readonly Queue<IToken> _pending = new Queue<IToken>();
  private readonly Stack<int> _levels = new Stack<int>();
  // Open [ and { seen so far (explicit ones only; the inserted braces are not counted).
  private int _depth = 0;
  private IToken _previous = null;
  private string _source = null;

  public override IToken NextToken() {
    if (_levels.Count == 0) _levels.Push(1);
    if (_pending.Count > 0) return _pending.Dequeue();

    IToken t = base.NextToken();

    if (t.Type == TokenConstants.EOF) {
      // Close every level that is still open.
      while (_levels.Count > 1) {
        _levels.Pop();
        _pending.Enqueue(Synthetic(RBRACE, "}", t));
      }
      _pending.Enqueue(t);
      return _pending.Dequeue();
    }

    // A token that starts a line, outside any brackets, opens or closes levels.
    if (_previous != null && t.Line > _previous.Line && _depth <= 0) {
      int level = ColumnOf(t);
      if (level > _levels.Peek()) {
        _levels.Push(level);
        _pending.Enqueue(Synthetic(LBRACE, "{", t));
      } else {
        while (level < _levels.Peek()) {
          _levels.Pop();
          _pending.Enqueue(Synthetic(RBRACE, "}", t));
        }
      }
    }

    if (t.Type == LBRACK || t.Type == LBRACE) _depth++;
    else if (t.Type == RBRACK || t.Type == RBRACE) _depth--;

    _previous = t;
    _pending.Enqueue(t);
    return _pending.Dequeue();
  }

  private static IToken Synthetic(int type, string text, IToken at) {
    var token = new CommonToken(type, text);
    token.Line = at.Line;
    token.Column = at.Column;
    token.StartIndex = at.StartIndex;
    token.StopIndex = at.StartIndex - 1;
    return token;
  }

  // 1-based column of the token's first character, counting tabs as the official lexer does.
  private int ColumnOf(IToken t) {
    if (_source == null) {
      var stream = (ICharStream)InputStream;
      _source = stream.Size == 0 ? "" : stream.GetText(Interval.Of(0, stream.Size - 1));
    }
    int lineStart = t.StartIndex == 0 ? 0 : _source.LastIndexOf('\n', t.StartIndex - 1) + 1;
    int column = 1;
    for (int i = lineStart; i < t.StartIndex; i++) {
      column = _source[i] == '\t' ? ((column + 7) / 8) * 8 + 1 : column + 1;
    }
    return column;
  }
}
