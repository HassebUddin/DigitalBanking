using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DigitalBanking.BuildingBlocks.Regions;

// Every banking table carries a fencing trigger (installed by the cluster coordinator) that refuses writes
// on a database without a valid primary lease. SQL Server rejects OUTPUT without INTO on tables with
// triggers, so EF is told to read generated values (row versions) back with a separate SELECT instead.
public sealed class FencedTablesModelCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(entity => entity.GetTableName() is not null && !entity.IsOwned()).ToList())
        {
            modelBuilder.Entity(entityType.ClrType).ToTable(table => table.UseSqlOutputClause(false));
        }
    }
}
