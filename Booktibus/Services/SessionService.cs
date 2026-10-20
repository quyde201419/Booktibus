using Booktibus.Models;

namespace Booktibus.Services
{
    /// <summary>
    /// Maintains the active authenticated user session across the application lifecycle.
    /// </summary>
    public class SessionService
    {
        public UserSession? CurrentSession { get; private set; }

        public bool IsAuthenticated => CurrentSession is not null;

        public event EventHandler<UserSession?>? SessionChanged;

        public void StartSession(UserSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            CurrentSession = session;
            SessionChanged?.Invoke(this, CurrentSession);
        }

        public void EndSession()
        {
            CurrentSession = null;
            SessionChanged?.Invoke(this, null);
        }

        public void UpdateCurrentSessionFullName(string updatedFullName)
        {
            if (CurrentSession is null || string.IsNullOrWhiteSpace(updatedFullName))
            {
                return;
            }

            CurrentSession = new UserSession
            {
                UserId = CurrentSession.UserId,
                Username = CurrentSession.Username,
                FullName = updatedFullName.Trim(),
                Role = CurrentSession.Role,
                ReaderCardId = CurrentSession.ReaderCardId,
                SignedInAtLocal = CurrentSession.SignedInAtLocal
            };
        }
    }
}

