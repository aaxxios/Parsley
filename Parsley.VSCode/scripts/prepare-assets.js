// Copies what the extension shares with the rest of the repository into it, since a .vsix can only contain
// files under the extension folder: the TextMate grammar and language configuration (from
// Parsley.VisualStudio/Grammars), the license, and the language server, published with dotnet.
const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const repo = path.resolve(root, '..');

const syntaxes = path.join(root, 'syntaxes');
fs.mkdirSync(syntaxes, { recursive: true });
for (const f of ['xbnf.tmLanguage.json', 'xbnf.language-configuration.json'])
	fs.copyFileSync(path.join(repo, 'Parsley.VisualStudio', 'Grammars', f), path.join(syntaxes, f));
fs.copyFileSync(path.join(repo, 'LICENSE.txt'), path.join(root, 'LICENSE.txt'));

const server = path.join(root, 'server');
fs.rmSync(server, { recursive: true, force: true });
// framework-dependent and not for one runtime identifier, so the same files run anywhere .NET 8 does; on
// Windows the build also leaves an .exe launcher
execFileSync('dotnet', [
	'publish', path.join(repo, 'Parsley.LanguageServer', 'Parsley.LanguageServer.csproj'),
	'-c', 'Release', '-o', server, '-p:DebugType=none', '-p:GenerateDocumentationFile=false',
], { stdio: 'inherit' });
console.log('assets ready');
