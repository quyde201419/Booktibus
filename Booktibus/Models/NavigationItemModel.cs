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

        public string DisplayTitle => LocalizationService.GetString(TitleResourceKey, PageKey.ToString());

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
            IsVisible = AllowedRoles.Contains(activeRole);
        }

        public void RefreshLocalization()
        {
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }
}

