// The other half of the parser synthesized from c99.xbnf: the hooks that let it tell typedef names from
// ordinary identifiers, which C needs to be parsed at all ("T * x;" declares x if T names a type, and
// multiplies otherwise). The grammar's typedef_name is a contextual terminal: the lexer only ever reports
// identifier, and the parser calls IsTypedefName each time it looks at one.
//
// What's tracked:
// - Scopes: file scope, each block, each selection and iteration statement (C99 6.8.4, 6.8.5), and each
//   parameter list. A function's own parameters carry over into its body. Each scope maps a name to
//   whether it's a typedef there, so an inner "int T;" hides an outer typedef T.
// - Whether a type specifier has been seen in the declaration specifiers being read. Once one has, a
//   name right after a specifier, qualifier, *, } or another name is what's being declared, not another
//   type: "typedef int T; { long T; }" declares a variable T. After ( or [ it's a type again (casts,
//   sizeof, parameter lists, array sizes), as it is in an initializer. Deciding by the token before,
//   rather than by where the parse has got to, gives the same answer when the parser looks ahead.
// - The token before: after struct, union or enum a name is a tag, and after . or -> a member, neither
//   of which a typedef can hide, as in "typedef struct node node;".
//
// Known gaps: a label with the same name as a typedef in scope ("T: ...") reads as a type, and a
// typedef declared inside a parameter list stays local to it. Both are rare in real code.
#nullable enable
using System.Collections.Generic;
using Parsley.Runtime;

public partial class C99Parser
{
	// innermost scope last; true where the name is a typedef
	readonly List<Dictionary<string, bool>> _scopes = new() { new() };
	// for each declaration, parameter, struct member or type name being read: has a type specifier been seen
	readonly List<bool> _typeSeen = new();
	// for each declaration being read: is it a typedef
	readonly List<bool> _isTypedef = new();
	// a function definition's parameters, waiting to be added to its body's scope
	Dictionary<string, bool>? _parameters;
	bool _inFunctionHead;

	private partial bool IsTypedefName(Token token, Token previous)
	{
		// a tag or a member name, which typedefs don't hide
		if (previous.SymbolId is Struct or Union or Enum or Dot or Arrow) return false;
		// right after the specifiers, once a type specifier has been seen, the name is the one being declared
		if (0 < _typeSeen.Count && _typeSeen[^1] && _EndsSpecifiers(previous.SymbolId)) return false;
		for (var i = _scopes.Count - 1; i >= 0; --i)
			if (_scopes[i].TryGetValue(token.Value, out var isType))
			{
				return isType;
			}
		return false;
	}

	// a token after which, in declaration specifiers, a name can only be a declarator: a specifier or
	// qualifier keyword, the * of a pointer, the } of a struct or enum body, or a name (a typedef name
	// arrives here as the identifier the lexer read)
	static bool _EndsSpecifiers(int id) => id is Void or Char or Short or Int or Long or Float or Double or Signed or Unsigned
		or Bool or Complex or Const or Restrict or Volatile or Typedef or Extern or Static or Auto or Register or Inline
		or Star or Rbrace or Identifier;

	void _Declare(string? name, bool isType)
	{
		if (null != name) _scopes[^1][name] = isType;
	}

	// the name a declarator declares: its identifier, inside any pointers and parentheses
	static string? _DeclaratorName(ParseNode? declarator)
	{
		while (null != declarator)
		{
			var direct = declarator.Children.Find(c => "direct_declarator" == c.Symbol);
			if (null == direct || 0 == direct.Children.Count) return null;
			var first = direct.Children[0];
			if ("identifier" == first.Symbol) return first.Value;
			declarator = direct.Children.Find(c => "declarator" == c.Symbol);
		}
		return null;
	}

	#region Scopes
	partial void PushScope() => _scopes.Add(new());
	partial void PopScope(ParseNode node) => _PopScope();
	void _PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

	partial void BeginBlock()
	{
		PushScope();
		_typeSeen.Add(false);
		// a function's parameters are in scope in its body
		if (_inFunctionHead)
		{
			_inFunctionHead = false;
			if (null != _parameters)
				foreach (var p in _parameters) _scopes[^1][p.Key] = p.Value;
			_parameters = null;
		}
	}
	partial void EndBlock(ParseNode node)
	{
		_typeSeen.RemoveAt(_typeSeen.Count - 1);
		_PopScope();
	}

	partial void BeginFunction()
	{
		// a scope for old-style parameter declarations, between the declarator and the body
		PushScope();
		_typeSeen.Add(false);
		_inFunctionHead = true;
		_parameters = null;
	}
	partial void EndFunction(ParseNode node)
	{
		_typeSeen.RemoveAt(_typeSeen.Count - 1);
		_PopScope();
	}

	partial void EndParameters(ParseNode node)
	{
		var scope = _scopes[^1];
		_PopScope();
		// the first parameter list in a function definition's head is the function's own
		if (_inFunctionHead && null == _parameters) _parameters = scope;
	}
	#endregion

	#region Declarations
	partial void BeginDeclaration()
	{
		_typeSeen.Add(false);
		_isTypedef.Add(false);
	}
	partial void EndDeclaration(ParseNode node)
	{
		_isTypedef.RemoveAt(_isTypedef.Count - 1);
		_typeSeen.RemoveAt(_typeSeen.Count - 1);
	}
	partial void OnTypedefKeyword(Token token)
	{
		if (0 < _isTypedef.Count) _isTypedef[^1] = true;
	}
	// a name is in scope as soon as its declarator ends, before any initializer or the next declarator
	partial void OnInitDeclarator(ParseNode node)
		=> _Declare(_DeclaratorName(node.Children.Find(c => "declarator" == c.Symbol)), 0 < _isTypedef.Count && _isTypedef[^1]);
	partial void OnEnumerator(ParseNode node)
		=> _Declare(node.Children.Find(c => "identifier" == c.Symbol)?.Value, false);

	partial void BeginSpecifiers() => _typeSeen.Add(false);
	partial void EndSpecifiers(ParseNode node) => _typeSeen.RemoveAt(_typeSeen.Count - 1);
	partial void OnParameter(ParseNode node)
	{
		EndSpecifiers(node);
		_Declare(_DeclaratorName(node.Children.Find(c => "declarator" == c.Symbol)), false);
	}
	partial void OnTypeSpecifier(ParseNode node)
	{
		if (0 < _typeSeen.Count) _typeSeen[^1] = true;
	}

	// in an initializer a typedef name is a type again, as in a cast, not the name being declared
	partial void BeginExpression() => _typeSeen.Add(false);
	partial void EndExpression(ParseNode node) => _typeSeen.RemoveAt(_typeSeen.Count - 1);
	#endregion
}
