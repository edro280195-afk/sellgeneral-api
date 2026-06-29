using EntregasApi.Migrator.Migration;
using Xunit;

namespace EntregasApi.Migrator.Tests;

public class MigrationPlanTests
{
    [Fact]
    public void Plan_incluye_las_tablas_esperadas_del_corte()
    {
        var tablas = MigrationPlan.TablesInOrder.Select(t => t.TableName).ToHashSet();
        // Raices de identidad
        Assert.Contains("Businesses", tablas);
        Assert.Contains("Accounts", tablas);
        Assert.Contains("Memberships", tablas);
        // Raices tenant-ownadas
        Assert.Contains("Clients", tablas);
        Assert.Contains("DeliveryRoutes", tablas);
        Assert.Contains("Suppliers", tablas);
        Assert.Contains("LoyaltyRewards", tablas);
        Assert.Contains("FcmTokens", tablas);
        Assert.Contains("PushSubscriptions", tablas);
        // Tandas
        Assert.Contains("products", tablas);
        Assert.Contains("tandas", tablas);
        Assert.Contains("tanda_participants", tablas);
        Assert.Contains("payments", tablas);
        // Sorteos
        Assert.Contains("raffles", tablas);
        Assert.Contains("raffle_participants", tablas);
        Assert.Contains("raffle_entries", tablas);
        Assert.Contains("raffle_draws", tablas);
        // Pedidos
        Assert.Contains("Orders", tablas);
        Assert.Contains("OrderItems", tablas);
        Assert.Contains("OrderPayments", tablas);
        Assert.Contains("OrderPackages", tablas);
        Assert.Contains("Deliveries", tablas);
        Assert.Contains("DeliveryEvidences", tablas);
    }

    [Fact]
    public void Plan_cubre_las_cuatro_colisiones_criticas()
    {
        var tablas = MigrationPlan.TablesInOrder.Select(t => t.TableName).ToHashSet();
        // products (Tanda) != Products (POS)
        Assert.Contains("Products", tablas);
        Assert.Contains("products", tablas);
        // payments (TandaPayment) != OrderPayments
        Assert.Contains("OrderPayments", tablas);
        Assert.Contains("payments", tablas);
        // orders (no existe snake_case) vs Orders PascalCase
        Assert.Contains("Orders", tablas);
        // tandas snake_case
        Assert.Contains("tandas", tablas);
    }

    [Fact]
    public void Plan_incluye_las_7_tablas_vacias_del_corte()
    {
        var tablas = MigrationPlan.TablesInOrder.Select(t => t.TableName).ToHashSet();
        foreach (var vacia in MigrationPlan.EmptyTables)
        {
            Assert.Contains(vacia, tablas);
        }
    }

    [Fact]
    public void Cuentas_esperadas_son_4_con_1_owner_1_driver_2_scaner()
    {
        Assert.Equal(4, MigrationPlan.ExpectedRowCounts["Accounts"]);
        Assert.Equal(4, MigrationPlan.ExpectedRowCounts["Memberships"]);
        Assert.Equal(1, MigrationPlan.ExpectedMembershipRoles["Owner"]);
        Assert.Equal(1, MigrationPlan.ExpectedMembershipRoles["Driver"]);
        Assert.Equal(2, MigrationPlan.ExpectedMembershipRoles["Scaner"]);
    }

    [Fact]
    public void Spot_check_incluye_los_4_ids_de_orden_referenciados_en_el_plan()
    {
        Assert.Contains(118, MigrationPlan.SpotCheckOrderIds);
        Assert.Contains(168, MigrationPlan.SpotCheckOrderIds);
        Assert.Contains(190, MigrationPlan.SpotCheckOrderIds);
        Assert.Contains(970, MigrationPlan.SpotCheckOrderIds);
    }

    [Fact]
    public void Tablas_con_Guid_PK_no_estan_en_int_pk()
    {
        foreach (var guidTable in MigrationPlan.GuidPrimaryKeyTables)
        {
            Assert.False(MigrationPlan.IntPrimaryKeyTables.ContainsKey(guidTable),
                $"{guidTable} tiene Guid PK; no debe estar en IntPrimaryKeyTables.");
        }
    }

    [Fact]
    public void Quote_escapa_comillas_y_envuelve_en_doble_comillas()
    {
        Assert.Equal("\"tabla\"", MigrationPlan.Quote("tabla"));
        Assert.Equal("\"weird\"\"name\"", MigrationPlan.Quote("weird\"name"));
    }

    [Fact]
    public void OrphanChecks_tienen_al_menos_una_entrada_por_FK_comun()
    {
        // El plan exige >= 9 chequeos de huerfanos.
        Assert.True(MigrationPlan.OrphanChecks.Count >= 9,
            $"OrphanChecks tiene {MigrationPlan.OrphanChecks.Count} entradas; se esperaban >= 9.");
    }
}
