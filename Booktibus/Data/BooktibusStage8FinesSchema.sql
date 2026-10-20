/* ============================================================================
   BOOKTIBUS — STAGE 8 SAFE SQL SERVER SCHEMA EXTENSION (FINES MANAGEMENT)
   Database: BooktibusDB
   New Tables:
     1. dbo.FineSettings   (Configurable fine policy rates, default 5,000 VND/day)
     2. dbo.Fines          (Official overdue fine records linked to BorrowRecords & Users)
     3. dbo.FineAuditLogs  (Audit history for fine creation, adjustments, and waivers)
   Safety Rules:
     - Checks IF OBJECT_ID(...) IS NULL before creating any table or constraint.
     - Does NOT drop, truncate, or modify existing tables or data (Users, Readers,
       Books, ReaderFavorites, BorrowRecords, PasswordResetOtps).
     - Enforces UQ_Fines_BorrowRecord_FineType to prevent duplicate overdue fines
       for the same loan transaction.
   ============================================================================ */

USE [BooktibusDB];
GO

-- 1. Create FineSettings table if it does not exist (Stores configurable daily fine rate in VND)
IF OBJECT_ID(N'dbo.FineSettings', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[FineSettings]
    (
        [SettingKey]          NVARCHAR(60)   NOT NULL,
        [SettingValueDecimal] DECIMAL(18, 0) NOT NULL,
        [Description]         NVARCHAR(300)  NULL,
        [UpdatedAtUtc]        DATETIME2(0)   NOT NULL CONSTRAINT [DF_FineSettings_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedByUserId]     INT            NULL,

        CONSTRAINT [PK_FineSettings] PRIMARY KEY CLUSTERED ([SettingKey] ASC),
        CONSTRAINT [FK_FineSettings_Users] FOREIGN KEY ([UpdatedByUserId]) REFERENCES [dbo].[Users] ([UserId]),
        CONSTRAINT [CK_FineSettings_NonNegative] CHECK ([SettingValueDecimal] >= 0)
    );
    PRINT N'Created table [dbo].[FineSettings].';
END
GO

-- Seed default OverdueDailyRateVnd = 5000 VND if not already present
IF NOT EXISTS (SELECT 1 FROM [dbo].[FineSettings] WHERE [SettingKey] = N'OverdueDailyRateVnd')
BEGIN
    INSERT INTO [dbo].[FineSettings] ([SettingKey], [SettingValueDecimal], [Description])
    VALUES (N'OverdueDailyRateVnd', 5000, N'Mức phạt quá hạn mặc định (VNĐ / cuốn sách / ngày quá hạn).');
    PRINT N'Seeded default OverdueDailyRateVnd = 5000 VND.';
END
GO

-- 2. Create Fines table if it does not exist
IF OBJECT_ID(N'dbo.Fines', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Fines]
    (
        [FineId]           INT            IDENTITY(1,1) NOT NULL,
        [FineCode]         NVARCHAR(30)   NOT NULL,
        [BorrowRecordId]   INT            NOT NULL,
        [UserId]           INT            NOT NULL,
        [FineType]         NVARCHAR(30)   NOT NULL CONSTRAINT [DF_Fines_FineType] DEFAULT (N'Overdue'),
        [DaysOverdue]      INT            NOT NULL,
        [DailyRateVnd]     DECIMAL(18, 0) NOT NULL,
        [Amount]           DECIMAL(18, 0) NOT NULL,
        [FineStatus]       NVARCHAR(30)   NOT NULL CONSTRAINT [DF_Fines_FineStatus] DEFAULT (N'Unpaid'),
        [CreatedAtUtc]     DATETIME2(0)   NOT NULL CONSTRAINT [DF_Fines_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [UpdatedAtUtc]     DATETIME2(0)   NULL,
        [PaidAtUtc]        DATETIME2(0)   NULL,
        [WaivedAtUtc]      DATETIME2(0)   NULL,
        [ResolvedByUserId] INT            NULL,
        [Notes]            NVARCHAR(500)  NULL,

        CONSTRAINT [PK_Fines] PRIMARY KEY CLUSTERED ([FineId] ASC),
        CONSTRAINT [UQ_Fines_FineCode] UNIQUE NONCLUSTERED ([FineCode] ASC),
        CONSTRAINT [UQ_Fines_BorrowRecord_FineType] UNIQUE NONCLUSTERED ([BorrowRecordId] ASC, [FineType] ASC),
        CONSTRAINT [FK_Fines_BorrowRecords] FOREIGN KEY ([BorrowRecordId]) REFERENCES [dbo].[BorrowRecords] ([BorrowRecordId]),
        CONSTRAINT [FK_Fines_Users] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]),
        CONSTRAINT [FK_Fines_ResolvedByUsers] FOREIGN KEY ([ResolvedByUserId]) REFERENCES [dbo].[Users] ([UserId]),
        CONSTRAINT [CK_Fines_FineTypeValid] CHECK ([FineType] IN (N'Overdue')),
        CONSTRAINT [CK_Fines_FineStatusValid] CHECK ([FineStatus] IN (N'Unpaid', N'Paid', N'Waived')),
        CONSTRAINT [CK_Fines_DaysOverdueNonNegative] CHECK ([DaysOverdue] >= 0),
        CONSTRAINT [CK_Fines_DailyRateNonNegative] CHECK ([DailyRateVnd] >= 0),
        CONSTRAINT [CK_Fines_AmountNonNegative] CHECK ([Amount] >= 0)
    );

    CREATE NONCLUSTERED INDEX [IX_Fines_UserId_FineStatus]
        ON [dbo].[Fines] ([UserId] ASC, [FineStatus] ASC);

    PRINT N'Created table [dbo].[Fines].';
END
GO

-- 3. Create FineAuditLogs table if it does not exist (Tracks adjustments, waivers, and confirmations)
IF OBJECT_ID(N'dbo.FineAuditLogs', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[FineAuditLogs]
    (
        [AuditLogId]          INT            IDENTITY(1,1) NOT NULL,
        [FineId]              INT            NOT NULL,
        [BorrowRecordId]      INT            NOT NULL,
        [ActionType]          NVARCHAR(40)   NOT NULL,
        [PreviousAmount]      DECIMAL(18, 0) NULL,
        [NewAmount]           DECIMAL(18, 0) NOT NULL,
        [PreviousStatus]      NVARCHAR(30)   NULL,
        [NewStatus]           NVARCHAR(30)   NOT NULL,
        [Reason]              NVARCHAR(500)  NOT NULL,
        [PerformedByUserId]   INT            NULL,
        [PerformedByUsername] NVARCHAR(50)   NOT NULL,
        [CreatedAtUtc]        DATETIME2(0)   NOT NULL CONSTRAINT [DF_FineAuditLogs_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT [PK_FineAuditLogs] PRIMARY KEY CLUSTERED ([AuditLogId] ASC),
        CONSTRAINT [FK_FineAuditLogs_Fines] FOREIGN KEY ([FineId]) REFERENCES [dbo].[Fines] ([FineId]),
        CONSTRAINT [FK_FineAuditLogs_BorrowRecords] FOREIGN KEY ([BorrowRecordId]) REFERENCES [dbo].[BorrowRecords] ([BorrowRecordId]),
        CONSTRAINT [FK_FineAuditLogs_Users] FOREIGN KEY ([PerformedByUserId]) REFERENCES [dbo].[Users] ([UserId])
    );

    CREATE NONCLUSTERED INDEX [IX_FineAuditLogs_FineId_CreatedAtUtc]
        ON [dbo].[FineAuditLogs] ([FineId] ASC, [CreatedAtUtc] DESC);

    PRINT N'Created table [dbo].[FineAuditLogs].';
END
GO

PRINT N'Stage 8 Fines schema verification completed successfully.';
GO

