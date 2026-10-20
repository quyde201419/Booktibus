using System.Collections.ObjectModel;
using System.Windows.Input;
using Booktibus.Commands;
using Booktibus.Models;
using Booktibus.Services;

namespace Booktibus.ViewModels
{
    /// <summary>
    /// ViewModel for the Reader's Personal Favorites page (Stage 5.3).
    /// Displays and manages only the authenticated user's favorites stored in dbo.ReaderFavorites.
    /// </summary>
    public class ReaderFavoritesViewModel : PageViewModelBase
    {
        private readonly ReaderWorkspaceService _readerService;
        private readonly Action<NavigationPageKey> _navigateCallback;
        private readonly Action<string> _statusFeedbackCallback;

        private FavoriteBookItemModel? _selectedFavorite;
        private string _errorMessage = string.Empty;

        public ReaderFavoritesViewModel(
            ReaderWorkspaceService readerService,
            Action<NavigationPageKey> navigateCallback,
            Action<string> statusFeedbackCallback)
            : base(
                NavigationPageKey.Favorites,
                "Str.Page.Favorites.Title",
                "Str.Page.Favorites.Subtitle")
        {
            _readerService = readerService;
            _navigateCallback = navigateCallback;
            _statusFeedbackCallback = statusFeedbackCallback;

            Favorites = new ObservableCollection<FavoriteBookItemModel>();

            RefreshCommand = new RelayCommand(ExecuteRefresh);
            RemoveFavoriteCommand = new RelayCommand(ExecuteRemoveFavorite);
            BrowseCatalogCommand = new RelayCommand(() => _navigateCallback(NavigationPageKey.Books));

            _readerService.FavoritesChanged += (_, _) => LoadFavoritesFromDatabase();
        }

        public ObservableCollection<FavoriteBookItemModel> Favorites { get; }

        public FavoriteBookItemModel? SelectedFavorite
        {
            get => _selectedFavorite;
            set
            {
                if (SetProperty(ref _selectedFavorite, value))
                {
                    OnPropertyChanged(nameof(HasSelectedFavorite));
                }
            }
        }

        public bool HasSelectedFavorite => SelectedFavorite is not null;

        public bool IsFavoritesEmpty => Favorites.Count == 0;

        public string FavoritesCountSummary => LocalizationService.IsVietnamese
            ? $"Danh sách yêu thích cá nhân của bạn hiện có {Favorites.Count} cuốn sách"
            : $"Your personal Favorites list currently contains {Favorites.Count} book(s)";

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

        public ICommand RefreshCommand { get; }

        public ICommand RemoveFavoriteCommand { get; }

        public ICommand BrowseCatalogCommand { get; }

        public override void OnLanguageUpdated()
        {
            base.OnLanguageUpdated();
            foreach (FavoriteBookItemModel fav in Favorites)
            {
                fav.Book.RefreshLocalization();
            }
            OnPropertyChanged(nameof(FavoritesCountSummary));
        }

        public void OnSessionUpdated()
        {
            LoadFavoritesFromDatabase();
        }

        private void ExecuteRefresh()
        {
            LoadFavoritesFromDatabase();
            _statusFeedbackCallback(FavoritesCountSummary);
        }

        private void ExecuteRemoveFavorite(object? parameter)
        {
            FavoriteBookItemModel? target = parameter as FavoriteBookItemModel ?? SelectedFavorite;
            if (target is null)
            {
                return;
            }

            ServiceOperationResult result = _readerService.RemoveFromOwnFavorites(target.Book.BookId);
            _statusFeedbackCallback(result.Message);
        }

        private void LoadFavoritesFromDatabase()
        {
            IReadOnlyList<FavoriteBookItemModel> list = _readerService.GetOwnFavorites(out string? error);
            ErrorMessage = error ?? string.Empty;

            Favorites.Clear();
            foreach (FavoriteBookItemModel item in list)
            {
                Favorites.Add(item);
            }

            SelectedFavorite = Favorites.FirstOrDefault();
            OnPropertyChanged(nameof(IsFavoritesEmpty));
            OnPropertyChanged(nameof(FavoritesCountSummary));
        }
    }
}

