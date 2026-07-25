using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStudentsEducationModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LastModifiedUtc",
                schema: "Platform",
                table: "AttendanceLogs",
                newName: "ModifiedAt");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "Platform",
                table: "AttendanceLogs",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletedBy",
                schema: "Platform",
                table: "AttendanceLogs",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "Platform",
                table: "AttendanceLogs");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "Platform",
                table: "AttendanceLogs");

            migrationBuilder.RenameColumn(
                name: "ModifiedAt",
                schema: "Platform",
                table: "AttendanceLogs",
                newName: "LastModifiedUtc");
        }
    }
}
