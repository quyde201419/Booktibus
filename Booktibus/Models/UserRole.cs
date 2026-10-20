namespace Booktibus.Models
{
    /// <summary>
    /// Defines the three core user roles in Booktibus.
    /// </summary>
    public enum UserRole
    {
        Reader = 0,
        Librarian = 1,
        Administrator = 2
    }

    /// <summary>
    /// Identifies the top-level navigation pages in the main window sidebar.
    /// </summary>
    public enum NavigationPageKey
    {
        Dashboard = 0,
        Books = 1,
        Favorites = 2,
        Readers = 3,
        BorrowingReturns = 4,
        ReaderProfile = 5,
        Fines = 6,
        Reports = 7,
        Settings = 8
    }
}

