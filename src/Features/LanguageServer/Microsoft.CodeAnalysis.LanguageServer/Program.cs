﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.CommandLine;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis.Contracts.Telemetry;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.BrokeredServices;
using Microsoft.CodeAnalysis.LanguageServer.BrokeredServices.Services.HelloWorld;
using Microsoft.CodeAnalysis.LanguageServer.HostWorkspace;
using Microsoft.CodeAnalysis.LanguageServer.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Logging;
using Microsoft.CodeAnalysis.LanguageServer.StarredSuggestions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

// Setting the title can fail if the process is run without a window, such
// as when launched detached from nodejs
try
{
    Console.Title = "Microsoft.CodeAnalysis.LanguageServer";
}
catch (IOException)
{
}

WindowsErrorReporting.SetErrorModeOnWindows();

var parser = CreateCommandLineParser();
return await parser.Parse(args).InvokeAsync(CancellationToken.None);

static async Task RunAsync(ServerConfiguration serverConfiguration, CancellationToken cancellationToken)
{
    // Before we initialize the LSP server we can't send LSP log messages.
    // Create a console logger as a fallback to use before the LSP server starts.
    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(serverConfiguration.MinimumLogLevel);
        builder.AddProvider(new LspLogMessageLoggerProvider(fallbackLoggerFactory:
            // Add a console logger as a fallback for when the LSP server has not finished initializing.
            LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(serverConfiguration.MinimumLogLevel);
                builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
                // The console logger outputs control characters on unix for colors which don't render correctly in VSCode.
                builder.AddSimpleConsole(formatterOptions => formatterOptions.ColorBehavior = LoggerColorBehavior.Disabled);
            })
        ));
    });

    if (serverConfiguration.LaunchDebugger)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Debugger.Launch() only works on Windows.
            _ = Debugger.Launch();
        }
        else
        {
            var logger = loggerFactory.CreateLogger<Program>();
            var timeout = TimeSpan.FromMinutes(1);
            logger.LogCritical($"Server started with process ID {Environment.ProcessId}");
            logger.LogCritical($"Waiting {timeout:g} for a debugger to attach");
            using var timeoutSource = new CancellationTokenSource(timeout);
            while (!Debugger.IsAttached && !timeoutSource.Token.IsCancellationRequested)
            {
                await Task.Delay(100, CancellationToken.None);
            }
        }
    }

    using var exportProvider = await ExportProviderBuilder.CreateExportProviderAsync(serverConfiguration.ExtensionAssemblyPaths, serverConfiguration.SharedDependenciesPath, loggerFactory);

    // The log file directory passed to us by VSCode might not exist yet, though its parent directory is guaranteed to exist.
    Directory.CreateDirectory(serverConfiguration.ExtensionLogDirectory);

    // Initialize the server configuration MEF exported value.
    exportProvider.GetExportedValue<ServerConfigurationFactory>().InitializeConfiguration(serverConfiguration);

    // Initialize the fault handler if it's available
    var telemetryReporter = exportProvider.GetExports<ITelemetryReporter>().SingleOrDefault()?.Value;
    RoslynLogger.Initialize(telemetryReporter, serverConfiguration.TelemetryLevel, serverConfiguration.SessionId);

    // Create the workspace first, since right now the language server will assume there's at least one Workspace
    var workspaceFactory = exportProvider.GetExportedValue<LanguageServerWorkspaceFactory>();

    var analyzerPaths = new DirectoryInfo(AppContext.BaseDirectory).GetFiles("*.dll")
        .Where(f => f.Name.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal) && !f.Name.Contains("LanguageServer", StringComparison.Ordinal))
        .Select(f => f.FullName)
        .ToImmutableArray();

    await workspaceFactory.InitializeSolutionLevelAnalyzersAsync(analyzerPaths);

    var serviceBrokerFactory = exportProvider.GetExportedValue<ServiceBrokerFactory>();
    StarredCompletionAssemblyHelper.InitializeInstance(serverConfiguration.StarredCompletionsPath, loggerFactory, serviceBrokerFactory);

    // TODO: Remove, the path should match exactly. Workaround for https://devdiv.visualstudio.com/DevDiv/_workitems/edit/1830914.
    Microsoft.CodeAnalysis.EditAndContinue.EditAndContinueMethodDebugInfoReader.IgnoreCaseWhenComparingDocumentNames = Path.DirectorySeparatorChar == '\\';

    var server = new LanguageServerHost(Console.OpenStandardInput(), Console.OpenStandardOutput(), exportProvider, loggerFactory.CreateLogger(nameof(LanguageServerHost)));
    server.Start();

    try
    {
        await server.WaitForExitAsync();
    }
    finally
    {
        // After the LSP server shutdown, report session wide telemetry
        RoslynLogger.ShutdownAndReportSessionTelemetry();

        // Server has exited, cancel our service broker service
        await serviceBrokerFactory.ShutdownAndWaitForCompletionAsync();
    }
}

static CliRootCommand CreateCommandLineParser()
{
    var debugOption = new CliOption<bool>("--debug")
    {
        Description = "Flag indicating if the debugger should be launched on startup.",
        Required = false,
        DefaultValueFactory = _ => false,
    };
    var brokeredServicePipeNameOption = new CliOption<string?>("--brokeredServicePipeName")
    {
        Description = "The name of the pipe used to connect to a remote process (if one exists).",
        Required = false,
    };

    var logLevelOption = new CliOption<LogLevel>("--logLevel")
    {
        Description = "The minimum log verbosity.",
        Required = true,
    };
    var starredCompletionsPathOption = new CliOption<string?>("--starredCompletionComponentPath")
    {
        Description = "The location of the starred completion component (if one exists).",
        Required = false,
    };

    var telemetryLevelOption = new CliOption<string?>("--telemetryLevel")
    {
        Description = "Telemetry level, Defaults to 'off'. Example values: 'all', 'crash', 'error', or 'off'.",
        Required = false,
    };
    var extensionLogDirectoryOption = new CliOption<string>("--extensionLogDirectory")
    {
        Description = "The directory where we should write log files to",
        Required = true,
    };

    var sessionIdOption = new CliOption<string?>("--sessionId")
    {
        Description = "Session Id to use for telemetry",
        Required = false
    };

    var sharedDependenciesOption = new CliOption<string?>("--sharedDependencies")
    {
        Description = "Full path of the directory containing shared assemblies (optional).",
        Required = false
    };

    var extensionAssemblyPathsOption = new CliOption<string[]?>("--extension", "--extensions") // TODO: remove plural form
    {
        Description = "Full paths of extension assemblies to load (optional).",
        Required = false
    };

    var rootCommand = new CliRootCommand()
    {
        debugOption,
        brokeredServicePipeNameOption,
        logLevelOption,
        starredCompletionsPathOption,
        telemetryLevelOption,
        sessionIdOption,
        sharedDependenciesOption,
        extensionAssemblyPathsOption,
        extensionLogDirectoryOption
    };
    rootCommand.SetAction((parseResult, cancellationToken) =>
    {
        var launchDebugger = parseResult.GetValue(debugOption);
        var logLevel = parseResult.GetValue(logLevelOption);
        var starredCompletionsPath = parseResult.GetValue(starredCompletionsPathOption);
        var telemetryLevel = parseResult.GetValue(telemetryLevelOption);
        var sessionId = parseResult.GetValue(sessionIdOption);
        var sharedDependenciesPath = parseResult.GetValue(sharedDependenciesOption);
        var extensionAssemblyPaths = parseResult.GetValue(extensionAssemblyPathsOption) ?? Array.Empty<string>();
        var extensionLogDirectory = parseResult.GetValue(extensionLogDirectoryOption)!;

        var serverConfiguration = new ServerConfiguration(
            LaunchDebugger: launchDebugger,
            MinimumLogLevel: logLevel,
            StarredCompletionsPath: starredCompletionsPath,
            TelemetryLevel: telemetryLevel,
            SessionId: sessionId,
            SharedDependenciesPath: sharedDependenciesPath,
            ExtensionAssemblyPaths: extensionAssemblyPaths,
            ExtensionLogDirectory: extensionLogDirectory);

        return RunAsync(serverConfiguration, cancellationToken);
    });

    return rootCommand;
}

