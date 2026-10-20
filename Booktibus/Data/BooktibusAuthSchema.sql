/* ============================================================================
   BOOKTIBUS — SAFE, IDEMPOTENT SQL SERVER INITIALIZATION & MIGRATION SCRIPT
   Database: BooktibusDB
   Tables  : dbo.Roles, dbo.Users, dbo.Readers
   Notes   :
   - Checks object existence before creation or alteration (IF NOT EXISTS / IF EXISTS).
   - Does NOT drop or overwrite existing tables or user data.
   - Allows multiple user accounts to share the same Email address while keeping
     Username, UserId, and ReaderCardId strictly unique.
   - Safely migrates existing databases by dropping any UNIQUE constraint or
     unique index on dbo.Users.Email without touching existing rows.
   ============================================================================ */

-- 1. Create Database BooktibusDB if it does not exist
IF DB_ID(N'BooktibusDB') IS NULL
BEGIN
    CREATE DATABASE [BooktibusDB];
    PRINT N'Created database [BooktibusDB].';
END
ELSE
BEGIN
    PRINT N'Database [BooktibusDB] already exists. Skipping creation.';
END
GO

USE [BooktibusDB];
GO

-- 2. Create Roles reference table if it does not exist
IF OBJECT_ID(N'dbo.Roles', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Roles]
    (
        [RoleName]    NVARCHAR(30)  NOT NULL,
        [DisplayName] NVARCHAR(100) NOT NULL,
        [Description] NVARCHAR(255) NULL,
        CONSTRAINT [PK_Roles] PRIMARY KEY CLUSTERED ([RoleName] ASC),
        CONSTRAINT [CK_Roles_AllowedNames] CHECK ([RoleName] IN (N'Reader', N'Librarian', N'Administrator'))
    );
    PRINT N'Created table [dbo].[Roles].';
END
GO

-- Seed the 3 required system roles safely without overwriting existing rows
IF NOT EXISTS (SELECT 1 FROM [dbo].[Roles] WHERE [RoleName] = N'Reader')
BEGIN
    INSERT INTO [dbo].[Roles] ([RoleName], [DisplayName], [Description])
    VALUES (N'Reader', N'Độc giả / Library Reader', N'Can browse catalog and view own profile, borrowing history, and fines.');
END

IF NOT EXISTS (SELECT 1 FROM [dbo].[Roles] WHERE [RoleName] = N'Librarian')
BEGIN
    INSERT INTO [dbo].[Roles] ([RoleName], [DisplayName], [Description])
    VALUES (N'Librarian', N'Thủ thư / Librarian', N'Manages catalog, readers, borrowing/returns, fines, and reports.');
END

IF NOT EXISTS (SELECT 1 FROM [dbo].[Roles] WHERE [RoleName] = N'Administrator')
BEGIN
    INSERT INTO [dbo].[Roles] ([RoleName], [DisplayName], [Description])
    VALUES (N'Administrator', N'Quản trị viên / System Administrator', N'Full access to all modules and system settings.');
END
GO

-- 3. Create Users table if it does not exist (Username is UNIQUE; Email allows multiple accounts)
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Users]
    (
        [UserId]              INT            IDENTITY(1,1) NOT NULL,
        [Username]            NVARCHAR(50)   NOT NULL,
        [FullName]            NVARCHAR(120)  NOT NULL,
        [Email]               NVARCHAR(150)  NOT NULL,
        [Role]                NVARCHAR(30)   NOT NULL CONSTRAINT [DF_Users_Role] DEFAULT (N'Reader'),
        [PasswordHash]        NVARCHAR(128)  NOT NULL,
        [PasswordSalt]        NVARCHAR(128)  NOT NULL,
        [IsActive]            BIT            NOT NULL CONSTRAINT [DF_Users_IsActive] DEFAULT (1),
        [FailedLoginAttempts] INT            NOT NULL CONSTRAINT [DF_Users_FailedLoginAttempts] DEFAULT (0),
        [LockoutEndUtc]       DATETIME2(0)   NULL,
        [CreatedAtUtc]        DATETIME2(0)   NOT NULL CONSTRAINT [DF_Users_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([UserId] ASC),
        CONSTRAINT [UQ_Users_Username] UNIQUE NONCLUSTERED ([Username] ASC),
        CONSTRAINT [FK_Users_Roles] FOREIGN KEY ([Role]) REFERENCES [dbo].[Roles] ([RoleName]),
        CONSTRAINT [CK_Users_RoleValid] CHECK ([Role] IN (N'Reader', N'Librarian', N'Administrator')),
        CONSTRAINT [CK_Users_FailedAttemptsNonNegative] CHECK ([FailedLoginAttempts] >= 0)
    );
    PRINT N'Created table [dbo].[Users].';
END
GO

-- 4. Safe Migration for existing databases:
--    Remove any UNIQUE constraint or standalone unique index on dbo.Users.Email
--    while keeping PK_Users, UQ_Users_Username, and all other constraints intact.
IF OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
BEGIN
    -- 4A. Drop named UNIQUE constraint UQ_Users_Email if present
    IF EXISTS (
        SELECT 1
        FROM sys.key_constraints
        WHERE [name] = N'UQ_Users_Email'
          AND [parent_object_id] = OBJECT_ID(N'dbo.Users')
          AND [type] = 'UQ'
    )
    BEGIN
        ALTER TABLE [dbo].[Users] DROP CONSTRAINT [UQ_Users_Email];
        PRINT N'Migrated [dbo].[Users]: Dropped UNIQUE constraint [UQ_Users_Email].';
    END

    -- 4B. Check for any other UNIQUE constraint on dbo.Users(Email)
    DECLARE @OtherEmailConstraint NVARCHAR(128);
    SELECT TOP (1) @OtherEmailConstraint = kc.[name]
    FROM sys.key_constraints kc
    INNER JOIN sys.index_columns ic
        ON kc.[parent_object_id] = ic.[object_id]
       AND kc.[unique_index_id] = ic.[index_id]
    INNER JOIN sys.columns c
        ON ic.[object_id] = c.[object_id]
       AND ic.[column_id] = c.[column_id]
    WHERE kc.[parent_object_id] = OBJECT_ID(N'dbo.Users')
      AND kc.[type] = 'UQ'
      AND c.[name] = N'Email';

    IF @OtherEmailConstraint IS NOT NULL
    BEGIN
        DECLARE @DropConstraintSql NVARCHAR(400) =
            N'ALTER TABLE [dbo].[Users] DROP CONSTRAINT ' + QUOTENAME(@OtherEmailConstraint) + N';';
        EXEC sp_executesql @DropConstraintSql;
        PRINT N'Migrated [dbo].[Users]: Dropped UNIQUE constraint ' + @OtherEmailConstraint + N' on Email.';
    END

    -- 4C. Check for any standalone unique index (not a constraint) on dbo.Users(Email)
    DECLARE @UniqueEmailIndex NVARCHAR(128);
    SELECT TOP (1) @UniqueEmailIndex = i.[name]
    FROM sys.indexes i
    INNER JOIN sys.index_columns ic
        ON i.[object_id] = ic.[object_id]
       AND i.[index_id] = ic.[index_id]
    INNER JOIN sys.columns c
        ON ic.[object_id] = c.[object_id]
       AND ic.[column_id] = c.[column_id]
    WHERE i.[object_id] = OBJECT_ID(N'dbo.Users')
      AND i.[is_unique] = 1
      AND i.[is_primary_key] = 0
      AND i.[is_unique_constraint] = 0
      AND c.[name] = N'Email';

    IF @UniqueEmailIndex IS NOT NULL
    BEGIN
        DECLARE @DropIndexSql NVARCHAR(400) =
            N'DROP INDEX ' + QUOTENAME(@UniqueEmailIndex) + N' ON [dbo].[Users];';
        EXEC sp_executesql @DropIndexSql;
        PRINT N'Migrated [dbo].[Users]: Dropped standalone unique index ' + @UniqueEmailIndex + N' on Email.';
    END
END
GO

-- 5. Create Readers table if it does not exist
IF OBJECT_ID(N'dbo.Readers', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Readers]
    (
        [ReaderId]         INT           IDENTITY(1,1) NOT NULL,
        [UserId]           INT           NOT NULL,
        [ReaderCardId]     NVARCHAR(30)  NOT NULL,
        [FullName]         NVARCHAR(120) NOT NULL,
        [Email]            NVARCHAR(150) NOT NULL,
        [PhoneNumber]      NVARCHAR(25)  NULL,
        [MembershipStatus] NVARCHAR(30)  NOT NULL CONSTRAINT [DF_Readers_Status] DEFAULT (N'Active'),
        [RegisteredAtUtc]  DATETIME2(0)  NOT NULL CONSTRAINT [DF_Readers_RegisteredAtUtc] DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT [PK_Readers] PRIMARY KEY CLUSTERED ([ReaderId] ASC),
        CONSTRAINT [UQ_Readers_UserId] UNIQUE NONCLUSTERED ([UserId] ASC),
        CONSTRAINT [UQ_Readers_ReaderCardId] UNIQUE NONCLUSTERED ([ReaderCardId] ASC),
        CONSTRAINT [FK_Readers_Users] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId])
    );
    PRINT N'Created table [dbo].[Readers].';
END
GO

-- 6. Create PasswordResetOtps table if it does not exist (Forgot Password OTP storage)
--    Stores PBKDF2-SHA256 hashed OTPs (never plaintext), linked strictly to UserId
--    (supporting multiple accounts sharing the same email), with 5-minute expiry,
--    attempt limit, and single-use invalidation.
IF OBJECT_ID(N'dbo.PasswordResetOtps', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[PasswordResetOtps]
    (
        [OtpRequestId] INT           IDENTITY(1,1) NOT NULL,
        [UserId]       INT           NOT NULL,
        [OtpHash]      NVARCHAR(128) NOT NULL,
        [OtpSalt]      NVARCHAR(128) NOT NULL,
        [CreatedAtUtc] DATETIME2(0)  NOT NULL CONSTRAINT [DF_PasswordResetOtps_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [ExpiresAtUtc] DATETIME2(0)  NOT NULL,
        [AttemptCount] INT           NOT NULL CONSTRAINT [DF_PasswordResetOtps_AttemptCount] DEFAULT (0),
        [MaxAttempts]  INT           NOT NULL CONSTRAINT [DF_PasswordResetOtps_MaxAttempts] DEFAULT (5),
        [IsUsed]       BIT           NOT NULL CONSTRAINT [DF_PasswordResetOtps_IsUsed] DEFAULT (0),
        [UsedAtUtc]    DATETIME2(0)  NULL,

        CONSTRAINT [PK_PasswordResetOtps] PRIMARY KEY CLUSTERED ([OtpRequestId] ASC),
        CONSTRAINT [FK_PasswordResetOtps_Users] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]),
        CONSTRAINT [CK_PasswordResetOtps_AttemptsNonNegative] CHECK ([AttemptCount] >= 0 AND [MaxAttempts] >= 1)
    );

    CREATE NONCLUSTERED INDEX [IX_PasswordResetOtps_UserId_CreatedAtUtc]
        ON [dbo].[PasswordResetOtps] ([UserId] ASC, [CreatedAtUtc] DESC);

    PRINT N'Created table [dbo].[PasswordResetOtps].';
END
GO

PRINT N'BooktibusDB schema and email-sharing migration verification completed successfully.';
GO

