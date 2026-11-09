using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyTracker.Migrations
{
    /// <inheritdoc />
    public partial class RolesAndGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Assignments_Subjects_SubjectId_OwnerId",
                table: "Assignments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Subjects_Id_OwnerId",
                table: "Subjects");

            migrationBuilder.DropIndex(
                name: "IX_Assignments_SubjectId_OwnerId",
                table: "Assignments");

            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "Subtasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "OwnerId",
                table: "Subjects",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "Subjects",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "OwnerId",
                table: "Assignments",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "Assignments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOwner",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "GroupProgress",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AssignmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupProgress", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupProgress_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupProgress_Assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "Assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    NameKey = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    GuestKeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    GuestKeyCreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupMembers_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Subtasks_UserId",
                table: "Subtasks",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Subjects_GroupId_NameKey",
                table: "Subjects",
                columns: new[] { "GroupId", "NameKey" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Subjects_OwnerOrGroup",
                table: "Subjects",
                sql: "(\"OwnerId\" IS NULL) <> (\"GroupId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_GroupId",
                table: "Assignments",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_SubjectId",
                table: "Assignments",
                column: "SubjectId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Assignments_OwnerOrGroup",
                table: "Assignments",
                sql: "(\"OwnerId\" IS NULL) <> (\"GroupId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_GroupId",
                table: "GroupMembers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_UserId",
                table: "GroupMembers",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupProgress_AssignmentId_UserId",
                table: "GroupProgress",
                columns: new[] { "AssignmentId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupProgress_UserId",
                table: "GroupProgress",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Groups_GuestKeyHash",
                table: "Groups",
                column: "GuestKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Groups_NameKey",
                table: "Groups",
                column: "NameKey",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Assignments_Groups_GroupId",
                table: "Assignments",
                column: "GroupId",
                principalTable: "Groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Assignments_Subjects_SubjectId",
                table: "Assignments",
                column: "SubjectId",
                principalTable: "Subjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Subjects_Groups_GroupId",
                table: "Subjects",
                column: "GroupId",
                principalTable: "Groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Subtasks_AspNetUsers_UserId",
                table: "Subtasks",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Групповым данным в старой схеме места нет: при откате они удаляются.
            migrationBuilder.Sql("DELETE FROM \"Assignments\" WHERE \"GroupId\" IS NOT NULL;");
            migrationBuilder.Sql("DELETE FROM \"Subjects\" WHERE \"GroupId\" IS NOT NULL;");
            // EF переписывает Assignments, пока у старых Subjects ещё нет ключа (Id, OwnerId), на который
            // ссылается внешний ключ: уникальный индекс временно играет эту роль (таблицу потом пересоберут).
            migrationBuilder.Sql("CREATE UNIQUE INDEX \"TMP_Subjects_Id_OwnerId\" ON \"Subjects\" (\"Id\", \"OwnerId\");");

            migrationBuilder.DropForeignKey(
                name: "FK_Subjects_Groups_GroupId",
                table: "Subjects");

            migrationBuilder.DropIndex(
                name: "IX_Subjects_GroupId_NameKey",
                table: "Subjects");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Subjects_OwnerOrGroup",
                table: "Subjects");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Subjects");

            migrationBuilder.AlterColumn<string>(
                name: "OwnerId",
                table: "Subjects",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Subjects_Id_OwnerId",
                table: "Subjects",
                columns: new[] { "Id", "OwnerId" });

            migrationBuilder.DropForeignKey(
                name: "FK_Assignments_Groups_GroupId",
                table: "Assignments");

            migrationBuilder.DropForeignKey(
                name: "FK_Assignments_Subjects_SubjectId",
                table: "Assignments");

            migrationBuilder.DropForeignKey(
                name: "FK_Subtasks_AspNetUsers_UserId",
                table: "Subtasks");

            migrationBuilder.DropTable(
                name: "GroupMembers");

            migrationBuilder.DropTable(
                name: "GroupProgress");

            migrationBuilder.DropTable(
                name: "Groups");

            migrationBuilder.DropIndex(
                name: "IX_Subtasks_UserId",
                table: "Subtasks");

            migrationBuilder.DropIndex(
                name: "IX_Assignments_GroupId",
                table: "Assignments");

            migrationBuilder.DropIndex(
                name: "IX_Assignments_SubjectId",
                table: "Assignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Assignments_OwnerOrGroup",
                table: "Assignments");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "Subtasks");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Assignments");

            migrationBuilder.DropColumn(
                name: "IsOwner",
                table: "AspNetUsers");

            migrationBuilder.AlterColumn<string>(
                name: "OwnerId",
                table: "Assignments",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_SubjectId_OwnerId",
                table: "Assignments",
                columns: new[] { "SubjectId", "OwnerId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Assignments_Subjects_SubjectId_OwnerId",
                table: "Assignments",
                columns: new[] { "SubjectId", "OwnerId" },
                principalTable: "Subjects",
                principalColumns: new[] { "Id", "OwnerId" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
