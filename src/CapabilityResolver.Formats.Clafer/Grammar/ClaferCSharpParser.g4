// C# parser for Clafer: the base parser (generated from clafer.cf by BNFC and transform.py) over the C# lexer's
// token vocabulary.
parser grammar ClaferCSharpParser;

options {
	tokenVocab = ClaferCSharpLexer;
}

import ClaferParser;
