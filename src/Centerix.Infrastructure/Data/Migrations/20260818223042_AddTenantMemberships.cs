using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantMemberships",
                schema: "Platform",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0),
                    JoinedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantMemberships", x => new { x.UserId, x.TenantId });
                    table.ForeignKey(
                        name: "FK_TenantMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantMemberships_Status",
                schema: "Platform",
                table: "TenantMemberships",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TenantMemberships_TenantId",
                schema: "Platform",
                table: "TenantMemberships",
                column: "TenantId");

            // Cross-context foreign key to the runtime tenant registry (Platform.TenantRegistry),
            // which is owned by a SEPARATE DbContext (TenantDbContext) and therefore cannot be
            // expressed as an EF relationship here. Column types match TenantRegistry.Id (nvarchar(64)).
            // ON DELETE NO ACTION (default) prevents hard-deleting a tenant while memberships reference it.
            migrationBuilder.Sql(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_TenantMemberships_TenantRegistry_TenantId')
                BEGIN
                    ALTER TABLE [Platform].[TenantMemberships]
                    ADD CONSTRAINT [FK_TenantMemberships_TenantRegistry_TenantId]
                    FOREIGN KEY ([TenantId]) REFERENCES [Platform].[TenantRegistry] ([Id]);
                END
                """,
                suppressTransaction: false);

            // Backfill existing users into their tenant(s) using the SAME deterministic association
            // the application seeder used to provision each tenant's admin user: a user whose email
            // equals a tenant's email owns/is a member of that tenant. This reconstructs the real,
            // seeded relationship and does NOT invent any new associations. Idempotent: re-running
            // will not duplicate rows. Only Active memberships are created.
            migrationBuilder.Sql(
                """
                INSERT INTO [Platform].[TenantMemberships] (UserId, TenantId, Status, JoinedAtUtc)
                SELECT u.Id, t.Id, 0, SYSUTCDATETIME()
                FROM [dbo].[AspNetUsers] u
                INNER JOIN [Platform].[TenantRegistry] t ON LOWER(u.Email) = LOWER(t.Email)
                WHERE NOT EXISTS (
                    SELECT 1 FROM [Platform].[TenantMemberships] tm
                    WHERE tm.UserId = u.Id AND tm.TenantId = t.Id);
                """,
                suppressTransaction: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_TenantMemberships_TenantRegistry_TenantId') " +
                "ALTER TABLE [Platform].[TenantMemberships] DROP CONSTRAINT [FK_TenantMemberships_TenantRegistry_TenantId];",
                suppressTransaction: false);

            migrationBuilder.DropTable(
                name: "TenantMemberships",
                schema: "Platform");
        }
    }
}
