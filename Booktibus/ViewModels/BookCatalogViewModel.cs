using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Booktibus.Commands;
using Booktibus.Models;
using Booktibus.Services;

namespace Booktibus.ViewModels
{
    public class FilterOptionItem
    {
        public string Key { get; init; } = string.Empty;

        public string DisplayText { get; init; } = string.Empty;
    }

    /// <summary>
    /// ViewModel for Book Catalog Search, Book Details (Stage 5.1 &amp; 5.2)
    /// and Librarian/Administrator Book Catalog CRUD Management (Stage 6.3.1).
    /// All staff operations are guarded at the service layer by AppPermission.ManageCatalog.
    /// </summary>
    public class BookCatalogViewModel : PageViewModelBase
    {
        private readonly ReaderWorkspaceService _readerService;
        private readonly LibraryManagementService _libraryManagementService;
        private readonly AuthorizationService _authorizationService;
        private readonly Action<NavigationPageKey> _navigateCallback;
        private readonly Action<string> _statusFeedbackCallback;

        private string _searchKeyword = string.Empty;
        private FilterOptionItem? _selectedCategoryOption;
        private FilterOptionItem? _selectedAvailabilityOption;
        private BookItemModel? _selectedBook;
        private string _errorMessage = string.Empty;
        private bool _isSuppressingFilterReload;

        // Stage 6.3.1: Staff Book Management Form Fields
        private bool _isAddNewMode;
        private int? _editingBookId;
        private string _formBookCode = string.Empty;
        private string _formTitle = string.Empty;
        private string _formAuthor = string.Empty;
        private string _formCategory = string.Empty;
        private string _formPublisher = string.Empty;
        private string _formPublishYear = string.Empty;
        private string _formIsbn = string.Empty;
        private string _formTotalCopies = "1";
        private string _formShelfLocation = string.Empty;
        private string _formDescription = string.Empty;
        private string _staffActionFeedback = string.Empty;
        private bool _isStaffFeedbackSuccess;

        public BookCatalogViewModel(
            ReaderWorkspaceService readerService,
            LibraryManagementService libraryManagementService,
            AuthorizationService authorizationService,
            Action<NavigationPageKey> navigateCallback,
            Action<string> statusFeedbackCallback)
            : base(
                NavigationPageKey.Books,
                "Str.Page.Books.Title",
                "Str.Page.Books.Subtitle")
        {
            _readerService = readerService;
            _libraryManagementService = libraryManagementService;
            _authorizationService = authorizationService;
            _navigateCallback = navigateCallback;
            _statusFeedbackCallback = statusFeedbackCallback;

            Books = new ObservableCollection<BookItemModel>();
            CategoryOptions = new ObservableCollection<FilterOptionItem>();
            AvailabilityOptions = new ObservableCollection<FilterOptionItem>();

            SearchCommand = new RelayCommand(ExecuteSearch);
            ClearFiltersCommand = new RelayCommand(ExecuteClearFilters);
            ToggleFavoriteCommand = new RelayCommand(ExecuteToggleFavorite);
            ReserveBookCommand = new RelayCommand(ExecuteReserveBook);
            OpenFavoritesPageCommand = new RelayCommand(() => _navigateCallback(NavigationPageKey.Favorites));

            // Stage 6 Staff CRUD Commands
            StartAddNewBookCommand = new RelayCommand(ExecuteStartAddNewBook);
            LoadSelectedBookToEditCommand = new RelayCommand(ExecuteLoadSelectedBookToEdit);
            SaveBookCommand = new RelayCommand(ExecuteSaveBook);
            DeleteSelectedBookCommand = new RelayCommand(ExecuteDeleteSelectedBook);

            _readerService.FavoritesChanged += (_, _) => ReloadCatalogPreservingSelection();
            _libraryManagementService.CirculationChanged += (_, _) => ReloadCatalogPreservingSelection();
            _libraryManagementService.CatalogChanged += (_, _) =>
            {
                InitializeFilterOptions();
                ReloadCatalogPreservingSelection();
            };

            InitializeFilterOptions();
            LoadBooksFromDatabase();
        }

        public ObservableCollection<BookItemModel> Books { get; }

        public ObservableCollection<FilterOptionItem> CategoryOptions { get; }

        public ObservableCollection<FilterOptionItem> AvailabilityOptions { get; }

        public bool IsStaffCatalogManager => _authorizationService.HasPermission(AppPermission.ManageCatalog);

        public bool CanOpenReaderFavorites => _authorizationService.CanAccessPage(NavigationPageKey.Favorites);

        public string SearchKeyword
        {
            get => _searchKeyword;
            set => SetProperty(ref _searchKeyword, value);
        }

        public FilterOptionItem? SelectedCategoryOption
        {
            get => _selectedCategoryOption;
            set
            {
                if (SetProperty(ref _selectedCategoryOption, value) && !_isSuppressingFilterReload)
                {
                    LoadBooksFromDatabase();
                }
            }
        }

        public FilterOptionItem? SelectedAvailabilityOption
        {
            get => _selectedAvailabilityOption;
            set
            {
                if (SetProperty(ref _selectedAvailabilityOption, value) && !_isSuppressingFilterReload)
                {
                    LoadBooksFromDatabase();
                }
            }
        }

        public BookItemModel? SelectedBook
        {
            get => _selectedBook;
            set
            {
                if (SetProperty(ref _selectedBook, value))
                {
                    OnPropertyChanged(nameof(HasSelectedBook));
                    if (value is not null && !_isAddNewMode)
                    {
                        PopulateFormFromBook(value);
                    }
                }
            }
        }

        public bool HasSelectedBook => SelectedBook is not null;

        public bool IsCatalogEmpty => Books.Count == 0;

        public string ResultSummaryText => LocalizationService.IsVietnamese
            ? $"Hiển thị {Books.Count} đầu sách từ cơ sở dữ liệu BooktibusDB"
            : $"Showing {Books.Count} book title(s) from BooktibusDB";

        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                SetProperty(ref _errorMessage, value);
                OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

        // =====================================================================
        // STAGE 6.3.1: STAFF BOOK FORM PROPERTIES
        // =====================================================================

        public bool IsAddNewMode
        {
            get => _isAddNewMode;
            private set
            {
                if (SetProperty(ref _isAddNewMode, value))
                {
                    OnPropertyChanged(nameof(StaffFormModeTitle));
                    OnPropertyChanged(nameof(SaveButtonLabel));
                }
            }
        }

        public string StaffFormModeTitle => IsAddNewMode
            ? (LocalizationService.IsVietnamese ? "CHẾ ĐỘ: THÊM ĐẦU SÁCH MỚI VÀO KHO" : "MODE: ADD NEW BOOK TO CATALOG")
            : (LocalizationService.IsVietnamese
                ? $"CHẾ ĐỘ: CHỈNH SỬA ĐẦU SÁCH #{_editingBookId}"
                : $"MODE: EDITING BOOK #{_editingBookId}");

        public string SaveButtonLabel => IsAddNewMode
            ? (LocalizationService.IsVietnamese ? "+ Lưu Đầu sách Mới vào CSDL" : "+ Save New Book to DB")
            : (LocalizationService.IsVietnamese ? "✓ Cập nhật Thông tin & Số lượng Sách" : "✓ Update Book & Copy Count");

        public string FormBookCode
        {
            get => _formBookCode;
            set => SetProperty(ref _formBookCode, value);
        }

        public string FormTitle
        {
            get => _formTitle;
            set => SetProperty(ref _formTitle, value);
        }

        public string FormAuthor
        {
            get => _formAuthor;
            set => SetProperty(ref _formAuthor, value);
        }

        public string FormCategory
        {
            get => _formCategory;
            set => SetProperty(ref _formCategory, value);
        }

        public string FormPublisher
        {
            get => _formPublisher;
            set => SetProperty(ref _formPublisher, value);
        }

        public string FormPublishYear
        {
            get => _formPublishYear;
            set => SetProperty(ref _formPublishYear, value);
        }

        public string FormIsbn
        {
            get => _formIsbn;
            set => SetProperty(ref _formIsbn, value);
        }

        public string FormTotalCopies
        {
            get => _formTotalCopies;
            set => SetProperty(ref _formTotalCopies, value);
        }

        public string FormShelfLocation
        {
            get => _formShelfLocation;
            set => SetProperty(ref _formShelfLocation, value);
        }

        public string FormDescription
        {
            get => _formDescription;
            set => SetProperty(ref _formDescription, value);
        }

        public string StaffActionFeedback
        {
            get => _staffActionFeedback;
            private set
            {
                SetProperty(ref _staffActionFeedback, value);
                OnPropertyChanged(nameof(HasStaffFeedback));
            }
        }

        public bool IsStaffFeedbackSuccess
        {
            get => _isStaffFeedbackSuccess;
            private set => SetProperty(ref _isStaffFeedbackSuccess, value);
        }

        public bool HasStaffFeedback => !string.IsNullOrWhiteSpace(StaffActionFeedback);

        public ICommand SearchCommand { get; }

        public ICommand ClearFiltersCommand { get; }

        public ICommand ToggleFavoriteCommand { get; }

        public ICommand ReserveBookCommand { get; }

        public ICommand OpenFavoritesPageCommand { get; }

        public ICommand StartAddNewBookCommand { get; }

        public ICommand LoadSelectedBookToEditCommand { get; }

        public ICommand SaveBookCommand { get; }

        public ICommand DeleteSelectedBookCommand { get; }

        public override void OnLanguageUpdated()
        {
            base.OnLanguageUpdated();
            InitializeFilterOptions();
            foreach (BookItemModel book in Books)
            {
                book.RefreshLocalization();
            }
            OnPropertyChanged(nameof(ResultSummaryText));
            OnPropertyChanged(nameof(StaffFormModeTitle));
            OnPropertyChanged(nameof(SaveButtonLabel));
        }

        public void OnSessionUpdated()
        {
            OnPropertyChanged(nameof(IsStaffCatalogManager));
            OnPropertyChanged(nameof(CanOpenReaderFavorites));
            StaffActionFeedback = string.Empty;
            InitializeFilterOptions();
            LoadBooksFromDatabase();
        }

        private void ExecuteSearch()
        {
            LoadBooksFromDatabase();
            _statusFeedbackCallback(ResultSummaryText);
        }

        private void ExecuteClearFilters()
        {
            _isSuppressingFilterReload = true;
            SearchKeyword = string.Empty;
            SelectedCategoryOption = CategoryOptions.FirstOrDefault();
            SelectedAvailabilityOption = AvailabilityOptions.FirstOrDefault();
            _isSuppressingFilterReload = false;

            LoadBooksFromDatabase();
            _statusFeedbackCallback(LocalizationService.IsVietnamese
                ? "[Tra cứu sách] Đã đặt lại bộ lọc tìm kiếm."
                : "[Book Catalog] Reset search and filter criteria.");
        }

        private void ExecuteToggleFavorite(object? parameter)
        {
            BookItemModel? targetBook = parameter as BookItemModel ?? SelectedBook;
            if (targetBook is null)
            {
                return;
            }

            ServiceOperationResult result = _readerService.ToggleOwnFavorite(targetBook);
            _statusFeedbackCallback(result.Message);
        }

        private void ExecuteReserveBook(object? parameter)
        {
            BookItemModel? targetBook = parameter as BookItemModel ?? SelectedBook;
            if (targetBook is null)
            {
                return;
            }

            ServiceOperationResult result = _libraryManagementService.ReserveBook(targetBook.BookId);
            IsStaffFeedbackSuccess = result.IsSuccess;
            StaffActionFeedback = result.Message;
            _statusFeedbackCallback(result.Message);
        }

        private void ExecuteStartAddNewBook()
        {
            IsAddNewMode = true;
            _editingBookId = null;
            FormBookCode = $"BK-{DateTime.Now.Year}-{Books.Count + 1:D3}";
            FormTitle = string.Empty;
            FormAuthor = string.Empty;
            FormCategory = string.Empty;
            FormPublisher = string.Empty;
            FormPublishYear = DateTime.Now.Year.ToString();
            FormIsbn = string.Empty;
            FormTotalCopies = "3";
            FormShelfLocation = "Kệ A1-01";
            FormDescription = string.Empty;
            StaffActionFeedback = string.Empty;
            OnPropertyChanged(nameof(StaffFormModeTitle));
        }

        private void ExecuteLoadSelectedBookToEdit()
        {
            if (SelectedBook is null)
            {
                return;
            }

            IsAddNewMode = false;
            PopulateFormFromBook(SelectedBook);
            StaffActionFeedback = string.Empty;
        }

        private void ExecuteSaveBook()
        {
            StaffActionFeedback = string.Empty;

            BookUpsertRequest req = new()
            {
                BookId = IsAddNewMode ? null : _editingBookId,
                BookCode = FormBookCode,
                Title = FormTitle,
                Author = FormAuthor,
                Category = FormCategory,
                Publisher = FormPublisher,
                PublishYearText = FormPublishYear,
                Isbn = FormIsbn,
                TotalCopiesText = FormTotalCopies,
                ShelfLocation = FormShelfLocation,
                Description = FormDescription
            };

            ServiceOperationResult result = IsAddNewMode
                ? _libraryManagementService.CreateBook(req)
                : _libraryManagementService.UpdateBook(req);

            IsStaffFeedbackSuccess = result.IsSuccess;
            StaffActionFeedback = result.Message;
            _statusFeedbackCallback(result.Message);

            if (result.IsSuccess && IsAddNewMode)
            {
                IsAddNewMode = false;
                if (SelectedBook is not null)
                {
                    PopulateFormFromBook(SelectedBook);
                }
            }
        }

        private void ExecuteDeleteSelectedBook()
        {
            if (SelectedBook is null)
            {
                return;
            }

            string confirmPrompt = LocalizationService.IsVietnamese
                ? $"Bạn có chắc chắn muốn xóa đầu sách '{SelectedBook.Title}' ({SelectedBook.BookCode}) khỏi BooktibusDB?\n\nLưu ý: Hệ thống sẽ từ chối xóa nếu sách đã từng phát sinh giao dịch mượn trả."
                : $"Are you sure you want to delete book '{SelectedBook.Title}' ({SelectedBook.BookCode}) from BooktibusDB?\n\nNote: Deletion will be blocked if any borrowing history references this book.";

            string confirmTitle = LocalizationService.IsVietnamese
                ? "Xác nhận Xóa Đầu sách"
                : "Confirm Book Deletion";

            MessageBoxResult answer = MessageBox.Show(
                confirmPrompt,
                confirmTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            ServiceOperationResult result = _libraryManagementService.DeleteBook(SelectedBook.BookId);
            IsStaffFeedbackSuccess = result.IsSuccess;
            StaffActionFeedback = result.Message;
            _statusFeedbackCallback(result.Message);
        }

        private void PopulateFormFromBook(BookItemModel book)
        {
            _editingBookId = book.BookId;
            FormBookCode = book.BookCode;
            FormTitle = book.Title;
            FormAuthor = book.Author;
            FormCategory = book.Category;
            FormPublisher = book.Publisher;
            FormPublishYear = book.PublishYear?.ToString() ?? string.Empty;
            FormIsbn = book.Isbn;
            FormTotalCopies = book.TotalCopies.ToString();
            FormShelfLocation = book.ShelfLocation;
            FormDescription = book.Description;
            OnPropertyChanged(nameof(StaffFormModeTitle));
        }

        private void InitializeFilterOptions()
        {
            _isSuppressingFilterReload = true;

            string prevCategoryKey = _selectedCategoryOption?.Key ?? string.Empty;
            string prevAvailabilityKey = _selectedAvailabilityOption?.Key ?? "ALL";

            CategoryOptions.Clear();
            CategoryOptions.Add(new FilterOptionItem
            {
                Key = string.Empty,
                DisplayText = LocalizationService.IsVietnamese ? "Tất cả thể loại" : "All Categories"
            });

            foreach (string cat in _readerService.GetDistinctCategories())
            {
                CategoryOptions.Add(new FilterOptionItem
                {
                    Key = cat,
                    DisplayText = cat
                });
            }

            AvailabilityOptions.Clear();
            if (LocalizationService.IsVietnamese)
            {
                AvailabilityOptions.Add(new FilterOptionItem { Key = "ALL", DisplayText = "Tất cả tình trạng" });
                AvailabilityOptions.Add(new FilterOptionItem { Key = "AVAILABLE", DisplayText = "Chỉ sách còn bản sao" });
                AvailabilityOptions.Add(new FilterOptionItem { Key = "OUT_OF_STOCK", DisplayText = "Sách tạm hết bản sao" });
            }
            else
            {
                AvailabilityOptions.Add(new FilterOptionItem { Key = "ALL", DisplayText = "All Availability" });
                AvailabilityOptions.Add(new FilterOptionItem { Key = "AVAILABLE", DisplayText = "Available Copies Only" });
                AvailabilityOptions.Add(new FilterOptionItem { Key = "OUT_OF_STOCK", DisplayText = "Out of Stock Only" });
            }

            _selectedCategoryOption = CategoryOptions.FirstOrDefault(c => c.Key == prevCategoryKey) ?? CategoryOptions.FirstOrDefault();
            _selectedAvailabilityOption = AvailabilityOptions.FirstOrDefault(a => a.Key == prevAvailabilityKey) ?? AvailabilityOptions.FirstOrDefault();

            OnPropertyChanged(nameof(SelectedCategoryOption));
            OnPropertyChanged(nameof(SelectedAvailabilityOption));

            _isSuppressingFilterReload = false;
        }

        private void ReloadCatalogPreservingSelection()
        {
            int? previousSelectedId = SelectedBook?.BookId;
            LoadBooksFromDatabase(previousSelectedId);
        }

        private void LoadBooksFromDatabase(int? preserveBookId = null)
        {
            string categoryKey = _selectedCategoryOption?.Key ?? string.Empty;
            string availabilityKey = _selectedAvailabilityOption?.Key ?? "ALL";

            IReadOnlyList<BookItemModel> items = _readerService.SearchBooks(
                SearchKeyword,
                categoryKey,
                availabilityKey,
                out string? error);

            ErrorMessage = error ?? string.Empty;

            Books.Clear();
            foreach (BookItemModel item in items)
            {
                Books.Add(item);
            }

            if (preserveBookId.HasValue)
            {
                SelectedBook = Books.FirstOrDefault(b => b.BookId == preserveBookId.Value) ?? Books.FirstOrDefault();
            }
            else
            {
                SelectedBook = Books.FirstOrDefault();
            }

            OnPropertyChanged(nameof(IsCatalogEmpty));
            OnPropertyChanged(nameof(ResultSummaryText));
        }
    }
}

