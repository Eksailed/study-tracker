using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyTracker.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationSettings",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimeZone = table.Column<string>(type: "TEXT", nullable: false),
                    BeforeEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    BeforeMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    DueEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DueMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    BindingVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    CodeWindowStartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CodeIssueCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastTestAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSettings", x => x.OwnerId);
                    table.ForeignKey(
                        name: "FK_NotificationSettings_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReminderDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    AssignmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduledAt = table.Column<long>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    BindingVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<long>(type: "INTEGER", nullable: true),
                    SentAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Problem = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReminderDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReminderDeliveries_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TelegramConnections",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    BotId = table.Column<long>(type: "INTEGER", nullable: false),
                    Problem = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramConnections", x => x.OwnerId);
                    table.ForeignKey(
                        name: "FK_TelegramConnections_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TelegramLinkCodes",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramLinkCodes", x => x.OwnerId);
                    table.ForeignKey(
                        name: "FK_TelegramLinkCodes_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TelegramUpdateStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BotId = table.Column<long>(type: "INTEGER", nullable: false),
                    NextOffset = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramUpdateStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReminderDeliveries_AssignmentId_DueDate_Kind",
                table: "ReminderDeliveries",
                columns: new[] { "AssignmentId", "DueDate", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReminderDeliveries_OwnerId",
                table: "ReminderDeliveries",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ReminderDeliveries_State_NextAttemptAt",
                table: "ReminderDeliveries",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramConnections_ChatId",
                table: "TelegramConnections",
                column: "ChatId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramLinkCodes_Hash",
                table: "TelegramLinkCodes",
                column: "Hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationSettings");

            migrationBuilder.DropTable(
                name: "ReminderDeliveries");

            migrationBuilder.DropTable(
                name: "TelegramConnections");

            migrationBuilder.DropTable(
                name: "TelegramLinkCodes");

            migrationBuilder.DropTable(
                name: "TelegramUpdateStates");
        }
    }
}
