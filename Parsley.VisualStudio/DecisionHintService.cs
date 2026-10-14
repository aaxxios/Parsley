using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace Parsley.VisualStudio
{
    /// <summary>
    /// A decision that needs more than one token of lookahead, as the server reports it: where it is in the
    /// document (zero-based line and UTF-16 character), its glyph and its tooltip
    /// </summary>
    internal sealed class DecisionHintInfo
    {
        public int Line { get; set; }
        public int Character { get; set; }
        public string Label { get; set; }
        public bool AllStar { get; set; }
        public string Tooltip { get; set; }
    }

    /// <summary>
    /// The language server's parsley/decisionHints request and parsley/decisionHintsChanged notification, which the
    /// glyph taggers use. The server sends the notification after each full analysis of a file: when it's
    /// opened and when it's saved.
    /// </summary>
    internal static class DecisionHintService
    {
        static JsonRpc s_rpc;

        /// <summary>
        /// Raised with the path of a file whose hints changed, or null when they all may have: the server has
        /// (re)started. Raised on whatever thread the message arrived on.
        /// </summary>
        public static event Action<string> Changed;

        internal static void Attach(JsonRpc rpc)
        {
            s_rpc = rpc;
            Changed?.Invoke(null);
        }

        internal static void OnServerChanged(string uri)
        {
            string path;
            try { path = new Uri(uri).LocalPath; }
            catch (UriFormatException) { return; }
            Changed?.Invoke(path);
        }

        public static bool SamePath(string a, string b)
        {
            if (null == a || null == b) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        /// <summary>
        /// The hints for an open file, or none when the server isn't running yet or hasn't analyzed it
        /// </summary>
        public static async Task<IReadOnlyList<DecisionHintInfo>> GetAsync(string path, CancellationToken ct)
        {
            var result = new List<DecisionHintInfo>();
            var rpc = s_rpc;
            if (null == rpc || null == path) return result;
            JToken reply;
            try
            {
                reply = await rpc.InvokeWithParameterObjectAsync<JToken>("parsley/decisionHints",
                    new { textDocument = new { uri = new Uri(path).AbsoluteUri } }, ct);
            }
            catch (Exception ex) when (ex is RemoteInvocationException || ex is ConnectionLostException
                || ex is ObjectDisposedException || ex is OperationCanceledException || ex is UriFormatException)
            {
                return result;
            }
            if (!(reply is JArray hints)) return result;
            foreach (var h in hints)
            {
                result.Add(new DecisionHintInfo
                {
                    Line = (int?)h["line"] ?? -1,
                    Character = (int?)h["character"] ?? -1,
                    Label = (string)h["label"] ?? "",
                    AllStar = (bool?)h["allStar"] ?? false,
                    Tooltip = (string)h["tooltip"] ?? "",
                });
            }
            return result;
        }
    }

    /// <summary>
    /// What the server's notifications are delivered to. It has to be public for StreamJsonRpc to call it.
    /// </summary>
    public sealed class DecisionHintTarget
    {
        [JsonRpcMethod("parsley/decisionHintsChanged")]
        public void DecisionHintsChanged(string uri) => DecisionHintService.OnServerChanged(uri);
    }
}
