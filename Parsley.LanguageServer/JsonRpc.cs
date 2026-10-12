using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Parsley.LanguageServer
{
    /// <summary>
    /// JSON-RPC 2.0 over the LSP base protocol: each message is a Content-Length header, a blank line and UTF-8 JSON
    /// </summary>
    sealed class JsonRpc
    {
        readonly Stream _in;
        readonly Stream _out;
        readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        public JsonRpc(Stream input, Stream output) { _in = input; _out = output; }

        /// <summary>
        /// Reads the next message, or null at the end of the input
        /// </summary>
        public async Task<JsonObject?> ReadAsync(CancellationToken ct)
        {
            var length = -1;
            while (true)
            {
                var line = await _ReadHeaderLineAsync(ct);
                if (null == line) return null;
                if (0 == line.Length)
                {
                    if (length >= 0) break;
                    continue; // stray blank line before any header
                }
                var colon = line.IndexOf(':');
                if (colon > 0 && line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line.Substring(colon + 1).Trim());
            }
            var body = new byte[length];
            var read = 0;
            while (read < length)
            {
                var n = await _in.ReadAsync(body.AsMemory(read, length - read), ct);
                if (0 == n) return null;
                read += n;
            }
            return JsonNode.Parse(body) as JsonObject;
        }

        async Task<string?> _ReadHeaderLineAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                var n = await _in.ReadAsync(one.AsMemory(0, 1), ct);
                if (0 == n) return sb.Length > 0 ? sb.ToString() : null;
                var c = (char)one[0];
                if (c == '\n') return sb.ToString().TrimEnd('\r');
                sb.Append(c);
            }
        }

        public async Task WriteAsync(JsonObject message)
        {
            message["jsonrpc"] = "2.0";
            var body = Encoding.UTF8.GetBytes(message.ToJsonString());
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await _writeLock.WaitAsync();
            try
            {
                await _out.WriteAsync(header);
                await _out.WriteAsync(body);
                await _out.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public Task RespondAsync(JsonNode? id, JsonNode? result) =>
            WriteAsync(new JsonObject { ["id"] = id?.DeepClone(), ["result"] = result });

        public Task RespondErrorAsync(JsonNode? id, int code, string message) =>
            WriteAsync(new JsonObject { ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

        public Task NotifyAsync(string method, JsonNode? @params) =>
            WriteAsync(new JsonObject { ["method"] = method, ["params"] = @params });
    }
}
