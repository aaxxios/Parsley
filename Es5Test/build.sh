#!/bin/sh
# Regenerates the parser and both lexers from es5.xbnf.
# Run from a folder at the root of the Parsley repo (like C99Test), after building Parsley and LexGen
# into its bin folder, or set PARSLEY and LUTHOR to parsley.dll and parsley-luthor.dll.
set -e
cd "$(dirname "$0")"
PARSLEY=${PARSLEY:-../bin/parsley.dll}
LUTHOR=${LUTHOR:-../bin/parsley-luthor.dll}
dotnet "$PARSLEY" es5.xbnf -s -l es5.all.lex -o ES5Parser.cs
# regular_expression_literal is the last terminal: the main lexer leaves it out, and it gets a lexer of its own
grep -v '^regular_expression_literal ' es5.all.lex > es5.lex
grep '^regular_expression_literal ' es5.all.lex > es5regex.lex
rm es5.all.lex
dotnet "$LUTHOR" es5.lex -c ES5Lexer -o ES5Lexer.cs
# its own namespace, so its copy of the Luthor runtime doesn't clash with the main lexer's
dotnet "$LUTHOR" es5regex.lex -c ES5RegexLexer -n ES5RegexLexing -o ES5RegexLexer.cs
# LexGen's list of C# keywords is missing "in" (and "event"), so it writes "const int in"
sed -i 's/public const int in = /public const int @in = /' ES5Lexer.cs
