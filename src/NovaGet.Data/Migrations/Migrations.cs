namespace NovaGet.Data.Migrations;

/// <summary>The ordered schema history. Append new migrations; never edit a shipped one.</summary>
public static class SchemaMigrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        new(1, "Initial schema", V1InitialSchema),
    ];

    public static int LatestVersion => All[^1].Version;

    private const string V1InitialSchema = """
        CREATE TABLE Category (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            name           TEXT    NOT NULL,
            parentId       INTEGER NULL REFERENCES Category(id) ON DELETE SET NULL,
            extensions     TEXT    NOT NULL DEFAULT '',
            defaultSaveDir TEXT    NULL,
            icon           TEXT    NULL,
            isBuiltIn      INTEGER NOT NULL DEFAULT 0,
            sortOrder      INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE Queue (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            name              TEXT    NOT NULL COLLATE NOCASE UNIQUE,
            isBuiltIn         INTEGER NOT NULL DEFAULT 0,
            simultaneousCount INTEGER NOT NULL DEFAULT 1,
            scheduleJson      TEXT    NOT NULL DEFAULT '{}',
            isSyncQueue       INTEGER NOT NULL DEFAULT 0,
            syncIntervalMin   INTEGER NOT NULL DEFAULT 60
        );

        CREATE TABLE Download (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            url                TEXT    NOT NULL,
            originalUrl        TEXT    NOT NULL,
            referrer           TEXT    NULL,
            fileName           TEXT    NOT NULL,
            savePath           TEXT    NOT NULL,
            categoryId         INTEGER NOT NULL DEFAULT 1 REFERENCES Category(id) ON DELETE SET DEFAULT,
            size               INTEGER NOT NULL DEFAULT -1,
            downloaded         INTEGER NOT NULL DEFAULT 0,
            status             INTEGER NOT NULL DEFAULT 0,
            resumeCapable      INTEGER NULL,
            description        TEXT    NULL,
            userAgent          TEXT    NULL,
            cookies            TEXT    NULL,
            authUser           TEXT    NULL,
            authPass           TEXT    NULL,
            maxConnections     INTEGER NULL,
            speedLimitKBps     INTEGER NULL,
            queueId            INTEGER NULL REFERENCES Queue(id) ON DELETE SET NULL,
            queuePosition      INTEGER NOT NULL DEFAULT 0,
            addedAt            TEXT    NOT NULL,
            lastTryAt          TEXT    NULL,
            completedAt        TEXT    NULL,
            lastError          TEXT    NULL,
            etag               TEXT    NULL,
            lastModified       TEXT    NULL,
            isStream           INTEGER NOT NULL DEFAULT 0,
            streamManifestJson TEXT    NULL,
            checksumAlgo       TEXT    NULL,
            checksumExpected   TEXT    NULL
        );
        CREATE INDEX IX_Download_Queue    ON Download(queueId, queuePosition);
        CREATE INDEX IX_Download_Category ON Download(categoryId);
        CREATE INDEX IX_Download_Status   ON Download(status);

        CREATE TABLE Segment (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            downloadId  INTEGER NOT NULL REFERENCES Download(id) ON DELETE CASCADE,
            startByte   INTEGER NOT NULL,
            endByte     INTEGER NOT NULL,
            currentByte INTEGER NOT NULL,
            state       INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IX_Segment_Download ON Segment(downloadId);

        CREATE TABLE GrabberProject (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            name         TEXT NOT NULL,
            settingsJson TEXT NOT NULL DEFAULT '{}',
            lastRunAt    TEXT NULL
        );

        CREATE TABLE GrabberResult (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            projectId INTEGER NOT NULL REFERENCES GrabberProject(id) ON DELETE CASCADE,
            url       TEXT    NOT NULL,
            type      TEXT    NULL,
            size      INTEGER NOT NULL DEFAULT -1,
            status    INTEGER NOT NULL DEFAULT 0,
            localPath TEXT    NULL
        );
        CREATE INDEX IX_GrabberResult_Project ON GrabberResult(projectId);
        CREATE UNIQUE INDEX UX_GrabberResult_ProjectUrl ON GrabberResult(projectId, url);

        CREATE TABLE SiteLogin (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            urlPattern    TEXT NOT NULL,
            user          TEXT NOT NULL DEFAULT '',
            passwordDpapi TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE ServerException (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            host           TEXT    NOT NULL COLLATE NOCASE UNIQUE,
            maxConnections INTEGER NOT NULL
        );

        INSERT INTO Category (id, name, parentId, extensions, isBuiltIn, sortOrder, icon) VALUES
            (1, 'General',    NULL, '', 1, 0, 'general'),
            (2, 'Compressed', 1, 'zip rar r0* r1* arj gz sit sitx sea ace bz2 7z tar tgz xz zst', 1, 1, 'compressed'),
            (3, 'Documents',  1, 'doc docx pdf ppt pptx xls xlsx txt rtf odt ods epub', 1, 2, 'documents'),
            (4, 'Music',      1, 'mp3 wav wma mpa ram ra aac aif m4a flac ogg opus', 1, 3, 'music'),
            (5, 'Programs',   1, 'exe msi msix apk iso img bin dmg', 1, 4, 'programs'),
            (6, 'Video',      1, 'avi mpg mpe mpeg asf wmv mov qt rm mp4 mkv flv m4v webm 3gp ts', 1, 5, 'video');

        INSERT INTO Queue (id, name, isBuiltIn, simultaneousCount, scheduleJson, isSyncQueue, syncIntervalMin) VALUES
            (1, 'Main download queue',   1, 1, '{}', 0, 60),
            (2, 'Synchronization queue', 1, 1, '{}', 1, 60);
        """;
}
