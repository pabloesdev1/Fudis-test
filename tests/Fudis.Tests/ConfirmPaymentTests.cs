using System.Net;
using System.Net.Http.Json;
using Fudis.Api.Data;
using Fudis.Api.Domain;
using Fudis.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Xunit;

namespace Fudis.Tests;

public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string TestDb =
        "Server=localhost,1433;Database=Fudis_Tests;User Id=sa;Password=Changethistestpass0!;TrustServerCertificate=True";

    async Task IAsyncLifetime.InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FudisDbContext>();
        await db.Database.EnsureDeletedAsync();   // empieza siempre con una BD vacía
        await db.Database.MigrateAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", TestDb);
        builder.ConfigureServices(services =>
        {
            var worker = services.SingleOrDefault(d => d.ImplementationType == typeof(OutboxWorker));
            if (worker is not null) services.Remove(worker);
        });
    }
}

public class ConfirmPaymentTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ConfirmPaymentTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string Txn() => Guid.NewGuid().ToString("N");

    private async Task<int> SeedOrder(int? driverId = 10, decimal fee = 3.00m)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FudisDbContext>();
        var o = new Order { DriverId = driverId, DeliveryFee = fee };
        db.Orders.Add(o);
        await db.SaveChangesAsync();
        return o.Id;
    }

    private Task<HttpResponseMessage> Post(int orderId, string txn) =>
        _client.PostAsJsonAsync("/confirm-payment",
            new { orderId, transactionId = txn, amount = 15.00m, currency = "USD" });

    private async Task<(OrderStatus Status, int Earnings, int Outbox, decimal? EarningAmount)> State(int orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FudisDbContext>();
        var status = (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId)).Status;
        var earnings = await db.DriverEarnings.Where(e => e.OrderId == orderId).ToListAsync();
        var payload = $"{{\"orderId\":{orderId}}}";
        var outbox = await db.OutboxMessages.CountAsync(m => m.Payload == payload);
        return (status, earnings.Count, outbox, earnings.FirstOrDefault()?.Amount);
    }

    [Fact]
    public async Task Pago_valido_marca_pagado_crea_ganancia_y_outbox()
    {
        var id = await SeedOrder();
        var r = await Post(id, Txn());

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var s = await State(id);
        Assert.Equal(OrderStatus.Paid, s.Status);
        Assert.Equal(1, s.Earnings);
        Assert.Equal(2.10m, s.EarningAmount);   // 70 % de 3.00
        Assert.Equal(1, s.Outbox);
    }

    [Fact]
    public async Task Duplicado_secuencial_no_genera_segunda_ganancia()
    {
        var id = await SeedOrder();
        var txn = Txn();

        Assert.Equal(HttpStatusCode.OK, (await Post(id, txn)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(id, txn)).StatusCode);

        var s = await State(id);
        Assert.Equal(1, s.Earnings);
        Assert.Equal(1, s.Outbox);
    }

    [Fact]
    public async Task Veinte_notificaciones_simultaneas_generan_una_sola_ganancia()
    {
        var id = await SeedOrder();
        var txn = Txn();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Post(id, txn)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var s = await State(id);
        Assert.Equal(OrderStatus.Paid, s.Status);
        Assert.Equal(1, s.Earnings);
        Assert.Equal(1, s.Outbox);
    }

    [Fact]
    public async Task Otra_transaccion_sobre_pedido_ya_pagado_devuelve_409_sin_ganancia_extra()
    {
        var id = await SeedOrder();
        Assert.Equal(HttpStatusCode.OK, (await Post(id, Txn())).StatusCode);

        var r = await Post(id, Txn());

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(1, (await State(id)).Earnings);
    }

    [Fact]
    public async Task Pedido_sin_repartidor_queda_pagado_sin_ganancia()
    {
        var id = await SeedOrder(driverId: null);
        var r = await Post(id, Txn());

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var s = await State(id);
        Assert.Equal(OrderStatus.Paid, s.Status);
        Assert.Equal(0, s.Earnings);
    }

    [Fact]
    public async Task Pedido_inexistente_devuelve_404()
    {
        var r = await Post(999999, Txn());
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Cuerpo_invalido_devuelve_400()
    {
        var r = await _client.PostAsJsonAsync("/confirm-payment", new { orderId = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}