using System;
using System.Threading.Tasks;

namespace Parsley.LanguageServer
{
    /// <summary>
    /// Runs the XBNF language server over stdin and stdout. Editors start it; it isn't meant to be run by hand.
    /// </summary>
    static class Program
    {
        static async Task<int> Main(string[] args)
        {
            var rpc = new JsonRpc(Console.OpenStandardInput(), Console.OpenStandardOutput());
            return await new XbnfLanguageServer(rpc).RunAsync();
        }
    }
}
