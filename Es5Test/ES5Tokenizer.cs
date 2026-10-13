// The token stream Es5Parser reads: ES5Lexer's tokens, except where a / starts a regular expression
// literal.
//
// Whether a / is division or the start of a regular expression depends on the syntax before it, and
// it decides how the characters after it split into tokens: in a = b / c / d it's division twice, in
// a = /c / d/ it's one literal. A contextual terminal can't help, since it only relabels a token the
// lexer has already cut out. So this sits between the lexer and the parser, watches the tokens go by,
// and where a regular expression can start, matches one with Es5RegexLexer from the / on, hands the
// parser that one token, and restarts the main lexer after it.
//
// Whether a regular expression can start is decided from the token before the /, the same rule
// tokenizers like Esprima's use:
// - After a value (a name, a literal, this, ], postfix ++ or --), / is division.
// - After an operator, a keyword, ( [ , ; { or the start of the input, it starts a regular expression.
// - After ), it starts one only if the parentheses were an if, while, for or with condition:
//   if (x) /re/.test(s) is a statement, (x) / 2 is division.
// - After }, it starts one only if the braces were a block, not an object literal. A { is taken as an
//   object literal where an expression is expected: after an operator, ( [ , ? : or a keyword like
//   return. Otherwise (after ), ; { }, else, do, try, finally or the start) it's a block.
// - A keyword right after a . is a property name (a.default / 2), so it counts as a name.
//
// It also does the part of automatic semicolon insertion that depends on line breaks the parser can't
// see coming (7.9.1, the restricted productions): after return, break, continue or throw, a line
// break ends the statement, and a ++ or -- at the start of a line is never postfix. In those places
// it inserts a ; token (with empty text). The rest of semicolon insertion is in the grammar's
// EndOfStatement.
//
// Known gaps: a block after a label or case (case 1: { } /re/) is taken as an object literal, and a
// function expression's body as a block (x = function () {} / 2). Neither shows up in real code.
#nullable enable
using System.Collections.Generic;
using System.IO;
using Parsley.Runtime;

public static class ES5Tokenizer
{
	/// <summary>
	/// The lexer id the parser expects for a regular expression literal. It's the last terminal in the
	/// grammar, the one the build leaves out of es5.lex, so it comes right after the main lexer's last.
	/// </summary>
	public const int RegularExpressionLiteral = ES5Lexer.TerminalCount;

	public static IEnumerable<Token> Tokenize(TextReader reader) => Tokenize(reader.ReadToEnd());

	public static IEnumerable<Token> Tokenize(string text)
	{
		var context = new _Context();
		long offset = 0;
		int line = 1, column = 1;
		while (true)
		{
			var restart = false;
			// the lexer reports positions, lines and columns from where it's told the text starts
			foreach (var token in ES5Lexer.Tokenize(0 == offset ? text : text.Substring((int)offset), offset, line, column))
			{
				if (context.InsertsSemicolonBefore(token))
				{
					var semi = new Token(ES5Lexer.semi, "", token.Line, token.Column, token.Position);
					context.Add(semi);
					yield return semi;
				}
				if ((ES5Lexer.slash == token.SymbolId || ES5Lexer.div_assign == token.SymbolId) && context.RegexCanStart)
				{
					var literal = _MatchRegex(text, token);
					if (null != literal)
					{
						var regex = literal.Value;
						context.Add(regex);
						yield return regex;
						// a regular expression literal can't hold a line break, so it ends on its own line
						offset = token.Position + regex.Value.Length;
						line = token.Line;
						column = token.Column + regex.Value.Length;
						restart = true;
						break;
					}
					// not a well formed literal: leave the / as it is, and the parser reports what follows
				}
				context.Add(token);
				yield return token;
			}
			if (!restart) yield break;
		}
	}

	/// <summary>
	/// The line a token ends on: its own, unless it holds a line break (a string continued onto the next line)
	/// </summary>
	public static int EndLine(Token token)
	{
		var line = token.Line;
		var text = token.Value;
		for (var i = 0; i < text.Length; ++i)
		{
			var c = text[i];
			if ('\n' == c || '\u2028' == c || '\u2029' == c || ('\r' == c && (i + 1 == text.Length || '\n' != text[i + 1]))) ++line;
		}
		return line;
	}

	static Token? _MatchRegex(string text, Token slash)
	{
		foreach (var t in ES5RegexLexing.ES5RegexLexer.Tokenize(text.Substring((int)slash.Position), slash.Position, slash.Line, slash.Column))
			// the only rule is the literal; anything else comes back as an error
			return ES5RegexLexing.ES5RegexLexer.regular_expression_literal == t.SymbolId
				? new Token(RegularExpressionLiteral, t.Value, t.Line, t.Column, t.Position)
				: null;
		return null;
	}

	sealed class _Context
	{
		const int _Start = -1;
		int _previous = _Start; // the last token, as a lexer id
		int _beforePrevious = _Start;
		int _previousEndLine; // the line the last token ends on
		// for each open ( : was it an if, while, for or with condition. For each open { : is it a block.
		readonly List<bool> _parens = new();
		readonly List<bool> _braces = new();
		bool _closedCondition; // the last ) closed a condition
		bool _closedBlock = true; // the last } closed a block

		// a ; goes in before next: a line break after return, break, continue or throw (unless it's a
		// property name, a.return), or before a ++ or -- that starts a line after an operand
		public bool InsertsSemicolonBefore(Token next)
		{
			if (_Start == _previous || ES5Lexer.semi == next.SymbolId || next.Line <= _previousEndLine) return false;
			if (_previous is ES5Lexer.@return or ES5Lexer.@break or ES5Lexer.@continue or ES5Lexer.@throw)
				return ES5Lexer.dot != _beforePrevious;
			return next.SymbolId is ES5Lexer.inc or ES5Lexer.dec
				&& (_previous is ES5Lexer.rparen or ES5Lexer.rbrace || _EndsValue(_previous, _beforePrevious));
		}

		public bool RegexCanStart => _previous switch
		{
			_Start => true,
			ES5Lexer.rparen => _closedCondition,
			ES5Lexer.rbrace => _closedBlock,
			_ => !_EndsValue(_previous, _beforePrevious),
		};

		public void Add(Token token)
		{
			var id = token.SymbolId;
			switch (id)
			{
				case ES5Lexer.lparen:
					_parens.Add(_previous is ES5Lexer.@if or ES5Lexer.@while or ES5Lexer.@for or ES5Lexer.with);
					break;
				case ES5Lexer.rparen:
					_closedCondition = _Pop(_parens, false);
					break;
				case ES5Lexer.lbrace:
					_braces.Add(_StartsBlock(_previous));
					break;
				case ES5Lexer.rbrace:
					_closedBlock = _Pop(_braces, true);
					break;
			}
			_beforePrevious = _previous;
			_previous = id;
			_previousEndLine = EndLine(token);
		}

		static bool _Pop(List<bool> stack, bool empty)
		{
			if (0 == stack.Count) return empty;
			var result = stack[^1];
			stack.RemoveAt(stack.Count - 1);
			return result;
		}

		// a token after which a / divides: the end of an operand
		static bool _EndsValue(int id, int before) => id switch
		{
			ES5Lexer.identifier or ES5Lexer.numeric_literal or ES5Lexer.string_literal or RegularExpressionLiteral
				or ES5Lexer.rbracket or ES5Lexer.@this or ES5Lexer.@null or ES5Lexer.@true or ES5Lexer.@false
				or ES5Lexer.inc or ES5Lexer.dec => true,
			// any other keyword after a . is a property name: a.default / 2
			_ => ES5Lexer.dot == before && _IsKeyword(id),
		};

		// the keywords are the first terminals in es5.xbnf, break through false
		static bool _IsKeyword(int id) => id >= ES5Lexer.@break && id <= ES5Lexer.@false;

		// where a { starts a block rather than an object literal
		static bool _StartsBlock(int previous) => previous is _Start or ES5Lexer.rparen or ES5Lexer.semi
			or ES5Lexer.lbrace or ES5Lexer.rbrace or ES5Lexer.@else or ES5Lexer.@do or ES5Lexer.@try or ES5Lexer.@finally;
	}
}
