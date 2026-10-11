// The base class for LL(k) parsers synthesized by Parsley: decisions look a fixed number of tokens
// ahead, which the parser's predictor methods read with Peek. Needs ParsleyRuntime.cs. Requires C# 12.
#nullable enable
using System.Collections.Generic;

namespace Parsley.Runtime
{
    /// <summary>
    /// The base of a synthesized LL(k) parser: the current token plus a fixed amount of lookahead
    /// </summary>
    public abstract class LLkParserBase : ParserBase
    {
        readonly LookAheadEnumerator<Token> _tokens;

        protected LLkParserBase(IEnumerable<Token> tokens, ParserTables tables) : base(tables)
        {
            _tokens = new LookAheadEnumerator<Token>(FromLexer(tokens));
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

        /// <summary>
        /// The token n places after Current, or end of input
        /// </summary>
        protected Token Peek(int n) => _tokens.TryPeek(n, out var t) ? t : EndToken(Current);
    }
}
