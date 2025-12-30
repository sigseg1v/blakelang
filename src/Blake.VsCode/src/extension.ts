import * as vscode from 'vscode';
import * as path from 'path';
import { LanguageClient, LanguageClientOptions, ServerOptions, TransportKind } from 'vscode-languageclient/node';

let metaBlockDecorationType: vscode.TextEditorDecorationType;
let client: LanguageClient;

export function activate(context: vscode.ExtensionContext) {
    // Start the LSP server
    startLanguageServer(context);

    // Create decoration type for meta-blocks
    metaBlockDecorationType = vscode.window.createTextEditorDecorationType({
        backgroundColor: 'rgba(255, 255, 200, 0.1)', // Light yellow background with 10% opacity
        isWholeLine: false
    });

    // Decorate visible editors on activation
    vscode.window.visibleTextEditors.forEach(editor => {
        if (editor.document.languageId === 'blake') {
            updateDecorations(editor);
        }
    });

    // Update decorations when active editor changes
    context.subscriptions.push(
        vscode.window.onDidChangeActiveTextEditor(editor => {
            if (editor && editor.document.languageId === 'blake') {
                updateDecorations(editor);
            }
        })
    );

    // Update decorations when document changes
    context.subscriptions.push(
        vscode.workspace.onDidChangeTextDocument(event => {
            const editor = vscode.window.activeTextEditor;
            if (editor && event.document === editor.document && editor.document.languageId === 'blake') {
                updateDecorations(editor);
            }
        })
    );

    // Update decorations when configuration changes
    context.subscriptions.push(
        vscode.workspace.onDidChangeConfiguration(e => {
            if (e.affectsConfiguration('blake.enableMetaBlockHighlighting')) {
                vscode.window.visibleTextEditors.forEach(editor => {
                    if (editor.document.languageId === 'blake') {
                        updateDecorations(editor);
                    }
                });
            }
        })
    );
}


interface MetaBlock {
    start: number;
    end: number;
    quasiQuotes: QuasiQuote[];
}

interface QuasiQuote {
    start: number;
    end: number;
    splices: { start: number; end: number }[];
}

function updateDecorations(editor: vscode.TextEditor) {
    const config = vscode.workspace.getConfiguration('blake');
    const enabled = config.get<boolean>('enableMetaBlockHighlighting', true);

    if (!enabled) {
        editor.setDecorations(metaBlockDecorationType, []);
        return;
    }

    const text = editor.document.getText();
    const metaBlocks = findMetaBlocks(text);
    const ranges: vscode.Range[] = [];

    for (const block of metaBlocks) {
        // Add ranges for meta-block content, excluding quasi-quotes
        const blockRanges = getMetaBlockRanges(editor.document, block);
        ranges.push(...blockRanges);
    }

    editor.setDecorations(metaBlockDecorationType, ranges);
}

function findMetaBlocks(text: string): MetaBlock[] {
    const blocks: MetaBlock[] = [];
    const regex = /@\{\|/g;
    let match: RegExpExecArray | null;

    while ((match = regex.exec(text)) !== null) {
        const start = match.index;
        const end = findMetaBlockEnd(text, start);

        if (end !== -1) {
            const blockText = text.substring(start, end);
            const quasiQuotes = findQuasiQuotes(blockText, start);

            blocks.push({ start, end, quasiQuotes });
        }
    }

    return blocks;
}

function findMetaBlockEnd(text: string, start: number): number {
    let depth = 1;
    let i = start + 3; // Skip @{|
    let inQuasiQuote = false;

    while (i < text.length && depth > 0) {
        const char = text[i];
        const nextChar = text[i + 1];

        // Handle escaped characters
        if (char === '`' && (i === 0 || text[i - 1] !== '`')) {
            inQuasiQuote = !inQuasiQuote;
        }

        // Only count delimiters outside of quasi-quotes
        if (!inQuasiQuote) {
            if (char === '@' && nextChar === '{' && text[i + 2] === '|') {
                depth++;
                i += 2;
            } else if (char === '|' && nextChar === '}') {
                depth--;
                if (depth === 0) {
                    return i + 2; // Include |}
                }
                i++;
            }
        }

        i++;
    }

    return -1;
}

function findQuasiQuotes(blockText: string, blockStart: number): QuasiQuote[] {
    const quotes: QuasiQuote[] = [];
    let inQuote = false;
    let quoteStart = -1;

    for (let i = 0; i < blockText.length; i++) {
        const char = blockText[i];
        const prevChar = i > 0 ? blockText[i - 1] : '';

        // Check for unescaped backtick
        if (char === '`' && prevChar !== '`') {
            if (!inQuote) {
                quoteStart = blockStart + i;
                inQuote = true;
            } else {
                const quoteEnd = blockStart + i + 1;
                const quoteText = blockText.substring(quoteStart - blockStart, i + 1);
                const splices = findSplices(quoteText, quoteStart);
                quotes.push({ start: quoteStart, end: quoteEnd, splices });
                inQuote = false;
            }
        }
    }

    return quotes;
}

function findSplices(quoteText: string, quoteStart: number): { start: number; end: number }[] {
    const splices: { start: number; end: number }[] = [];
    const regex = /@\(/g;
    let match: RegExpExecArray | null;

    while ((match = regex.exec(quoteText)) !== null) {
        const start = quoteStart + match.index;
        const end = findSpliceEnd(quoteText, match.index);

        if (end !== -1) {
            splices.push({ start, end: quoteStart + end });
        }
    }

    return splices;
}

function findSpliceEnd(text: string, start: number): number {
    let depth = 1;
    let i = start + 2; // Skip @(

    while (i < text.length && depth > 0) {
        const char = text[i];

        if (char === '(') {
            depth++;
        } else if (char === ')') {
            depth--;
            if (depth === 0) {
                return i + 1; // Include )
            }
        }

        i++;
    }

    return -1;
}

function getMetaBlockRanges(document: vscode.TextDocument, block: MetaBlock): vscode.Range[] {
    const ranges: vscode.Range[] = [];
    const text = document.getText();

    // Start from @{| (include the opening delimiter)
    let currentPos = block.start;

    // Sort quasi-quotes by position
    const sortedQuotes = [...block.quasiQuotes].sort((a, b) => a.start - b.start);

    for (const quote of sortedQuotes) {
        // Add range from current position to start of quasi-quote (including opening `)
        if (currentPos < quote.start + 1) {
            const startPos = document.positionAt(currentPos);
            const endPos = document.positionAt(quote.start + 1); // Include opening `
            ranges.push(new vscode.Range(startPos, endPos));
        }

        // Add ranges for splices inside the quasi-quote
        for (const splice of quote.splices) {
            const startPos = document.positionAt(splice.start);
            const endPos = document.positionAt(splice.end);
            ranges.push(new vscode.Range(startPos, endPos));
        }

        // Add range for closing ` delimiter
        const closingBacktickPos = quote.end - 1;
        const closingStartPos = document.positionAt(closingBacktickPos);
        const closingEndPos = document.positionAt(quote.end);
        ranges.push(new vscode.Range(closingStartPos, closingEndPos));

        // Skip the quasi-quote content
        currentPos = quote.end;
    }

    // Add range from last quasi-quote (or block start) to end of block (include |})
    if (currentPos < block.end) {
        const startPos = document.positionAt(currentPos);
        const endPos = document.positionAt(block.end);
        ranges.push(new vscode.Range(startPos, endPos));
    }

    return ranges;
}

function startLanguageServer(context: vscode.ExtensionContext) {
    try {
        // Path to the LSP server DLL
        const serverDllPath = context.asAbsolutePath(path.join('server', 'Blake.LanguageServer.dll'));

        console.log('Blake LSP: Starting language server...');
        console.log('Blake LSP: Server path:', serverDllPath);

        // Server options: launch the LSP server using dotnet
        const serverOptions: ServerOptions = {
            run: { command: 'dotnet', args: [serverDllPath], transport: TransportKind.stdio },
            debug: { command: 'dotnet', args: [serverDllPath], transport: TransportKind.stdio }
        };

        // Client options: configure which files to watch
        const clientOptions: LanguageClientOptions = {
            documentSelector: [{ scheme: 'file', language: 'blake' }],
            synchronize: {
                // Notify the server about file changes to '.blake' files
                fileEvents: vscode.workspace.createFileSystemWatcher('**/*.blake')
            },
            outputChannelName: 'Blake Language Server'
        };

        // Create and start the language client
        client = new LanguageClient(
            'blakeLanguageServer',
            'Blake Language Server',
            serverOptions,
            clientOptions
        );

        client.onDidChangeState((e) => {
            console.log('Blake LSP: State changed:', e);
        });

        client.start().then(() => {
            console.log('Blake LSP: Language server started successfully');
        }).catch((error) => {
            console.error('Blake LSP: Failed to start language server:', error);
            vscode.window.showErrorMessage('Blake Language Server failed to start: ' + error.message);
        });
    } catch (error: any) {
        console.error('Blake LSP: Error in startLanguageServer:', error);
        vscode.window.showErrorMessage('Blake Language Server error: ' + error.message);
    }
}

export function deactivate(): Thenable<void> | undefined {
    // Clean up decorations
    if (metaBlockDecorationType) {
        metaBlockDecorationType.dispose();
    }

    // Stop LSP client
    if (!client) {
        return undefined;
    }
    return client.stop();
}
