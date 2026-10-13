using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Parsley
{
	/// <summary>
	/// Another grammar whose shared productions parse islands in this one. Written in XBNF as
	/// <c>@external Json "json.xbnf";</c>
	/// </summary>
	public sealed class ExternalGrammar : GrammarNode
	{
		public ExternalGrammar(string alias, string path, Grammar? grammar)
		{
			Alias = alias ?? throw new ArgumentNullException(nameof(alias));
			Path = path ?? throw new ArgumentNullException(nameof(path));
			Grammar = grammar;
		}
		/// <summary>
		/// The name islands use for it, as in <c>entry="Json.Object"</c>
		/// </summary>
		public string Alias { get; }
		/// <summary>
		/// The file, as written, relative to the grammar that declares it
		/// </summary>
		public string Path { get; }
		/// <summary>
		/// The grammar read from the file, or null if it couldn't be read
		/// </summary>
		public Grammar? Grammar { get; }
		public ExternalGrammar Clone()
		{
			var result = new ExternalGrammar(Alias, Path, Grammar?.Clone());
			result.CopyLocation(this);
			return result;
		}
		public override string ToString() => $"@external {Alias} {GrammarAttribute.FormatValue(Path)};";
	}

	/// <summary>
	/// The C# type whose static Tokenize lexes for this grammar, or for one of its aliases: an @external grammar,
	/// or the lexer some terminals name with <c>lexer=</c>. Written in XBNF as <c>@lexer "BamcLexer";</c> or
	/// <c>@lexer Json "Foo.JsonLexer";</c>
	/// </summary>
	public sealed class LexerDirective : GrammarNode
	{
		public LexerDirective(string? alias, string typeName)
		{
			Alias = alias;
			TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
		}
		/// <summary>
		/// The alias it's for, or null for the grammar's own lexer
		/// </summary>
		public string? Alias { get; }
		/// <summary>
		/// The type, as written. Its static Tokenize(TextReader, long, int, int, int) returns the tokens.
		/// </summary>
		public string TypeName { get; }
		public LexerDirective Clone()
		{
			var result = new LexerDirective(Alias, TypeName);
			result.CopyLocation(this);
			return result;
		}
		public override string ToString() => null == Alias ? $"@lexer {GrammarAttribute.FormatValue(TypeName)};" : $"@lexer {Alias} {GrammarAttribute.FormatValue(TypeName)};";
	}

	partial class Grammar
	{
		public ExternalGrammar? GetExternal(string alias) => Externals.FirstOrDefault(x => x.Alias == alias);
		/// <summary>
		/// The type @lexer names for the alias (null: for this grammar), as written, or null if none does
		/// </summary>
		public string? GetLexerType(string? alias) => Lexers.LastOrDefault(l => l.Alias == alias)?.TypeName;
		/// <summary>
		/// The aliases of the lexers terminals name with <c>lexer=</c>, in the order they're first named
		/// </summary>
		public IReadOnlyList<string> LexerGroups => Terminals.Where(t => null != t.Lexer).Select(t => t.Lexer!).Distinct().ToList();
		/// <summary>
		/// The grammar has islands or terminals from other lexers, so its tokens come from a lexer controller
		/// </summary>
		public bool IsCompound => Terminals.Any(t => t.IsIsland || null != t.Lexer);
		/// <summary>
		/// The name from the file name with a suffix, as the class name is made: <c>bamc.xbnf</c> and "Lexer" make <c>BamcLexer</c>
		/// </summary>
		public string? NameFromFile(string suffix) => _NameFromFile(suffix);

		/// <summary>
		/// An island's external grammar and shared production, split from its <c>entry</c>. Either can be null
		/// if the entry doesn't name one.
		/// </summary>
		public (ExternalGrammar? External, Production? Production) ResolveEntry(TerminalDeclaration island)
		{
			var entry = island.Entry;
			if (null == entry) return (null, null);
			var dot = entry.IndexOf('.');
			if (0 >= dot) return (null, null);
			var x = GetExternal(entry.Substring(0, dot));
			return (x, x?.Grammar?.GetProduction(entry.Substring(dot + 1)));
		}

		/// <summary>
		/// The external grammar's terminal an island's <c>nest</c> or <c>pop</c> names: <c>Json.rbrace</c>, or just
		/// <c>rbrace</c>, by name or literal text
		/// </summary>
		public static TerminalDeclaration? ResolveExternalTerminal(ExternalGrammar x, string name)
		{
			if (null == x.Grammar) return null;
			if (name.StartsWith(x.Alias + ".", StringComparison.Ordinal)) name = name.Substring(x.Alias.Length + 1);
			return x.Grammar.GetTerminal(name) ?? x.Grammar.Terminals.FirstOrDefault(t => t.Literal == name);
		}

		/// <summary>
		/// One of this grammar's terminals, by name or literal text
		/// </summary>
		public TerminalDeclaration? FindTerminal(string nameOrLiteral)
			=> GetTerminal(nameOrLiteral) ?? Terminals.FirstOrDefault(t => t.Literal == nameOrLiteral);

		// the checks for shared productions, @external, @lexer, islands and terminals from other lexers
		void _ValidateIslands(List<GrammarMessage> result)
		{
			var aliases = new HashSet<string>();
			foreach (var x in Externals)
			{
				if (!aliases.Add(x.Alias))
					result.Add(new GrammarMessage(ErrorLevel.Error, $"@external {x.Alias} is declared more than once", x));
				if (null != GetProduction(x.Alias) || null != GetTerminal(x.Alias))
					result.Add(new GrammarMessage(ErrorLevel.Error, $"@external {x.Alias} has the same name as a rule; give it another alias", x));
			}
			var groups = LexerGroups;
			var lexerAliases = new HashSet<string?>();
			foreach (var l in Lexers)
			{
				if (!lexerAliases.Add(l.Alias))
					result.Add(new GrammarMessage(ErrorLevel.Warning, null == l.Alias ? "@lexer is given more than once; the last one is used" : $"@lexer {l.Alias} is given more than once; the last one is used", l));
				if (!_IsTypeName(l.TypeName))
					result.Add(new GrammarMessage(ErrorLevel.Error, $"@lexer expects a C# type name, such as \"MyLexer\" or \"My.Namespace.MyLexer\", not \"{l.TypeName}\"", l));
				if (null != l.Alias && null == GetExternal(l.Alias) && !groups.Contains(l.Alias))
					result.Add(new GrammarMessage(ErrorLevel.Warning, $"@lexer {l.Alias} names neither an @external grammar nor a lexer any terminal uses with lexer=, so it's ignored", l));
			}
			foreach (var p in Productions)
			{
				if (!p.Attributes.IsSet("shared")) continue;
				if (p.IsCollapsed)
					result.Add(new GrammarMessage(ErrorLevel.Error, $"\"{p.Name}\" is shared, so it can't be collapsed: an island needs its node", p));
			}
			var islands = Terminals.Where(t => t.IsIsland).ToList();
			foreach (var t in Terminals)
			{
				if (t.IsIsland)
					_ValidateIsland(t, result);
				else if (null != t.Lexer)
					_ValidateFromLexer(t, result);
				else
				{
					foreach (var a in new[] { "after", "nest", "pop", "popOnError" })
						if (t.Attributes.Contains(a))
							result.Add(new GrammarMessage(ErrorLevel.Warning, $"On \"{t.Name}\": {a} only applies to an island (a rule with entry=), so it's ignored", t.Attributes.Find(a)!));
					if (t.Attributes.Contains("on"))
						result.Add(new GrammarMessage(ErrorLevel.Warning, $"On \"{t.Name}\": on only applies to a terminal with lexer=, so it's ignored", t.Attributes.Find("on")!));
				}
				if (t.Attributes.Contains("shared"))
					result.Add(new GrammarMessage(ErrorLevel.Error, $"\"{t.Name}\" is a terminal; only productions can be shared", t.Attributes.Find("shared")!));
			}
			// an island the productions never use would only produce tokens nothing expects
			var used = new HashSet<string>(Productions.SelectMany(p => p.Expression.Descendants()).OfType<SymbolRef>().Where(r => null != r.Name).Select(r => r.Name!));
			foreach (var i in islands)
				if (!used.Contains(i.Name))
					result.Add(new GrammarMessage(ErrorLevel.Warning, $"The island \"{i.Name}\" isn't used by any production", i));
			foreach (var x in Externals)
				if (!islands.Any(i => ResolveEntry(i).External == x))
					result.Add(new GrammarMessage(ErrorLevel.Warning, $"@external {x.Alias} isn't used by any island", x));
		}

		static bool _IsTypeName(string s)
		{
			var parts = s.Split('.');
			return parts.All(p =>
			{
				var i = 0 < p.Length && '@' == p[0] ? 1 : 0;
				if (i >= p.Length || !(char.IsLetter(p[i]) || '_' == p[i])) return false;
				for (++i; i < p.Length; ++i)
					if (!(char.IsLetterOrDigit(p[i]) || '_' == p[i])) return false;
				return true;
			});
		}

		void _ValidateIsland(TerminalDeclaration t, List<GrammarMessage> result)
		{
			GrammarNode at(string attr) => (GrammarNode?)t.Attributes.Find(attr) ?? t;
			var entry = t.Entry!;
			var dot = entry.IndexOf('.');
			if (0 >= dot || dot == entry.Length - 1)
			{
				result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": entry expects an external grammar's alias and one of its shared productions, as in entry=\"Json.Object\", not \"{entry}\"", at("entry")));
				return;
			}
			var (x, p) = ResolveEntry(t);
			if (null == x)
			{
				result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": \"{entry.Substring(0, dot)}\" isn't declared with @external", at("entry")));
				return;
			}
			if (null == x.Grammar) return; // reading it failed, which was reported
			if (null == p)
				result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": {x.Alias} has no production \"{entry.Substring(dot + 1)}\"", at("entry")));
			else if (!p.Attributes.IsSet("shared"))
				result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": {x.Alias}'s \"{p.Name}\" isn't marked shared, so other grammars can't parse with it", at("entry")));
			if (t.IsHidden)
				result.Add(new GrammarMessage(ErrorLevel.Error, $"The island \"{t.Name}\" can't be hidden", at("hidden")));
			if (null != t.When)
				result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": when doesn't apply to an island; after says where it starts", at("when")));
			if (null != t.Lexer)
				result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": lexer doesn't apply to an island; its grammar's lexer lexes it (see @lexer {x.Alias})", at("lexer")));
			if (!(t.Definition is EmptyExpression))
				result.Add(new GrammarMessage(ErrorLevel.Error, $"The island \"{t.Name}\" has a body; an island is declared without one, as in {t.Name}<entry=\"{entry}\">;", t));
			var after = t.GetNames("after");
			if (0 == after.Count)
				result.Add(new GrammarMessage(ErrorLevel.Error, $"The island \"{t.Name}\" needs after=, naming the terminals it starts after", at("after")));
			foreach (var name in after)
			{
				var a = FindTerminal(name);
				if (null == a)
					result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": after names \"{name}\", which isn't a terminal", at("after")));
				else if (a.IsHidden || a.IsContextual || a.IsIsland)
					result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": after names \"{a.Name}\", which the lexer controller never sees (it's {(a.IsHidden ? "hidden" : a.IsContextual ? "contextual" : "an island")})", at("after")));
			}
			foreach (var attr in new[] { "nest", "pop" })
			{
				var v = t.Attributes.Get(attr);
				if (null == v) continue;
				if (!(v is string name) || 0 == name.Trim().Length)
				{
					result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": {attr} expects one of {x.Alias}'s terminals, as in {attr}=\"{x.Alias}.{("nest" == attr ? "lbrace" : "rbrace")}\"", at(attr)));
					continue;
				}
				var term = ResolveExternalTerminal(x, name.Trim());
				if (null == term)
					result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": {attr} names \"{name}\", which isn't one of {x.Alias}'s terminals", at(attr)));
				else if (!term.IsLexed || term.IsHidden)
					result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": {attr} names {x.Alias}'s \"{term.Name}\", which {x.Alias}'s lexer never reports", at(attr)));
			}
			if (t.Attributes.Contains("nest") && !t.Attributes.Contains("pop"))
				result.Add(new GrammarMessage(ErrorLevel.Warning, $"On \"{t.Name}\": nest does nothing without pop", at("nest")));
			if (t.Attributes.Contains("popOnError") && !(t.Attributes.Get("popOnError") is bool))
				result.Add(new GrammarMessage(ErrorLevel.Warning, $"On \"{t.Name}\": popOnError expects true or false", at("popOnError")));
			if (!t.Attributes.Contains("pop") && !t.PopOnError)
				result.Add(new GrammarMessage(ErrorLevel.Warning, $"The island \"{t.Name}\" has no pop and popOnError=false, so it only ends at the end of the input", t));
		}

		void _ValidateFromLexer(TerminalDeclaration t, List<GrammarMessage> result)
		{
			GrammarNode at(string attr) => (GrammarNode?)t.Attributes.Find(attr) ?? t;
			var alias = t.Lexer!;
			if (!_IsTypeName(alias) || alias.Contains('.'))
				result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": lexer expects a name for the lexer, such as lexer=Regex, which @lexer can give a type", at("lexer")));
			if (null != GetExternal(alias))
				result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": {alias} is an @external grammar; text in another grammar is an island, declared with entry=", at("lexer")));
			if (t.IsHidden)
				result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": a terminal from another lexer can't be hidden", at("hidden")));
			var on = t.GetNames("on");
			if (0 == on.Count)
				result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": lexer needs on=, naming the terminals of this grammar's lexer where {alias} may lex instead", at("lexer")));
			foreach (var name in on)
			{
				var o = FindTerminal(name);
				if (null == o)
					result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": on names \"{name}\", which isn't a terminal", at("on")));
				else if (!o.IsLexed || o.IsHidden)
					result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": on names \"{o.Name}\", which this grammar's lexer never reports", at("on")));
			}
			if (null != t.When && !_IsMethodName(t.When))
				result.Add(new GrammarMessage(ErrorLevel.Error, $"On \"{t.Name}\": when expects the name of a C# method, such as when=\"RegexCanStart\"", at("when")));
			if (t.Definition is EmptyExpression)
				result.Add(new GrammarMessage(ErrorLevel.Error, $"\"{t.Name}\" needs a definition for {alias} to lex it", t));
		}
	}
}
