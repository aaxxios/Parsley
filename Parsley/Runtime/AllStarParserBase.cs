// The base class for ALL(*) parsers synthesized by Parsley. Decisions that a fixed amount of lookahead
// can't settle are predicted at parse time by simulating the grammar's network of states (its ATN) over
// as much of the upcoming input as it takes: first without regard to where the parser was called from
// (SLL, cached as a DFA per decision), then, only when that is inconclusive, with the parser's actual
// call stack (full LL). Needs ParsleyRuntime.cs. Requires C# 12.
#nullable enable
using System;
using System.Collections.Generic;

namespace Parsley.Runtime
{
    /// <summary>
    /// The grammar as an augmented transition network: one start and one stop state per non-terminal,
    /// joined by transitions that match a terminal, call a non-terminal, or move on without input.
    /// A synthesized parser builds one of these from its tables; the predictions it caches are shared by
    /// every parse that uses it.
    /// </summary>
    public sealed class Atn
    {
        /// <summary>
        /// A transition taken without input: <c>Epsilon, target, 0</c>
        /// </summary>
        public const int Epsilon = 0;
        /// <summary>
        /// A transition that matches a terminal: <c>Match, terminal, target</c>
        /// </summary>
        public const int Match = 1;
        /// <summary>
        /// A transition that calls a non-terminal: <c>Call, non-terminal, the state to return to</c>
        /// </summary>
        public const int Call = 2;

        // pseudo states: the start rule has ended, so only end of input can follow; end of input was matched
        internal const int Accept = -1;
        internal const int Final = -2;

        readonly int[][] _transitions; // per state: kind, a, b for each transition
        readonly int[] _ruleStart;
        readonly int[] _ruleStop;
        readonly int[] _stopRule; // per state: the non-terminal it ends, or -1
        readonly int[][] _follow; // per non-terminal: the states its calls return to
        readonly int[] _decisions;
        readonly int _startRule;
        readonly int _endOfInput;
        internal readonly Dfa[] Dfas;

        /// <param name="startRule">The start symbol's id</param>
        /// <param name="endOfInput">The end of input symbol's id</param>
        /// <param name="rules">For each non-terminal in id order, its start state then its stop state</param>
        /// <param name="states">For each state in order, its number of transitions, then kind, a, b for each</param>
        /// <param name="decisions">For each adaptive decision, the state whose epsilon transitions are its branches, in order</param>
        public Atn(int startRule, int endOfInput, int[] rules, int[] states, int[] decisions)
        {
            _startRule = startRule;
            _endOfInput = endOfInput;
            _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
            var list = new List<int[]>();
            for (var i = 0; i < states.Length;)
            {
                var n = states[i++] * 3;
                var t = new int[n];
                Array.Copy(states, i, t, 0, n);
                i += n;
                list.Add(t);
            }
            _transitions = list.ToArray();
            var ruleCount = rules.Length / 2;
            _ruleStart = new int[ruleCount];
            _ruleStop = new int[ruleCount];
            _stopRule = new int[_transitions.Length];
            for (var i = 0; i < _stopRule.Length; ++i) _stopRule[i] = -1; // Array.Fill isn't in netstandard2.0
            for (var r = 0; r < ruleCount; ++r)
            {
                _ruleStart[r] = rules[r * 2];
                _ruleStop[r] = rules[r * 2 + 1];
                _stopRule[_ruleStop[r]] = r;
            }
            var follow = new List<int>[ruleCount];
            for (var r = 0; r < ruleCount; ++r) follow[r] = new List<int>();
            foreach (var t in _transitions)
                for (var i = 0; i < t.Length; i += 3)
                    if (Call == t[i]) follow[t[i + 1]].Add(t[i + 2]);
            _follow = new int[ruleCount][];
            for (var r = 0; r < ruleCount; ++r) _follow[r] = follow[r].ToArray();
            Dfas = new Dfa[decisions.Length];
            for (var i = 0; i < Dfas.Length; ++i) Dfas[i] = new Dfa();
        }

        #region Configurations
        /// <summary>
        /// The return states still to be visited, as a graph-structured stack: each entry is a return state
        /// and the stack beneath it, and stacks that meet at the same configuration are merged rather than
        /// kept apart, which keeps an ambiguous grammar from multiplying them. Return state -1 is the empty
        /// stack: in SLL it means "anywhere this rule is called from" and absorbs anything merged with it;
        /// in full LL it means the start rule has ended. Stacks come from a <see cref="StackPool"/>, which
        /// makes each distinct one a single instance, so they compare by reference.
        /// </summary>
        internal sealed class Stack
        {
            public static readonly Stack Empty = new Stack(new[] { -1 }, new Stack?[] { null });
            public readonly int[] Returns; // ascending, so -1 comes first
            public readonly Stack?[] Parents;
            readonly int _hash;
            internal Stack(int[] returns, Stack?[] parents)
            {
                Returns = returns;
                Parents = parents;
                var h = 17;
                for (var i = 0; i < returns.Length; ++i)
                    h = unchecked((h * 31 + returns[i]) * 31 + (parents[i]?._hash ?? 0));
                _hash = h;
            }
            public bool IsEmpty => 1 == Returns.Length && -1 == Returns[0];
            // equality is identity: the pool hands out one instance per distinct stack
            public override int GetHashCode() => _hash;
        }

        /// <summary>
        /// Makes and merges stacks, keeping one instance of each. Not thread safe: each DFA has its own,
        /// and each full-context prediction makes one.
        /// </summary>
        internal sealed class StackPool
        {
            sealed class _Shallow : IEqualityComparer<Stack>
            {
                public bool Equals(Stack? x, Stack? y)
                {
                    if (ReferenceEquals(x, y)) return true;
                    if (null == x || null == y || x.GetHashCode() != y.GetHashCode() || x.Returns.Length != y.Returns.Length) return false;
                    for (var i = 0; i < x.Returns.Length; ++i)
                        if (x.Returns[i] != y.Returns[i] || !ReferenceEquals(x.Parents[i], y.Parents[i])) return false;
                    return true;
                }
                public int GetHashCode(Stack obj) => obj.GetHashCode();
            }
            readonly Dictionary<Stack, Stack> _stacks = new(new _Shallow());
            readonly Dictionary<(Stack, Stack), Stack> _merges = new();
            public StackPool() { _stacks.Add(Stack.Empty, Stack.Empty); }
            Stack _Get(int[] returns, Stack?[] parents)
            {
                var s = new Stack(returns, parents);
                if (_stacks.TryGetValue(s, out var existing)) return existing;
                _stacks.Add(s, s);
                return s;
            }
            public Stack Push(int returnState, Stack parent) => _Get(new[] { returnState }, new Stack?[] { parent });
            public Stack Merge(Stack a, Stack b, bool wildcard)
            {
                if (ReferenceEquals(a, b)) return a;
                if (wildcard && (a.IsEmpty || b.IsEmpty)) return Stack.Empty;
                if (_merges.TryGetValue((a, b), out var done)) return done;
                var returns = new List<int>(a.Returns.Length + b.Returns.Length);
                var parents = new List<Stack?>(returns.Capacity);
                int i = 0, j = 0;
                while (i < a.Returns.Length || j < b.Returns.Length)
                {
                    if (j == b.Returns.Length || (i < a.Returns.Length && a.Returns[i] < b.Returns[j]))
                    {
                        returns.Add(a.Returns[i]);
                        parents.Add(a.Parents[i++]);
                    }
                    else if (i == a.Returns.Length || b.Returns[j] < a.Returns[i])
                    {
                        returns.Add(b.Returns[j]);
                        parents.Add(b.Parents[j++]);
                    }
                    else
                    {
                        // the same return state: merge what lies beneath
                        returns.Add(a.Returns[i]);
                        var pa = a.Parents[i++];
                        var pb = b.Parents[j++];
                        parents.Add(null == pa || null == pb ? null : Merge(pa, pb, wildcard));
                    }
                }
                var result = _Get(returns.ToArray(), parents.ToArray());
                _merges[(a, b)] = result;
                _merges[(b, a)] = result;
                return result;
            }
        }

        // an ATN state reached on behalf of one alternative, with the stacks it would return through
        internal readonly struct Config : IEquatable<Config>
        {
            public readonly int State;
            public readonly int Alt;
            public readonly Stack Stack;
            public Config(int state, int alt, Stack stack)
            {
                State = state;
                Alt = alt;
                Stack = stack;
            }
            public bool Equals(Config rhs) => State == rhs.State && Alt == rhs.Alt && ReferenceEquals(Stack, rhs.Stack);
            public override bool Equals(object? obj) => obj is Config c && Equals(c);
            public override int GetHashCode() => unchecked((State * 397 + Alt) * 397 + Stack.GetHashCode());
        }

        // configurations with one merged stack per state and alternative
        internal sealed class ConfigSet : IEquatable<ConfigSet>
        {
            readonly Dictionary<(int State, int Alt), Stack> _items = new();
            readonly bool _wildcard;
            int _hash;
            bool _hashed;
            public ConfigSet(StackPool pool, bool fullContext)
            {
                Pool = pool;
                _wildcard = !fullContext;
            }
            public StackPool Pool { get; }
            public int Count => _items.Count;
            public void Add(Config c)
            {
                var key = (c.State, c.Alt);
                _items[key] = _items.TryGetValue(key, out var s) ? Pool.Merge(s, c.Stack, _wildcard) : c.Stack;
            }
            public IEnumerable<Config> Items
            {
                get
                {
                    foreach (var kv in _items)
                        yield return new Config(kv.Key.State, kv.Key.Alt, kv.Value);
                }
            }
            public bool Equals(ConfigSet? rhs)
            {
                if (null == rhs || rhs.Count != Count || rhs.GetHashCode() != GetHashCode()) return false;
                foreach (var kv in _items)
                    if (!rhs._items.TryGetValue(kv.Key, out var s) || !ReferenceEquals(s, kv.Value)) return false;
                return true;
            }
            public override bool Equals(object? obj) => Equals(obj as ConfigSet);
            public override int GetHashCode()
            {
                if (!_hashed)
                {
                    var h = 0;
                    foreach (var c in Items) h = unchecked(h + c.GetHashCode());
                    _hash = h;
                    _hashed = true;
                }
                return _hash;
            }
        }

        internal sealed class DfaState
        {
            public readonly ConfigSet Configs;
            /// <summary>
            /// The alternative every configuration agrees on, or -1
            /// </summary>
            public readonly int Prediction;
            /// <summary>
            /// SLL can't go further: the decision needs the parser's call stack
            /// </summary>
            public readonly bool Conflict;
            public readonly Dictionary<int, DfaState?> Edges = new();
            public DfaState(ConfigSet configs, int prediction, bool conflict)
            {
                Configs = configs;
                Prediction = prediction;
                Conflict = conflict;
            }
        }

        internal sealed class Dfa
        {
            public DfaState? Start;
            public readonly StackPool Pool = new();
            readonly Dictionary<ConfigSet, DfaState> _states = new();
            public DfaState Intern(ConfigSet configs)
            {
                if (_states.TryGetValue(configs, out var s)) return s;
                var alt = UniqueAlt(configs);
                s = new DfaState(configs, alt, -1 == alt && SllConflict(configs));
                _states.Add(configs, s);
                return s;
            }
        }
        #endregion

        #region Simulation
        /// <summary>
        /// The configurations at the start of a decision, one alternative per branch
        /// </summary>
        internal ConfigSet StartSet(int decision, Stack stack, StackPool pool, bool fullContext)
        {
            var result = new ConfigSet(pool, fullContext);
            var busy = new HashSet<Config>();
            var d = _transitions[_decisions[decision]];
            for (int i = 0, alt = 0; i < d.Length; i += 3, ++alt)
                _Closure(new Config(d[i + 1], alt, stack), result, busy, fullContext);
            return result;
        }

        /// <summary>
        /// Where the configurations go on the given terminal
        /// </summary>
        internal ConfigSet Move(ConfigSet configs, int symbol, bool fullContext)
        {
            var result = new ConfigSet(configs.Pool, fullContext);
            var busy = new HashSet<Config>();
            foreach (var c in configs.Items)
            {
                if (Accept == c.State)
                {
                    if (symbol == _endOfInput) result.Add(new Config(Final, c.Alt, Stack.Empty));
                    continue;
                }
                if (Final == c.State) continue;
                var t = _transitions[c.State];
                for (var i = 0; i < t.Length; i += 3)
                    if (Match == t[i] && symbol == t[i + 1])
                        _Closure(new Config(t[i + 2], c.Alt, c.Stack), result, busy, fullContext);
            }
            return result;
        }

        // follows epsilon, call and return moves, keeping the configurations that wait on a terminal
        void _Closure(Config c, ConfigSet result, HashSet<Config> busy, bool fullContext)
        {
            if (!busy.Add(c)) return;
            var rule = _stopRule[c.State];
            if (-1 < rule)
            {
                var stack = c.Stack;
                for (var i = 0; i < stack.Returns.Length; ++i)
                {
                    if (-1 < stack.Returns[i])
                    {
                        _Closure(new Config(stack.Returns[i], c.Alt, stack.Parents[i]!), result, busy, fullContext);
                        continue;
                    }
                    // the stack is empty. With the parser's real stack, that means the start rule is done.
                    // Without it, the rule could have been called from anywhere, so go everywhere it returns to.
                    if (fullContext || rule == _startRule)
                        result.Add(new Config(Accept, c.Alt, Stack.Empty));
                    if (!fullContext)
                        foreach (var f in _follow[rule])
                            _Closure(new Config(f, c.Alt, Stack.Empty), result, busy, fullContext);
                }
                return;
            }
            var t = _transitions[c.State];
            var waits = false;
            for (var i = 0; i < t.Length; i += 3)
            {
                switch (t[i])
                {
                    case Epsilon:
                        _Closure(new Config(t[i + 1], c.Alt, c.Stack), result, busy, fullContext);
                        break;
                    case Call:
                        _Closure(new Config(_ruleStart[t[i + 1]], c.Alt, result.Pool.Push(t[i + 2], c.Stack)), result, busy, fullContext);
                        break;
                    default:
                        waits = true;
                        break;
                }
            }
            if (waits) result.Add(c);
        }

        /// <summary>
        /// The alternative every configuration agrees on, or -1
        /// </summary>
        internal static int UniqueAlt(ConfigSet configs)
        {
            var alt = -1;
            foreach (var c in configs.Items)
            {
                if (-1 == alt) alt = c.Alt;
                else if (alt != c.Alt) return -1;
            }
            return alt;
        }

        static Dictionary<(int, Stack), SortedSet<int>> _Groups(ConfigSet configs)
        {
            var result = new Dictionary<(int, Stack), SortedSet<int>>();
            foreach (var c in configs.Items)
            {
                if (!result.TryGetValue((c.State, c.Stack), out var alts))
                    result.Add((c.State, c.Stack), alts = new SortedSet<int>());
                alts.Add(c.Alt);
            }
            return result;
        }

        /// <summary>
        /// SLL can't separate the alternatives: wherever they are, more than one is there, and no state
        /// belongs to a single alternative. Looking further without the call stack won't help.
        /// </summary>
        internal static bool SllConflict(ConfigSet configs)
        {
            foreach (var alts in _Groups(configs).Values)
                if (2 > alts.Count) return false;
            var byState = new Dictionary<int, SortedSet<int>>();
            foreach (var c in configs.Items)
            {
                if (!byState.TryGetValue(c.State, out var alts))
                    byState.Add(c.State, alts = new SortedSet<int>());
                alts.Add(c.Alt);
            }
            foreach (var alts in byState.Values)
                if (1 == alts.Count) return false;
            return true;
        }

        /// <summary>
        /// With the full call stack: if every position is shared by the same alternatives, they will match
        /// whatever follows in the same way, so the input really is ambiguous. Returns the earliest of them
        /// (which keeps a loop going and takes an optional), or -1 if it isn't ambiguous yet.
        /// </summary>
        internal static int Ambiguity(ConfigSet configs)
        {
            SortedSet<int>? first = null;
            foreach (var alts in _Groups(configs).Values)
            {
                if (2 > alts.Count) return -1;
                if (null == first) first = alts;
                else if (!first.SetEquals(alts)) return -1;
            }
            return null == first ? -1 : first.Min;
        }
        #endregion
    }

    /// <summary>
    /// The base of a synthesized ALL(*) parser: unlimited lookahead, and adaptive prediction for the
    /// decisions that need it
    /// </summary>
    public abstract class AllStarParserBase : ParserBase
    {
        readonly LookAheadEnumerator<Token> _tokens;
        readonly Atn _atn;
        readonly List<int> _returns = new();

        protected AllStarParserBase(IEnumerable<Token> tokens, ParserTables tables, Atn atn) : base(tables)
        {
            _atn = atn ?? throw new ArgumentNullException(nameof(atn));
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
        protected Token Peek(int n)
        {
            var t = _tokens.TryPeek(n, out var x) ? x : EndToken(CurrentUnclassified);
            if (!Tables.HasContextual) return t;
            // classified in the context of the token before it, as the lexer produced it
            var previous = 1 == n ? CurrentUnclassified : _tokens.TryPeek(n - 1, out var p) ? p : t;
            return Classified(t, previous);
        }

        /// <summary>
        /// Marks the start of a non-terminal, for error recovery and prediction. returnState is the ATN
        /// state the caller continues from afterward, or -1 for the start symbol.
        /// </summary>
        protected void Enter(int nonTerminal, int returnState)
        {
            Enter(nonTerminal);
            _returns.Add(returnState);
        }
        protected override void Leave()
        {
            base.Leave();
            if (0 < _returns.Count) _returns.RemoveAt(_returns.Count - 1);
        }

        int _LookAt(int n) => (0 == n ? Current : Peek(n)).SymbolId;

        /// <summary>
        /// Chooses a branch of an adaptive decision by looking as far ahead as it takes. Returns the
        /// branch (for a loop or optional, 0 to go in and 1 not to), or -1 if no branch can match.
        /// </summary>
        protected int AdaptivePredict(int decision)
        {
            var dfa = _atn.Dfas[decision];
            lock (dfa)
            {
                var s = dfa.Start ??= dfa.Intern(_atn.StartSet(decision, Atn.Stack.Empty, dfa.Pool, false));
                for (var i = 0; ; ++i)
                {
                    var t = _LookAt(i);
                    if (!s.Edges.TryGetValue(t, out var next))
                    {
                        var reach = _atn.Move(s.Configs, t, false);
                        next = 0 == reach.Count ? null : dfa.Intern(reach);
                        s.Edges.Add(t, next);
                    }
                    // SLL considers every context, so if nothing matches here, nothing matches with the real one
                    if (null == next) return -1;
                    s = next;
                    if (-1 < s.Prediction) return s.Prediction;
                    if (s.Conflict) break;
                }
            }
            return _FullContextPredict(decision);
        }

        // the same simulation, with the parser's actual call stack, and not cached since that differs every time
        int _FullContextPredict(int decision)
        {
            var pool = new Atn.StackPool();
            var stack = Atn.Stack.Empty;
            foreach (var r in _returns)
                if (-1 < r) stack = pool.Push(r, stack);
            var configs = _atn.StartSet(decision, stack, pool, true);
            for (var i = 0; ; ++i)
            {
                configs = _atn.Move(configs, _LookAt(i), true);
                if (0 == configs.Count) return -1;
                var alt = Atn.UniqueAlt(configs);
                if (-1 < alt) return alt;
                alt = Atn.Ambiguity(configs);
                if (-1 < alt) return alt;
            }
        }
    }
}
