using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ebank.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoTransactionType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "AccountTransactions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Other");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Type",
                table: "AccountTransactions");
        }
    }
}
