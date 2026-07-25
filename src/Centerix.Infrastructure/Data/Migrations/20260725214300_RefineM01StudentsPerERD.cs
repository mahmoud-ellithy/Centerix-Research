using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RefineM01StudentsPerERD : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "Students",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "Branches",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "AttendanceLogs",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "AcademicYears",
                newName: "ModifiedBy");

            migrationBuilder.RenameColumn(
                name: "LastModifiedBy",
                schema: "Platform",
                table: "AcademicStages",
                newName: "ModifiedBy");

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                schema: "Platform",
                table: "Students",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(30)",
                oldMaxLength: 30);

            migrationBuilder.AlterColumn<string>(
                name: "Gender",
                schema: "Platform",
                table: "Students",
                type: "nchar(1)",
                maxLength: 1,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nchar(1)");

            migrationBuilder.AlterColumn<string>(
                name: "FullNameEn",
                schema: "Platform",
                table: "Students",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<decimal>(
                name: "DiscountValue",
                schema: "Platform",
                table: "Students",
                type: "decimal(10,2)",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AlterColumn<string>(
                name: "DiscountType",
                schema: "Platform",
                table: "Students",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "DateOfBirth",
                schema: "Platform",
                table: "Students",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateOnly),
                oldType: "date");

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                schema: "Platform",
                table: "Branches",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(30)",
                oldMaxLength: 30);

            migrationBuilder.AlterColumn<string>(
                name: "Address",
                schema: "Platform",
                table: "Branches",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(300)",
                oldMaxLength: 300);

            migrationBuilder.AlterColumn<DateTime>(
                name: "SyncedAt",
                schema: "Platform",
                table: "AttendanceLogs",
                type: "datetime2",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "datetime2");

            migrationBuilder.AlterColumn<TimeOnly>(
                name: "CheckInTime",
                schema: "Platform",
                table: "AttendanceLogs",
                type: "time",
                nullable: true,
                oldClrType: typeof(TimeOnly),
                oldType: "time");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "Students",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "Branches",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "AttendanceLogs",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "AcademicYears",
                newName: "LastModifiedBy");

            migrationBuilder.RenameColumn(
                name: "ModifiedBy",
                schema: "Platform",
                table: "AcademicStages",
                newName: "LastModifiedBy");

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                schema: "Platform",
                table: "Students",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(30)",
                oldMaxLength: 30,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Gender",
                schema: "Platform",
                table: "Students",
                type: "nchar(1)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nchar(1)",
                oldMaxLength: 1,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FullNameEn",
                schema: "Platform",
                table: "Students",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "DiscountValue",
                schema: "Platform",
                table: "Students",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(10,2)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "DiscountType",
                schema: "Platform",
                table: "Students",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "DateOfBirth",
                schema: "Platform",
                table: "Students",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1),
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                schema: "Platform",
                table: "Branches",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(30)",
                oldMaxLength: 30,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Address",
                schema: "Platform",
                table: "Branches",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(300)",
                oldMaxLength: 300,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "SyncedAt",
                schema: "Platform",
                table: "AttendanceLogs",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                oldClrType: typeof(DateTime),
                oldType: "datetime2",
                oldNullable: true);

            migrationBuilder.AlterColumn<TimeOnly>(
                name: "CheckInTime",
                schema: "Platform",
                table: "AttendanceLogs",
                type: "time",
                nullable: false,
                defaultValue: new TimeOnly(0, 0, 0),
                oldClrType: typeof(TimeOnly),
                oldType: "time",
                oldNullable: true);
        }
    }
}
