using System.ComponentModel.Composition;

using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

#pragma warning disable CS0649 // MEF reads the attributes; the fields are never assigned

namespace Parsley.VisualStudio
{
    /// <summary>
    /// The XBNF content type and its .xbnf extension. Based on the code-remote content type, so the language
    /// client handles it and the TextMate grammar colors it.
    /// </summary>
    internal static class XbnfContentDefinition
    {
        public const string ContentType = "xbnf";

        [Export]
        [Name(ContentType)]
        [BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]
        internal static ContentTypeDefinition XbnfContentTypeDefinition;

        [Export]
        [FileExtension(".xbnf")]
        [ContentType(ContentType)]
        internal static FileExtensionToContentTypeDefinition XbnfFileExtensionDefinition;
    }
}
