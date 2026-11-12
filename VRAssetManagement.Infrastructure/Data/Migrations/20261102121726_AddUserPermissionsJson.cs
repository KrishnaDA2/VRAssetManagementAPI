using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VRAssetManagement.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPermissionsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PermissionsJson",
                table: "Users",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PermissionsJson",
                table: "Users");
        }
    }
}
