using Booktibus.Models;
using Booktibus.Services;

namespace Booktibus.ViewModels
{
    /// <summary>
    /// Base class for all navigable page ViewModels displayed in the main content area.
    /// </summary>
    public abstract class PageViewModelBase : BaseViewModel
    {
        private readonly string _titleResourceKey;
        private readonly string _subtitleResourceKey;

        protected PageViewModelBase(
            NavigationPageKey pageKey,
            string titleResourceKey,
            string subtitleResourceKey)
        {
            PageKey = pageKey;
            _titleResourceKey = titleResourceKey;
            _subtitleResourceKey = subtitleResourceKey;
        }

        public NavigationPageKey PageKey { get; }

        public virtual string PageTitle => LocalizationService.GetString(_titleResourceKey, PageKey.ToString());

        public virtual string PageSubtitle => LocalizationService.GetString(_subtitleResourceKey, string.Empty);

        public virtual void OnLanguageUpdated()
        {
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
        }
    }
}

