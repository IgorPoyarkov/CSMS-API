using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "access");

            migrationBuilder.CreateTable(
                name: "contract_services",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrganizationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_services", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "employees",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrganizationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employees", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "functions",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_functions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Inn = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "permissions",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "requests",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrganizationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EmployeeKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "access",
                columns: table => new
                {
                    RoleCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PermissionCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => new { x.RoleCode, x.PermissionCode });
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "access",
                columns: table => new
                {
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.Token);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                schema: "access",
                columns: table => new
                {
                    UserKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RoleCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.UserKey, x.RoleCode });
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OrganizationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EmployeeKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contract_services_ExternalKey",
                schema: "access",
                table: "contract_services",
                column: "ExternalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_contract_services_OrganizationKey",
                schema: "access",
                table: "contract_services",
                column: "OrganizationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employees_ExternalKey",
                schema: "access",
                table: "employees",
                column: "ExternalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_employees_OrganizationKey",
                schema: "access",
                table: "employees",
                column: "OrganizationKey");

            migrationBuilder.CreateIndex(
                name: "IX_employees_UserKey",
                schema: "access",
                table: "employees",
                column: "UserKey");

            migrationBuilder.CreateIndex(
                name: "IX_functions_Code",
                schema: "access",
                table: "functions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organizations_ExternalKey",
                schema: "access",
                table: "organizations",
                column: "ExternalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_permissions_Code",
                schema: "access",
                table: "permissions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_requests_ExternalKey",
                schema: "access",
                table: "requests",
                column: "ExternalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_roles_Code",
                schema: "access",
                table: "roles",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_ExpiresAt",
                schema: "access",
                table: "sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_UserKey",
                schema: "access",
                table: "sessions",
                column: "UserKey");

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                schema: "access",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_ExternalKey",
                schema: "access",
                table: "users",
                column: "ExternalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_OrganizationKey",
                schema: "access",
                table: "users",
                column: "OrganizationKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contract_services",
                schema: "access");

            migrationBuilder.DropTable(
                name: "employees",
                schema: "access");

            migrationBuilder.DropTable(
                name: "functions",
                schema: "access");

            migrationBuilder.DropTable(
                name: "organizations",
                schema: "access");

            migrationBuilder.DropTable(
                name: "permissions",
                schema: "access");

            migrationBuilder.DropTable(
                name: "requests",
                schema: "access");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "access");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "access");

            migrationBuilder.DropTable(
                name: "sessions",
                schema: "access");

            migrationBuilder.DropTable(
                name: "user_roles",
                schema: "access");

            migrationBuilder.DropTable(
                name: "users",
                schema: "access");
        }
    }
}
