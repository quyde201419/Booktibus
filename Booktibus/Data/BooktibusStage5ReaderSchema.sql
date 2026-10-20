/* ============================================================================
   BOOKTIBUS — STAGE 5 SAFE SQL SERVER SCHEMA EXTENSION (READER FEATURES)
   Database: BooktibusDB
   New Tables:
     1. dbo.Books            (Catalog for Book Search & Book Details)
     2. dbo.ReaderFavorites  (Personal Favorite Books isolated by UserId)
     3. dbo.BorrowRecords    (Borrowing & Return transactions isolated by UserId)
   Safety Rules:
     - Checks IF OBJECT_ID(...) IS NULL before creating any table.
     - Does NOT drop, truncate, or overwrite existing tables or user data.
     - Initial sample catalog books are explicitly flagged with IsSampleData = 1.
   ============================================================================ */

USE [BooktibusDB];
GO

-- 1. Create Books table if it does not exist
IF OBJECT_ID(N'dbo.Books', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[Books]
    (
        [BookId]          INT            IDENTITY(1,1) NOT NULL,
        [BookCode]        NVARCHAR(30)   NOT NULL,
        [Title]           NVARCHAR(200)  NOT NULL,
        [Author]          NVARCHAR(150)  NOT NULL,
        [Category]        NVARCHAR(80)   NOT NULL,
        [Publisher]       NVARCHAR(120)  NULL,
        [PublishYear]     INT            NULL,
        [Isbn]            NVARCHAR(30)   NULL,
        [TotalCopies]     INT            NOT NULL CONSTRAINT [DF_Books_TotalCopies] DEFAULT (1),
        [AvailableCopies] INT            NOT NULL CONSTRAINT [DF_Books_AvailableCopies] DEFAULT (1),
        [ShelfLocation]   NVARCHAR(50)   NULL,
        [Description]     NVARCHAR(1000) NULL,
        [IsSampleData]    BIT            NOT NULL CONSTRAINT [DF_Books_IsSampleData] DEFAULT (0),
        [CreatedAtUtc]    DATETIME2(0)   NOT NULL CONSTRAINT [DF_Books_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT [PK_Books] PRIMARY KEY CLUSTERED ([BookId] ASC),
        CONSTRAINT [UQ_Books_BookCode] UNIQUE NONCLUSTERED ([BookCode] ASC),
        CONSTRAINT [CK_Books_CopiesValid] CHECK ([TotalCopies] >= 0 AND [AvailableCopies] >= 0 AND [AvailableCopies] <= [TotalCopies])
    );
    PRINT N'Created table [dbo].[Books].';
END
GO

-- 2. Create ReaderFavorites table if it does not exist (Strictly scoped to UserId)
IF OBJECT_ID(N'dbo.ReaderFavorites', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ReaderFavorites]
    (
        [FavoriteId] INT          IDENTITY(1,1) NOT NULL,
        [UserId]     INT          NOT NULL,
        [BookId]     INT          NOT NULL,
        [AddedAtUtc] DATETIME2(0) NOT NULL CONSTRAINT [DF_ReaderFavorites_AddedAtUtc] DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT [PK_ReaderFavorites] PRIMARY KEY CLUSTERED ([FavoriteId] ASC),
        CONSTRAINT [UQ_ReaderFavorites_User_Book] UNIQUE NONCLUSTERED ([UserId] ASC, [BookId] ASC),
        CONSTRAINT [FK_ReaderFavorites_Users] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]),
        CONSTRAINT [FK_ReaderFavorites_Books] FOREIGN KEY ([BookId]) REFERENCES [dbo].[Books] ([BookId])
    );
    PRINT N'Created table [dbo].[ReaderFavorites].';
END
GO

-- 3. Create BorrowRecords table if it does not exist (Scoped to UserId & ReaderCardId)
IF OBJECT_ID(N'dbo.BorrowRecords', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[BorrowRecords]
    (
        [BorrowRecordId] INT           IDENTITY(1,1) NOT NULL,
        [RecordCode]     NVARCHAR(30)  NOT NULL,
        [UserId]         INT           NOT NULL,
        [ReaderCardId]   NVARCHAR(30)  NOT NULL,
        [BookId]         INT           NOT NULL,
        [BorrowedAtUtc]  DATETIME2(0)  NOT NULL CONSTRAINT [DF_BorrowRecords_BorrowedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [DueAtUtc]       DATETIME2(0)  NOT NULL,
        [ReturnedAtUtc]  DATETIME2(0)  NULL,
        [BorrowStatus]   NVARCHAR(30)  NOT NULL CONSTRAINT [DF_BorrowRecords_Status] DEFAULT (N'Borrowed'),
        [Notes]          NVARCHAR(400) NULL,

        CONSTRAINT [PK_BorrowRecords] PRIMARY KEY CLUSTERED ([BorrowRecordId] ASC),
        CONSTRAINT [UQ_BorrowRecords_RecordCode] UNIQUE NONCLUSTERED ([RecordCode] ASC),
        CONSTRAINT [FK_BorrowRecords_Users] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]),
        CONSTRAINT [FK_BorrowRecords_Books] FOREIGN KEY ([BookId]) REFERENCES [dbo].[Books] ([BookId]),
        CONSTRAINT [CK_BorrowRecords_StatusValid] CHECK ([BorrowStatus] IN (N'Borrowed', N'Returned', N'Overdue'))
    );
    PRINT N'Created table [dbo].[BorrowRecords].';
END
GO

-- 4. Seed clearly marked Sample Catalog Books (IsSampleData = 1) ONLY if dbo.Books is empty
IF NOT EXISTS (SELECT 1 FROM [dbo].[Books])
BEGIN
    INSERT INTO [dbo].[Books]
        ([BookCode], [Title], [Author], [Category], [Publisher], [PublishYear], [Isbn], [TotalCopies], [AvailableCopies], [ShelfLocation], [Description], [IsSampleData])
    VALUES
        (N'BK-SMP-001', N'Clean Architecture: A Craftsman''s Guide to Software Structure', N'Robert C. Martin', N'Công nghệ thông tin / Software Engineering', N'Prentice Hall', 2017, N'978-0134494166', 5, 4, N'Kệ A1-01', N'[Dữ liệu mẫu thử nghiệm giao diện] Phân tích các nguyên tắc thiết kế kiến trúc phần mềm bền vững, tách biệt nghiệp vụ và giao diện.', 1),
        (N'BK-SMP-002', N'Patterns of Enterprise Application Architecture', N'Martin Fowler', N'Công nghệ thông tin / Software Engineering', N'Addison-Wesley', 2002, N'978-0321127426', 3, 2, N'Kệ A1-02', N'[Dữ liệu mẫu thử nghiệm giao diện] Các mẫu kiến trúc kinh điển cho ứng dụng doanh nghiệp và xử lý dữ liệu.', 1),
        (N'BK-SMP-003', N'Cơ sở Dữ liệu & Hệ Quản trị SQL Server', N'Nguyễn Văn Minh', N'Cơ sở dữ liệu / Database Systems', N'NXB Khoa học và Kỹ thuật', 2022, N'978-6046719201', 6, 6, N'Kệ B2-01', N'[Dữ liệu mẫu thử nghiệm giao diện] Giáo trình thiết kế cơ sở dữ liệu quan hệ, chuẩn hóa lược đồ, truy vấn T-SQL và bảo mật trên SQL Server.', 1),
        (N'BK-SMP-004', N'Designing Data-Intensive Applications', N'Martin Kleppmann', N'Cơ sở dữ liệu / Database Systems', N'O''Reilly Media', 2017, N'978-1449373320', 4, 0, N'Kệ B2-04', N'[Dữ liệu mẫu thử nghiệm giao diện] Kiến trúc hệ thống dữ liệu lớn, độ tin cậy, khả năng mở rộng và tính nhất quán giao dịch (Hiện đã mượn hết bản sao để kiểm thử trạng thái Hết sách).', 1),
        (N'BK-SMP-005', N'Lịch sử Văn minh Thế giới', N'Will Durant', N'Lịch sử & Văn hóa / History', N'NXB Khoa học Xã hội', 2020, N'978-6049581120', 4, 3, N'Kệ C1-05', N'[Dữ liệu mẫu thử nghiệm giao diện] Tác phẩm tổng hợp lịch sử, triết học, nghệ thuật và văn hóa nhân loại qua các thời kỳ.', 1),
        (N'BK-SMP-006', N'Tư duy Nhanh và Chậm (Thinking, Fast and Slow)', N'Daniel Kahneman', N'Kinh tế & Tâm lý / Psychology', N'NXB Thế Giới', 2019, N'978-6047758104', 5, 5, N'Kệ D3-02', N'[Dữ liệu mẫu thử nghiệm giao diện] Nghiên cứu về hai hệ thống tư duy chi phối cách con người đưa ra phán đoán và quyết định.', 1);

    PRINT N'Seeded 6 sample catalog books (explicitly marked with IsSampleData = 1).';
END
GO

PRINT N'Stage 5 schema verification completed successfully.';
GO

