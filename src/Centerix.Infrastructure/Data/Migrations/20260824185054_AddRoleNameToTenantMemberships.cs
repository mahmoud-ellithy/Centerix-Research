using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleNameToTenantMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RoleName",
                schema: "Platform",
                table: "TenantMemberships",
                type: "nvarchar(128)",
                nullable: false,
                defaultValue: "TenantUser");

            migrationBuilder.CreateTable(
                name: "TenantInvitations",
                schema: "Platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(64)", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", nullable: false),
                    InvitedByUserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    RoleName = table.Column<string>(type: "nvarchar(128)", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(128)", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false, defaultValue: (byte)0),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcceptedByUserId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedByUserId = table.Column<string>(type: "nvarchar(450)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantInvitations_AspNetUsers_AcceptedByUserId",
                        column: x => x.AcceptedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantInvitations_AspNetUsers_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantInvitations_AspNetUsers_RevokedByUserId",
                        column: x => x.RevokedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_AcceptedByUserId",
                schema: "Platform",
                table: "TenantInvitations",
                column: "AcceptedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_InvitedByUserId",
                schema: "Platform",
                table: "TenantInvitations",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_NormalizedEmail",
                schema: "Platform",
                table: "TenantInvitations",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_RevokedByUserId",
                schema: "Platform",
                table: "TenantInvitations",
                column: "RevokedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_Status",
                schema: "Platform",
                table: "TenantInvitations",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_Tenant_Email_Status",
                schema: "Platform",
                table: "TenantInvitations",
                columns: new[] { "TenantId", "NormalizedEmail", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_TenantId",
                schema: "Platform",
                table: "TenantInvitations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantInvitations_TokenHash",
                schema: "Platform",
                table: "TenantInvitations",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantInvitations",
                schema: "Platform");

            migrationBuilder.DropColumn(
                name: "RoleName",
                schema: "Platform",
                table: "TenantMemberships");
        }
    }
}
