using EntregasApi.Migrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

MigratorOptions? options;
try
{
    options = MigratorOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Error de argumentos: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(MigratorOptions.HelpText);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(MigratorOptions.HelpText);
    return 0;
}

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(opt =>
    {
        opt.SingleLine = true;
        opt.TimestampFormat = "HH:mm:ss ";
    });
    builder.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Information);
});
var logger = loggerFactory.CreateLogger<MigratorRunner>();

var runner = new MigratorRunner(options, logger, loggerFactory);
try
{
    var exitCode = await runner.RunAsync(CancellationToken.None);
    return exitCode;
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Migrador abortado con excepcion no controlada");
    return 1;
}
