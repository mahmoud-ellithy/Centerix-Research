using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeacherSalaryModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Subjects",
                schema: "Platform",
                columns: table => new
                {
                    SubjectId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    StageId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subjects", x => x.SubjectId);
                });

            migrationBuilder.CreateTable(
                name: "Teachers",
                schema: "Platform",
                columns: table => new
                {
                    TeacherId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FullName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Qualification = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    YearsExp = table.Column<byte>(type: "tinyint", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    JoinedAt = table.Column<DateOnly>(type: "date", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teachers", x => x.TeacherId);
                    table.ForeignKey(
                        name: "FK_Teachers_Branches_BranchId",
                        column: x => x.BranchId,
                        principalSchema: "Platform",
                        principalTable: "Branches",
                        principalColumn: "BranchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalaryPayments",
                schema: "Platform",
                columns: table => new
                {
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeacherId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodMonth = table.Column<byte>(type: "tinyint", nullable: false),
                    PeriodYear = table.Column<short>(type: "smallint", nullable: false),
                    GrossAmount = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    NetAmount = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    PaidAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalaryPayments", x => x.PaymentId);
                    table.ForeignKey(
                        name: "FK_SalaryPayments_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalSchema: "Platform",
                        principalTable: "Teachers",
                        principalColumn: "TeacherId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeacherRatings",
                schema: "Platform",
                columns: table => new
                {
                    RatingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TeacherId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StudentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Stars = table.Column<byte>(type: "tinyint", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PeriodMonth = table.Column<byte>(type: "tinyint", nullable: false),
                    PeriodYear = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeacherRatings", x => x.RatingId);
                    table.ForeignKey(
                        name: "FK_TeacherRatings_Students_StudentId",
                        column: x => x.StudentId,
                        principalSchema: "Platform",
                        principalTable: "Students",
                        principalColumn: "StudentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TeacherRatings_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalSchema: "Platform",
                        principalTable: "Teachers",
                        principalColumn: "TeacherId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TeacherSalaryConfigs",
                schema: "Platform",
                columns: table => new
                {
                    ConfigId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TeacherId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SalaryType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(8,2)", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ModifiedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeacherSalaryConfigs", x => x.ConfigId);
                    table.ForeignKey(
                        name: "FK_TeacherSalaryConfigs_Teachers_TeacherId",
                        column: x => x.TeacherId,
                        principalSchema: "Platform",
                        principalTable: "Teachers",
                        principalColumn: "TeacherId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalaryPayments_TenantId_PeriodYear_PeriodMonth",
                schema: "Platform",
                table: "SalaryPayments",
                columns: new[] { "TenantId", "PeriodYear", "PeriodMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_SalaryPayments_TenantId_Status",
                schema: "Platform",
                table: "SalaryPayments",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_SalaryPayments_Teacher_Period",
                schema: "Platform",
                table: "SalaryPayments",
                columns: new[] { "TeacherId", "PeriodYear", "PeriodMonth" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subjects_TenantId_StageId",
                schema: "Platform",
                table: "Subjects",
                columns: new[] { "TenantId", "StageId" });

            migrationBuilder.CreateIndex(
                name: "UX_Subjects_TenantId_StageId_Name",
                schema: "Platform",
                table: "Subjects",
                columns: new[] { "TenantId", "StageId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeacherRatings_GroupId",
                schema: "Platform",
                table: "TeacherRatings",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherRatings_StudentId",
                schema: "Platform",
                table: "TeacherRatings",
                column: "StudentId");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherRatings_TeacherId",
                schema: "Platform",
                table: "TeacherRatings",
                column: "TeacherId");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherRatings_TenantId_StudentId",
                schema: "Platform",
                table: "TeacherRatings",
                columns: new[] { "TenantId", "StudentId" });

            migrationBuilder.CreateIndex(
                name: "IX_TeacherRatings_TenantId_TeacherId_PeriodYear_PeriodMonth",
                schema: "Platform",
                table: "TeacherRatings",
                columns: new[] { "TenantId", "TeacherId", "PeriodYear", "PeriodMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_Teachers_BranchId",
                schema: "Platform",
                table: "Teachers",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_Teachers_TenantId",
                schema: "Platform",
                table: "Teachers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Teachers_TenantId_BranchId",
                schema: "Platform",
                table: "Teachers",
                columns: new[] { "TenantId", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_Teachers_TenantId_Status",
                schema: "Platform",
                table: "Teachers",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_Teachers_TenantId_UserId",
                schema: "Platform",
                table: "Teachers",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeacherSalaryConfigs_GroupId",
                schema: "Platform",
                table: "TeacherSalaryConfigs",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TeacherSalaryConfigs_TeacherId_EffectiveFrom",
                schema: "Platform",
                table: "TeacherSalaryConfigs",
                columns: new[] { "TeacherId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_TeacherSalaryConfigs_TenantId",
                schema: "Platform",
                table: "TeacherSalaryConfigs",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SalaryPayments",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "Subjects",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "TeacherRatings",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "TeacherSalaryConfigs",
                schema: "Platform");

            migrationBuilder.DropTable(
                name: "Teachers",
                schema: "Platform");
        }
    }
}
