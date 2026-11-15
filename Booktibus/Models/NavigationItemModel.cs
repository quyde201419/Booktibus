using Booktibus.Services;
using Booktibus.ViewModels;

namespace Booktibus.Models
{
    /// <summary>
    /// Represents a navigation item in the left Sidebar with role-based visibility and selection state.
    /// </summary>
    public class NavigationItemModel : BaseViewModel
    {
        private bool _isSelected;
        private bool _isVisible = true;
        private UserRole _activeRole = UserRole.Reader;

        public NavigationItemModel(
            NavigationPageKey pageKey,
            string iconGlyph,
            string titleResourceKey,
            IReadOnlyCollection<UserRole> allowedRoles)
        {
            PageKey = pageKey;
            IconGlyph = iconGlyph;
            TitleResourceKey = titleResourceKey;
            AllowedRoles = allowedRoles;
        }

        public NavigationPageKey PageKey { get; }

        public string IconGlyph { get; }

        public string TitleResourceKey { get; }

        public IReadOnlyCollection<UserRole> AllowedRoles { get; }

        public string DisplayTitle
        {
            get
            {
                if (PageKey == NavigationPageKey.BorrowingReturns && _activeRole == UserRole.Reader)
                {
                    return LocalizationService.IsVietnamese ? "Sách mượn & Hạn trả" : "My Loans & Due Dates";
                }
                return LocalizationService.GetString(TitleResourceKey, PageKey.ToString());
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        public void UpdateForRole(UserRole activeRole)
        {
            _activeRole = activeRole;
            IsVisible = AllowedRoles.Contains(activeRole);
            OnPropertyChanged(nameof(DisplayTitle));
        }

        public void RefreshLocalization()
        {
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }
}

