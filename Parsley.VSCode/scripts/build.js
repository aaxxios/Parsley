// Bundles the extension and vscode-languageclient into dist/extension.js, so the .vsix needs no node_modules
const esbuild = require('esbuild');
const path = require('path');

const production = process.argv.includes('--production');
esbuild.build({
	entryPoints: [path.join(__dirname, '..', 'src', 'extension.ts')],
	outfile: path.join(__dirname, '..', 'dist', 'extension.js'),
	bundle: true,
	platform: 'node',
	format: 'cjs',
	target: 'node18',
	external: ['vscode'],
	sourcemap: !production,
	minify: production,
	logLevel: 'info',
}).catch(() => process.exit(1));
