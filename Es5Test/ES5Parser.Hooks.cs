// The other half of the parser synthesized from es5.xbnf: the checks its hooks make.
#nullable enable
using Parsley.Runtime;

public partial class ES5Parser
{
	// These run on every node as it's parsed, including nodes that error recovery left incomplete or
	// holding skipped tokens. Those they leave alone: the parser has already reported the error there,
	// and a check that threw would replace its message with a misleading one.
	static bool _Recovered(ParseNode node) => node.Children.Exists(c => c.IsError);

	// AssignmentExpression is ConditionalExpression [ AssignmentOperator AssignmentExpression ], which
	// takes any expression on the left. The standard only allows a left-hand side expression there:
	// a name, a member access, a call, a new or a parenthesized expression (11.13). Each expression level
	// with a single child just passes its operand through, so the left side is valid if it's a chain of
	// single children down to a LeftHandSideExpression.
	partial void CheckAssignment(ParseNode node)
	{
		if (node.Children.Count < 2 || _Recovered(node)) return;
		if (!_IsLeftHandSide(node.Children[0]))
			_Fail($"Invalid left-hand side for {node.Children[1].Value}", node.Children[1]);
	}

	// a chain of single children down to a LeftHandSideExpression
	static bool _IsLeftHandSide(ParseNode node)
	{
		while (LeftHandSideExpression != node.SymbolId)
		{
			if (node.IsTerminal || 1 != node.Children.Count) return false;
			node = node.Children[0];
		}
		return true;
	}

	static void _Fail(string message, ParseNode at)
		=> throw new ParseException(new[] { new ParseError(message, at.Line, at.Column, at.Position) });

	// for-in takes one variable (12.6.4: for (var VariableDeclarationNoIn in Expression)) or a left-hand
	// side expression, which the factored ForStatement doesn't check
	partial void CheckForIn(ParseNode node)
	{
		var inAt = node.Children.FindIndex(c => In == c.SymbolId);
		if (0 > inAt || _Recovered(node)) return;
		var head = node.Children.GetRange(1, inAt - 1);
		if (0 == head.Count)
			_Fail("Expected a variable or left-hand side before in", node.Children[inAt]);
		else if (Var == head[0].SymbolId)
		{
			if (2 < head.Count) _Fail("Only one variable can be declared in a for-in", head[2]);
		}
		else if (1 != head[0].Children.Count || !_IsLeftHandSide(head[0].Children[0]))
			_Fail("Invalid left-hand side in for-in", head[0]);
	}

	// 11.1.5: get name () { } and set name (value) { }
	partial void CheckAccessor(ParseNode node)
	{
		// after a syntax error, recovery can leave the node short of children
		if (2 > node.Children.Count || _Recovered(node) || PropertyName != node.Children[1].SymbolId || 0 == node.Children[0].Children.Count) return;
		var kind = node.Children[0].Children[0];
		var parameters = node.Children.Count - 3; // kind, name, [parameter], body
		// Identifier2 is the identifier terminal (Identifier is the collapsed rule)
		if (Identifier2 != kind.SymbolId || ("get" != kind.Value && "set" != kind.Value))
			_Fail("Expected get or set before an accessor's name", kind);
		else if (("get" == kind.Value) != (0 == parameters))
			_Fail("get takes no parameters and set takes one", node.Children[1]);
	}

	// 12.4: an expression statement can't start with function (it would be a function declaration)
	partial void CheckExpressionStatement(ParseNode node)
	{
		// ( isn't in the tree, so the first token in it can be function in (function () {})(); the
		// statement starts at the function only if they're at the same place
		if (_Recovered(node)) return;
		var first = node;
		while (!first.IsTerminal && 0 < first.Children.Count) first = first.Children[0];
		if (Function == first.SymbolId && first.Position == node.Position) _Fail("A function expression can't start a statement; wrap it in parentheses", first);
	}
}

public partial class ES5Parser
{
	// Automatic semicolon insertion (7.9.1, rule 1 and 2): a statement can leave out its ; only before a
	// }, at the end of the input, or before a token on a later line. EndOfStatement's ; is optional and
	// the parse greedy, so by the time this runs the statement has taken everything that could continue
	// it; all that's left is to check that leaving the ; out was allowed.
	partial void CheckSemicolon(ParseNode node)
	{
		var last = Previous;
		if (Semi == last.SymbolId) return;
		var next = Current;
		if (Rbrace == next.SymbolId || EndOfInput == next.SymbolId || next.Line > ES5Tokenizer.EndLine(last)) return;
		throw new ParseException(new[] { new ParseError($"Expected \";\" but found \"{next.Value}\"", next.Line, next.Column, next.Position) });
	}
}
