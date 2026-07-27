using EntregasApi.Models;
using EntregasApi.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntregasApi.Tests;

public class TandaDrawTurnsTests
{
    [Fact]
    public async Task DrawTurnsAsync_RejectsTandaWithRegisteredPayment()
    {
        using var ctx = TestDbContextFactory.Create();
        var tandaId = Guid.NewGuid();
        var participants = CreateParticipants(tandaId);
        ctx.TandaParticipants.AddRange(participants);
        ctx.TandaPayments.Add(new TandaPayment
        {
            BusinessId = 1,
            ParticipantId = participants[0].Id,
            WeekNumber = 1,
            AmountPaid = 100m,
            PaymentDate = DateTime.UtcNow,
            IsVerified = true
        });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var service = new TandaService(ctx);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DrawTurnsAsync(tandaId));

        Assert.Equal(
            "No se pueden sortear los turnos porque la tanda ya tiene pagos registrados o entregas realizadas.",
            error.Message);
        Assert.Equal(
            new[] { 1, 2 },
            await GetTurnsAsync(ctx, participants));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DrawTurnsAsync_RejectsTandaWithRecordedDelivery(
        bool isDelivered,
        bool hasDeliveryDate)
    {
        using var ctx = TestDbContextFactory.Create();
        var tandaId = Guid.NewGuid();
        var participants = CreateParticipants(tandaId);
        participants[0].IsDelivered = isDelivered;
        participants[0].DeliveryDate = hasDeliveryDate ? DateTime.UtcNow.Date : null;
        ctx.TandaParticipants.AddRange(participants);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var service = new TandaService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DrawTurnsAsync(tandaId));

        Assert.Equal(
            new[] { 1, 2 },
            await GetTurnsAsync(ctx, participants));
    }

    private static List<TandaParticipant> CreateParticipants(Guid tandaId)
    {
        return
        [
            new TandaParticipant
            {
                Id = Guid.NewGuid(),
                BusinessId = 1,
                TandaId = tandaId,
                CustomerId = 1,
                AssignedTurn = 1
            },
            new TandaParticipant
            {
                Id = Guid.NewGuid(),
                BusinessId = 1,
                TandaId = tandaId,
                CustomerId = 2,
                AssignedTurn = 2
            }
        ];
    }

    private static async Task<int[]> GetTurnsAsync(
        EntregasApi.Data.AppDbContext ctx,
        IReadOnlyList<TandaParticipant> participants)
    {
        var participantIds = participants.Select(p => p.Id).ToList();
        return await ctx.TandaParticipants
            .Where(p => participantIds.Contains(p.Id))
            .OrderBy(p => p.Id == participants[0].Id ? 0 : 1)
            .Select(p => p.AssignedTurn)
            .ToArrayAsync();
    }
}
