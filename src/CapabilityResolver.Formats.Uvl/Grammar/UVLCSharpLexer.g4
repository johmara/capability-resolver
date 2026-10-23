// C# lexer for UVL, written for this project following the pattern of the official
// language-specific lexers (uvl/Java/UVLJavaLexer.g4, uvl/Python/UVLPythonLexer.g4):
// it imports the base UVLLexer and adds the indentation handling (INDENT/DEDENT tokens).
lexer grammar UVLCSharpLexer;

import UVLLexer;

@header {
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
}

@members {
  // Extra tokens produced by a single NEWLINE match (NEWLINE, INDENT, DEDENT...), delivered in order.
  private readonly Queue<IToken> _tokens = new Queue<IToken>();
  // The stack that keeps track of the indentation level.
  private readonly Stack<int> _indents = new Stack<int>();
  // The amount of opened braces, brackets and parenthesis.
  private int _opened = 0;
  // The most recently produced token on the default channel.
  private IToken _lastToken = null;

  public override void Emit(IToken t) {
    base.Emit(t);
    _tokens.Enqueue(t);
  }

  public override IToken NextToken() {
    // Check if the end-of-file is ahead and there are still some DEDENTS expected.
    if (InputStream.LA(1) == TokenConstants.EOF && _indents.Count > 0) {
      // Remove any trailing EOF tokens from our buffer.
      var kept = _tokens.Where(t => t.Type != TokenConstants.EOF).ToList();
      _tokens.Clear();
      foreach (var t in kept) _tokens.Enqueue(t);

      // First emit an extra line break that serves as the end of the statement.
      Emit(MakeToken(NEWLINE, "\n"));
      // Now emit as much DEDENT tokens as needed.
      while (_indents.Count > 0) {
        Emit(CreateDedent());
        _indents.Pop();
      }
      // Put the EOF back on the token stream.
      Emit(MakeToken(TokenConstants.EOF, "<EOF>"));
    }

    IToken next = base.NextToken();
    if (next.Channel == TokenConstants.DefaultChannel) {
      // Keep track of the last token on the default channel.
      _lastToken = next;
    }

    return _tokens.Count == 0 ? next : _tokens.Dequeue();
  }

  private IToken CreateDedent() {
    var dedent = MakeToken(DEDENT, "");
    dedent.Line = _lastToken.Line;
    return dedent;
  }

  private CommonToken MakeToken(int type, string text) {
    int stop = CharIndex - 1;
    int start = text.Length == 0 ? stop : stop - text.Length + 1;
    return new CommonToken(new Tuple<ITokenSource, ICharStream>(this, (ICharStream)InputStream), type, TokenConstants.DefaultChannel, start, stop);
  }

  // Tabs are replaced (from left to right) by one to eight spaces such that the total number
  // of characters up to and including the replacement is a multiple of eight (as in Python).
  private static int GetIndentationCount(string spaces) {
    int count = 0;
    foreach (char ch in spaces) {
      if (ch == '\t') count += 8 - (count % 8);
      else count++;
    }
    return count;
  }

  private bool AtStartOfInput() {
    return Column == 0 && Line == 1;
  }

  private void HandleNewline() {
    string newLine = Regex.Replace(Text, "[^\r\n]+", "");
    string spaces = Regex.Replace(Text, "[\r\n]+", "");
    int next = InputStream.LA(1);
    int nextNext = InputStream.LA(2);

    // Line breaks before the first token (blank or comment-only lines at the top of the file) are ignored:
    // the base grammar has no place for a NEWLINE before 'namespace'. This differs from the Java/Python lexers.
    bool beforeFirstToken = _lastToken == null && newLine.Length > 0;

    if (beforeFirstToken || _opened > 0 || next == '\r' || next == '\n' || (next == '/' && nextNext == '/')) {
      // Inside a list or on a blank line: ignore all indents, dedents and line breaks.
      Skip();
    } else {
      Emit(MakeToken(NEWLINE, newLine));
      int indent = GetIndentationCount(spaces);
      int previous = _indents.Count == 0 ? 0 : _indents.Peek();
      if (indent == previous) {
        // Skip indents of the same size as the present indent-size.
        Skip();
      } else if (indent > previous) {
        _indents.Push(indent);
        Emit(MakeToken(INDENT, spaces));
      } else {
        // Possibly emit more than 1 DEDENT token.
        while (_indents.Count > 0 && _indents.Peek() > indent) {
          Emit(CreateDedent());
          _indents.Pop();
        }
      }
    }
  }
}

// Indentation-sensitive tokens
OPEN_PAREN: '(' {_opened += 1;};
CLOSE_PAREN: ')' {_opened -= 1;};
OPEN_BRACK: '[' {_opened += 1;};
CLOSE_BRACK: ']' {_opened -= 1;};
OPEN_BRACE: '{' {_opened += 1;};
CLOSE_BRACE: '}' {_opened -= 1;};
OPEN_COMMENT: '/*' {_opened += 1;};
CLOSE_COMMENT: '*/' {_opened -= 1;};

// Indentation-sensitive NEWLINE rule
NEWLINE: (
		{AtStartOfInput()}? SPACES
		| ( '\r'? '\n' | '\r') SPACES?
	) {HandleNewline();};

// Fragment used by NEWLINE
fragment SPACES: [ \t]+;
