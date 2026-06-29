using EntregasApi.Migrator.Migration;
using EntregasApi.Migrator.Verify;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EntregasApi.Migrator;

/// <summary>
/// Punto de entrada logico del migrador. Decide entre modo preflight, copia y verify.
/// </summary>
public sealed class MigratorRunner
{
    private readonly MigratorOptions _options;
    private readonly ILogger<MigratorRunner> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public MigratorRunner(MigratorOptions options, ILogger<MigratorRunner> logger, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (_options.Preflight)
        {
            _logger.LogInformation("EntregasApi.Migrator - modo PREFLIGHT (sin escrituras)");
            var preflight = new PreflightChecker(_loggerFactory.CreateLogger<PreflightChecker>());
            return await preflight.RunAsync(_options, cancellationToken);
        }

        _logger.LogInformation("EntregasApi.Migrator - modo {Mode}", _options.Verify ? "VERIFY (sin escrituras)" : "COPIA");

        if (_options.Verify)
        {
            var verifier = new Verifier(_options, _loggerFactory.CreateLogger<Verifier>());
            var (passed, failures) = await verifier.RunAsync(cancellationToken);
            _logger.LogInformation("Veredicto global: {Verdict}", passed ? "PASS" : "FAIL");
            return passed ? 0 : 3;
        }

        var copier = new Copier(_options, _loggerFactory.CreateLogger<Copier>(), _loggerFactory);
        await copier.RunAsync(cancellationToken);
        _logger.LogInformation("Copia finalizada con exito.");
        return 0;
    }
}
