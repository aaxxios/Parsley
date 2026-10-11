// The base class for LL(1) parsers synthesized by Parsley: every decision is made by looking at the
// current token alone, so tokens are read straight from the lexer with no lookahead buffer.
// Needs ParsleyRuntime.cs. Requires C# 12.
#nullable enable
using System.Collections.Generic;

namespace Parsley.Runtime
{
    /// <summary>
    /// The base of a synthesized LL(1) parser: one token of lookahead, nothing buffered
    /// </summary>
    public abstract class LL1ParserBase : ParserBase
    {
        readonly IEnumerator<Token> _tokens;

        protected LL1ParserBase(IEnumerable<Token> tokens, ParserTables tables) : base(tables)
        {
            _tokens = FromLexer(tokens).GetEnumerator();
            Start();
        }

        protected sealed override bool ReadToken(out Token token)
        {
            if (_tokens.MoveNext())
            {
                token = _tokens.Current;
                return true;
            }
            token = default;
            return false;
        }
    }
}
