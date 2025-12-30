using System.Threading;
using System.Threading.Tasks;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Blake.LanguageServer.Handlers;

public class BlakeDefinitionHandler : DefinitionHandlerBase
{
    private readonly BlakeWorkspace _workspace;

    public BlakeDefinitionHandler(BlakeWorkspace workspace)
    {
        _workspace = workspace;
    }

    public override Task<LocationOrLocationLinks?> Handle(DefinitionParams request, CancellationToken cancellationToken)
    {
        var document = _workspace.GetDocument(request.TextDocument.Uri);
        if (document == null)
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        // Check context
        var context = document.GetContextAtPosition(
            request.Position.Line,
            request.Position.Character
        );

        if (context != BlakeContext.MetaBlockCode && context != BlakeContext.QuasiQuoteSplice)
        {
            return Task.FromResult<LocationOrLocationLinks?>(null);
        }

        // TODO: Implement go-to-definition using Roslyn SymbolFinder
        // For now, return null (no definition found)
        return Task.FromResult<LocationOrLocationLinks?>(null);
    }

    protected override DefinitionRegistrationOptions CreateRegistrationOptions(
        DefinitionCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new DefinitionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("blake")
        };
    }
}
