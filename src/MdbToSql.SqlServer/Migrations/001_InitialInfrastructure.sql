IF OBJECT_ID(N'dbo.MdbImportRun', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MdbImportRun
    (
        Id bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_MdbImportRun PRIMARY KEY,
        SourceMdb nvarchar(1000) NOT NULL,
        SourceMdbName nvarchar(260) NOT NULL,
        SourceMdbSize bigint NOT NULL,
        SourceMdbLastWriteTime datetime2 NOT NULL,
        SourceMdbHash varchar(64) NOT NULL,
        StartedAt datetime2 NOT NULL,
        FinishedAt datetime2 NULL,
        Status nvarchar(50) NOT NULL,
        ErrorMessage nvarchar(max) NULL
    );

    CREATE INDEX IX_MdbImportRun_Hash
        ON dbo.MdbImportRun (SourceMdbHash, Status);
END;

IF OBJECT_ID(N'dbo.MdbImportTableHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MdbImportTableHistory
    (
        Id bigint IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_MdbImportTableHistory PRIMARY KEY,
        RunId bigint NULL,
        SourceMdb nvarchar(1000) NOT NULL,
        SourceMdbName nvarchar(260) NOT NULL,
        SourceMdbSize bigint NOT NULL,
        SourceMdbLastWriteTime datetime2 NOT NULL,
        SourceMdbHash varchar(64) NOT NULL,
        TableName nvarchar(255) NOT NULL,
        StartedAt datetime2 NOT NULL,
        FinishedAt datetime2 NULL,
        SourceRowCount bigint NULL,
        ImportedRowCount bigint NULL,
        Status nvarchar(50) NOT NULL,
        Action nvarchar(50) NOT NULL,
        ErrorMessage nvarchar(max) NULL
    );

    CREATE INDEX IX_MdbImportTableHistory_Hash_Table
        ON dbo.MdbImportTableHistory (SourceMdbHash, TableName, Status);

    CREATE INDEX IX_MdbImportTableHistory_TableName
        ON dbo.MdbImportTableHistory (TableName, Status);
END;
