using Booktibus.Models;

namespace Booktibus.Services
{
    /// <summary>
    /// Contract for user authentication and registration services
    /// (Temporary Stage 4 test store or SQL Server BooktibusDB repository).
    /// </summary>
    public interface IAuthenticationService
    {
        bool IsTemporaryTestProvider { get; }

        int MaxFailedAttemptsBeforeLockout { get; }

        TimeSpan LockoutDuration { get; }

        bool RequiresInitialAdminSetup();

        AuthenticationResult Authenticate(string username, string password);

        RegistrationResult RegisterReader(ReaderRegistrationRequest request);

        RegistrationResult SetupInitialAdministrator(InitialAdminSetupRequest request);

        IReadOnlyList<TestCredentialGuideItem> GetStage4TestCredentialGuides();

        void ResetTestAccountLockouts();
    }
}

