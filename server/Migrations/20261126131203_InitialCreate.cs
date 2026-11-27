using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Murly.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Lessons",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    TargetLanguage = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    OpeningContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Examples = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lessons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Chats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LearnerId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LessonId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Chats_Lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "Lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Turns",
                columns: table => new
                {
                    ChatId = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Reply = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Turns", x => new { x.ChatId, x.Revision });
                    table.ForeignKey(
                        name: "FK_Turns_Chats_ChatId",
                        column: x => x.ChatId,
                        principalTable: "Chats",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Chats_LearnerId_LessonId_Mode",
                table: "Chats",
                columns: new[] { "LearnerId", "LessonId", "Mode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Chats_LessonId",
                table: "Chats",
                column: "LessonId");

            migrationBuilder.CreateIndex(
                name: "IX_Turns_ChatId_RequestId",
                table: "Turns",
                columns: new[] { "ChatId", "RequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Turns");

            migrationBuilder.DropTable(
                name: "Chats");

            migrationBuilder.DropTable(
                name: "Lessons");
        }
    }
}
