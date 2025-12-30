using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;

namespace Blake.LanguageServer.Handlers;

public class BlakeTextDocumentSyncHandler : TextDocumentSyncHandlerBase
{
    private readonly BlakeWorkspace _workspace;
    private readonly ILanguageServerFacade _languageServer;

    public BlakeTextDocumentSyncHandler(BlakeWorkspace workspace, ILanguageServerFacade languageServer)
    {
        _workspace = workspace;
        _languageServer = languageServer;
    }

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        return new TextDocumentAttributes(uri, "blake");
    }

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var text = request.TextDocument.Text;

        _workspace.OpenDocument(uri, text);

        // Trigger diagnostics
        PublishDiagnostics(uri);

        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        // Get the full text from the last content change
        var change = request.ContentChanges.LastOrDefault();
        if (change != null)
        {
            _workspace.UpdateDocument(uri, change.Text);

            // Trigger diagnostics
            PublishDiagnostics(uri);
        }

        return Unit.Task;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        // Re-publish diagnostics on save
        PublishDiagnostics(request.TextDocument.Uri);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        _workspace.CloseDocument(request.TextDocument.Uri);

        // Clear diagnostics
        _languageServer.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = request.TextDocument.Uri,
            Diagnostics = new Container<Diagnostic>()
        });

        return Unit.Task;
    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new TextDocumentSyncRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("blake"),
            Change = TextDocumentSyncKind.Full,
            Save = new SaveOptions { IncludeText = false }
        };
    }

    private void PublishDiagnostics(DocumentUri uri)
    {
        var document = _workspace.GetDocument(uri);
        if (document == null) return;

        var diagnostics = new System.Collections.Generic.List<Diagnostic>();

        try
        {
            // Try to parse the document
            var ast = document.GetOrParseAst();

            if (ast == null)
            {
                // Parse error occurred
                diagnostics.Add(new Diagnostic
                {
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 0, 1),
                    Severity = DiagnosticSeverity.Error,
                    Source = "Blake",
                    Message = "Failed to parse .blake file. Check meta-block syntax."
                });
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new Diagnostic
            {
                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 0, 1),
                Severity = DiagnosticSeverity.Error,
                Source = "Blake",
                Message = $"Parse error: {ex.Message}"
            });
        }

        _languageServer.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = uri,
            Diagnostics = new Container<Diagnostic>(diagnostics)
        });
    }
}
