using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class _20261125175559_AddDiscogsCatalog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "DiscogsArtistId",
            table: "Songs",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "DiscogsMasterId",
            table: "Songs",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "DiscogsReleaseId",
            table: "Songs",
            type: "bigint",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "CatalogArtists",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                NameKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                RealName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Profile = table.Column<string>(type: "text", nullable: true),
                DiscographyFetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DiscographyItemCount = table.Column<int>(type: "integer", nullable: false),
                DiscogsId = table.Column<long>(type: "bigint", nullable: false),
                FetchState = table.Column<int>(type: "integer", nullable: false),
                DataQuality = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                DiscogsUri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RefreshAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogArtists", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CatalogBackfillRuns",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                DryRun = table.Column<bool>(type: "boolean", nullable: false),
                Cursor = table.Column<string>(type: "character varying(1100)", maxLength: 1100, nullable: true),
                Processed = table.Column<int>(type: "integer", nullable: false),
                Linked = table.Column<int>(type: "integer", nullable: false),
                Candidates = table.Column<int>(type: "integer", nullable: false),
                Unmatched = table.Column<int>(type: "integer", nullable: false),
                Failed = table.Column<int>(type: "integer", nullable: false),
                SongsUpdated = table.Column<int>(type: "integer", nullable: false),
                RequestsUsed = table.Column<int>(type: "integer", nullable: false),
                LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                StartedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RolledBackAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogBackfillRuns", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CatalogExternalIds",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                EntityKind = table.Column<int>(type: "integer", nullable: false),
                EntityId = table.Column<int>(type: "integer", nullable: false),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ExternalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogExternalIds", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CatalogIdentityDecisions",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                SubjectKind = table.Column<int>(type: "integer", nullable: false),
                SubjectKey = table.Column<string>(type: "character varying(1100)", maxLength: 1100, nullable: false),
                SubjectDisplay = table.Column<string>(type: "character varying(1100)", maxLength: 1100, nullable: false),
                TargetKind = table.Column<int>(type: "integer", nullable: false),
                DiscogsId = table.Column<long>(type: "bigint", nullable: false),
                TargetName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                Method = table.Column<int>(type: "integer", nullable: false),
                Confidence = table.Column<double>(type: "double precision", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                EvidenceJson = table.Column<string>(type: "text", nullable: true),
                RunId = table.Column<int>(type: "integer", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DecidedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogIdentityDecisions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CatalogImages",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                EntityKind = table.Column<int>(type: "integer", nullable: false),
                EntityId = table.Column<int>(type: "integer", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                ThumbnailUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                Width = table.Column<int>(type: "integer", nullable: false),
                Height = table.Column<int>(type: "integer", nullable: false),
                Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                LocalPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                ContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                ByteSize = table.Column<long>(type: "bigint", nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                SeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DownloadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogImages", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CatalogLabels",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                NameKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Profile = table.Column<string>(type: "text", nullable: true),
                ContactInfo = table.Column<string>(type: "text", nullable: true),
                ParentDiscogsId = table.Column<long>(type: "bigint", nullable: true),
                ParentName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                ReleasesFetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ReleaseItemCount = table.Column<int>(type: "integer", nullable: false),
                DiscogsId = table.Column<long>(type: "bigint", nullable: false),
                FetchState = table.Column<int>(type: "integer", nullable: false),
                DataQuality = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                DiscogsUri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RefreshAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogLabels", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CatalogMasters",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                TitleKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ArtistDisplay = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                ArtistKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Year = table.Column<int>(type: "integer", nullable: true),
                MainReleaseDiscogsId = table.Column<long>(type: "bigint", nullable: true),
                MostRecentReleaseDiscogsId = table.Column<long>(type: "bigint", nullable: true),
                Notes = table.Column<string>(type: "text", nullable: true),
                GenresJson = table.Column<string>(type: "text", nullable: true),
                Thumb = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                VersionsFetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                VersionCount = table.Column<int>(type: "integer", nullable: false),
                DiscogsId = table.Column<long>(type: "bigint", nullable: false),
                FetchState = table.Column<int>(type: "integer", nullable: false),
                DataQuality = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                DiscogsUri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RefreshAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogMasters", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CatalogRawSnapshots",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ResourceKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                DiscogsId = table.Column<long>(type: "bigint", nullable: false),
                PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PayloadJson = table.Column<string>(type: "text", nullable: false),
                MapperVersion = table.Column<int>(type: "integer", nullable: false),
                FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogRawSnapshots", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CatalogStyles",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                NameKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogStyles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CatalogUrls",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                EntityKind = table.Column<int>(type: "integer", nullable: false),
                EntityId = table.Column<int>(type: "integer", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogUrls", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CatalogArtistNameVariations",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ArtistId = table.Column<int>(type: "integer", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogArtistNameVariations", x => x.Id);
                table.ForeignKey(
                    name: "FK_CatalogArtistNameVariations_CatalogArtists_ArtistId",
                    column: x => x.ArtistId,
                    principalTable: "CatalogArtists",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CatalogArtistRelations",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ArtistId = table.Column<int>(type: "integer", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                RelatedDiscogsId = table.Column<long>(type: "bigint", nullable: false),
                RelatedName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Active = table.Column<bool>(type: "boolean", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogArtistRelations", x => x.Id);
                table.ForeignKey(
                    name: "FK_CatalogArtistRelations_CatalogArtists_ArtistId",
                    column: x => x.ArtistId,
                    principalTable: "CatalogArtists",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CatalogLabelSublabels",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                LabelId = table.Column<int>(type: "integer", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                DiscogsId = table.Column<long>(type: "bigint", nullable: false),
                Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogLabelSublabels", x => x.Id);
                table.ForeignKey(
                    name: "FK_CatalogLabelSublabels_CatalogLabels_LabelId",
                    column: x => x.LabelId,
                    principalTable: "CatalogLabels",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CatalogReleases",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                MasterId = table.Column<int>(type: "integer", nullable: true),
                MasterDiscogsId = table.Column<long>(type: "bigint", nullable: true),
                Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                TitleKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                ArtistDisplay = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                ArtistKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Year = table.Column<int>(type: "integer", nullable: true),
                Country = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                Released = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                Notes = table.Column<string>(type: "text", nullable: true),
                FormatSummary = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                LabelSummary = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                CatalogNumberSummary = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                GenresJson = table.Column<string>(type: "text", nullable: true),
                Thumb = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                DiscogsId = table.Column<long>(type: "bigint", nullable: false),
                FetchState = table.Column<int>(type: "integer", nullable: false),
                DataQuality = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                DiscogsUri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RefreshAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogReleases", x => x.Id);
                table.ForeignKey(
                    name: "FK_CatalogReleases_CatalogMasters_MasterId",
                    column: x => x.MasterId,
                    principalTable: "CatalogMasters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "CatalogMasterStyles",
            columns: table => new
            {
                MasterId = table.Column<int>(type: "integer", nullable: false),
                StyleId = table.Column<int>(type: "integer", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogMasterStyles", x => new { x.MasterId, x.StyleId });
                table.ForeignKey(
                    name: "FK_CatalogMasterStyles_CatalogMasters_MasterId",
                    column: x => x.MasterId,
                    principalTable: "CatalogMasters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CatalogMasterStyles_CatalogStyles_StyleId",
                    column: x => x.StyleId,
                    principalTable: "CatalogStyles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CatalogCredits",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ReleaseId = table.Column<int>(type: "integer", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                TrackSequence = table.Column<int>(type: "integer", nullable: true),
                ArtistDiscogsId = table.Column<long>(type: "bigint", nullable: false),
                Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Anv = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Join = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                Role = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Tracks = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogCredits", x => x.Id);
                table.ForeignKey(
                    name: "FK_CatalogCredits_CatalogReleases_ReleaseId",
                    column: x => x.ReleaseId,
                    principalTable: "CatalogReleases",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CatalogDiscographyEntries",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                OwnerKind = table.Column<int>(type: "integer", nullable: false),
                OwnerId = table.Column<int>(type: "integer", nullable: false),
                ItemKind = table.Column<int>(type: "integer", nullable: false),
                ItemDiscogsId = table.Column<long>(type: "bigint", nullable: false),
                MasterId = table.Column<int>(type: "integer", nullable: true),
                ReleaseId = table.Column<int>(type: "integer", nullable: true),
                Role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CatalogNumber = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                SeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogDiscographyEntries", x => x.Id);
                table.ForeignKey(
                    name: "FK_CatalogDiscographyEntries_CatalogMasters_MasterId",
                    column: x => x.MasterId,
                    principalTable: "CatalogMasters",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CatalogDiscographyEntries_CatalogReleases_ReleaseId",
                    column: x => x.ReleaseId,
                    principalTable: "CatalogReleases",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CatalogReleaseFormats",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ReleaseId = table.Column<int>(type: "integer", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Quantity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                Text = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                DescriptionsJson = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogReleaseFormats", x => x.Id);
                table.ForeignKey(
                    name: "FK_CatalogReleaseFormats_CatalogReleases_ReleaseId",
                    column: x => x.ReleaseId,
                    principalTable: "CatalogReleases",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CatalogReleaseIdentifiers",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ReleaseId = table.Column<int>(type: "integer", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Value = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogReleaseIdentifiers", x => x.Id);
                table.ForeignKey(
                    name: "FK_CatalogReleaseIdentifiers_CatalogReleases_ReleaseId",
                    column: x => x.ReleaseId,
                    principalTable: "CatalogReleases",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CatalogReleaseLabels",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ReleaseId = table.Column<int>(type: "integer", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                Kind = table.Column<int>(type: "integer", nullable: false),
                LabelDiscogsId = table.Column<long>(type: "bigint", nullable: false),
                Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                CatalogNumber = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                EntityType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                EntityTypeName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogReleaseLabels", x => x.Id);
                table.ForeignKey(
                    name: "FK_CatalogReleaseLabels_CatalogReleases_ReleaseId",
                    column: x => x.ReleaseId,
                    principalTable: "CatalogReleases",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CatalogReleaseStyles",
            columns: table => new
            {
                ReleaseId = table.Column<int>(type: "integer", nullable: false),
                StyleId = table.Column<int>(type: "integer", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogReleaseStyles", x => new { x.ReleaseId, x.StyleId });
                table.ForeignKey(
                    name: "FK_CatalogReleaseStyles_CatalogReleases_ReleaseId",
                    column: x => x.ReleaseId,
                    principalTable: "CatalogReleases",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CatalogReleaseStyles_CatalogStyles_StyleId",
                    column: x => x.StyleId,
                    principalTable: "CatalogStyles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "CatalogTracks",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ReleaseId = table.Column<int>(type: "integer", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                ParentSequence = table.Column<int>(type: "integer", nullable: true),
                Position = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                TitleKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Duration = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                DiscNumber = table.Column<int>(type: "integer", nullable: true),
                TrackNumber = table.Column<int>(type: "integer", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CatalogTracks", x => x.Id);
                table.ForeignKey(
                    name: "FK_CatalogTracks_CatalogReleases_ReleaseId",
                    column: x => x.ReleaseId,
                    principalTable: "CatalogReleases",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Songs_OwnerUserId_DiscogsMasterId",
            table: "Songs",
            columns: new[] { "OwnerUserId", "DiscogsMasterId" });

        migrationBuilder.CreateIndex(
            name: "IX_Songs_OwnerUserId_DiscogsReleaseId",
            table: "Songs",
            columns: new[] { "OwnerUserId", "DiscogsReleaseId" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogArtistNameVariations_ArtistId_Sequence",
            table: "CatalogArtistNameVariations",
            columns: new[] { "ArtistId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogArtistRelations_ArtistId_Kind_Sequence",
            table: "CatalogArtistRelations",
            columns: new[] { "ArtistId", "Kind", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogArtistRelations_RelatedDiscogsId",
            table: "CatalogArtistRelations",
            column: "RelatedDiscogsId");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogArtists_DiscogsId",
            table: "CatalogArtists",
            column: "DiscogsId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogArtists_FetchState_RefreshAfterUtc",
            table: "CatalogArtists",
            columns: new[] { "FetchState", "RefreshAfterUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogArtists_NameKey",
            table: "CatalogArtists",
            column: "NameKey");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogBackfillRuns_OwnerUserId_Kind_Status",
            table: "CatalogBackfillRuns",
            columns: new[] { "OwnerUserId", "Kind", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogCredits_ArtistDiscogsId_Kind",
            table: "CatalogCredits",
            columns: new[] { "ArtistDiscogsId", "Kind" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogCredits_ReleaseId_Sequence",
            table: "CatalogCredits",
            columns: new[] { "ReleaseId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogDiscographyEntries_MasterId",
            table: "CatalogDiscographyEntries",
            column: "MasterId");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogDiscographyEntries_OwnerKind_OwnerId_ItemKind_ItemDi~",
            table: "CatalogDiscographyEntries",
            columns: new[] { "OwnerKind", "OwnerId", "ItemKind", "ItemDiscogsId", "Role" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogDiscographyEntries_OwnerKind_OwnerId_Sequence",
            table: "CatalogDiscographyEntries",
            columns: new[] { "OwnerKind", "OwnerId", "Sequence" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogDiscographyEntries_ReleaseId",
            table: "CatalogDiscographyEntries",
            column: "ReleaseId");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogExternalIds_EntityKind_EntityId",
            table: "CatalogExternalIds",
            columns: new[] { "EntityKind", "EntityId" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogExternalIds_Provider_EntityKind_ExternalId",
            table: "CatalogExternalIds",
            columns: new[] { "Provider", "EntityKind", "ExternalId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogIdentityDecisions_OwnerUserId_Status_CreatedAtUtc",
            table: "CatalogIdentityDecisions",
            columns: new[] { "OwnerUserId", "Status", "CreatedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogIdentityDecisions_OwnerUserId_SubjectKind_SubjectKey~",
            table: "CatalogIdentityDecisions",
            columns: new[] { "OwnerUserId", "SubjectKind", "SubjectKey", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogIdentityDecisions_RunId",
            table: "CatalogIdentityDecisions",
            column: "RunId");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogIdentityDecisions_TargetKind_DiscogsId",
            table: "CatalogIdentityDecisions",
            columns: new[] { "TargetKind", "DiscogsId" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogImages_EntityKind_EntityId_Sequence",
            table: "CatalogImages",
            columns: new[] { "EntityKind", "EntityId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogImages_Status_Id",
            table: "CatalogImages",
            columns: new[] { "Status", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogLabels_DiscogsId",
            table: "CatalogLabels",
            column: "DiscogsId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogLabels_FetchState_RefreshAfterUtc",
            table: "CatalogLabels",
            columns: new[] { "FetchState", "RefreshAfterUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogLabels_NameKey",
            table: "CatalogLabels",
            column: "NameKey");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogLabels_ParentDiscogsId",
            table: "CatalogLabels",
            column: "ParentDiscogsId");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogLabelSublabels_LabelId_Sequence",
            table: "CatalogLabelSublabels",
            columns: new[] { "LabelId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogMasters_ArtistKey_TitleKey",
            table: "CatalogMasters",
            columns: new[] { "ArtistKey", "TitleKey" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogMasters_DiscogsId",
            table: "CatalogMasters",
            column: "DiscogsId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogMasters_FetchState_RefreshAfterUtc",
            table: "CatalogMasters",
            columns: new[] { "FetchState", "RefreshAfterUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogMasters_TitleKey",
            table: "CatalogMasters",
            column: "TitleKey");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogMasterStyles_StyleId",
            table: "CatalogMasterStyles",
            column: "StyleId");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogRawSnapshots_ResourceKind_DiscogsId_FetchedAtUtc",
            table: "CatalogRawSnapshots",
            columns: new[] { "ResourceKind", "DiscogsId", "FetchedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogRawSnapshots_ResourceKind_DiscogsId_PayloadHash",
            table: "CatalogRawSnapshots",
            columns: new[] { "ResourceKind", "DiscogsId", "PayloadHash" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleaseFormats_Name",
            table: "CatalogReleaseFormats",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleaseFormats_ReleaseId_Sequence",
            table: "CatalogReleaseFormats",
            columns: new[] { "ReleaseId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleaseIdentifiers_ReleaseId_Sequence",
            table: "CatalogReleaseIdentifiers",
            columns: new[] { "ReleaseId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleaseIdentifiers_Type_Value",
            table: "CatalogReleaseIdentifiers",
            columns: new[] { "Type", "Value" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleaseLabels_LabelDiscogsId_Kind",
            table: "CatalogReleaseLabels",
            columns: new[] { "LabelDiscogsId", "Kind" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleaseLabels_ReleaseId_Sequence",
            table: "CatalogReleaseLabels",
            columns: new[] { "ReleaseId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleases_ArtistKey_TitleKey",
            table: "CatalogReleases",
            columns: new[] { "ArtistKey", "TitleKey" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleases_DiscogsId",
            table: "CatalogReleases",
            column: "DiscogsId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleases_FetchState_RefreshAfterUtc",
            table: "CatalogReleases",
            columns: new[] { "FetchState", "RefreshAfterUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleases_MasterDiscogsId",
            table: "CatalogReleases",
            column: "MasterDiscogsId");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleases_MasterId",
            table: "CatalogReleases",
            column: "MasterId");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleases_TitleKey",
            table: "CatalogReleases",
            column: "TitleKey");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleases_Year_Country",
            table: "CatalogReleases",
            columns: new[] { "Year", "Country" });

        migrationBuilder.CreateIndex(
            name: "IX_CatalogReleaseStyles_StyleId",
            table: "CatalogReleaseStyles",
            column: "StyleId");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogStyles_NameKey",
            table: "CatalogStyles",
            column: "NameKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogTracks_ReleaseId_Sequence",
            table: "CatalogTracks",
            columns: new[] { "ReleaseId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CatalogTracks_TitleKey",
            table: "CatalogTracks",
            column: "TitleKey");

        migrationBuilder.CreateIndex(
            name: "IX_CatalogUrls_EntityKind_EntityId_Sequence",
            table: "CatalogUrls",
            columns: new[] { "EntityKind", "EntityId", "Sequence" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CatalogArtistNameVariations");

        migrationBuilder.DropTable(
            name: "CatalogArtistRelations");

        migrationBuilder.DropTable(
            name: "CatalogBackfillRuns");

        migrationBuilder.DropTable(
            name: "CatalogCredits");

        migrationBuilder.DropTable(
            name: "CatalogDiscographyEntries");

        migrationBuilder.DropTable(
            name: "CatalogExternalIds");

        migrationBuilder.DropTable(
            name: "CatalogIdentityDecisions");

        migrationBuilder.DropTable(
            name: "CatalogImages");

        migrationBuilder.DropTable(
            name: "CatalogLabelSublabels");

        migrationBuilder.DropTable(
            name: "CatalogMasterStyles");

        migrationBuilder.DropTable(
            name: "CatalogRawSnapshots");

        migrationBuilder.DropTable(
            name: "CatalogReleaseFormats");

        migrationBuilder.DropTable(
            name: "CatalogReleaseIdentifiers");

        migrationBuilder.DropTable(
            name: "CatalogReleaseLabels");

        migrationBuilder.DropTable(
            name: "CatalogReleaseStyles");

        migrationBuilder.DropTable(
            name: "CatalogTracks");

        migrationBuilder.DropTable(
            name: "CatalogUrls");

        migrationBuilder.DropTable(
            name: "CatalogArtists");

        migrationBuilder.DropTable(
            name: "CatalogLabels");

        migrationBuilder.DropTable(
            name: "CatalogStyles");

        migrationBuilder.DropTable(
            name: "CatalogReleases");

        migrationBuilder.DropTable(
            name: "CatalogMasters");

        migrationBuilder.DropIndex(
            name: "IX_Songs_OwnerUserId_DiscogsMasterId",
            table: "Songs");

        migrationBuilder.DropIndex(
            name: "IX_Songs_OwnerUserId_DiscogsReleaseId",
            table: "Songs");

        migrationBuilder.DropColumn(
            name: "DiscogsArtistId",
            table: "Songs");

        migrationBuilder.DropColumn(
            name: "DiscogsMasterId",
            table: "Songs");

        migrationBuilder.DropColumn(
            name: "DiscogsReleaseId",
            table: "Songs");
    }
}
