using EntregasApi.Migrator;
using Xunit;

namespace EntregasApi.Migrator.Tests;

public class MigratorOptionsTests
{
    [Fact]
    public void Parse_minimo_source_y_dest_devuelve_opciones_validas()
    {
        var opts = MigratorOptions.Parse(new[] { "--source", "Host=src", "--dest", "Host=dst" });
        Assert.False(opts.Verify);
        Assert.False(opts.Verbose);
        Assert.False(opts.ShowHelp);
        Assert.Equal("Host=src", opts.Source);
        Assert.Equal("Host=dst", opts.Destination);
        Assert.Null(opts.RbMpToken);
        Assert.Null(opts.EvidenceMapPath);
    }

    [Fact]
    public void Parse_con_todas_las_opciones_asigna_todos_los_campos()
    {
        var opts = MigratorOptions.Parse(new[]
        {
            "--source", "Host=src",
            "--dest", "Host=dst",
            "--rb-mp-token", "APP_USR-123",
            "--evidence-map", "./map.json",
            "--verify",
            "--verbose",
        });
        Assert.True(opts.Verify);
        Assert.True(opts.Verbose);
        Assert.Equal("APP_USR-123", opts.RbMpToken);
        Assert.Equal("./map.json", opts.EvidenceMapPath);
    }

    [Fact]
    public void Parse_alias_dest_acepta_destination()
    {
        var opts = MigratorOptions.Parse(new[] { "--source", "Host=src", "--destination", "Host=dst" });
        Assert.Equal("Host=dst", opts.Destination);
    }

    [Fact]
    public void Parse_help_devuelve_showHelp_true_y_sin_validar_campos()
    {
        var opts = MigratorOptions.Parse(new[] { "--help" });
        Assert.True(opts.ShowHelp);
    }

    [Fact]
    public void Parse_alias_help_corto_tambien_funciona()
    {
        var opts = MigratorOptions.Parse(new[] { "-h" });
        Assert.True(opts.ShowHelp);
    }

    [Fact]
    public void Parse_sin_source_lanza_ArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => MigratorOptions.Parse(new[] { "--dest", "Host=dst" }));
        Assert.Contains("--source", ex.Message);
    }

    [Fact]
    public void Parse_sin_dest_lanza_ArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => MigratorOptions.Parse(new[] { "--source", "Host=src" }));
        Assert.Contains("--dest", ex.Message);
    }

    [Fact]
    public void Parse_source_y_dest_iguales_lanza_para_evitar_doble_corrida()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            MigratorOptions.Parse(new[] { "--source", "Host=x", "--dest", "Host=x" }));
        Assert.Contains("mismo", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void Parse_argumento_desconocido_lanza_ArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            MigratorOptions.Parse(new[] { "--source", "Host=src", "--dest", "Host=dst", "--pepe" }));
        Assert.Contains("desconocido", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void Parse_bandera_sin_valor_lanza_ArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            MigratorOptions.Parse(new[] { "--source" }));
        Assert.Contains("requiere un valor", ex.Message);
    }
}
