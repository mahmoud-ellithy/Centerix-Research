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
            migrationBuilder.CreateTable(
                name: "AcademicStages",
                schema: "Platform",
                columns: table => new
                {
                    StageId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicStages", x => x.StageId);
                });

            migrationBuilder.CreateTable(
                name: "Branches",
                schema: "Platform",
                columns: table => new
                {
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ManagerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Branches", x => x.BranchId);
                });

            migrationBuilder.CreateTable(
                name: "AcademicYears",
                schema: "Platform",
                columns: table => new
                {
                    YearId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StageId = table.Column<int>(type: "int", nullable: false),
                    YearCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    YearName = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcademicYears", x => x.YearId);
                    table.ForeignKey(
                        name: "FK_AcademicYears_AcademicStages_StageId",
                        column: x => x.StageId,
                        principalSchema: "Platform",
                        principalTable: "AcademicStages",
                        principalColumn: "StageId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Students",
                schema: "Platform",
                columns: table => new
                {
                    StudentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StageId = table.Column<int>(type: "int", nullable: false),
                    YearId = table.Column<int>(type: "int", nullable: false),
                    FullNameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FullNameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DateOfBirth = table.Column<DateOnly>(type: "date", nullable: false),
                    Gender = table.Column<string>(type: "nchar(1)", nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    QRCode = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DiscountType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DiscountValue = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    EnrolledAt = table.Column<DateOnly>(type: "date", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Students", x => x.StudentId);
                    table.ForeignKey(
                        name: "FK_Students_AcademicStages_StageId",
                        column: x => x.StageId,
                        principalSchema: "Platform",
                        principalTable: "AcademicStages",
                        principalColumn: "StageId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Students_AcademicYears_YearId",
                        column: x => x.YearId,
                        principalSchema: "Platform",
                        principalTable: "AcademicYears",
                        principalColumn: "YearId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Students_Branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "Platform",
                        principalTable: "Branches",
                        principalColumn: "BranchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceLogs",
                schema: "Platform",
                columns: table => new
                {
                    AttendanceId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StudentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CheckInTime = table.Column<TimeOnly>(type: "time", nullable: false),
                    IsOffline = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    SyncedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceLogs", x => x.AttendanceId);
                    table.ForeignKey(
                        name: "FK_AttendanceLogs_Students_StudentId",
                        column: x => x.StudentId,
                        principalSchema: "Platform",
                        principalTable: "Students",
                        principalColumn: "StudentId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AcademicStages_TenantId_SortOrder",
                schema: "Platform",
                table: "AcademicStages",
                columns: new[] { "TenantId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "UX_AcademicStages_TenantId_Code",
                schema: "Platform",
                table: "AcademicStages",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AcademicYears_StageId",
                schema: "Platform",
                table: "AcademicYears",
                column: "StageId");

            migrationBuilder.CreateIndex(
                name: "IX_AcademicYears_TenantId_StageId",
                schema: "Platform",
                table: "AcademicYears",
                columns: new[] { "TenantId", "StageId" });

            migrationBuilder.CreateIndex(
                name: "UX_AcademicYears_TenantId_StageId_YearCode",
                schema: "Platform",
                table: "AcademicYears",
                columns: new[] { "TenantId", "StageId", "YearCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceLogs_GroupId_SessionDate",
                schema: "Platform",
                table: "AttendanceLogs",
                columns: new[] { "GroupId", "SessionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceLogs_IsOffline",
                schema: "Platform",
                table: "AttendanceLogs",
                column: "IsOffline");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceLogs_TenantId_SessionDate",
                schema: "Platform",
                table: "AttendanceLogs",
                columns: new[] { "TenantId", "SessionDate" });

            migrationBuilder.CreateIndex(
                name: "UX_AttendanceLogs_Student_Session",
                schema: "Platform",
                table: "AttendanceLogs",
                columns: new[] { "StudentId", "SessionDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Branches_TenantId",
                schema: "Platform",
                table: "Branches",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Branches_TenantId_IsActive",
                schema: "Platform",
                table: "Branches",
                columns: new[] { "TenantId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Students_BranchId",
                schema: "Platform",
                table: "Students",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_Students_StageId",
                schema: "Platform",
                table: "Students",
                column: "StageId");

            migrationBuilder.CreateIndex(
                name: "IX_Students_TenantId",
                schema: "Platform",
                table: "Students",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Students_TenantId_BranchId",
                schema: "Platform",
                table: "Students",
                columns: new[] { "TenantId", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_Students_TenantId_StageId_YearId",
                schema: "Platform",
                table: "Students",
                columns: new[] { "TenantId", "StageId", "YearId" });

            migrationBuilder.CreateIndex(
                name: "IX_Students_TenantId_Status",
                schema: "Platform",
                table: "Students",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Students_YearId",
                schema: "Platform",
                table: "Students",
                column: "YearId");

            migrationBuilder.CreateIndex(
                name: "UX_Students_QRCode",
                schema: "Platform",
                table: "Students",
                column: "QRCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceLogs",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "Students",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "AcademicYears",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "Branches",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "AcademicStages",
                schema: "Platform");
        }
    }
}
