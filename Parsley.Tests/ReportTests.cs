using System;
using System.IO;
using System.Linq;

namespace Parsley.Tests
{
	public static class ReportTests
	{
		static string _Report(string xbnf, ParserEngine engine = ParserEngine.Auto)
			=> GrammarReport.Create(GrammarAnalysis.Analyze(Grammar.Parse(xbnf, "r.xbnf"), engine));

		public static void ReportsTheEngineAndWhy()
		{
			var json = GrammarReport.Create(GrammarAnalysis.Analyze(Grammar.ReadFrom(Path.Combine(T.Dir, "json.xbnf"))));
			T.Check(json.Contains("Engine: LL(1) (chosen automatically)") && json.Contains("every decision is made by the current token"), "LL(1) and why");
			T.Check(json.Contains("Every decision is a one-token switch or if."), "nothing costly listed");
			T.Check(json.Contains("ParsleyRuntime.cs, LL1ParserBase.cs"), "runtime files");

			var llk = _Report("P= S x | y; S= a b c | a b d; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; x=\"x\"; y=\"y\";");
			T.Check(llk.Contains("Engine: LL(k)") && llk.Contains("the deepest needs 3 (S #1)"), "LL(k) and the deepest decision");
			T.Check(llk.Contains("S #1 alternation needs 3 tokens: a b c | a b d"), "names what needs it");
			T.Check(llk.Split('\n').Any(l => l.Contains("LL(1)") && l.Contains("no") && l.Contains("1 alternation can't be decided") && !l.Contains("greedily")), "LL(1) compared, without phantom loops");
			T.Check(llk.Contains("1 predictor method"), "code cost");

			var all = _Report("S= \"if\" e \"then\" S [ \"else\" S ] | s; e=\"e\"; s=\"s\";");
			T.Check(all.Contains("Engine: ALL(*)") && all.Contains("Needs ALL(*) because of:") && all.Contains("S #2 optional"), "ALL(*) and the culprit");
			T.Check(all.Contains("a <policy> on the decision"), "suggests a policy");
			T.Check(all.Split('\n').Any(l => l.TrimStart().StartsWith("LL(1)") && l.Contains("partly")), "LL(1) only with a default policy");
			T.Check(all.Contains("Network ") && all.Contains("states"), "network size");

			var policy = _Report("S= \"if\" e \"then\" S [ \"else\" S ]<policy=greedy> | s; e=\"e\"; s=\"s\";");
			T.Check(policy.Contains("Engine: LL(1)") && policy.Contains("1 written in the grammar"), "an explicit policy keeps it LL(1), and is counted");

			var forced = _Report("P= S x | y; S= a b c | a b d; a=\"a\"; b=\"b\"; c=\"c\"; d=\"d\"; x=\"x\"; y=\"y\";", ParserEngine.AllStar);
			T.Check(forced.Contains("Engine: ALL(*) (forced)") && forced.Contains("ALL(*), LL(3) would do"), "forced engine noted");
		}

		public static void ReportsProductionsAndSymbols()
		{
			var r = _Report("E= E \"+\" T | T; T<collapsed>= n | \"(\" E \")\"; Unused<nowarn>= n; n='[0-9]+'; ws<hidden>= '[ ]+';");
			T.Check(r.Contains("left recursion rewritten") && r.Contains("1 unreachable"), "symbol summary");
			var line = r.Split('\n').First(l => l.TrimStart().StartsWith("T "));
			T.Check(line.Contains("collapsed") && line.Contains("recursive") && line.Contains("n \"(\""), "production row: " + line);
			T.Check(r.Contains("1 hidden"), "terminal summary");
		}

		public static void ReportsAGrammarWithErrors()
		{
			var r = _Report("S= { [ a ] }; a=\"a\";");
			T.Check(r.Contains("Grammar r.xbnf") && r.Contains("1 error, so it can't be analyzed further"), "errors stop the report early: " + r);
		}
	}
}
