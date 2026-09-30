using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Parrot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPhoneNumberLoginIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_PhoneDialCode_PhoneNumber",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_PhoneNumber",
                table: "AspNetUsers",
                column: "PhoneNumber",
                unique: true,
                filter: "\"PhoneNumber\" IS NOT NULL");

            // The other half of sign-in's comparison is the two columns joined,
            // which no column holds — so the index sits on the expression
            // itself. It is written as SQL because the model has no expression
            // to declare; a second, unique one is needed so an international
            // number can never resolve to two accounts.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_AspNetUsers_FullNumber\" " +
                "ON \"AspNetUsers\" ((\"PhoneDialCode\" || \"PhoneNumber\")) " +
                "WHERE \"PhoneDialCode\" IS NOT NULL AND \"PhoneNumber\" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX \"IX_AspNetUsers_FullNumber\";");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_PhoneNumber",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_PhoneDialCode_PhoneNumber",
                table: "AspNetUsers",
                columns: new[] { "PhoneDialCode", "PhoneNumber" },
                unique: true,
                filter: "\"PhoneDialCode\" IS NOT NULL AND \"PhoneNumber\" IS NOT NULL");
        }
    }
}
