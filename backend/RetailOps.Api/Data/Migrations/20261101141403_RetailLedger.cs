using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace RetailOps.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RetailLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_Type",
                table: "StockMovements");

            migrationBuilder.CreateTable(
                name: "Branches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Branches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Branches_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FinancialAccounts",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialAccounts", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "ProductPrices",
                columns: table => new
                {
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    RetailPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    WholesalePrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    WholesaleMinimum = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductPrices", x => x.ProductId);
                    table.CheckConstraint("CK_ProductPrices_Values", "\"RetailPrice\">0 AND \"WholesalePrice\">0 AND \"WholesalePrice\"<=\"RetailPrice\" AND \"WholesaleMinimum\">=2 AND \"Version\">0");
                    table.ForeignKey(
                        name: "FK_ProductPrices_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RetailMutations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResponseJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetailMutations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BranchAssignments",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchAssignments", x => new { x.UserId, x.BranchId });
                    table.ForeignKey(
                        name: "FK_BranchAssignments_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BranchAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CashShifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenedById = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenedByName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OpeningCash = table.Column<decimal>(type: "numeric(28,2)", precision: 28, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CountedCash = table.Column<decimal>(type: "numeric(28,2)", precision: 28, scale: 2, nullable: true),
                    ExpectedCash = table.Column<decimal>(type: "numeric(28,2)", precision: 28, scale: 2, nullable: true),
                    Variance = table.Column<decimal>(type: "numeric(28,2)", precision: 28, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashShifts", x => x.Id);
                    table.CheckConstraint("CK_CashShifts_State", "\"OpeningCash\">=0 AND ((\"Status\"='Open' AND \"ClosedAt\" IS NULL AND \"CountedCash\" IS NULL AND \"ExpectedCash\" IS NULL AND \"Variance\" IS NULL) OR (\"Status\"='Closed' AND \"ClosedAt\" IS NOT NULL AND \"CountedCash\">=0 AND \"ExpectedCash\" IS NOT NULL AND \"Variance\"=\"CountedCash\"-\"ExpectedCash\"))");
                    table.ForeignKey(
                        name: "FK_CashShifts_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashShifts_Users_OpenedById",
                        column: x => x.OpenedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JournalEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JournalEntries_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RetailSales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PriceTier = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CashTendered = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Change = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClientCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetailSales", x => x.Id);
                    table.CheckConstraint("CK_RetailSales_Values", "\"Total\">0 AND \"CashTendered\">=\"Total\" AND \"Change\"=\"CashTendered\"-\"Total\" AND \"PriceTier\" IN ('Retail','Wholesale')");
                    table.ForeignKey(
                        name: "FK_RetailSales_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RetailSales_CashShifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "CashShifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JournalLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountCode = table.Column<string>(type: "character varying(4)", nullable: false),
                    Debit = table.Column<decimal>(type: "numeric(38,2)", precision: 38, scale: 2, nullable: false),
                    Credit = table.Column<decimal>(type: "numeric(38,2)", precision: 38, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalLines", x => x.Id);
                    table.CheckConstraint("CK_JournalLines_OneSide", "(\"Debit\">0 AND \"Credit\"=0) OR (\"Credit\">0 AND \"Debit\"=0)");
                    table.ForeignKey(
                        name: "FK_JournalLines_FinancialAccounts_AccountCode",
                        column: x => x.AccountCode,
                        principalTable: "FinancialAccounts",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalLines_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RetailReturns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SaleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: false),
                    BranchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetailReturns", x => x.Id);
                    table.CheckConstraint("CK_RetailReturns_Total", "\"Total\">0");
                    table.ForeignKey(
                        name: "FK_RetailReturns_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RetailReturns_CashShifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "CashShifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RetailReturns_RetailSales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "RetailSales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RetailSaleLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(28,2)", precision: 28, scale: 2, nullable: false),
                    StandardCost = table.Column<decimal>(type: "numeric(28,2)", precision: 28, scale: 2, nullable: false),
                    LineTotal = table.Column<decimal>(type: "numeric(28,2)", precision: 28, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetailSaleLines", x => x.Id);
                    table.CheckConstraint("CK_RetailSaleLines_Values", "\"Quantity\">0 AND \"UnitPrice\">0 AND \"StandardCost\">=0 AND \"LineTotal\"=\"Quantity\"*\"UnitPrice\"");
                    table.ForeignKey(
                        name: "FK_RetailSaleLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RetailSaleLines_RetailSales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "RetailSales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RetailReturnLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                    SaleLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetailReturnLines", x => x.Id);
                    table.CheckConstraint("CK_RetailReturnLines_Quantity", "\"Quantity\">0");
                    table.ForeignKey(
                        name: "FK_RetailReturnLines_RetailReturns_ReturnId",
                        column: x => x.ReturnId,
                        principalTable: "RetailReturns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RetailReturnLines_RetailSaleLines_SaleLineId",
                        column: x => x.SaleLineId,
                        principalTable: "RetailSaleLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "FinancialAccounts",
                columns: new[] { "Code", "Name", "Type" },
                values: new object[,]
                {
                    { "1000", "Cash", "Asset" },
                    { "1200", "Inventory", "Asset" },
                    { "2000", "Trade payables", "Liability" },
                    { "3000", "Opening equity", "Equity" },
                    { "4000", "Sales revenue", "Revenue" },
                    { "5000", "Cost of goods sold", "Expense" },
                    { "5100", "Inventory and purchase variance", "Expense" }
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_Type",
                table: "StockMovements",
                sql: "\"Type\" IN ('Receipt','Adjustment','TransferIn','TransferOut','Sale','Return')");

            migrationBuilder.CreateIndex(
                name: "IX_BranchAssignments_BranchId",
                table: "BranchAssignments",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_Branches_Code",
                table: "Branches",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Branches_WarehouseId",
                table: "Branches",
                column: "WarehouseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashShifts_BranchId",
                table: "CashShifts",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_CashShifts_OpenedById",
                table: "CashShifts",
                column: "OpenedById",
                unique: true,
                filter: "\"Status\"='Open'");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_BranchId",
                table: "JournalEntries",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_CreatedAt",
                table: "JournalEntries",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_Number",
                table: "JournalEntries",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_SourceType_SourceId",
                table: "JournalEntries",
                columns: new[] { "SourceType", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_AccountCode",
                table: "JournalLines",
                column: "AccountCode");

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_JournalEntryId",
                table: "JournalLines",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_RetailReturnLines_ReturnId_SaleLineId",
                table: "RetailReturnLines",
                columns: new[] { "ReturnId", "SaleLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RetailReturnLines_SaleLineId",
                table: "RetailReturnLines",
                column: "SaleLineId");

            migrationBuilder.CreateIndex(
                name: "IX_RetailReturns_BranchId",
                table: "RetailReturns",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_RetailReturns_Number",
                table: "RetailReturns",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RetailReturns_SaleId",
                table: "RetailReturns",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_RetailReturns_ShiftId",
                table: "RetailReturns",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_RetailSaleLines_ProductId",
                table: "RetailSaleLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_RetailSaleLines_SaleId_ProductId",
                table: "RetailSaleLines",
                columns: new[] { "SaleId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RetailSales_BranchId_CreatedAt",
                table: "RetailSales",
                columns: new[] { "BranchId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RetailSales_Number",
                table: "RetailSales",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RetailSales_ShiftId",
                table: "RetailSales",
                column: "ShiftId");

            migrationBuilder.Sql("""
                ALTER TABLE "JournalEntries" ADD COLUMN "PostingTransactionId" xid8 NOT NULL DEFAULT pg_current_xact_id();
                CREATE FUNCTION retailops_check_journal() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE journal_id uuid; line_count bigint; debit_total numeric; credit_total numeric;
                BEGIN
                    IF TG_TABLE_NAME = 'JournalEntries' THEN journal_id := NEW."Id";
                    ELSE journal_id := NEW."JournalEntryId"; END IF;
                    SELECT count(*), COALESCE(sum("Debit"),0), COALESCE(sum("Credit"),0)
                      INTO line_count, debit_total, credit_total FROM "JournalLines" WHERE "JournalEntryId" = journal_id;
                    IF line_count < 2 OR debit_total <= 0 OR credit_total <= 0 OR debit_total <> credit_total THEN
                        RAISE EXCEPTION 'RetailOps journal must have at least two positive balanced lines.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER "JournalEntries_Balanced" AFTER INSERT ON "JournalEntries"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION retailops_check_journal();
                CREATE CONSTRAINT TRIGGER "JournalLines_Balanced" AFTER INSERT ON "JournalLines"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION retailops_check_journal();
                CREATE FUNCTION retailops_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'RetailOps append-only financial or receipt records cannot be changed.' USING ERRCODE = '55000';
                END $$;
                CREATE FUNCTION retailops_new_journal_line() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS(SELECT 1 FROM "JournalEntries" WHERE "Id"=NEW."JournalEntryId" AND "PostingTransactionId"=pg_current_xact_id()) THEN
                        RAISE EXCEPTION 'RetailOps cannot append lines to an already posted journal.' USING ERRCODE='55000';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER "JournalLines_NewPostingOnly" BEFORE INSERT ON "JournalLines" FOR EACH ROW EXECUTE FUNCTION retailops_new_journal_line();
                CREATE TRIGGER "JournalEntries_Immutable" BEFORE UPDATE OR DELETE ON "JournalEntries" FOR EACH ROW EXECUTE FUNCTION retailops_immutable();
                CREATE TRIGGER "JournalLines_Immutable" BEFORE UPDATE OR DELETE ON "JournalLines" FOR EACH ROW EXECUTE FUNCTION retailops_immutable();
                CREATE TRIGGER "FinancialAccounts_Immutable" BEFORE UPDATE OR DELETE ON "FinancialAccounts" FOR EACH ROW EXECUTE FUNCTION retailops_immutable();
                CREATE TRIGGER "JournalEntries_NoTruncate" BEFORE TRUNCATE ON "JournalEntries" FOR EACH STATEMENT EXECUTE FUNCTION retailops_immutable();
                CREATE TRIGGER "JournalLines_NoTruncate" BEFORE TRUNCATE ON "JournalLines" FOR EACH STATEMENT EXECUTE FUNCTION retailops_immutable();
                CREATE TRIGGER "FinancialAccounts_NoTruncate" BEFORE TRUNCATE ON "FinancialAccounts" FOR EACH STATEMENT EXECUTE FUNCTION retailops_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION retailops_new_journal_line() CASCADE; DROP FUNCTION retailops_check_journal() CASCADE; DROP FUNCTION retailops_immutable() CASCADE;");
            migrationBuilder.DropTable(
                name: "BranchAssignments");

            migrationBuilder.DropTable(
                name: "JournalLines");

            migrationBuilder.DropTable(
                name: "ProductPrices");

            migrationBuilder.DropTable(
                name: "RetailMutations");

            migrationBuilder.DropTable(
                name: "RetailReturnLines");

            migrationBuilder.DropTable(
                name: "FinancialAccounts");

            migrationBuilder.DropTable(
                name: "JournalEntries");

            migrationBuilder.DropTable(
                name: "RetailReturns");

            migrationBuilder.DropTable(
                name: "RetailSaleLines");

            migrationBuilder.DropTable(
                name: "RetailSales");

            migrationBuilder.DropTable(
                name: "CashShifts");

            migrationBuilder.DropTable(
                name: "Branches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockMovements_Type",
                table: "StockMovements");

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockMovements_Type",
                table: "StockMovements",
                sql: "\"Type\" IN ('Receipt','Adjustment','TransferIn','TransferOut')");
        }
    }
}
