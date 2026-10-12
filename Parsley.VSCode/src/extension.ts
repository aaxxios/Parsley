// The XBNF extension: the grammar and language configuration (in package.json) color and edit .xbnf files,
// and this starts the Parsley language server for everything else.
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { LanguageClient, LanguageClientOptions, ServerOptions, TransportKind } from 'vscode-languageclient/node';

const serverName = 'Parsley.LanguageServer';
let client: LanguageClient | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
	context.subscriptions.push(
		vscode.commands.registerCommand('parsley.xbnf.restartServer', async () => {
			await stopServer();
			await startServer(context);
		}),
		vscode.workspace.onDidChangeConfiguration(async e => {
			if (e.affectsConfiguration('parsley.xbnf.dotnetPath') || e.affectsConfiguration('parsley.xbnf.serverPath')) {
				await stopServer();
				await startServer(context);
			}
		}),
	);
	await startServer(context);
}

export async function deactivate(): Promise<void> {
	await stopServer();
}

/**
 * How to run the server: the apphost in the extension's server folder when it's there and works on this
 * platform, otherwise its dll through dotnet. A serverPath setting overrides the dll.
 */
export function serverOptions(extensionPath: string, config: { dotnetPath?: string; serverPath?: string }, platform: string = process.platform): ServerOptions {
	const dotnet = config.dotnetPath?.trim() || 'dotnet';
	const custom = config.serverPath?.trim();
	const dll = custom || path.join(extensionPath, 'server', serverName + '.dll');
	const cwd = path.dirname(dll);
	// the packed server is framework-dependent; on Windows its .exe launcher finds the runtime without dotnet on the PATH
	const exe = path.join(cwd, serverName + '.exe');
	if (!custom && !config.dotnetPath?.trim() && platform === 'win32' && fs.existsSync(exe))
		return { command: exe, transport: TransportKind.stdio, options: { cwd } };
	return { command: dotnet, args: [dll], transport: TransportKind.stdio, options: { cwd } };
}

async function startServer(context: vscode.ExtensionContext): Promise<void> {
	const config = vscode.workspace.getConfiguration('parsley.xbnf');
	const options = serverOptions(context.extensionPath, {
		dotnetPath: config.get<string>('dotnetPath'),
		serverPath: config.get<string>('serverPath'),
	});
	const clientOptions: LanguageClientOptions = {
		documentSelector: [{ language: 'xbnf' }],
		// grammars on disk that an open one @imports are read by the server itself, so nothing to watch here
	};
	client = new LanguageClient('parsley.xbnf', 'Parsley XBNF', options, clientOptions);
	try {
		await client.start();
	} catch (e) {
		const message = e instanceof Error ? e.message : String(e);
		const choice = await vscode.window.showErrorMessage(
			`The XBNF language server didn't start: ${message}. It needs the .NET 8 runtime (or later); ` +
			`set parsley.xbnf.dotnetPath if dotnet isn't on the PATH. Highlighting still works without it.`,
			'Open Settings');
		if (choice === 'Open Settings')
			await vscode.commands.executeCommand('workbench.action.openSettings', 'parsley.xbnf');
		client = undefined;
	}
}

async function stopServer(): Promise<void> {
	const c = client;
	client = undefined;
	if (c) {
		try {
			await c.stop();
		} catch {
			// it may already be gone
		}
	}
}
