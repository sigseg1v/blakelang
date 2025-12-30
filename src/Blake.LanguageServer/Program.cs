using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Server;
using LspServer = OmniSharp.Extensions.LanguageServer.Server.LanguageServer;

namespace Blake.LanguageServer;

class Program
{
    static async Task Main(string[] args)
    {
        // Create LSP server that communicates over stdio
        var server = await LspServer.From(options => options
            .WithInput(Console.OpenStandardInput())
            .WithOutput(Console.OpenStandardOutput())
            .ConfigureLogging(x => x
                .AddLanguageProtocolLogging()
                .SetMinimumLevel(LogLevel.Debug))
            .WithServices(ConfigureServices)
            .WithHandler<Handlers.BlakeTextDocumentSyncHandler>()
            .WithHandler<Handlers.BlakeCompletionHandler>()
            .WithHandler<Handlers.BlakeHoverHandler>()
            .WithHandler<Handlers.BlakeDefinitionHandler>()
        );

        await server.WaitForExit;
    }

    static void ConfigureServices(IServiceCollection services)
    {
        // Register BlakeWorkspace as singleton to track open documents
        services.AddSingleton<BlakeWorkspace>();
    }
}
