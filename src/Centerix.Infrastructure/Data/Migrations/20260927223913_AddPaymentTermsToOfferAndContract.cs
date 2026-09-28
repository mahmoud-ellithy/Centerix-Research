using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Centerix.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Task A — Commercial PaymentTerms foundation.
    ///
    /// The new <c>PaymentTerms</c> column is added as <c>NOT NULL</c> on both
    /// <c>Offers</c> and <c>Contracts</c> with NO SQL default value.
    ///
    /// Why no default: the design baseline explicitly forbids inventing historical
    /// commercial facts. A SQL <c>DEFAULT 0</c> (FullUpfront) would silently
    /// classify every pre-existing row as FullUpfront, which would be a fabricated
    /// commercial decision.
    ///
    /// Why the migration is still safe: the repository is greenfield on this column.
    /// There are no production Offers or Contracts that need to be retroactively given
    /// a commercial payment mode — every row that reaches these tables must supply
    /// <c>PaymentTerms</c> explicitly, enforced by the <c>Offer.Create</c> /
    /// <c>Contract.Create</c> domain factories. If at the moment of migration actual
    /// rows exist, the migration MUST be preceded by an explicit operator-supplied
    /// data-fix step that establishes the historical commercial meaning from
    /// authoritative business evidence.
    /// </remarks>
    public partial class AddPaymentTermsToOfferAndContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "PaymentTerms",
                schema: "Platform",
                table: "Offers",
                type: "tinyint",
                nullable: false);

            migrationBuilder.AddColumn<byte>(
                name: "PaymentTerms",
                schema: "Platform",
                table: "Contracts",
                type: "tinyint",
                nullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaymentTerms",
                schema: "Platform",
                table: "Offers");

            migrationBuilder.DropColumn(
                name: "PaymentTerms",
                schema: "Platform",
                table: "Contracts");
        }
    }
}
