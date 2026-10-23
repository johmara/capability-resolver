// C# parser for UVL: the official base parser grammar over the C# lexer's token vocabulary.
parser grammar UVLCSharpParser;

options {
	tokenVocab = UVLCSharpLexer;
}

import UVLParser;
