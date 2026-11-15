/* ============================================================================
   BOOKTIBUS — CIRCULATION & RESERVATION SCHEMA EXTENSION
   Database: BooktibusDB
   Changes:
     1. Adds [RenewalCount] column to [dbo].[BorrowRecords] (max 2 renewals/loan)
     2. Creates [dbo].[BookReservations] table for Book Reservation / Hold when
        AvailableCopies == 0
   Safety Rules:
     - Checks IF COL_LENGTH / OBJECT_ID IS NULL before altering/creating.
     - Does NOT drop, truncate, or overwrite existing tables or user data.
   ============================================================================ */

USE [BooktibusDB];
GO

-- 1. Add RenewalCount column to dbo.BorrowRecords if it does not exist
IF COL_LENGTH(N'dbo.BorrowRecords', N'RenewalCount') IS NULL
BEGIN
    ALTER TABLE [dbo].[BorrowRecords]
    ADD [RenewalCount] INT NOT NULL CONSTRAINT [DF_BorrowRecords_RenewalCount] DEFAULT (0);

    PRINT N'Added column [RenewalCount] to [dbo].[BorrowRecords].';
END
GO

-- 2. Create dbo.BookReservations table if it does not exist
IF OBJECT_ID(N'dbo.BookReservations', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[BookReservations]
    (
        [ReservationId]           INT           IDENTITY(1,1) NOT NULL,
        [ReservationCode]         NVARCHAR(30)  NOT NULL,
        [BookId]                  INT           NOT NULL,
        [UserId]                  INT           NOT NULL,
        [ReaderCardId]            NVARCHAR(30)  NOT NULL,
        [ReservedAtUtc]           DATETIME2(0)  NOT NULL CONSTRAINT [DF_BookReservations_ReservedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [ReservationStatus]       NVARCHAR(30)  NOT NULL CONSTRAINT [DF_BookReservations_Status] DEFAULT (N'Pending'),
        [ReadyAtUtc]              DATETIME2(0)  NULL,
        [ExpiresAtUtc]            DATETIME2(0)  NULL,
        [FulfilledBorrowRecordId] INT           NULL,
        [Notes]                   NVARCHAR(400) NULL,

        CONSTRAINT [PK_BookReservations] PRIMARY KEY CLUSTERED ([ReservationId] ASC),
        CONSTRAINT [UQ_BookReservations_Code] UNIQUE NONCLUSTERED ([ReservationCode] ASC),
        CONSTRAINT [FK_BookReservations_Books] FOREIGN KEY ([BookId]) REFERENCES [dbo].[Books] ([BookId]),
        CONSTRAINT [FK_BookReservations_Users] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]),
        CONSTRAINT [CK_BookReservations_StatusValid] CHECK ([ReservationStatus] IN (N'Pending', N'Ready', N'Fulfilled', N'Cancelled'))
    );

    CREATE NONCLUSTERED INDEX [IX_BookReservations_BookId_Status]
        ON [dbo].[BookReservations] ([BookId] ASC, [ReservationStatus] ASC, [ReservedAtUtc] ASC);

    PRINT N'Created table [dbo].[BookReservations].';
END
GO
