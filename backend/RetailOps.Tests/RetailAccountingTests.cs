using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RetailOps.Api.Data;
using RetailOps.Api.Domain;
using Xunit;

namespace RetailOps.Tests;

public sealed class RetailAccountingTests : RetailApiTest
{
    [Theory]
    [InlineData(60)]
    [InlineData(20)]
    public async Task Purchase_receipt_posts_actual_payable_standard_inventory_and_signed_variance_once(int actualUnitCost)
    {
        using var fixture = await Retail(stock: 0);
        var order = await Ok(await Send(Client, "/api/orders", new
        {
            supplierId = fixture.Supplier, warehouseId = fixture.Warehouse,
            expectedDate = TodayWib(), notes = "Synthetic standard-cost purchase",
            lines = new[] { new { productId = fixture.Product, quantity = 3, unitCost = actualUnitCost } }
        }));
        var orderId = order.GetProperty("id").GetGuid();
        var lineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await Ok(await Send(Client, $"/api/orders/{orderId}/submit"));
        await Ok(await Send(fixture.Manager, $"/api/orders/{orderId}/approve"));
        var request = new { notes = "Synthetic verified delivery", lines = new[] { new { orderLineId = lineId, quantity = 3 } } };
        var key = Key();
        var received = await Ok(await Send(Client, $"/api/orders/{orderId}/receipts", request, idempotencyKey: key));
        var replay = await Ok(await Send(Client, $"/api/orders/{orderId}/receipts", request, idempotencyKey: key));
        var receiptId = received.GetProperty("receiptId").GetGuid();
        Assert.Equal(receiptId, replay.GetProperty("receiptId").GetGuid());
        Assert.True(replay.GetProperty("replayed").GetBoolean());

        var payable = actualUnitCost * 3m;
        var variance = payable - 120m;
        var journal = await Journal("PurchaseReceipt", receiptId);
        Assert.Equal(fixture.Branch, journal.GetProperty("branchId").GetGuid());
        AssertPosting(journal, ("1200", 120m, 0m), ("2000", 0m, payable),
            ("5100", Math.Max(variance, 0m), Math.Max(-variance, 0m)));
        Assert.Single((await Get("/api/retail/journals")).EnumerateArray());
        Assert.Equal(3, await Stock(fixture.Product, fixture.Warehouse));

        var report = await AccountingReport();
        Assert.Equal(payable, Balance(report, "2000"));
        Assert.Equal(variance, report.GetProperty("varianceExpense").GetDecimal());
        Assert.Equal(-variance, report.GetProperty("netProfit").GetDecimal());
        Assert.Equal(-variance, report.GetProperty("balanceSheet").GetProperty("retainedEarnings").GetDecimal());
        AssertBalanced(report);
        await AssertInventoryReconciles(report);

        var overReceipt = new { notes = "Rejected extra delivery", lines = new[] { new { orderLineId = lineId, quantity = 1 } } };
        Assert.Equal(HttpStatusCode.Conflict,
            (await Send(Client, $"/api/orders/{orderId}/receipts", overReceipt, idempotencyKey: Key())).StatusCode);
        Assert.Equal(3, await Stock(fixture.Product, fixture.Warehouse));
        Assert.Single((await Get("/api/retail/journals")).EnumerateArray());
    }

    [Fact]
    public async Task Adjustments_and_transfers_reconcile_positions_to_inventory_without_transfer_profit()
    {
        using var fixture = await Retail(stock: 10);
        var opening = Assert.Single((await Get("/api/retail/journals")).EnumerateArray());
        AssertPosting(opening, ("1200", 400m, 0m), ("3000", 0m, 400m));

        var decrease = await Adjust(fixture.Product, fixture.Warehouse, -2);
        AssertPosting(await Journal("Adjustment", decrease), ("5100", 80m, 0m), ("1200", 0m, 80m));
        var transfer = await Ok(await Send(Client, "/api/inventory/transfers", new
        {
            productId = fixture.Product, fromWarehouseId = fixture.Warehouse,
            toWarehouseId = fixture.OtherWarehouse, quantity = 3, reason = "Synthetic branch replenishment"
        }));
        var transferJournal = await Journal("Transfer", transfer.GetProperty("referenceId").GetGuid());
        Assert.Equal(JsonValueKind.Null, transferJournal.GetProperty("branchId").ValueKind);
        AssertPosting(transferJournal, ("1200", 120m, 0m), ("1200", 0m, 120m));
        var increase = await Adjust(fixture.Product, fixture.OtherWarehouse, 1);
        AssertPosting(await Journal("Adjustment", increase), ("1200", 40m, 0m), ("3000", 0m, 40m));
        Assert.Equal(5, await Stock(fixture.Product, fixture.Warehouse));
        Assert.Equal(4, await Stock(fixture.Product, fixture.OtherWarehouse));

        var report = await AccountingReport();
        Assert.Equal(360m, Balance(report, "1200"));
        Assert.Equal(440m, Balance(report, "3000"));
        Assert.Equal(80m, report.GetProperty("varianceExpense").GetDecimal());
        Assert.Equal(-80m, report.GetProperty("netProfit").GetDecimal());
        Assert.Equal(0m, report.GetProperty("costOfGoodsSold").GetDecimal());
        AssertBalanced(report);
        await AssertInventoryReconciles(report);
        Assert.Equal(80m, (await AccountingReport(fixture.Branch)).GetProperty("varianceExpense").GetDecimal());
        Assert.Equal(0m, (await AccountingReport(fixture.OtherBranch)).GetProperty("varianceExpense").GetDecimal());

        Assert.Equal(HttpStatusCode.Conflict, (await Send(Client, "/api/inventory/transfers", new
        {
            productId = fixture.Product, fromWarehouseId = fixture.Warehouse,
            toWarehouseId = fixture.OtherWarehouse, quantity = 6, reason = "Rejected excessive transfer"
        })).StatusCode);
        Assert.Equal(4, (await Get("/api/retail/journals")).GetArrayLength());
        Assert.Equal(5, await Stock(fixture.Product, fixture.Warehouse));
        Assert.Equal(4, await Stock(fixture.Product, fixture.OtherWarehouse));
    }

    [Fact]
    public async Task Zero_cost_inventory_movements_do_not_create_empty_journals()
    {
        using var fixture = await Retail(stock: 3, standardCost: 0m);
        await Adjust(fixture.Product, fixture.Warehouse, -1);
        await Ok(await Send(Client, "/api/inventory/transfers", new
        {
            productId = fixture.Product, fromWarehouseId = fixture.Warehouse,
            toWarehouseId = fixture.OtherWarehouse, quantity = 1, reason = "Synthetic zero-cost transfer"
        }));
        Assert.Equal(1, await Stock(fixture.Product, fixture.Warehouse));
        Assert.Equal(1, await Stock(fixture.Product, fixture.OtherWarehouse));
        Assert.Empty((await Get("/api/retail/journals")).EnumerateArray());
        var report = await AccountingReport();
        Assert.Equal(0m, Balance(report, "1200"));
        AssertBalanced(report);
        await AssertInventoryReconciles(report);
    }

    [Fact]
    public async Task Branch_profit_is_period_scoped_while_trial_balance_and_balance_sheet_are_organization_as_of()
    {
        using var fixture = await Retail(stock: 10);
        await Ok(await Send(Client, "/api/inventory/transfers", new
        {
            productId = fixture.Product, fromWarehouseId = fixture.Warehouse,
            toWarehouseId = fixture.OtherWarehouse, quantity = 3, reason = "Synthetic second-branch stock"
        }));
        var firstSale = await Ok(await Sell(fixture.Cashier, Command(fixture, quantity: 2)));
        var otherShift = await OpenShift(fixture.OtherCashier, fixture.OtherBranch);
        await Ok(await Sell(fixture.OtherCashier, Command(fixture, branch: fixture.OtherBranch,
            shift: otherShift.GetProperty("id").GetGuid())));
        var managerShift = await OpenShift(fixture.Manager, fixture.Branch);
        await Ok(await Send(fixture.Manager, $"/api/retail/sales/{firstSale.GetProperty("id").GetGuid()}/returns", new
        {
            shiftId = managerShift.GetProperty("id").GetGuid(), reason = "Synthetic customer refund",
            lines = new[] { new { saleLineId = firstSale.GetProperty("lines")[0].GetProperty("id").GetGuid(), quantity = 1 } }
        }, idempotencyKey: Key()));

        var organization = await AccountingReport();
        var firstBranch = await AccountingReport(fixture.Branch);
        var otherBranch = await AccountingReport(fixture.OtherBranch);
        AssertProfit(organization, 2, 300m, 100m, 80m, 120m);
        AssertProfit(firstBranch, 1, 200m, 100m, 40m, 60m);
        AssertProfit(otherBranch, 1, 100m, 0m, 40m, 60m);
        Assert.Equal(fixture.Branch, firstBranch.GetProperty("branchId").GetGuid());
        Assert.Equal(fixture.OtherBranch, otherBranch.GetProperty("branchId").GetGuid());
        Assert.Equal(organization.GetProperty("trialBalance").GetRawText(), firstBranch.GetProperty("trialBalance").GetRawText());
        Assert.Equal(organization.GetProperty("trialBalance").GetRawText(), otherBranch.GetProperty("trialBalance").GetRawText());
        Assert.Equal(organization.GetProperty("balanceSheet").GetRawText(), firstBranch.GetProperty("balanceSheet").GetRawText());
        Assert.Equal(organization.GetProperty("balanceSheet").GetRawText(), otherBranch.GetProperty("balanceSheet").GetRawText());
        Assert.Equal(200m, Balance(organization, "1000"));
        Assert.Equal(320m, Balance(organization, "1200"));
        Assert.Equal(120m, organization.GetProperty("balanceSheet").GetProperty("retainedEarnings").GetDecimal());
        AssertBalanced(organization);
        await AssertInventoryReconciles(organization);

        var futureDay = WibDate().AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var future = await Report(fixture.Branch, futureDay, futureDay);
        AssertProfit(future, 0, 0m, 0m, 0m, 0m);
        Assert.Equal(organization.GetProperty("trialBalance").GetRawText(), future.GetProperty("trialBalance").GetRawText());
        Assert.Equal(120m, future.GetProperty("balanceSheet").GetProperty("retainedEarnings").GetDecimal());
        Assert.Equal(futureDay, future.GetProperty("balanceSheet").GetProperty("asOf").GetString());
        AssertBalanced(future);

        var pastDay = WibDate().AddDays(-2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var past = await Report(fixture.OtherBranch, pastDay, pastDay);
        AssertProfit(past, 0, 0m, 0m, 0m, 0m);
        Assert.All(past.GetProperty("trialBalance").EnumerateArray(), row =>
        {
            Assert.Equal(0m, row.GetProperty("debit").GetDecimal());
            Assert.Equal(0m, row.GetProperty("credit").GetDecimal());
        });
        Assert.Equal(0m, past.GetProperty("balanceSheet").GetProperty("retainedEarnings").GetDecimal());
        AssertBalanced(past);
    }

    [Fact]
    public async Task Standard_cost_cannot_change_while_stock_remains_in_any_warehouse()
    {
        using var fixture = await Retail(stock: 2);
        await Ok(await Send(Client, "/api/inventory/transfers", new
        {
            productId = fixture.Product, fromWarehouseId = fixture.Warehouse,
            toWarehouseId = fixture.OtherWarehouse, quantity = 2, reason = "Synthetic stock outside original branch"
        }));
        Assert.Equal(0, await Stock(fixture.Product, fixture.Warehouse));
        Assert.Equal(HttpStatusCode.Conflict, (await ChangeCost(fixture, 60m)).StatusCode);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(40m, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == fixture.Product)).StandardCost);
        }
        Assert.Equal(2, (await Get("/api/retail/journals")).GetArrayLength());
        await AssertInventoryReconciles(await AccountingReport());

        await Adjust(fixture.Product, fixture.OtherWarehouse, -2);
        var changed = await Ok(await ChangeCost(fixture, 60m));
        Assert.Equal(60m, changed.GetProperty("standardCost").GetDecimal());
        Assert.Equal(3, (await Get("/api/retail/journals")).GetArrayLength());
        var movementId = await Adjust(fixture.Product, fixture.Warehouse, 1);
        AssertPosting(await Journal("Adjustment", movementId), ("1200", 60m, 0m), ("3000", 0m, 60m));
        await AssertInventoryReconciles(await AccountingReport());
    }

    [Fact]
    public async Task Concurrent_cost_change_and_first_stock_adjustment_keep_inventory_at_the_committed_standard_cost()
    {
        using var fixture = await Retail(stock: 0);
        using var otherAdmin = await Login("admin@retailops.test");
        var responses = await Task.WhenAll(ChangeCost(fixture, 60m), Send(otherAdmin, "/api/inventory/adjustments", new
        {
            productId = fixture.Product, warehouseId = fixture.Warehouse, quantity = 1,
            reason = "Synthetic first stock racing with standard-cost edit"
        })).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(responses[0].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        var adjustment = await Ok(responses[1]);
        var expectedCost = responses[0].StatusCode == HttpStatusCode.OK ? 60m : 40m;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(expectedCost, (await db.Products.AsNoTracking().SingleAsync(x => x.Id == fixture.Product)).StandardCost);
        }
        Assert.Equal(1, await Stock(fixture.Product, fixture.Warehouse));
        AssertPosting(await Journal("Adjustment", adjustment.GetProperty("movementId").GetGuid()),
            ("1200", expectedCost, 0m), ("3000", 0m, expectedCost));
        Assert.Single((await Get("/api/retail/journals")).EnumerateArray());
        var report = await AccountingReport();
        AssertBalanced(report);
        await AssertInventoryReconciles(report);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(20)]
    public async Task Return_reverses_original_revenue_and_cost_but_restores_current_inventory_with_separate_variance(int currentCost)
    {
        using var fixture = await Retail(stock: 1);
        var sale = await Ok(await Sell(fixture.Cashier, Command(fixture)));
        var saleId = sale.GetProperty("id").GetGuid();
        var saleJournal = await Journal("Sale", saleId);
        AssertPosting(saleJournal, ("1000", 100m, 0m), ("4000", 0m, 100m), ("5000", 40m, 0m), ("1200", 0m, 40m));
        Assert.Equal(0, await Stock(fixture.Product, fixture.Warehouse));
        await Ok(await ChangeCost(fixture, currentCost));
        await Ok(await Send(Client, $"/api/retail/prices/{fixture.Product}", new
        {
            retailPrice = 125m, wholesalePrice = 100m, wholesaleMinimum = 3, expectedVersion = fixture.PriceVersion
        }, HttpMethod.Put));
        var shift = await OpenShift(fixture.Manager, fixture.Branch);
        var returned = await Ok(await Send(fixture.Manager, $"/api/retail/sales/{saleId}/returns", new
        {
            shiftId = shift.GetProperty("id").GetGuid(), reason = "Synthetic return after zero-stock cost change",
            lines = new[] { new { saleLineId = sale.GetProperty("lines")[0].GetProperty("id").GetGuid(), quantity = 1 } }
        }, idempotencyKey: Key()));
        Assert.Equal(100m, returned.GetProperty("total").GetDecimal());
        var variance = 40m - currentCost;
        AssertPosting(await Journal("Return", returned.GetProperty("id").GetGuid()),
            ("4000", 100m, 0m), ("1000", 0m, 100m), ("1200", currentCost, 0m), ("5000", 0m, 40m),
            ("5100", Math.Max(variance, 0m), Math.Max(-variance, 0m)));
        Assert.Equal(saleJournal.GetRawText(), (await Journal("Sale", saleId)).GetRawText());
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var original = await db.RetailSaleLines.AsNoTracking().SingleAsync(x => x.SaleId == saleId);
            Assert.Equal(40m, original.StandardCost);
            Assert.Equal(100m, original.UnitPrice);
        }
        Assert.Equal(1, await Stock(fixture.Product, fixture.Warehouse));
        var report = await AccountingReport();
        AssertProfit(report, 1, 100m, 100m, 0m, -variance);
        Assert.Equal(variance, report.GetProperty("varianceExpense").GetDecimal());
        Assert.Equal((decimal)currentCost, Balance(report, "1200"));
        Assert.Equal(0m, Balance(report, "1000"));
        AssertBalanced(report);
        await AssertInventoryReconciles(report);
    }

    [Fact]
    public async Task EF_rejects_modification_and_deletion_of_journal_headers_and_lines()
    {
        using var fixture = await Retail(stock: 1);
        var before = (await Get("/api/retail/journals")).GetRawText();
        foreach (var target in new[] { "header", "line" })
        foreach (var delete in new[] { false, true })
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (target == "header")
            {
                var entry = await db.JournalEntries.SingleAsync();
                if (delete) db.JournalEntries.Remove(entry);
                else entry.Description = "Rejected journal alteration";
            }
            else
            {
                var line = await db.JournalLines.OrderBy(x => x.Id).FirstAsync();
                if (delete) db.JournalLines.Remove(line);
                else line.Debit += 1m;
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        Assert.Equal(before, (await Get("/api/retail/journals")).GetRawText());
    }

    [Fact]
    public async Task EF_and_PostgreSQL_reject_appending_a_balanced_pair_to_an_already_posted_journal()
    {
        using var fixture = await Retail(stock: 1);
        var journals = await Get("/api/retail/journals");
        var journalId = Assert.Single(journals.EnumerateArray()).GetProperty("id").GetGuid();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.JournalLines.AddRange(
                new JournalLine { JournalEntryId = journalId, AccountCode = "1000", Debit = 10m },
                new JournalLine { JournalEntryId = journalId, AccountCode = "3000", Credit = 10m });
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("new journal header", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        await using var connection = new NpgsqlConnection(DatabaseConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO "JournalLines" ("Id", "JournalEntryId", "AccountCode", "Debit", "Credit")
            VALUES (@debitId, @journalId, '1000', 10, 0), (@creditId, @journalId, '3000', 0, 10)
            """, connection);
        command.Parameters.AddWithValue("debitId", Guid.NewGuid());
        command.Parameters.AddWithValue("creditId", Guid.NewGuid());
        command.Parameters.AddWithValue("journalId", journalId);
        var databaseError = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", databaseError.SqlState);
        Assert.Contains("already posted journal", databaseError.MessageText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(journals.GetRawText(), (await Get("/api/retail/journals")).GetRawText());
    }

    [Fact]
    public async Task PostgreSQL_rejects_update_delete_and_truncate_of_journal_headers_and_lines()
    {
        using var fixture = await Retail(stock: 1);
        var before = (await Get("/api/retail/journals")).GetRawText();
        await using var connection = new NpgsqlConnection(DatabaseConnection);
        await connection.OpenAsync();
        var statements = new[]
        {
            "UPDATE \"JournalEntries\" SET \"Description\" = 'Rejected raw journal alteration'",
            "DELETE FROM \"JournalEntries\"",
            "UPDATE \"JournalLines\" SET \"Debit\" = \"Debit\" + 1",
            "DELETE FROM \"JournalLines\"",
            "TRUNCATE TABLE \"JournalLines\"",
            "TRUNCATE TABLE \"JournalEntries\", \"JournalLines\""
        };
        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal("55000", error.SqlState);
            Assert.Contains("append-only", error.MessageText, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(before, (await Get("/api/retail/journals")).GetRawText());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PostgreSQL_deferred_constraint_rejects_empty_one_line_or_unbalanced_journals_at_commit(int lineCount)
    {
        var journalId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(DatabaseConnection);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await InsertHeader(connection, transaction, journalId, "ConstraintProbe", Guid.NewGuid());
            if (lineCount > 0) await InsertLine(connection, transaction, journalId, "1000", 10m, 0m);
            if (lineCount > 1) await InsertLine(connection, transaction, journalId, "3000", 0m, 9m);
            var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Contains("balanced lines", error.MessageText, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(0, await JournalCount(connection, journalId));
    }

    [Fact]
    public async Task PostgreSQL_deferred_constraint_allows_a_balanced_journal_assembled_with_separate_statements()
    {
        var journalId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(DatabaseConnection);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await InsertHeader(connection, transaction, journalId, "ConstraintProbe", Guid.NewGuid());
            await InsertLine(connection, transaction, journalId, "1000", 10m, 0m);
            await InsertLine(connection, transaction, journalId, "3000", 0m, 10m);
            await transaction.CommitAsync();
        }
        Assert.Equal(1, await JournalCount(connection, journalId));
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lines = await db.JournalLines.AsNoTracking().Where(x => x.JournalEntryId == journalId).ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.Equal(10m, lines.Sum(x => x.Debit));
        Assert.Equal(10m, lines.Sum(x => x.Credit));
    }

    [Fact]
    public async Task Reports_exclude_uncommitted_journal_lines_until_the_balanced_transaction_commits()
    {
        using var fixture = await Retail(stock: 1);
        var before = await AccountingReport();
        await using var connection = new NpgsqlConnection(DatabaseConnection);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var journalId = Guid.NewGuid();
            await InsertHeader(connection, transaction, journalId, "VisibilityProbe", Guid.NewGuid());
            await InsertLine(connection, transaction, journalId, "1000", 10m, 0m);
            await InsertLine(connection, transaction, journalId, "3000", 0m, 10m);
            var uncommitted = await AccountingReport();
            Assert.Equal(before.GetProperty("trialBalance").GetRawText(), uncommitted.GetProperty("trialBalance").GetRawText());
            Assert.Equal(before.GetProperty("balanceSheet").GetRawText(), uncommitted.GetProperty("balanceSheet").GetRawText());
            await transaction.CommitAsync();
        }
        var committed = await AccountingReport();
        Assert.Equal(10m, Balance(committed, "1000"));
        Assert.Equal(40m, Balance(committed, "1200"));
        Assert.Equal(50m, Balance(committed, "3000"));
        AssertBalanced(committed);
        await AssertInventoryReconciles(committed);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(1, 1)]
    public async Task PostgreSQL_rejects_zero_negative_and_two_sided_journal_lines(int debit, int credit)
    {
        var journalId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(DatabaseConnection);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await InsertHeader(connection, transaction, journalId, "ConstraintProbe", Guid.NewGuid());
            var error = await Assert.ThrowsAsync<PostgresException>(() => InsertLine(connection, transaction, journalId, "1000", debit, credit));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("CK_JournalLines_OneSide", error.ConstraintName);
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, await JournalCount(connection, journalId));
    }

    [Fact]
    public async Task PostgreSQL_journal_source_identity_is_unique_per_source_type()
    {
        using var fixture = await Retail(stock: 1);
        var original = Assert.Single((await Get("/api/retail/journals")).EnumerateArray());
        var sourceId = original.GetProperty("sourceId").GetGuid();
        var sourceType = original.GetProperty("sourceType").GetString()!;
        await using var connection = new NpgsqlConnection(DatabaseConnection);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var otherTypeId = Guid.NewGuid();
            await InsertHeader(connection, transaction, otherTypeId, "DistinctSourceProbe", sourceId);
            await InsertLine(connection, transaction, otherTypeId, "1000", 10m, 0m);
            await InsertLine(connection, transaction, otherTypeId, "3000", 0m, 10m);
            await transaction.CommitAsync();
        }
        var duplicateId = Guid.NewGuid();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => InsertHeader(connection, transaction, duplicateId, sourceType, sourceId));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
            Assert.Equal("IX_JournalEntries_SourceType_SourceId", error.ConstraintName);
            await transaction.RollbackAsync();
        }
        Assert.Equal(0, await JournalCount(connection, duplicateId));
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.JournalEntries.CountAsync(x => x.SourceId == sourceId));
        Assert.Single(await db.JournalEntries.Where(x => x.SourceId == sourceId && x.SourceType == sourceType).ToListAsync());
    }

    private static DateOnly WibDate() => DateOnly.ParseExact(TodayWib(), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private Task<JsonElement> AccountingReport(Guid? branch = null) => Report(branch,
        WibDate().AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        WibDate().AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private async Task<Guid> Adjust(Guid productId, Guid warehouseId, int quantity)
        => (await Ok(await Send(Client, "/api/inventory/adjustments", new
        {
            productId, warehouseId, quantity, reason = "Synthetic accounting stock reconciliation"
        }))).GetProperty("movementId").GetGuid();

    private Task<HttpResponseMessage> ChangeCost(RetailFixture fixture, decimal standardCost) => Send(Client,
        $"/api/products/{fixture.Product}", new
        {
            sku = "RTEST-001", name = "Synthetic retail item", unit = "unit", reorderPoint = 2, standardCost, isActive = true
        }, HttpMethod.Put);

    private async Task<JsonElement> Journal(string sourceType, Guid sourceId)
        => Assert.Single((await Get("/api/retail/journals")).EnumerateArray(), x =>
            x.GetProperty("sourceType").GetString() == sourceType && x.GetProperty("sourceId").GetGuid() == sourceId);

    private static decimal Balance(JsonElement report, string accountCode) => Assert.Single(
        report.GetProperty("trialBalance").EnumerateArray(), row => row.GetProperty("code").GetString() == accountCode)
        .GetProperty("balance").GetDecimal();

    private static void AssertPosting(JsonElement journal, params (string Account, decimal Debit, decimal Credit)[] expected)
    {
        var lines = journal.GetProperty("lines").EnumerateArray().Select(x =>
            new PostingLine(x.GetProperty("accountCode").GetString()!, x.GetProperty("debit").GetDecimal(), x.GetProperty("credit").GetDecimal())).ToArray();
        Assert.All(lines, line => Assert.True((line.Debit > 0m && line.Credit == 0m) || (line.Credit > 0m && line.Debit == 0m)));
        Assert.Equal(expected.Select(x => new PostingLine(x.Account, x.Debit, x.Credit)).OrderBy(x => x.Account).ThenBy(x => x.Debit).ThenBy(x => x.Credit),
            lines.OrderBy(x => x.Account).ThenBy(x => x.Debit).ThenBy(x => x.Credit));
        Assert.True(journal.GetProperty("totalDebit").GetDecimal() > 0m);
        Assert.Equal(journal.GetProperty("totalDebit").GetDecimal(), journal.GetProperty("totalCredit").GetDecimal());
        Assert.Equal(lines.Sum(x => x.Debit), journal.GetProperty("totalDebit").GetDecimal());
    }

    private static void AssertProfit(JsonElement report, int count, decimal gross, decimal refunds, decimal cogs, decimal profit)
    {
        Assert.Equal(count, report.GetProperty("salesCount").GetInt32());
        Assert.Equal(gross, report.GetProperty("grossSales").GetDecimal());
        Assert.Equal(refunds, report.GetProperty("refunds").GetDecimal());
        Assert.Equal(gross - refunds, report.GetProperty("netSales").GetDecimal());
        Assert.Equal(cogs, report.GetProperty("costOfGoodsSold").GetDecimal());
        Assert.Equal(profit, report.GetProperty("netProfit").GetDecimal());
    }

    private static void AssertBalanced(JsonElement report)
    {
        Assert.True(report.GetProperty("journalBalanced").GetBoolean());
        var trial = report.GetProperty("trialBalance").EnumerateArray().ToArray();
        Assert.Equal(new[] { "1000", "1200", "2000", "3000", "4000", "5000", "5100" }, trial.Select(x => x.GetProperty("code").GetString()));
        Assert.Equal(trial.Sum(x => x.GetProperty("debit").GetDecimal()), trial.Sum(x => x.GetProperty("credit").GetDecimal()));
        var balanceSheet = report.GetProperty("balanceSheet");
        Assert.True(balanceSheet.GetProperty("balanced").GetBoolean());
        Assert.Equal(balanceSheet.GetProperty("assets").GetDecimal(),
            balanceSheet.GetProperty("liabilities").GetDecimal() + balanceSheet.GetProperty("equity").GetDecimal());
    }

    private async Task AssertInventoryReconciles(JsonElement report)
    {
        var inventory = await Get("/api/inventory?pageSize=100");
        var apiValue = inventory.GetProperty("items").EnumerateArray().Sum(x => x.GetProperty("stockValue").GetDecimal());
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var databaseValue = await db.InventoryBalances.AsNoTracking().SumAsync(x => x.OnHand * x.Product.StandardCost);
        Assert.Equal(databaseValue, apiValue);
        Assert.Equal(databaseValue, Balance(report, "1200"));
    }

    private static async Task InsertHeader(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string sourceType, Guid sourceId)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO "JournalEntries" ("Id", "Number", "SourceType", "SourceId", "BranchId", "Description", "CreatedAt")
            VALUES (@id, @number, @sourceType, @sourceId, NULL, 'Synthetic database constraint probe', @createdAt)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("number", "TEST-" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("sourceType", sourceType);
        command.Parameters.AddWithValue("sourceId", sourceId);
        command.Parameters.AddWithValue("createdAt", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertLine(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid journalId, string account, decimal debit, decimal credit)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO "JournalLines" ("Id", "JournalEntryId", "AccountCode", "Debit", "Credit")
            VALUES (@id, @journalId, @account, @debit, @credit)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("journalId", journalId);
        command.Parameters.AddWithValue("account", account);
        command.Parameters.AddWithValue("debit", debit);
        command.Parameters.AddWithValue("credit", credit);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> JournalCount(NpgsqlConnection connection, Guid journalId)
    {
        await using var command = new NpgsqlCommand("SELECT count(*) FROM \"JournalEntries\" WHERE \"Id\" = @id", connection);
        command.Parameters.AddWithValue("id", journalId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private sealed record PostingLine(string Account, decimal Debit, decimal Credit);
}
